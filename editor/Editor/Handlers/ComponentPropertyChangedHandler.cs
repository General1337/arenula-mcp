// [HANDOFF-TRUST: feature-build library wiring for Phase 1.5a engine event; no hypothesis from inherited handoffs intersects engine-event-stream surface]
//
// engine-fork-elite-leverage Phase 1.5a — library bridge for the one new fork
// event shipped in engine partial Component.OnPropertyChanged.cs:
//
//   Sandbox.Component.OnPropertyChanged(Component)
//
// Producer (Component) + payload (Component) types are both library-visible
// public types, so this uses 1.1-style direct typed binding (typeof +
// Delegate.CreateDelegate) — no Expression.Lambda bridge needed.
//
// The subscription is reflection-probed so the library binary stays byte-
// identical between fork and stable engines (resilience seam — under stable the
// reflection probe returns null and the subscription silently no-ops, matching
// the 1.1/1.2/1.3/1.4/1.5 graceful-degradation pattern).
//
// This is the high-frequency companion to SceneMutationHandler.cs (Phase 1.5,
// four structural events). Component.OnPropertyChanged fires once per component
// per end-of-frame CallbackBatch (the engine coalesces property changes), so
// the per-fire work here is intentionally minimal: one atomic increment plus a
// bounded rolling-buffer append under a short lock. No scene access, no MCP
// round-trip on the fire path.
//
// Banned approaches enforced here (carried from 1.1/1.2/1.3/1.4/1.5):
//   * No msbuild Directory.Build.targets/.props injection of FORK_ENGINE.
//   * No FORK_ENGINE in sbox_mcp.sbproj Metadata.Compiler.DefineConstants.
//   * No "softsplit.sandbox" or "package.softsplit.sandbox" in AssemblyReferences.
//   * No editing the auto-generated arenula_mcp.editor.csproj.
//   * No direct C# type references to fork-only event NAMES — reflection only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Subscribes to the fork-only <c>Component.OnPropertyChanged</c> event for
/// component property-mutation observability. Caches the most recent N events as
/// a rolling buffer (read by MCP query handlers / regression tests without
/// polling component state). The binding degrades silently to null on stable
/// Facepunch engine.
/// </summary>
internal static class ComponentPropertyChangedHandler
{
    // ── Atomic fire counter (read by MCP handlers + test gates) ──

    internal static int _propertyChangedFireCount;

    // ── Most-recent property-change cache (capped rolling buffer; lock-free dirty-read OK) ──

    internal sealed record PropertyChangeEvent
    (
        string ComponentType,   // Component.GetType().Name, or "(null component)"
        string ComponentId,
        string GameObjectName,
        string SceneName,
        DateTimeOffset Captured
    );

    // Cap is larger than SceneMutationHandler's 32 because property changes are
    // intrinsically higher-frequency than structural register/unregister events.
    private const int RollingCap = 64;
    private static readonly object _bufferLock = new();
    private static readonly LinkedList<PropertyChangeEvent> _recentEvents = new();

    internal static PropertyChangeEvent[] SnapshotRecent()
    {
        lock ( _bufferLock )
        {
            return _recentEvents.ToArray();
        }
    }

    private static void AppendEvent( PropertyChangeEvent ev )
    {
        lock ( _bufferLock )
        {
            _recentEvents.AddLast( ev );
            while ( _recentEvents.Count > RollingCap )
                _recentEvents.RemoveFirst();
        }
    }

    // ── Event handler entrypoint (engine calls into this via a direct delegate) ──

    private static void OnEnginePropertyChanged( Component component )
    {
        System.Threading.Interlocked.Increment( ref _propertyChangedFireCount );

        // The engine raise helper passes the component through as-is; it may be
        // null on synthetic raise paths (the regression test invokes
        // RaiseOnPropertyChanged directly with a null component). Null-defend
        // every field — we're recording WIRING, not asserting scene state.
        AppendEvent( new PropertyChangeEvent(
            ComponentType: component?.GetType().Name ?? "(null component)",
            ComponentId: component?.Id.ToString() ?? "",
            GameObjectName: component?.GameObject?.Name ?? "(null gameobject)",
            SceneName: component?.Scene?.Name ?? "(null scene)",
            Captured: DateTimeOffset.UtcNow ) );
    }

    // ── Reflection-bind state ──

    private static EventInfo _evPropertyChanged;
    private static Delegate _hPropertyChanged;

    /// <summary>
    /// Diagnostic: report whether the fork-only event was successfully bound.
    /// Used by the sbep.test_component_property_changed PRECONDITION gate.
    /// </summary>
    internal static bool GetBindingState() => _evPropertyChanged is not null;

    // ── Static ctor + hotload re-bind ──

    // VERIFIED via /check-engine: [EditorEvent.Hotload] is the engine-standard
    // re-subscribe-after-hotload attribute (Editor assembly). Static event
    // subscriptions are dropped on hot-reload; re-binding on the Hotload event
    // is required so the subscription survives. Pattern copied verbatim from
    // SceneMutationHandler.cs (Phase 1.5, fork-runtime-PASS verified 2026-05-19)
    // and TypeLibraryHandler.cs (Phase 1.4). No new engine-internal behavior.
    [EditorEvent.Hotload]
    public static void RebindForkEngineEventOnHotload()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"ComponentPropertyChangedHandler.RebindForkEngineEventOnHotload failed: {ex.Message}" );
        }
    }

    static ComponentPropertyChangedHandler()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"ComponentPropertyChangedHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" );
        }
    }

    private static void EnsureBound()
    {
        BindForkEngineEvent_Typed(
            type: typeof( Component ),
            eventName: "OnPropertyChanged",
            handlerMethodName: nameof( OnEnginePropertyChanged ),
            existingHandler: ref _hPropertyChanged,
            eventInfoSink: ref _evPropertyChanged );
    }

    /// <summary>
    /// 1.1-style direct typed binding — used when producer type AND payload
    /// types are all library-visible. Mirror of the helper in
    /// SceneMutationHandler / TypeLibraryHandler (banned-approach #6:
    /// whitelist-only staging — duplicated narrowly to avoid touching those
    /// files this session).
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
            // Stable engine (or pre-1.5a fork) — event not present. Silent no-op
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

        var handlerMethod = typeof( ComponentPropertyChangedHandler ).GetMethod(
            handlerMethodName,
            BindingFlags.NonPublic | BindingFlags.Static );
        if ( handlerMethod == null )
        {
            Log.Warning( $"ComponentPropertyChangedHandler.BindForkEngineEvent_Typed: {handlerMethodName} not found via reflection — fork-engine event {eventName} will not bind." );
            return;
        }

        existingHandler = Delegate.CreateDelegate( ev.EventHandlerType, handlerMethod );
        ev.AddEventHandler( null, existingHandler );
        eventInfoSink = ev;
    }
}
