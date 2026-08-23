// Editor/Tests/MultiAgentVerdictIntegrityTest.cs
//
// mcp-multiagent-verdict-integrity (findings.md #4, 2026-06-11) — durable
// regression gate for (1) server-side qa run serialization (structured
// queued/running busy response, never a false Timeout verdict), (2) the
// session-keyed compile-output cache (a sibling trigger cannot clobber another
// agent's error view), and (3) the feed.poll agent_id filter (foreign-attributed
// events dropped server-side, unattributed events always pass).
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission code for sbep.test_mcp_multiagent_verdict_integrity; it does not CLAIM completion. The test runs post-editor-restart via editor.console_run (MCP plugin code does not hot-reload), and the PASS sentinel is written on observing the SUMMARY line.]
//
// Sibling to BatchAsyncIdentifyTest.cs: invokes handlers directly in-process via
// the same in-assembly Handler + ParseMcpEnvelope pattern (no MCP socket).
//
// PRECONDITION  Editor running (ConCmd context). No fixtures needed; does not
//               dispatch any real suite (the bogus-suite gate exercises only the
//               gate release path and returns on the bridge's fast error path).
// EXEC/ASSERT   Six gates, one log line each:
//   qa_busy_structured       — with the run gate held by a fake holder, a
//                              concurrent run_suite returns {status:"running",
//                              holder, elapsed_ms} — not a Timeout verdict.
//   qa_queued_after_wait     — a concurrent run_suite with wait_ms waits
//                              server-side then returns status:"queued" with
//                              waited_ms when the gate never frees.
//   qa_lease_token_safety    — Release with a stale token is a no-op; the
//                              current lease survives.
//   compile_cache_keyed      — agent A's cache entry survives agent B's
//                              subsequent records (the clobber repro).
//   compile_errors_session_view — compile.errors with agent_id=A reads A's own
//                              entry: cache_view.view=="session_keyed".
//   feed_filter_drops_foreign — feed.poll with agent_id=A returns A's +
//                              unattributed events, drops B's, and reports
//                              foreign_filtered_count.
// CLEANUP       Gate leases released + test cache entries evicted + feed
//               channel unsubscribed only if this test subscribed it (idempotent).

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class MultiAgentVerdictIntegrityTest
{
	/// <summary>Tier-3 regression test for findings.md #4 (multi-agent verdict integrity).</summary>
	[ConCmd( "sbep.test_mcp_multiagent_verdict_integrity" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_mcp_multiagent_verdict_integrity";
		int failures = 0;

		var agentA = "test-vi-a-" + Guid.NewGuid().ToString( "N" )[..8];
		var agentB = "test-vi-b-" + Guid.NewGuid().ToString( "N" )[..8];
		var holderId = "test-vi-holder-" + Guid.NewGuid().ToString( "N" )[..8];
		bool feedWasSubscribedBefore = false;
		bool feedSubscribedByTest = false;

		try
		{
			await GameTask.MainThread();
			Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );

			// ── Gate 1: qa_busy_structured ──────────────────────────────
			// Fake-hold the run gate, then submit a concurrent run_suite. The
			// contract (findings.md #4): the rival gets a structured busy
			// response — status/holder/elapsed_ms — and NO suite is dispatched
			// (so no false Timeout verdict can exist).
			if ( !QaRunGate.TryAcquire( holderId, "fake-holder-slug", "run_suite", "gh_full", out var holderLease, out _ ) )
			{
				failures++;
				Log.Info( $"[FAIL] {TestName}: ASSERT qa_busy_structured — could not take the run gate for the fixture (held by a real run?)" );
			}
			else
			{
				try
				{
					using var args1 = JsonDocument.Parse( JsonSerializer.Serialize(
						new Dictionary<string, object> { ["suite"] = "smoke", ["agent_id"] = agentA } ) );
					var resp1 = await QaHandler.HandleAsync( "run_suite", args1.RootElement );
					await GameTask.MainThread();
					using var doc1 = ParseMcpEnvelope( resp1 );
					var root1 = doc1.RootElement;
					string status1 = root1.TryGetProperty( "status", out var s1 ) ? s1.GetString() : null;
					string busyHolder = root1.TryGetProperty( "holder", out var h1 )
						&& h1.TryGetProperty( "agent_id", out var ha1 ) ? ha1.GetString() : null;
					bool hasElapsed = root1.TryGetProperty( "elapsed_ms", out var e1 )
						&& e1.ValueKind == JsonValueKind.Number && e1.GetInt64() >= 0;
					if ( status1 == "running" && busyHolder == holderId && hasElapsed )
					{
						Log.Info( $"[PASS] {TestName}: ASSERT qa_busy_structured — concurrent run_suite returned structured busy (status='running', holder='{busyHolder}', elapsed_ms present); no suite dispatched, no Timeout verdict possible" );
					}
					else
					{
						failures++;
						var raw1 = JsonSerializer.Serialize( resp1 );
						var head1 = raw1.Length > 400 ? raw1[..400] + "…" : raw1;
						Log.Info( $"[FAIL] {TestName}: ASSERT qa_busy_structured — status='{status1}' (want 'running') holder='{busyHolder}' (want '{holderId}') elapsed_ok:{hasElapsed}: {head1}" );
					}

					// ── Gate 2: qa_queued_after_wait ────────────────────
					var swWait = System.Diagnostics.Stopwatch.StartNew();
					using var args2 = JsonDocument.Parse( JsonSerializer.Serialize(
						new Dictionary<string, object> { ["suite"] = "smoke", ["agent_id"] = agentA, ["wait_ms"] = 600 } ) );
					var resp2 = await QaHandler.HandleAsync( "run_suite", args2.RootElement );
					await GameTask.MainThread();
					swWait.Stop();
					using var doc2 = ParseMcpEnvelope( resp2 );
					var root2 = doc2.RootElement;
					string status2 = root2.TryGetProperty( "status", out var s2 ) ? s2.GetString() : null;
					long waitedMs = root2.TryGetProperty( "waited_ms", out var w2 )
						&& w2.ValueKind == JsonValueKind.Number ? w2.GetInt64() : -1;
					if ( status2 == "queued" && waitedMs >= 500 && swWait.ElapsedMilliseconds >= 500 )
					{
						Log.Info( $"[PASS] {TestName}: ASSERT qa_queued_after_wait — run_suite wait_ms=600 waited server-side ({waitedMs}ms reported, {swWait.ElapsedMilliseconds}ms observed) then returned status='queued'" );
					}
					else
					{
						failures++;
						Log.Info( $"[FAIL] {TestName}: ASSERT qa_queued_after_wait — status='{status2}' (want 'queued') waited_ms={waitedMs} observed={swWait.ElapsedMilliseconds}ms (want >=500)" );
					}

					// ── Gate 3: qa_lease_token_safety ───────────────────
					// A stale token (holderLease-1 never issued to anyone alive)
					// must NOT release the current lease.
					QaRunGate.Release( holderLease - 1 );
					bool stillHeld = QaRunGate.IsHeld;
					if ( stillHeld )
					{
						Log.Info( $"[PASS] {TestName}: ASSERT qa_lease_token_safety — Release with a stale token was a no-op; current lease intact" );
					}
					else
					{
						failures++;
						Log.Info( $"[FAIL] {TestName}: ASSERT qa_lease_token_safety — stale-token Release dropped the live lease (token check broken)" );
					}
				}
				finally
				{
					QaRunGate.Release( holderLease );
				}

				if ( QaRunGate.IsHeld )
				{
					failures++;
					Log.Info( $"[FAIL] {TestName}: ASSERT qa_lease_token_safety — gate still held after holder Release" );
				}
			}

			// ── Gate 4: compile_cache_keyed (the clobber repro) ─────────
			CompileOutputCache.Record( Array.Empty<CompilerOutput>(), agentA, "slug-a", "test_seed_a" );
			CompileOutputCache.Record( Array.Empty<CompilerOutput>(), agentB, "slug-b", "test_seed_b1" );
			CompileOutputCache.Record( Array.Empty<CompilerOutput>(), agentB, "slug-b", "test_seed_b2" );
			bool aFound = CompileOutputCache.TryGetLatestFor( agentA, out var aEntry );
			bool bFound = CompileOutputCache.TryGetLatestFor( agentB, out var bEntry );
			bool aIntact = aFound && aEntry.AgentId == agentA && aEntry.Source == "test_seed_a";
			bool bNewest = bFound && bEntry.Source == "test_seed_b2";
			if ( aIntact && bNewest )
			{
				Log.Info( $"[PASS] {TestName}: ASSERT compile_cache_keyed — agent A's entry survived two subsequent agent-B records (A reads test_seed_a, B reads test_seed_b2); the global-snapshot clobber class is closed for keyed reads" );
			}
			else
			{
				failures++;
				Log.Info( $"[FAIL] {TestName}: ASSERT compile_cache_keyed — aFound:{aFound} aIntact:{aIntact} (source='{aEntry?.Source}') bNewest:{bNewest} (source='{bEntry?.Source}')" );
			}

			// ── Gate 5: compile_errors_session_view ─────────────────────
			using ( var argsErr = JsonDocument.Parse( JsonSerializer.Serialize(
				new Dictionary<string, object> { ["agent_id"] = agentA } ) ) )
			{
				var respErr = await CompileHandler.HandleAsync( "errors", argsErr.RootElement );
				await GameTask.MainThread();
				using var docErr = ParseMcpEnvelope( respErr );
				var rootErr = docErr.RootElement;
				string view = null;
				string viewAgent = null;
				if ( rootErr.TryGetProperty( "cache_view", out var cv ) && cv.ValueKind == JsonValueKind.Object )
				{
					if ( cv.TryGetProperty( "view", out var v ) ) view = v.GetString();
					if ( cv.TryGetProperty( "agent_id", out var va ) ) viewAgent = va.GetString();
				}
				if ( view == "session_keyed" && viewAgent == agentA )
				{
					Log.Info( $"[PASS] {TestName}: ASSERT compile_errors_session_view — compile.errors agent_id={agentA} read the caller's OWN cached entry (cache_view.view='session_keyed')" );
				}
				else
				{
					failures++;
					var rawErr = JsonSerializer.Serialize( respErr );
					var headErr = rawErr.Length > 400 ? rawErr[..400] + "…" : rawErr;
					Log.Info( $"[FAIL] {TestName}: ASSERT compile_errors_session_view — view='{view}' (want 'session_keyed') agent='{viewAgent}' (want '{agentA}'): {headErr}" );
				}
			}

			// ── Gate 6: feed_filter_drops_foreign ───────────────────────
			// Use the async_job channel (push-only; we control every emit). If a
			// sibling already subscribed it, reuse the subscription and DO NOT
			// tear it down in cleanup.
			long baselineSeq = -1;
			{
				using var chDoc = ParseMcpEnvelope( FeedHandler.Handle( "channels", default ) );
				if ( chDoc.RootElement.TryGetProperty( "channels", out var chArr ) && chArr.ValueKind == JsonValueKind.Array )
				{
					foreach ( var row in chArr.EnumerateArray() )
					{
						if ( row.TryGetProperty( "name", out var n ) && n.GetString() == "async_job" )
							feedWasSubscribedBefore = row.TryGetProperty( "subscribed", out var sub ) && sub.GetBoolean();
					}
				}

				using var subArgs = JsonDocument.Parse( "{\"channel\":\"async_job\"}" );
				using var subDoc = ParseMcpEnvelope( FeedHandler.Handle( "subscribe", subArgs.RootElement ) );
				feedSubscribedByTest = !feedWasSubscribedBefore;
				if ( subDoc.RootElement.TryGetProperty( "next_seq", out var ns ) && ns.ValueKind == JsonValueKind.Number )
					baselineSeq = ns.GetInt64();
			}

			var jobShared = "test-vi-job-shared-" + Guid.NewGuid().ToString( "N" )[..8];
			var jobMine = "test-vi-job-mine-" + Guid.NewGuid().ToString( "N" )[..8];
			var jobForeign = "test-vi-job-foreign-" + Guid.NewGuid().ToString( "N" )[..8];
			FeedHandler.EmitAsyncJob( jobShared, "running" );                    // unattributed — must pass any filter
			FeedHandler.EmitAsyncJob( jobMine, "running", null, agentA );        // mine — must pass agent_id=agentA
			FeedHandler.EmitAsyncJob( jobForeign, "running", null, agentB );     // foreign — must be dropped

			{
				using var pollArgs = JsonDocument.Parse( JsonSerializer.Serialize(
					new Dictionary<string, object>
					{
						["channel"] = "async_job",
						["since_seq"] = baselineSeq - 1,
						["agent_id"] = agentA,
						["limit"] = 1000
					} ) );
				var pollResp = FeedHandler.Handle( "poll", pollArgs.RootElement );
				var rawPoll = JsonSerializer.Serialize( pollResp );
				using var pollDoc = ParseMcpEnvelope( pollResp );
				int foreignFiltered = pollDoc.RootElement.TryGetProperty( "foreign_filtered_count", out var ff )
					&& ff.ValueKind == JsonValueKind.Number ? ff.GetInt32() : -1;
				bool sharedSeen = rawPoll.Contains( jobShared );
				bool mineSeen = rawPoll.Contains( jobMine );
				bool foreignDropped = !rawPoll.Contains( jobForeign );
				if ( sharedSeen && mineSeen && foreignDropped && foreignFiltered >= 1 )
				{
					Log.Info( $"[PASS] {TestName}: ASSERT feed_filter_drops_foreign — poll agent_id={agentA} returned own+unattributed events, dropped the foreign-attributed one server-side (foreign_filtered_count={foreignFiltered})" );
				}
				else
				{
					failures++;
					var headPoll = rawPoll.Length > 400 ? rawPoll[..400] + "…" : rawPoll;
					Log.Info( $"[FAIL] {TestName}: ASSERT feed_filter_drops_foreign — sharedSeen:{sharedSeen} mineSeen:{mineSeen} foreignDropped:{foreignDropped} foreign_filtered_count={foreignFiltered}: {headPoll}" );
				}
			}

			// ── CLEANUP ─────────────────────────────────────────────────
			int evicted = CompileOutputCache.EvictAgent( agentA ) + CompileOutputCache.EvictAgent( agentB );
			if ( feedSubscribedByTest )
			{
				using var unsubArgs = JsonDocument.Parse( "{\"channel\":\"async_job\"}" );
				FeedHandler.Handle( "unsubscribe", unsubArgs.RootElement );
			}
			Log.Info( $"[PASS] {TestName}: CLEANUP — {evicted} test cache entries evicted; feed async_job {( feedSubscribedByTest ? "unsubscribed (test-owned)" : "left subscribed (pre-existing subscriber)" )}; gate leases released (idempotent)" );
		}
		catch ( Exception ex )
		{
			failures++;
			Log.Info( $"[FAIL] {TestName}: EXEC — unhandled exception: {ex.GetType().Name}: {ex.Message}" );
		}

		// ── SUMMARY (recorded even on throw — async-void contract) ──────
		if ( failures == 0 )
		{
			Log.Info( $"[PASS] {TestName}: SUMMARY — 6/6 gates green (qa_busy_structured + qa_queued_after_wait + qa_lease_token_safety + compile_cache_keyed + compile_errors_session_view + feed_filter_drops_foreign)" );
		}
		else
		{
			Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of 6 total" );
		}
	}

	/// <summary>
	/// Unwrap the MCP envelope <c>{content:[{type:"text",text:"&lt;inner-json&gt;"}]}</c>
	/// that HandlerBase.Success wraps around handler responses. Copied verbatim
	/// from BatchAsyncIdentifyTest.cs / AgentObserveTest.cs per Phase 2
	/// banned-approach #2 — do not re-derive the envelope shape.
	/// </summary>
	private static JsonDocument ParseMcpEnvelope( object handlerResponse )
	{
		var envelopeJson = JsonSerializer.Serialize( handlerResponse );
		using ( var envelopeDoc = JsonDocument.Parse( envelopeJson ) )
		{
			if ( envelopeDoc.RootElement.TryGetProperty( "content", out var contentArr )
				&& contentArr.ValueKind == JsonValueKind.Array
				&& contentArr.GetArrayLength() > 0 )
			{
				var first = contentArr[0];
				if ( first.TryGetProperty( "text", out var textProp )
					&& textProp.ValueKind == JsonValueKind.String )
				{
					return JsonDocument.Parse( textProp.GetString() );
				}
			}
		}
		// Defensive fallback: return the envelope verbatim so TryGetProperty
		// lookups miss cleanly instead of crashing.
		return JsonDocument.Parse( envelopeJson );
	}
}
