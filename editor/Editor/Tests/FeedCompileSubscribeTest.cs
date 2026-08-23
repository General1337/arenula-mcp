// engine-fork-elite-leverage Phase 2.1 — test ConCmd for feed.compile.subscribe
//
// [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
//
// Durable regression gate for the Phase 2.1 wire-format consumer side of
// the CompileGroup.OnBuildCompleted (1.1) + OnCompileFailed (1.2) reflection
// subscription. Proves the consumer/producer split is symmetric with
// EmitAsyncJob: CompileHandler owns the engine seam; FeedHandler.EmitCompile
// fans out to feed.compile subscribers.
//
// Under fork engine: confirms feed.compile is in ActiveChannels, subscribes
// via FeedHandler.Handle("subscribe"), triggers EditorUtility.Projects.Compile
// (same path CompileHandler.Trigger uses + 1.1/1.2 tests use), polls via
// FeedHandler.Handle("poll"), asserts at least one build_completed event
// arrived with the expected wire-format payload shape (output_count >= 1,
// total_error_count + total_warning_count present, per-output diagnostics
// shape mirroring compile.errors).
//
// Under stable engine: the producer-side reflection probe (CompileGroup.
// OnBuildCompleted) returns null in CompileHandler.EnsureBound, so even
// though feed.compile is registered as active, no events will flow.
// Detected via the SAME reflection probe used by CompileOnBuildCompletedTest.
//
// VERIFIED via current source read 2026-05-19:
//   * FeedHandler.cs:74-83 — ChannelCompile now in ActiveChannels HashSet
//     (Phase 2.1 promotion).
//   * FeedHandler.cs:589 — EmitCompile(string, CompilerOutput[]) public static.
//   * CompileHandler.cs:1026 — OnEngineBuildCompleted now calls
//     FeedHandler.EmitCompile("build_completed", outputs).
//   * CompileHandler.cs:1050 — OnEngineCompileFailed now calls
//     FeedHandler.EmitCompile("compile_failed", failedOutputs).
//   * The deferred-Phase-2-hook comment at the old CompileHandler.cs:1030
//     is now resolved (replaced with the actual EmitCompile call).

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class FeedCompileSubscribeTest
{
	/// <summary>
	/// Tier-3 regression test for the Phase 2.1 wrapper.
	///   PRECONDITION — Active, non-transient, code-bearing project loaded.
	///                  Reflection-probe CompileGroup.OnBuildCompleted exists
	///                  (fork-only; under stable engine the test [SKIP]s).
	///                  feed.channels reports compile.available==true.
	///   EXEC         — feed.subscribe channel=compile; trigger Compile;
	///                  give 500ms grace; feed.poll channel=compile.
	///   ASSERT       — Subscribe returns active-channel shape (NOT stub shape).
	///                  Poll returns >= 1 event with type=build_completed and
	///                  payload.output_count >= 1, payload.outputs[*] carrying
	///                  the wire-format shape (compiler, successful, error_count,
	///                  warning_count, diagnostic_count, diagnostics_truncated,
	///                  diagnostics[]).
	///   CLEANUP      — feed.unsubscribe channel=compile (regardless of pass/fail).
	/// </summary>
	[ConCmd( "sbep.test_feed_compile_subscribe" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_feed_compile_subscribe";
		const string Channel = "compile";

		// ── PRECONDITION ────────────────────────────────────────────────
		await GameTask.MainThread();

		// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
		// Unique marker to verify hot-reload picked up the latest edit (v2 with ParseMcpEnvelope unwrap).
		Log.Info( $"[INFO] {TestName}: ENTER v2-envelope-unwrap @ {DateTime.UtcNow:O}" );

		// Producer-side reflection probe: under stable Facepunch engine
		// CompileGroup.OnBuildCompleted doesn't exist, so even though
		// feed.compile is in ActiveChannels, no events would flow. SKIP
		// rather than report a misleading FAIL.
		var producerEvent = typeof( CompileGroup ).GetEvent(
			"OnBuildCompleted",
			BindingFlags.Public | BindingFlags.Static );
		if ( producerEvent == null )
		{
			Log.Info( $"[SKIP] {TestName}: fork-only event CompileGroup.OnBuildCompleted not found — running under stable Facepunch engine. This test is fork-only by design (the producer-side reflection seam is absent; feed.compile remains in ActiveChannels but the channel never emits)." );
			return;
		}

		// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
		// Channel-presence probe: feed.channels must report compile.available==true.
		// FeedHandler.Handle returns an MCP envelope {content:[{type:"text",text:"<inner-json>"}]};
		// unwrap it via ParseMcpEnvelope (defined below) to reach the actual response payload.
		var channelsResp = FeedHandler.Handle( "channels", default );
		using var channelsDoc = ParseMcpEnvelope( channelsResp );
		bool compileAvailable = false;
		if ( channelsDoc.RootElement.TryGetProperty( "channels", out var channelsArray )
			&& channelsArray.ValueKind == JsonValueKind.Array )
		{
			foreach ( var row in channelsArray.EnumerateArray() )
			{
				if ( row.TryGetProperty( "name", out var nameProp )
					&& string.Equals( nameProp.GetString(), Channel, StringComparison.OrdinalIgnoreCase )
					&& row.TryGetProperty( "available", out var availProp )
					&& availProp.ValueKind == JsonValueKind.True )
				{
					compileAvailable = true;
					break;
				}
			}
		}
		if ( !compileAvailable )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — feed.channels did not report compile.available=true (Phase 2.1 promotion failed to land)" );
			return;
		}

		var projects = EditorUtility.Projects.GetAll();
		var compilable = projects?
			.FirstOrDefault( p => p.Active && !p.IsTransient && !p.IsBuiltIn && p.HasCodePath() );
		if ( compilable == null )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — no compilable project (need Active + non-transient + HasCodePath)" );
			return;
		}

		Log.Info( $"[PASS] {TestName}: PRECONDITION — producer event present + feed.compile active + compilable project '{compilable.Config?.Title ?? "?"}' loaded" );

		// ── EXEC ────────────────────────────────────────────────────────
		// Subscribe via FeedHandler.Handle directly (no MCP round-trip needed —
		// ConCmd runs in-process on the main thread, same as the handler).
		// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
		var subscribeArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\"}}" ).RootElement;
		var subscribeResp = FeedHandler.Handle( "subscribe", subscribeArgs );
		using var subscribeDoc = ParseMcpEnvelope( subscribeResp );

		// Active-channel shape: ok=true + subscribed=true + next_seq present.
		// Stub-channel shape (the v1.1-deferred behaviour we're replacing):
		// ok=false + available=false + reason string. The test's whole point
		// is to prove we got the active-channel shape, not the stub shape.
		// [HANDOFF-TRUST: revert mistaken camelCase, no live hypothesis]
		// Correction: JsonNamingPolicy.CamelCase only lowercases the FIRST
		// character of a property name (per .NET docs); it does NOT convert
		// snake_case→camelCase. So `next_seq` defined in C# stays `next_seq`
		// in the JSON. Live MCP probe verified: response shape uses snake_case.
		bool subscribedOk = false;
		long initialSeq = 0;
		if ( subscribeDoc.RootElement.TryGetProperty( "subscribed", out var subProp )
			&& subProp.ValueKind == JsonValueKind.True )
		{
			subscribedOk = true;
			if ( subscribeDoc.RootElement.TryGetProperty( "next_seq", out var seqProp )
				&& seqProp.TryGetInt64( out var seqVal ) )
			{
				initialSeq = seqVal;
			}
		}
		// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
		if ( !subscribedOk )
		{
			Log.Info( $"[FAIL] {TestName}: EXEC — subscribe returned non-active shape: {JsonSerializer.Serialize( subscribeResp )}" );
			return;
		}

		try
		{
			// Trigger a Compile — same path CompileOnBuildCompletedTest uses.
			// The fork-engine event raises synchronously inside BuildAsync before
			// the awaiter resumes, so by the time Compile returns the event
			// should already have fired and FeedHandler should have buffered it.
			await EditorUtility.Projects.Compile( compilable, null );
			await Task.Delay( 500 );

			// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
			// Poll the channel for events since our initial seq baseline.
			var pollArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\",\"since_seq\":{initialSeq - 1}}}" ).RootElement;
			var pollResp = FeedHandler.Handle( "poll", pollArgs );
			using var pollDoc = ParseMcpEnvelope( pollResp );

			Log.Info( $"[PASS] {TestName}: EXEC — Compile returned, poll returned response (initial seq baseline={initialSeq})" );

			// ── ASSERT ──────────────────────────────────────────────────
			// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
			if ( !pollDoc.RootElement.TryGetProperty( "events", out var eventsArr )
				|| eventsArr.ValueKind != JsonValueKind.Array )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — poll response missing events[] array: {JsonSerializer.Serialize( pollResp )}" );
				return;
			}

			int buildCompletedCount = 0;
			JsonElement firstBuildCompletedPayload = default;
			foreach ( var ev in eventsArr.EnumerateArray() )
			{
				if ( ev.TryGetProperty( "type", out var typeProp )
					&& string.Equals( typeProp.GetString(), "build_completed", StringComparison.Ordinal ) )
				{
					buildCompletedCount++;
					if ( buildCompletedCount == 1
						&& ev.TryGetProperty( "payload", out var payloadProp ) )
					{
						firstBuildCompletedPayload = payloadProp.Clone();
					}
				}
			}

			if ( buildCompletedCount == 0 )
			{
				// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
				Log.Info( $"[FAIL] {TestName}: ASSERT — no build_completed event in poll response (expected >=1; payload pattern: {JsonSerializer.Serialize( pollResp )})" );
				return;
			}

			// Validate payload shape on the first build_completed event.
			if ( firstBuildCompletedPayload.ValueKind != JsonValueKind.Object )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — first build_completed payload is not an object" );
				return;
			}
			// [HANDOFF-TRUST: revert mistaken camelCase, no live hypothesis]
			// Snake_case is correct per JsonNamingPolicy.CamelCase semantics.
			if ( !firstBuildCompletedPayload.TryGetProperty( "output_count", out var outCountProp )
				|| !outCountProp.TryGetInt32( out var outCount )
				|| outCount < 1 )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — payload.output_count missing or <1" );
				return;
			}
			if ( !firstBuildCompletedPayload.TryGetProperty( "outputs", out var outputsArr )
				|| outputsArr.ValueKind != JsonValueKind.Array
				|| outputsArr.GetArrayLength() < 1 )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — payload.outputs[] missing or empty" );
				return;
			}

			var firstOutput = outputsArr.EnumerateArray().First();
			string[] requiredKeys = new[]
			{
				"compiler", "successful", "error_count", "warning_count",
				"diagnostic_count", "diagnostics_truncated", "diagnostics"
			};
			foreach ( var key in requiredKeys )
			{
				if ( !firstOutput.TryGetProperty( key, out _ ) )
				{
					Log.Info( $"[FAIL] {TestName}: ASSERT — payload.outputs[0] missing required key '{key}'" );
					return;
				}
			}
			if ( !firstBuildCompletedPayload.TryGetProperty( "total_error_count", out _ )
				|| !firstBuildCompletedPayload.TryGetProperty( "total_warning_count", out _ ) )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — payload missing total_error_count / total_warning_count" );
				return;
			}

			Log.Info( $"[PASS] {TestName}: ASSERT — {buildCompletedCount} build_completed event(s) with {outCount} output(s); wire-format keys present (compiler/successful/error_count/warning_count/diagnostic_count/diagnostics_truncated/diagnostics); Phase 2.1 wrapper verified end-to-end (CompileHandler reflection-bind → OnEngineBuildCompleted → FeedHandler.EmitCompile → ring buffer → feed.poll)" );
		}
		finally
		{
			// ── CLEANUP ─────────────────────────────────────────────────
			try
			{
				var unsubArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\"}}" ).RootElement;
				FeedHandler.Handle( "unsubscribe", unsubArgs );
				Log.Info( $"[PASS] {TestName}: CLEANUP — unsubscribed from feed.compile" );
			}
			catch ( Exception ex )
			{
				Log.Info( $"[FAIL] {TestName}: CLEANUP — unsubscribe threw: {ex.Message}" );
			}
		}
	}

	// [HANDOFF-TRUST: fixing self-introduced test bug, no live hypothesis]
	/// <summary>
	/// Unwrap the MCP envelope <c>{content:[{type:"text",text:"&lt;inner-json&gt;"}]}</c>
	/// that HandlerBase.Success wraps around handler responses, returning a
	/// JsonDocument positioned at the inner response payload (which has the
	/// per-action keys: ok / subscribed / channel / channels / events / etc).
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
		// Defensive fallback: return the envelope verbatim. The caller's
		// TryGetProperty lookups will simply miss and report a clear failure
		// rather than crash on null.
		return JsonDocument.Parse( envelopeJson );
	}
}
