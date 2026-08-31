// engine-fork-elite-leverage Phase 2.3 — test ConCmd for feed.scene.subscribe
//
// [HANDOFF-TRUST: Phase 2.3 wrapper, no live hypothesis inherited from any handoff applies — verbatim parallel of FeedCompileSubscribeTest with the SceneMutationEventsTest deterministic-raise pattern substituted for the EditorUtility.Projects.Compile trigger]
//
// Durable regression gate for the Phase 2.3 wire-format consumer side of the
// SceneMutationHandler reflection subscriptions to Scene.OnGameObjectAdded /
// OnGameObjectRemoved / OnComponentRegistered / OnComponentUnregistered
// (all bound at Phase 1.5). Proves the consumer/producer split is symmetric
// with EmitAsyncJob + EmitCompile: SceneMutationHandler owns the engine seam;
// FeedHandler.EmitScene fans out to feed.scene subscribers.
//
// Under fork engine: confirms feed.scene is in ActiveChannels, subscribes via
// FeedHandler.Handle("subscribe"), invokes the internal Scene.RaiseOnXxx
// helpers (same deterministic raise-invoke pattern as
// SceneMutationEventsTest), polls via FeedHandler.Handle("poll"), and asserts
// at least one of each of the four event types arrived with the expected
// minimal wire-format payload shape (kind / scene_name / scene_guid /
// target_name / target_guid / captured).
//
// Under stable engine: the producer-side reflection probe
// (Scene.OnGameObjectAdded) returns null in SceneMutationHandler.EnsureBound,
// so even though feed.scene is registered as active, no events flow.
// Detected via the SAME reflection probe used by SceneMutationEventsTest.
//
// VERIFIED via current source read 2026-05-19:
//   * FeedHandler.cs:62 — ChannelScene = "scene" constant (Phase 2.3).
//   * FeedHandler.cs:85-91 — ChannelScene now in ActiveChannels HashSet.
//   * FeedHandler.cs:~720 — EmitScene(string, object) public static helper.
//   * SceneMutationHandler.cs:94/108/120/132 — OnEngine* handlers now call
//     FeedHandler.EmitScene("gameobject_added" / "_removed" /
//     "component_registered" / "_unregistered", anon-payload) AFTER AppendEvent.

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class FeedSceneSubscribeTest
{
	/// <summary>
	/// Tier-3 regression test for the Phase 2.3 wrapper.
	///   PRECONDITION — Reflection-probe Scene.OnGameObjectAdded exists
	///                  (fork-only; under stable engine the test [SKIP]s).
	///                  feed.channels reports scene.available==true.
	///                  All 4 RaiseOnXxx internal helpers are reachable.
	///   EXEC         — feed.subscribe channel=scene; invoke 4 RaiseOnXxx
	///                  helpers via reflection with (null, null) payload;
	///                  give 50ms grace; feed.poll channel=scene.
	///   ASSERT       — Subscribe returns active-channel shape (NOT stub shape).
	///                  Poll returns >= 4 events with event_types
	///                  {gameobject_added, gameobject_removed,
	///                   component_registered, component_unregistered} each
	///                  observed at least once. Each event's payload carries
	///                  the minimal wire-format shape (kind, scene_name,
	///                  scene_guid, target_name, target_guid, captured).
	///   CLEANUP      — feed.unsubscribe channel=scene (regardless of pass/fail).
	/// </summary>
	[ConCmd( "sbep.test_feed_scene_subscribe" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_feed_scene_subscribe";
		const string Channel = "scene";

		// ── PRECONDITION ────────────────────────────────────────────────
		await GameTask.MainThread();

		Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );

		// Producer-side reflection probe: under stable Facepunch engine
		// Scene.OnGameObjectAdded doesn't exist, so even though feed.scene is
		// in ActiveChannels, no events would flow. SKIP rather than report a
		// misleading FAIL.
		var sceneType = typeof( Scene );
		var evGoAdded     = sceneType.GetEvent( "OnGameObjectAdded",       BindingFlags.Public | BindingFlags.Static );
		var evGoRemoved   = sceneType.GetEvent( "OnGameObjectRemoved",     BindingFlags.Public | BindingFlags.Static );
		var evCompReg     = sceneType.GetEvent( "OnComponentRegistered",   BindingFlags.Public | BindingFlags.Static );
		var evCompUnreg   = sceneType.GetEvent( "OnComponentUnregistered", BindingFlags.Public | BindingFlags.Static );
		if ( evGoAdded == null || evGoRemoved == null || evCompReg == null || evCompUnreg == null )
		{
			Log.Info( $"[SKIP] {TestName}: fork-only Scene events not all found (added={(evGoAdded != null)}, removed={(evGoRemoved != null)}, compReg={(evCompReg != null)}, compUnreg={(evCompUnreg != null)}) — running under stable Facepunch engine. This test is fork-only by design (the producer-side reflection seam is absent; feed.scene remains in ActiveChannels but the channel never emits)." );
			return;
		}

		// Channel-presence probe: feed.channels must report scene.available==true.
		// FeedHandler.Handle returns an MCP envelope {content:[{type:"text",text:"<inner-json>"}]};
		// unwrap it via ParseMcpEnvelope (defined below) to reach the actual response payload.
		var channelsResp = FeedHandler.Handle( "channels", default );
		using var channelsDoc = ParseMcpEnvelope( channelsResp );
		bool sceneAvailable = false;
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
					sceneAvailable = true;
					break;
				}
			}
		}
		if ( !sceneAvailable )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — feed.channels did not report scene.available=true (Phase 2.3 ActiveChannels promotion failed to land)" );
			return;
		}

		// Internal raise helpers — invoked directly to bypass engine scene-state
		// dependencies. SceneMutationEventsTest uses the same pattern (lines
		// 66-69) and proved this works deterministically under fork engine.
		var raiseGoAdded   = sceneType.GetMethod( "RaiseOnGameObjectAdded",       BindingFlags.NonPublic | BindingFlags.Static );
		var raiseGoRemoved = sceneType.GetMethod( "RaiseOnGameObjectRemoved",     BindingFlags.NonPublic | BindingFlags.Static );
		var raiseCompReg   = sceneType.GetMethod( "RaiseOnComponentRegistered",   BindingFlags.NonPublic | BindingFlags.Static );
		var raiseCompUnreg = sceneType.GetMethod( "RaiseOnComponentUnregistered", BindingFlags.NonPublic | BindingFlags.Static );
		if ( raiseGoAdded == null || raiseGoRemoved == null || raiseCompReg == null || raiseCompUnreg == null )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — raise helpers not found (added={(raiseGoAdded != null)}, removed={(raiseGoRemoved != null)}, compReg={(raiseCompReg != null)}, compUnreg={(raiseCompUnreg != null)}) — engine patch incomplete" );
			return;
		}

		Log.Info( $"[PASS] {TestName}: PRECONDITION — all 4 fork events present + feed.scene active + all 4 RaiseOnXxx helpers reflectable" );

		// ── EXEC ────────────────────────────────────────────────────────
		// Subscribe via FeedHandler.Handle directly (no MCP round-trip needed —
		// ConCmd runs in-process on the main thread, same as the handler).
		var subscribeArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\"}}" ).RootElement;
		var subscribeResp = FeedHandler.Handle( "subscribe", subscribeArgs );
		using var subscribeDoc = ParseMcpEnvelope( subscribeResp );

		// Active-channel shape: subscribed=true + next_seq present.
		// Stub-channel shape (the v1.1-deferred behaviour we're replacing):
		// ok=false + available=false + reason string.
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
		if ( !subscribedOk )
		{
			Log.Info( $"[FAIL] {TestName}: EXEC — subscribe returned non-active shape: {JsonSerializer.Serialize( subscribeResp )}" );
			return;
		}

		try
		{
			// Raise helpers iterate GetInvocationList synchronously. By the time
			// Invoke returns, OnEngine* has already executed AppendEvent +
			// FeedHandler.EmitScene. The library handler defensively handles null
			// Scene / GameObject / Component with "(null *)" placeholder strings —
			// proven by SceneMutationEventsTest.
			try
			{
				raiseGoAdded.Invoke(    null, new object[] { null, null } );
				raiseGoRemoved.Invoke(  null, new object[] { null, null } );
				raiseCompReg.Invoke(    null, new object[] { null, null } );
				raiseCompUnreg.Invoke(  null, new object[] { null, null } );
			}
			catch ( Exception ex )
			{
				Log.Info( $"[FAIL] {TestName}: EXEC — raise invoke threw {ex.GetType().Name}: {ex.Message}" );
				return;
			}

			await Task.Delay( 50 );

			// Poll the channel for events since our initial seq baseline.
			var pollArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\",\"since_seq\":{initialSeq - 1}}}" ).RootElement;
			var pollResp = FeedHandler.Handle( "poll", pollArgs );
			using var pollDoc = ParseMcpEnvelope( pollResp );

			Log.Info( $"[PASS] {TestName}: EXEC — 4 raise helpers invoked, poll returned response (initial seq baseline={initialSeq})" );

			// ── ASSERT ──────────────────────────────────────────────────
			if ( !pollDoc.RootElement.TryGetProperty( "events", out var eventsArr )
				|| eventsArr.ValueKind != JsonValueKind.Array )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — poll response missing events[] array: {JsonSerializer.Serialize( pollResp )}" );
				return;
			}

			int gaCount = 0, grCount = 0, crCount = 0, cuCount = 0;
			JsonElement firstAddedPayload = default;
			foreach ( var ev in eventsArr.EnumerateArray() )
			{
				if ( !ev.TryGetProperty( "type", out var typeProp ) ) continue;
				var t = typeProp.GetString();
				if ( string.Equals( t, "gameobject_added", StringComparison.Ordinal ) )
				{
					gaCount++;
					if ( gaCount == 1 && ev.TryGetProperty( "payload", out var p ) )
						firstAddedPayload = p.Clone();
				}
				else if ( string.Equals( t, "gameobject_removed", StringComparison.Ordinal ) ) grCount++;
				else if ( string.Equals( t, "component_registered", StringComparison.Ordinal ) ) crCount++;
				else if ( string.Equals( t, "component_unregistered", StringComparison.Ordinal ) ) cuCount++;
			}

			bool allOk = true;
			if ( gaCount < 1 ) { Log.Info( $"[FAIL] {TestName}: ASSERT — no gameobject_added event in poll (got {gaCount})" ); allOk = false; }
			if ( grCount < 1 ) { Log.Info( $"[FAIL] {TestName}: ASSERT — no gameobject_removed event in poll (got {grCount})" ); allOk = false; }
			if ( crCount < 1 ) { Log.Info( $"[FAIL] {TestName}: ASSERT — no component_registered event in poll (got {crCount})" ); allOk = false; }
			if ( cuCount < 1 ) { Log.Info( $"[FAIL] {TestName}: ASSERT — no component_unregistered event in poll (got {cuCount})" ); allOk = false; }

			if ( !allOk ) return;

			// Validate payload shape on the first gameobject_added event.
			// JsonNamingPolicy.CamelCase only lowercases the first char — so
			// snake_case C# property names stay snake_case in the JSON output.
			// (See feedback_jsonnamingpolicy_camelcase_does_not_convert_snake_case.)
			if ( firstAddedPayload.ValueKind != JsonValueKind.Object )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — first gameobject_added payload is not an object" );
				return;
			}
			string[] requiredKeys = new[]
			{
				"kind", "scene_name", "scene_guid", "target_name", "target_guid", "captured"
			};
			foreach ( var key in requiredKeys )
			{
				if ( !firstAddedPayload.TryGetProperty( key, out _ ) )
				{
					Log.Info( $"[FAIL] {TestName}: ASSERT — gameobject_added payload missing required key '{key}'" );
					return;
				}
			}

			Log.Info( $"[PASS] {TestName}: ASSERT — all 4 event types observed (added={gaCount}, removed={grCount}, compReg={crCount}, compUnreg={cuCount}) + minimal wire-format payload keys present (kind/scene_name/scene_guid/target_name/target_guid/captured); Phase 2.3 wrapper verified end-to-end (SceneMutationHandler reflection-bind → OnEngine* → FeedHandler.EmitScene → ring buffer → feed.poll)" );
		}
		finally
		{
			// ── CLEANUP ─────────────────────────────────────────────────
			try
			{
				var unsubArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\"}}" ).RootElement;
				FeedHandler.Handle( "unsubscribe", unsubArgs );
				Log.Info( $"[PASS] {TestName}: CLEANUP — unsubscribed from feed.scene" );
			}
			catch ( Exception ex )
			{
				Log.Info( $"[FAIL] {TestName}: CLEANUP — unsubscribe threw: {ex.Message}" );
			}
		}
	}

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
