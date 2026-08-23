// Editor/Tests/TraceActionsTest.cs
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission
// code for sbep.test_mcp_trace_actions; it does not CLAIM completion. The actual run
// happens via editor.console_run, and the PASS sentinel is written by the test-evidence
// hook on observing the SUMMARY log line.]
//
// Durable regression gate for the 2026-08-05 upstream port that added
// sphere_cast / box_cast / sample_grid / multi_ray to the trace tool
// (upstream Arenula 74613e9, d476122), and for the tag-filter semantics that
// port deliberately did NOT adopt.
//
// The filter point is the one worth guarding. Upstream reads `tags` as an
// INCLUDE filter (WithAnyTags); this repo has always read it as EXCLUDE
// (WithoutTags), and every existing caller of ray / box / ground depends on
// that. Adopting upstream's meaning would have inverted them all silently, so
// here `tags` and `ignore_tags` exclude and `require_tags` includes. Gates
// filter_require / filter_exclude are what stop that from being re-broken.
//
// Determinism: the probe block carries a unique tag and EVERY trace passes
// require_tags for it. Nothing else in the scene can be hit, so the result does
// not depend on which scene is open or what a sibling agent spawned.
//
// PRECONDITION  Scene resolvable; spawn a tagged probe block with a BoxCollider
//               in this agent's scratch container.
// EXEC          Drive TraceHandler.Handle for each action against that block.
// ASSERT        Twelve gates, one log line each.
// CLEANUP       Destroy the probe block; verify it is gone.

using System;
using System.Linq;
using System.Text.Json;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class TraceActionsTest
{
	/// <summary>Tier-3 regression test for the trace tool's sweep / batch / grid actions.</summary>
	[ConCmd( "sbep.test_mcp_trace_actions" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_mcp_trace_actions";
		const string ProbeTag = "arenula_trace_probe";
		const string ProbeName = "arenula_trace_probe_block";
		const int TotalGates = 12;

		await GameTask.MainThread();
		Log.Info( $"[INFO] {TestName}: ENTER @ {DateTime.UtcNow:O}" );

		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
		{
			Log.Error( $"[FAIL] {TestName}: PRECONDITION — SceneHelpers.ResolveScene() returned null (no active editor session?)" );
			return;
		}

		// Clear any block left by an interrupted previous run.
		// DestroyImmediate so a leftover block cannot still be traceable when the
		// gates run a few lines below — Destroy would defer past them.
		foreach ( var stale in scene.GetAllObjects( true ).Where( o => o.Name == ProbeName ).ToList() )
			stale.DestroyImmediate();

		GameObject probe = null;
		int failures = 0;

		try
		{
			// ── PRECONDITION: a 100-unit block centred on the origin ──
			probe = scene.CreateObject();
			probe.Name = ProbeName;
			probe.WorldPosition = Vector3.Zero;
			probe.Flags |= GameObjectFlags.NotSaved;
			probe.Tags.Add( ProbeTag );

			var collider = probe.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 100, 100, 100 );

			// A collider added this frame is not in the physics world until it
			// steps. Without this every trace below misses and all twelve gates
			// fail for a reason that has nothing to do with the port.
			scene.PhysicsWorld?.Step( 0.1f );

			Log.Info( $"[PASS] {TestName}: PRECONDITION — probe block spawned at origin, 100^3, tagged '{ProbeTag}'" );

			// ── ray: straight down onto the top face at z=+50 ─────────
			var ray = Call( "ray", $@"{{""from"":""0,0,500"",""to"":""0,0,-500"",""require_tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "ray_hits",
				Bool( ray, "hit" ) && NearZ( ray, 50f ),
				$"ray hit the top face at z={Z( ray )}",
				$"expected a hit near z=50, got hit={Bool( ray, "hit" )} z={Z( ray )}" );

			// ── sphere_cast ───────────────────────────────────────────
			var sphere = Call( "sphere_cast", $@"{{""from"":""0,0,500"",""to"":""0,0,-500"",""radius"":""10"",""require_tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "sphere_cast_hits",
				Bool( sphere, "hit" ),
				"sphere_cast swept onto the block",
				"sphere_cast reported no hit" );

			// A sphere swept well clear of a 100-wide block must miss — AND the
			// call must have succeeded. Asserting only "no hit" was a false pass:
			// an errored call also reports no hit, so the gate stayed green on
			// 2026-08-05 while sphere_cast was rejecting its own radius argument.
			var sphereMiss = Call( "sphere_cast", $@"{{""from"":""400,0,500"",""to"":""400,0,-500"",""radius"":10,""require_tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "sphere_cast_misses_when_clear",
				!IsError( sphereMiss ) && !Bool( sphereMiss, "hit" ),
				"a sphere swept 400 units off-axis returns a successful miss",
				IsError( sphereMiss )
					? "the call errored rather than returning a miss — a no-hit result here proves nothing"
					: "a sphere swept clear of the block still reported a hit" );

			// ── box_cast ──────────────────────────────────────────────
			var boxCast = Call( "box_cast", $@"{{""from"":""0,0,500"",""to"":""0,0,-500"",""size"":""10,10,10"",""require_tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "box_cast_hits",
				Bool( boxCast, "hit" ),
				"box_cast swept onto the block",
				"box_cast reported no hit" );

			// box_cast SWEEPS; the older 'box' action is a stationary overlap at
			// one point. Confirm they stayed distinct — collapsing box_cast onto
			// box would still pass every hit gate above.
			var boxStationary = Call( "box", $@"{{""position"":""0,0,0"",""size"":""10,10,10"",""require_tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "box_overlap_still_works",
				Bool( boxStationary, "hit" ),
				"the original stationary 'box' overlap still hits at the block centre",
				"stationary 'box' missed a block it is standing inside" );

			// ── multi_ray ─────────────────────────────────────────────
			var multi = Call( "multi_ray", $@"{{""rays"":[
				{{""from"":""0,0,500"",""to"":""0,0,-500""}},
				{{""from"":""9000,9000,500"",""to"":""9000,9000,-500""}}
			],""require_tags"":""{ProbeTag}""}}" );

			var results = Payload( multi ).TryGetProperty( "results", out var rEl ) && rEl.ValueKind == JsonValueKind.Array
				? rEl.EnumerateArray().ToList()
				: new System.Collections.Generic.List<JsonElement>();

			Gate( ref failures, TestName, "multi_ray_count",
				results.Count == 2,
				"multi_ray returned one result per input ray",
				$"expected 2 results, got {results.Count}" );

			Gate( ref failures, TestName, "multi_ray_hit_and_miss",
				results.Count == 2 && BoolOf( results[0], "hit" ) && !BoolOf( results[1], "hit" ),
				"the on-target ray hit and the far ray missed — results are per-ray, not shared",
				"multi_ray did not report one hit and one miss in order" );

			var tooMany = "{\"rays\":[" + string.Join( ",", Enumerable.Repeat( @"{""from"":""0,0,1"",""to"":""0,0,0""}", 300 ) ) + "]}";
			var overflow = Call( "multi_ray", tooMany );
			Gate( ref failures, TestName, "multi_ray_caps_batch",
				IsError( overflow ),
				"a 300-ray batch is refused rather than silently truncated",
				"a 300-ray batch was accepted; the cap is not enforced" );

			// ── sample_grid ───────────────────────────────────────────
			// samples_x/y are passed as STRINGS on purpose: MCP clients stringify
			// scalars, and GetInt used to drop a stringified number and silently
			// use its default. That made this call return a 10x10 grid while
			// reporting samples_x=10 for a requested 4. Numeric coercion is what
			// this gate is really pinning down.
			var grid = Call( "sample_grid", $@"{{""center"":""0,0,500"",""area_size"":""80,80"",""samples_x"":""4"",""samples_y"":""4"",""require_tags"":""{ProbeTag}""}}" );
			var gridPayload = Payload( grid );
			int gridHits = gridPayload.TryGetProperty( "hits", out var hEl ) ? hEl.GetInt32() : -1;
			int gridSamplesX = gridPayload.TryGetProperty( "samples_x", out var sxEl ) ? sxEl.GetInt32() : -1;
			Gate( ref failures, TestName, "sample_grid_hits",
				gridHits == 16 && gridSamplesX == 4,
				$"all 16 samples over an 80x80 area landed on the 100-wide block, and the stringified samples_x=4 was honoured",
				$"expected 16 hits at samples_x=4, got {gridHits} hits at samples_x={gridSamplesX}" );

			// A silent cap reads as "that is the whole area" — the response must say so.
			var clamped = Call( "sample_grid", $@"{{""center"":""0,0,500"",""area_size"":""80,80"",""samples_x"":""99"",""samples_y"":""2"",""require_tags"":""{ProbeTag}""}}" );
			var clampPayload = Payload( clamped );
			bool reportsClamp = clampPayload.TryGetProperty( "clamped", out var cEl )
				&& cEl.ValueKind == JsonValueKind.String
				&& cEl.GetString().Contains( "clamped" );
			Gate( ref failures, TestName, "sample_grid_reports_clamp",
				reportsClamp,
				"a request over the per-axis cap reports the clamp instead of quietly shrinking",
				"samples_x=99 was clamped without saying so in the response" );

			// ── tag filter semantics ──────────────────────────────────
			// The whole reason this port deviates from upstream.
			var required = Call( "ray", @"{""from"":""0,0,500"",""to"":""0,0,-500"",""require_tags"":""a_tag_nothing_carries""}" );
			Gate( ref failures, TestName, "filter_require",
				!Bool( required, "hit" ),
				"require_tags INCLUDES — requiring a tag nothing carries yields no hit",
				"require_tags did not restrict the trace" );

			var excluded = Call( "ray", $@"{{""from"":""0,0,500"",""to"":""0,0,-500"",""tags"":""{ProbeTag}""}}" );
			Gate( ref failures, TestName, "filter_exclude",
				!Bool( excluded, "hit" ) || !string.Equals( StringOf( excluded, "hit_object_name" ), ProbeName, StringComparison.Ordinal ),
				"tags EXCLUDES — naming the probe's tag in 'tags' stops it being hit (upstream semantics would have made this an include and hit it)",
				"'tags' behaved as an include filter; the probe was hit despite being excluded" );
		}
		catch ( Exception ex )
		{
			failures++;
			Log.Info( $"[FAIL] {TestName}: EXEC — unhandled {ex.GetType().Name}: {ex.Message}" );
		}
		finally
		{
			// ── CLEANUP ───────────────────────────────────────────────
			// DestroyImmediate, not Destroy: Destroy is deferred to end-of-frame,
			// so the "is it gone" check below ran in the same tick and always
			// found the object still present.
			probe?.DestroyImmediate();
			bool gone = !scene.GetAllObjects( true ).Any( o => o.Name == ProbeName );
			if ( gone )
				Log.Info( $"[PASS] {TestName}: CLEANUP — probe block destroyed. Idempotent re-run safe (PRECONDITION pre-clears)." );
			else
			{
				failures++;
				Log.Info( $"[FAIL] {TestName}: CLEANUP — probe block still present after Destroy()" );
			}
		}

		// ── SUMMARY ───────────────────────────────────────────────────
		if ( failures == 0 )
			Log.Info( $"[PASS] {TestName}: SUMMARY — {TotalGates}/{TotalGates} gates green; sphere_cast / box_cast / sample_grid / multi_ray verified and tag-filter semantics held" );
		else
			Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of {TotalGates} total" );
	}

	// ── Harness helpers ───────────────────────────────────────────────

	private static JsonElement Call( string action, string argsJson )
	{
		using var doc = JsonDocument.Parse( argsJson );
		var response = TraceHandler.Handle( action, doc.RootElement );
		return JsonSerializer.SerializeToElement( response );
	}

	/// <summary>
	/// Unwrap the handler envelope. HandlerBase.Success wraps the payload as JSON
	/// text inside content[0].text, so reading fields off the envelope directly
	/// would silently find nothing and every gate would fail as a false negative.
	/// </summary>
	private static JsonElement Payload( JsonElement envelope )
	{
		if ( envelope.TryGetProperty( "content", out var content )
			&& content.ValueKind == JsonValueKind.Array
			&& content.GetArrayLength() > 0
			&& content[0].TryGetProperty( "text", out var textEl )
			&& textEl.ValueKind == JsonValueKind.String )
		{
			var text = textEl.GetString();
			try
			{
				using var doc = JsonDocument.Parse( text );
				return doc.RootElement.Clone();
			}
			catch { /* not JSON — fall through to the envelope */ }
		}
		return envelope;
	}

	private static bool IsError( JsonElement envelope ) =>
		envelope.TryGetProperty( "isError", out var e ) && e.ValueKind == JsonValueKind.True;

	private static bool Bool( JsonElement envelope, string name ) => BoolOf( Payload( envelope ), name );

	private static bool BoolOf( JsonElement obj, string name ) =>
		obj.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.True;

	private static string StringOf( JsonElement envelope, string name )
	{
		var p = Payload( envelope );
		return p.TryGetProperty( name, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	}

	private static float? Z( JsonElement envelope )
	{
		var p = Payload( envelope );
		if ( !p.TryGetProperty( "position", out var pos ) ) return null;
		if ( !pos.TryGetProperty( "z", out var z ) ) return null;
		return z.GetSingle();
	}

	private static bool NearZ( JsonElement envelope, float expected )
	{
		var z = Z( envelope );
		return z.HasValue && MathF.Abs( z.Value - expected ) < 2f;
	}

	/// <summary>Emit one PASS/FAIL log line for a gate and bump the failure count.</summary>
	private static void Gate( ref int failures, string testName, string gate, bool ok, string passMsg, string failMsg )
	{
		if ( ok )
		{
			Log.Info( $"[PASS] {testName}: ASSERT {gate} — {passMsg}" );
		}
		else
		{
			failures++;
			Log.Info( $"[FAIL] {testName}: ASSERT {gate} — {failMsg}" );
		}
	}
}
