// [HANDOFF-TRUST: feature-build library wiring for Phase 2.4 / 1.6 engine event; no hypothesis from inherited handoffs intersects asset-error-event-stream surface]
//
// engine-fork-elite-leverage Phase 2.4 / 1.6 — library bridge for the new
// fork event shipped in engine partial AssetSystem.OnError.cs:
//
//   1. Editor.AssetSystem.OnAssetCompileFailed(Asset, string, Exception)
//
// Producer + payload types (Asset, string, Exception) are all library-visible
// (Asset / AssetSystem live in Sandbox.Tools, which is in arenula_mcp's default
// reference set — see Handlers/AssetQueryHandler.cs:300 et al for direct
// AssetSystem.All / AssetSystem.FindByPath usage). Uses 1.1-style direct
// typed binding (typeof + Delegate.CreateDelegate) — no Expression.Lambda
// bridge needed (unlike 1.3 HotloadResultBridge which had to bridge
// not-in-scope HotloadResult).
//
// Subscription is reflection-probed so the library binary stays byte-identical
// between fork and stable engines (resilience seam — under stable the
// reflection probe returns null and silently no-ops, matching the
// 1.1/1.2/1.3/1.4/1.5 graceful-degradation pattern).
//
// Cures the editor.get_log filter:"compile" log-scrape workaround (the MCP
// plugin currently scrapes warn-level log entries to surface asset errors;
// this collapses it into passive subscription). Closes Phase 2.4 wrapper
// (charter row 2.4 — feed.assets.subscribe) and the Phase 2 exit criterion
// "four hand-rolled log-scrape paths replaced".
//
// Banned approaches enforced here (carried from 1.1/1.2/1.3/1.4/1.5/2.1/2.3):
//   * No msbuild Directory.Build.targets/.props injection of FORK_ENGINE.
//   * No FORK_ENGINE in arenula_mcp.sbproj Metadata.Compiler.DefineConstants.
//   * No "softsplit.sandbox" or "package.softsplit.sandbox" in AssemblyReferences.
//   * No editing the auto-generated arenula_mcp.editor.csproj.
//   * No direct C# type references to fork-only event NAMES — reflection only.
//   * Keep event payloads small (Phase 2.1 + 2.3 lesson: payload cap is 16384;
//     CursorStore.StoreAndPage breaks JSON parse at page boundary).

using System;
using System.Collections.Generic;
using System.Reflection;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Subscribes to the fork-only AssetSystem.OnAssetCompileFailed event for
/// asset-pipeline error observability. Caches the most recent N events as a
/// rolling buffer (read by MCP query handlers without polling the log) AND
/// forwards each event to FeedHandler.EmitAssets for live feed.assets
/// subscribers. All bindings degrade silently to null on stable Facepunch
/// engine. Phase 2.4 / 1.6 (charter engine-fork-elite-leverage).
/// </summary>
internal static class AssetSystemHandler
{
	// ── Atomic fire counter (read by MCP handlers + test gates) ──

	internal static int _assetCompileFailedFireCount;

	// ── Most-recent error cache (capped rolling buffer; lock-free dirty-read OK) ──

	internal sealed record AssetErrorEvent
	(
		string Kind,            // "AssetCompileFailed"
		string AssetPath,
		string AssetGuid,
		string AssetType,       // friendly type name from Asset.AssetType.FriendlyName, or "" if null
		string ErrorMessage,
		string ExceptionType,   // exception.GetType().Name, or "" if no inner exception
		string ExceptionMessage,// exception.Message, or "" if no inner exception
		DateTimeOffset Captured
	);

	private const int RollingCap = 32;
	private static readonly object _bufferLock = new();
	private static readonly LinkedList<AssetErrorEvent> _recentEvents = new();

	internal static AssetErrorEvent[] SnapshotRecent()
	{
		lock ( _bufferLock )
		{
			var arr = new AssetErrorEvent[_recentEvents.Count];
			int i = 0;
			foreach ( var e in _recentEvents ) arr[i++] = e;
			return arr;
		}
	}

	private static void AppendEvent( AssetErrorEvent ev )
	{
		lock ( _bufferLock )
		{
			_recentEvents.AddLast( ev );
			while ( _recentEvents.Count > RollingCap )
				_recentEvents.RemoveFirst();
		}
	}

	// ── Event handler entrypoint (engine calls into this via direct delegate) ──

	private static void OnEngineAssetCompileFailed( Asset asset, string errorMessage, Exception exception )
	{
		System.Threading.Interlocked.Increment( ref _assetCompileFailedFireCount );

		var assetPath = asset?.Path ?? "(null asset)";
		var assetGuid = ""; // Asset doesn't expose a stable Guid like GameObject.Id; AssetId is an int index, omit for now (engine source: Editor/Asset.cs has no public Guid surface as of 2026-05-19)
		var assetTypeName = asset?.AssetType?.FriendlyName ?? "";
		var exType = exception?.GetType().Name ?? "";
		var exMsg = exception?.Message ?? "";

		AppendEvent( new AssetErrorEvent(
			Kind: "AssetCompileFailed",
			AssetPath: assetPath,
			AssetGuid: assetGuid,
			AssetType: assetTypeName,
			ErrorMessage: errorMessage ?? "",
			ExceptionType: exType,
			ExceptionMessage: exMsg,
			Captured: DateTimeOffset.UtcNow ) );

		FeedHandler.EmitAssets( "asset_compile_failed", new
		{
			kind = "AssetCompileFailed",
			asset_path = assetPath,
			asset_guid = assetGuid,
			asset_type = assetTypeName,
			error_message = errorMessage ?? "",
			exception_type = exType,
			exception_message = exMsg,
			captured = DateTimeOffset.UtcNow.ToString( "o" ),
		} );
	}

	// ── Reflection-bind state ──

	private static EventInfo _evAssetCompileFailed;
	private static Delegate _hAssetCompileFailed;

	// [HANDOFF-TRUST: fix invalid single-element tuple syntax — additive feature-build cleanup, independent of any inherited handoff hypothesis]
	/// <summary>
	/// Diagnostic: true when the fork-only OnAssetCompileFailed event was
	/// successfully bound at static-ctor or hotload time. False under stable
	/// Facepunch engine (expected SKIP path) OR under fork engine before the
	/// Phase 2.4 patch lands. Used by sbep.test_feed_assets_subscribe
	/// PRECONDITION gate.
	/// </summary>
	internal static bool AssetCompileFailedBound => _evAssetCompileFailed is not null;

	// ── Static ctor + hotload re-bind ──

	[EditorEvent.Hotload]
	public static void RebindForkEngineEventsOnHotload()
	{
		try { EnsureBound(); }
		catch ( Exception ex )
		{
			Log.Warning( $"AssetSystemHandler.RebindForkEngineEventsOnHotload failed: {ex.Message}" );
		}
	}

	static AssetSystemHandler()
	{
		try { EnsureBound(); }
		catch ( Exception ex )
		{
			Log.Warning( $"AssetSystemHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" );
		}
	}

	private static void EnsureBound()
	{
		BindForkEngineEvent_Typed(
			type: typeof( AssetSystem ),
			eventName: "OnAssetCompileFailed",
			handlerMethodName: nameof( OnEngineAssetCompileFailed ),
			existingHandler: ref _hAssetCompileFailed,
			eventInfoSink: ref _evAssetCompileFailed );
	}

	/// <summary>
	/// 1.1-style direct typed binding — used when producer type AND payload
	/// types are all library-visible. Mirror of the helper in
	/// SceneMutationHandler (Phase 1.5) / TypeLibraryHandler (Phase 1.4) —
	/// duplicated narrowly to avoid cross-handler refactors this session.
	/// </summary>
	private static void BindForkEngineEvent_Typed(
		Type type,
		string eventName,
		string handlerMethodName,
		ref Delegate existingHandler,
		ref EventInfo eventInfoSink )
	{
		var ev = type.GetEvent(
			eventName,
			BindingFlags.Public | BindingFlags.Static );

		if ( ev == null )
		{
			// Stable engine (or pre-2.4 fork) — event not present. Silent no-op
			// is the resilience-seam invariant.
			eventInfoSink = null;
			existingHandler = null;
			return;
		}

		try
		{
			if ( existingHandler != null )
				ev.RemoveEventHandler( null, existingHandler );
		}
		catch { /* defensive — stale delegate post-hotload */ }

		var handlerMethod = typeof( AssetSystemHandler ).GetMethod(
			handlerMethodName,
			BindingFlags.NonPublic | BindingFlags.Static );
		if ( handlerMethod == null )
		{
			Log.Warning( $"AssetSystemHandler.BindForkEngineEvent_Typed: {handlerMethodName} not found via reflection — fork-engine event {eventName} will not bind." );
			return;
		}

		existingHandler = Delegate.CreateDelegate( ev.EventHandlerType, handlerMethod );
		ev.AddEventHandler( null, existingHandler );
		eventInfoSink = ev;
	}
}
