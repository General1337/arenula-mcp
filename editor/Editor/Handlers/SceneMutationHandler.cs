// [HANDOFF-TRUST: feature-build library wiring for Phase 1.5 engine events; no hypothesis from inherited handoffs intersects engine-event-stream surface]
//
// engine-fork-elite-leverage Phase 1.5 — library bridge for the four new fork
// events shipped in engine partial Scene.OnMutation.cs:
//
//   1. Sandbox.Scene.OnGameObjectAdded(Scene, GameObject)
//   2. Sandbox.Scene.OnGameObjectRemoved(Scene, GameObject)
//   3. Sandbox.Scene.OnComponentRegistered(Scene, Component)
//   4. Sandbox.Scene.OnComponentUnregistered(Scene, Component)
//
// All four producer + payload types are library-visible (Scene, GameObject,
// Component are all public). Uses 1.1-style direct typed binding
// (typeof + Delegate.CreateDelegate) for all four — no Expression.Lambda
// bridge needed (unlike 1.4b which had to bridge internal Sandbox.Command).
//
// All subscriptions are reflection-probed independently so the library binary
// stays byte-identical between fork and stable engines (resilience seam —
// under stable all four reflection probes return null and silently no-op,
// matching the 1.1/1.2/1.3/1.4 graceful-degradation pattern).
//
// Cures the snapshot.take + snapshot.diff polling workaround (the MCP plugin
// currently polls scene state to detect mutations; this replaces polling with
// passive subscription). Unblocks downstream Phase 2.3 (feed.scene.subscribe)
// and Phase 4.5 (symbolic pre-execution of mutations) which both gate on
// Phase 1.5 per charter.
//
// Banned approaches enforced here (carried from 1.1/1.2/1.3/1.4):
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
/// Subscribes to four fork-only Scene mutation events for GameObject + Component
/// register/unregister observability. Caches the most recent N events as a
/// rolling buffer (read by MCP query handlers without polling the scene).
/// All bindings degrade silently to null on stable Facepunch engine.
/// </summary>
internal static class SceneMutationHandler
{
    // ── Atomic fire counters (read by MCP handlers + test gates) ──

    internal static int _gameObjectAddedFireCount;
    internal static int _gameObjectRemovedFireCount;
    internal static int _componentRegisteredFireCount;
    internal static int _componentUnregisteredFireCount;

    // ── Most-recent mutation cache (capped rolling buffer; lock-free dirty-read OK) ──

    internal sealed record MutationEvent
    (
        string Kind,           // "GameObjectAdded" / "GameObjectRemoved" / "ComponentRegistered" / "ComponentUnregistered"
        string SceneName,
        string SceneGuid,
        string TargetName,     // GameObject.Name or Component type name
        string TargetGuid,
        DateTimeOffset Captured
    );

    private const int RollingCap = 32;
    private static readonly object _bufferLock = new();
    private static readonly LinkedList<MutationEvent> _recentEvents = new();

    internal static MutationEvent[] SnapshotRecent()
    {
        lock ( _bufferLock )
        {
            return _recentEvents.ToArray();
        }
    }

    private static void AppendEvent( MutationEvent ev )
    {
        lock ( _bufferLock )
        {
            _recentEvents.AddLast( ev );
            while ( _recentEvents.Count > RollingCap )
                _recentEvents.RemoveFirst();
        }
    }

    // ── Event handler entrypoints (engine calls into these via direct delegates) ──

    // [HANDOFF-TRUST: Phase 2.3 wrapper, additive EmitScene forwarders into existing handlers; no live hypothesis from any inherited handoff applies — AppendEvent stays the rolling-buffer source of truth, EmitScene is a parallel non-blocking fanout to feed.scene subscribers]
    private static void OnEngineGameObjectAdded( Scene scene, GameObject go )
    {
        System.Threading.Interlocked.Increment( ref _gameObjectAddedFireCount );
        AppendEvent( new MutationEvent(
            Kind: "GameObjectAdded",
            SceneName: scene?.Name ?? "(null scene)",
            SceneGuid: scene?.Id.ToString() ?? "",
            TargetName: go?.Name ?? "(null go)",
            TargetGuid: go?.Id.ToString() ?? "",
            Captured: DateTimeOffset.UtcNow ) );

        FeedHandler.EmitScene( "gameobject_added", new
        {
            kind = "GameObjectAdded",
            scene_name = scene?.Name ?? "(null scene)",
            scene_guid = scene?.Id.ToString() ?? "",
            target_name = go?.Name ?? "(null go)",
            target_guid = go?.Id.ToString() ?? "",
            captured = DateTimeOffset.UtcNow.ToString( "o" ),
        } );
    }

    private static void OnEngineGameObjectRemoved( Scene scene, GameObject go )
    {
        System.Threading.Interlocked.Increment( ref _gameObjectRemovedFireCount );
        AppendEvent( new MutationEvent(
            Kind: "GameObjectRemoved",
            SceneName: scene?.Name ?? "(null scene)",
            SceneGuid: scene?.Id.ToString() ?? "",
            TargetName: go?.Name ?? "(null go)",
            TargetGuid: go?.Id.ToString() ?? "",
            Captured: DateTimeOffset.UtcNow ) );

        FeedHandler.EmitScene( "gameobject_removed", new
        {
            kind = "GameObjectRemoved",
            scene_name = scene?.Name ?? "(null scene)",
            scene_guid = scene?.Id.ToString() ?? "",
            target_name = go?.Name ?? "(null go)",
            target_guid = go?.Id.ToString() ?? "",
            captured = DateTimeOffset.UtcNow.ToString( "o" ),
        } );
    }

    private static void OnEngineComponentRegistered( Scene scene, Component component )
    {
        System.Threading.Interlocked.Increment( ref _componentRegisteredFireCount );
        AppendEvent( new MutationEvent(
            Kind: "ComponentRegistered",
            SceneName: scene?.Name ?? "(null scene)",
            SceneGuid: scene?.Id.ToString() ?? "",
            TargetName: component?.GetType().Name ?? "(null component)",
            TargetGuid: component?.Id.ToString() ?? "",
            Captured: DateTimeOffset.UtcNow ) );

        FeedHandler.EmitScene( "component_registered", new
        {
            kind = "ComponentRegistered",
            scene_name = scene?.Name ?? "(null scene)",
            scene_guid = scene?.Id.ToString() ?? "",
            target_name = component?.GetType().Name ?? "(null component)",
            target_guid = component?.Id.ToString() ?? "",
            captured = DateTimeOffset.UtcNow.ToString( "o" ),
        } );
    }

    private static void OnEngineComponentUnregistered( Scene scene, Component component )
    {
        System.Threading.Interlocked.Increment( ref _componentUnregisteredFireCount );
        AppendEvent( new MutationEvent(
            Kind: "ComponentUnregistered",
            SceneName: scene?.Name ?? "(null scene)",
            SceneGuid: scene?.Id.ToString() ?? "",
            TargetName: component?.GetType().Name ?? "(null component)",
            TargetGuid: component?.Id.ToString() ?? "",
            Captured: DateTimeOffset.UtcNow ) );

        FeedHandler.EmitScene( "component_unregistered", new
        {
            kind = "ComponentUnregistered",
            scene_name = scene?.Name ?? "(null scene)",
            scene_guid = scene?.Id.ToString() ?? "",
            target_name = component?.GetType().Name ?? "(null component)",
            target_guid = component?.Id.ToString() ?? "",
            captured = DateTimeOffset.UtcNow.ToString( "o" ),
        } );
    }

    // ── Reflection-bind state (one slot per event) ──

    private static EventInfo _evGameObjectAdded;
    private static Delegate _hGameObjectAdded;
    private static EventInfo _evGameObjectRemoved;
    private static Delegate _hGameObjectRemoved;
    private static EventInfo _evComponentRegistered;
    private static Delegate _hComponentRegistered;
    private static EventInfo _evComponentUnregistered;
    private static Delegate _hComponentUnregistered;

    /// <summary>
    /// Diagnostic: report which fork-only events were successfully bound.
    /// Used by sbep.test_scene_mutation_events PRECONDITION gate.
    /// </summary>
    internal static (bool gameObjectAdded, bool gameObjectRemoved, bool componentRegistered, bool componentUnregistered) GetBindingState()
    {
        return (
            _evGameObjectAdded is not null,
            _evGameObjectRemoved is not null,
            _evComponentRegistered is not null,
            _evComponentUnregistered is not null );
    }

    // ── Static ctor + hotload re-bind ──

    [EditorEvent.Hotload]
    public static void RebindForkEngineEventsOnHotload()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"SceneMutationHandler.RebindForkEngineEventsOnHotload failed: {ex.Message}" );
        }
    }

    static SceneMutationHandler()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"SceneMutationHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" );
        }
    }

    private static void EnsureBound()
    {
        BindForkEngineEvent_Typed(
            type: typeof( Scene ),
            eventName: "OnGameObjectAdded",
            handlerMethodName: nameof( OnEngineGameObjectAdded ),
            existingHandler: ref _hGameObjectAdded,
            eventInfoSink: ref _evGameObjectAdded );

        BindForkEngineEvent_Typed(
            type: typeof( Scene ),
            eventName: "OnGameObjectRemoved",
            handlerMethodName: nameof( OnEngineGameObjectRemoved ),
            existingHandler: ref _hGameObjectRemoved,
            eventInfoSink: ref _evGameObjectRemoved );

        BindForkEngineEvent_Typed(
            type: typeof( Scene ),
            eventName: "OnComponentRegistered",
            handlerMethodName: nameof( OnEngineComponentRegistered ),
            existingHandler: ref _hComponentRegistered,
            eventInfoSink: ref _evComponentRegistered );

        BindForkEngineEvent_Typed(
            type: typeof( Scene ),
            eventName: "OnComponentUnregistered",
            handlerMethodName: nameof( OnEngineComponentUnregistered ),
            existingHandler: ref _hComponentUnregistered,
            eventInfoSink: ref _evComponentUnregistered );
    }

    /// <summary>
    /// 1.1-style direct typed binding — used when producer type AND payload
    /// types are all library-visible. Mirror of the helper in TypeLibraryHandler
    /// (banned-approach #6: whitelist-only staging — duplicated narrowly to
    /// avoid touching that file this session).
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
            // Stable engine (or pre-1.5 fork) — event not present. Silent no-op
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

        var handlerMethod = typeof( SceneMutationHandler ).GetMethod(
            handlerMethodName,
            BindingFlags.NonPublic | BindingFlags.Static );
        if ( handlerMethod == null )
        {
            Log.Warning( $"SceneMutationHandler.BindForkEngineEvent_Typed: {handlerMethodName} not found via reflection — fork-engine event {eventName} will not bind." );
            return;
        }

        existingHandler = Delegate.CreateDelegate( ev.EventHandlerType, handlerMethod );
        ev.AddEventHandler( null, existingHandler );
        eventInfoSink = ev;
    }
}
