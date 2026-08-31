// engine-fork-elite-leverage Phase 2.4 — test ConCmd for feed.assets.subscribe
//
// [HANDOFF-TRUST: Phase 2.4 wrapper, no live hypothesis inherited from any handoff applies — verbatim parallel of FeedSceneSubscribeTest with the AssetSystem.RaiseOnAssetCompileFailed deterministic-raise pattern substituted for the four Scene raise helpers]
//
// Durable regression gate for the Phase 2.4 wire-format consumer side of the
// AssetSystemHandler reflection subscription to
// AssetSystem.OnAssetCompileFailed (bound at Phase 2.4 / 1.6). Proves the
// consumer/producer split is symmetric with EmitAsyncJob + EmitCompile +
// EmitScene: AssetSystemHandler owns the engine seam; FeedHandler.EmitAssets
// fans out to feed.assets subscribers.
//
// Under fork engine: confirms feed.assets is in ActiveChannels, subscribes via
// FeedHandler.Handle("subscribe"), invokes the internal
// AssetSystem.RaiseOnAssetCompileFailed helper (same deterministic raise-invoke
// pattern as FeedSceneSubscribeTest), polls via FeedHandler.Handle("poll"),
// and asserts the asset_compile_failed event arrived with the expected
// minimal wire-format payload shape (kind / asset_path / asset_guid /
// asset_type / error_message / exception_type / exception_message / captured).
//
// Under stable engine: the producer-side reflection probe
// (AssetSystem.OnAssetCompileFailed) returns null in
// AssetSystemHandler.EnsureBound, so even though feed.assets is registered as
// active, no events flow. Detected via the SAME reflection probe used by the
// AssetSystemHandler itself.
//
// VERIFIED via current source read 2026-05-19:
//   * Engine fork commit (this session): AssetSystem.OnError.cs adds public
//     static event Action<Asset, string, Exception> OnAssetCompileFailed +
//     internal static void RaiseOnAssetCompileFailed(Asset, string, Exception).
//   * FeedHandler.cs:65 — ChannelAssets = "assets" constant (Phase 2.4).
//   * FeedHandler.cs:~99-107 — ChannelAssets now in ActiveChannels HashSet.
//   * FeedHandler.cs:~832 — EmitAssets(string, object) public static helper.
//   * AssetSystemHandler.cs:OnEngineAssetCompileFailed — calls
//     FeedHandler.EmitAssets("asset_compile_failed", anon-payload) AFTER
//     AppendEvent.

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class FeedAssetsSubscribeTest
{
	/// <summary>
	/// Tier-3 regression test for the Phase 2.4 wrapper.
	///   PRECONDITION — Reflection-probe AssetSystem.OnAssetCompileFailed
	///                  exists (fork-only; under stable engine the test [SKIP]s).
	///                  feed.channels reports assets.available==true.
	///                  RaiseOnAssetCompileFailed internal helper is reachable.
	///   EXEC         — feed.subscribe channel=assets; invoke
	///                  RaiseOnAssetCompileFailed via reflection with
	///                  (null, "synthetic test error", null) payload; give 50ms
	///                  grace; feed.poll channel=assets.
	///   ASSERT       — Subscribe returns active-channel shape (NOT stub shape).
	///                  Poll returns >= 1 event with event_type
	///                  asset_compile_failed. The event's payload carries the
	///                  minimal wire-format shape (kind, asset_path, asset_guid,
	///                  asset_type, error_message, exception_type,
	///                  exception_message, captured) and error_message matches
	///                  the synthetic test string verbatim.
	///   CLEANUP      — feed.unsubscribe channel=assets (regardless of pass/fail).
	/// </summary>
	[ConCmd( "sbep.test_feed_assets_subscribe" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_feed_assets_subscribe";
		const string Channel = "assets";
		const string SyntheticError = "synthetic test error — sbep.test_feed_assets_subscribe";

		// ── PRECONDITION ────────────────────────────────────────────────
		await GameTask.MainThread();

		Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );

		// Producer-side reflection probe: under stable Facepunch engine
		// AssetSystem.OnAssetCompileFailed doesn't exist, so even though
		// feed.assets is in ActiveChannels, no events would flow. SKIP rather
		// than report a misleading FAIL.
		var assetSystemType = typeof( AssetSystem );
		var evOnAssetCompileFailed = assetSystemType.GetEvent( "OnAssetCompileFailed", BindingFlags.Public | BindingFlags.Static );
		if ( evOnAssetCompileFailed == null )
		{
			Log.Info( $"[SKIP] {TestName}: fork-only AssetSystem.OnAssetCompileFailed event not found — running under stable Facepunch engine. This test is fork-only by design (the producer-side reflection seam is absent; feed.assets remains in ActiveChannels but the channel never emits)." );
			return;
		}

		// Channel-presence probe: feed.channels must report assets.available==true.
		// FeedHandler.Handle returns an MCP envelope {content:[{type:"text",text:"<inner-json>"}]};
		// unwrap it via ParseMcpEnvelope (defined below) to reach the actual response payload.
		var channelsResp = FeedHandler.Handle( "channels", default );
		using var channelsDoc = ParseMcpEnvelope( channelsResp );
		bool assetsAvailable = false;
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
					assetsAvailable = true;
					break;
				}
			}
		}
		if ( !assetsAvailable )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — feed.channels did not report assets.available=true (Phase 2.4 ActiveChannels promotion failed to land)" );
			return;
		}

		// Internal raise helper — invoked directly to bypass real asset compile
		// pipeline. AssetSystemHandler defensively handles null asset with
		// "(null asset)" placeholder; the synthetic error string is what we
		// validate the payload echoed back.
		var raiseOnAssetCompileFailed = assetSystemType.GetMethod( "RaiseOnAssetCompileFailed", BindingFlags.NonPublic | BindingFlags.Static );
		if ( raiseOnAssetCompileFailed == null )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — raise helper RaiseOnAssetCompileFailed not found — engine patch incomplete" );
			return;
		}

		// AssetSystemHandler binding probe — ensure the producer-side
		// subscription successfully attached. If false here, the engine event
		// is present but our handler failed to bind (regression on
		// AssetSystemHandler.EnsureBound).
		if ( !AssetSystemHandler.AssetCompileFailedBound )
		{
			Log.Info( $"[FAIL] {TestName}: PRECONDITION — AssetSystemHandler.AssetCompileFailedBound==false; engine event present but producer-side reflection-bind failed" );
			return;
		}

		Log.Info( $"[PASS] {TestName}: PRECONDITION — OnAssetCompileFailed event present + feed.assets active + RaiseOnAssetCompileFailed reflectable + AssetSystemHandler bound" );

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
			// Raise helper iterates GetInvocationList synchronously. By the time
			// Invoke returns, OnEngineAssetCompileFailed has already executed
			// AppendEvent + FeedHandler.EmitAssets. The library handler
			// defensively handles null Asset with "(null asset)" placeholder
			// string — same pattern as SceneMutationEventsTest.
			try
			{
				raiseOnAssetCompileFailed.Invoke( null, new object[] { null, SyntheticError, null } );
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

			Log.Info( $"[PASS] {TestName}: EXEC — raise helper invoked, poll returned response (initial seq baseline={initialSeq})" );

			// ── ASSERT ──────────────────────────────────────────────────
			if ( !pollDoc.RootElement.TryGetProperty( "events", out var eventsArr )
				|| eventsArr.ValueKind != JsonValueKind.Array )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — poll response missing events[] array: {JsonSerializer.Serialize( pollResp )}" );
				return;
			}

			int failedCount = 0;
			JsonElement firstFailedPayload = default;
			foreach ( var ev in eventsArr.EnumerateArray() )
			{
				if ( !ev.TryGetProperty( "type", out var typeProp ) ) continue;
				var t = typeProp.GetString();
				if ( string.Equals( t, "asset_compile_failed", StringComparison.Ordinal ) )
				{
					failedCount++;
					if ( failedCount == 1 && ev.TryGetProperty( "payload", out var p ) )
						firstFailedPayload = p.Clone();
				}
			}

			if ( failedCount < 1 )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — no asset_compile_failed event in poll (got {failedCount})" );
				return;
			}

			// Validate payload shape on the first asset_compile_failed event.
			// JsonNamingPolicy.CamelCase only lowercases the first char — so
			// snake_case C# property names stay snake_case in the JSON output.
			// (See feedback_jsonnamingpolicy_camelcase_does_not_convert_snake_case.)
			if ( firstFailedPayload.ValueKind != JsonValueKind.Object )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — first asset_compile_failed payload is not an object" );
				return;
			}
			string[] requiredKeys = new[]
			{
				"kind", "asset_path", "asset_guid", "asset_type",
				"error_message", "exception_type", "exception_message", "captured"
			};
			foreach ( var key in requiredKeys )
			{
				if ( !firstFailedPayload.TryGetProperty( key, out _ ) )
				{
					Log.Info( $"[FAIL] {TestName}: ASSERT — asset_compile_failed payload missing required key '{key}'" );
					return;
				}
			}

			// Verify the synthetic error string round-tripped through the producer
			// → ring buffer → EmitAssets → wire format → poll path. This proves
			// the payload value path (not just the key schema).
			var errorMsg = firstFailedPayload.GetProperty( "error_message" ).GetString();
			if ( !string.Equals( errorMsg, SyntheticError, StringComparison.Ordinal ) )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — error_message round-trip mismatch; expected '{SyntheticError}', got '{errorMsg}'" );
				return;
			}

			Log.Info( $"[PASS] {TestName}: ASSERT — asset_compile_failed event observed (count={failedCount}) + minimal wire-format payload keys present (kind/asset_path/asset_guid/asset_type/error_message/exception_type/exception_message/captured) + error_message round-trip exact; Phase 2.4 wrapper verified end-to-end (AssetSystemHandler reflection-bind → OnEngineAssetCompileFailed → FeedHandler.EmitAssets → ring buffer → feed.poll)" );
		}
		finally
		{
			// ── CLEANUP ─────────────────────────────────────────────────
			try
			{
				var unsubArgs = JsonDocument.Parse( $"{{\"channel\":\"{Channel}\"}}" ).RootElement;
				FeedHandler.Handle( "unsubscribe", unsubArgs );
				Log.Info( $"[PASS] {TestName}: CLEANUP — unsubscribed from feed.assets" );
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
