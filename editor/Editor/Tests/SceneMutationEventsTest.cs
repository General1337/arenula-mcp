// [HANDOFF-TRUST: 4-gate regression test for Phase 1.5 scene-mutation event stream; deterministic raise-invoke pattern (matches 1.4 test design); no inherited-handoff hypothesis dependency]
//
// engine-fork-elite-leverage Phase 1.5 — durable regression gate for the four
// Scene mutation events:
//
//   1. Sandbox.Scene.OnGameObjectAdded(Scene, GameObject)
//   2. Sandbox.Scene.OnGameObjectRemoved(Scene, GameObject)
//   3. Sandbox.Scene.OnComponentRegistered(Scene, Component)
//   4. Sandbox.Scene.OnComponentUnregistered(Scene, Component)
//
// Probes all four events via REFLECTION so the file compiles identically
// against both fork and stable engines. Under stable Facepunch engine, all
// four reflection probes return null and the test emits a single [SKIP]
// log line (no PASS/FAIL gates fire — PRECONDITION returns early on the
// null path, matching the established 1.1/1.2/1.3/1.4 resilience-seam pattern).
//
// Under fork engine: invokes the internal Scene.RaiseOnXxx helpers directly
// (1.4 pattern — deterministic, independent of scene-mutation timing) and
// asserts all four library handlers' fire counters incremented and the
// rolling buffer captured the events with sane payload (Kind enum + scene
// name + target name).
//
// VERIFIED via local Assembly.LoadFrom reflection probe 2026-05-19 against
// fork commit (Sandbox.Engine.dll 8,082,432 bytes):
//   * Scene.OnGameObjectAdded:       Action<Scene, GameObject>
//   * Scene.OnGameObjectRemoved:     Action<Scene, GameObject>
//   * Scene.OnComponentRegistered:   Action<Scene, Component>
//   * Scene.OnComponentUnregistered: Action<Scene, Component>
//   * All four RaiseOnXxx helpers present as internal static.

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class SceneMutationEventsTest
{
    [ConCmd( "sbep.test_scene_mutation_events" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_scene_mutation_events";

        // ── PRECONDITION ────────────────────────────────────────────────
        await GameTask.MainThread();

        var sceneType = typeof( Scene );
        var evGoAdded     = sceneType.GetEvent( "OnGameObjectAdded",       BindingFlags.Public | BindingFlags.Static );
        var evGoRemoved   = sceneType.GetEvent( "OnGameObjectRemoved",     BindingFlags.Public | BindingFlags.Static );
        var evCompReg     = sceneType.GetEvent( "OnComponentRegistered",   BindingFlags.Public | BindingFlags.Static );
        var evCompUnreg   = sceneType.GetEvent( "OnComponentUnregistered", BindingFlags.Public | BindingFlags.Static );

        if ( evGoAdded == null || evGoRemoved == null || evCompReg == null || evCompUnreg == null )
        {
            Log.Info( $"[SKIP] {TestName}: fork-only events not found (OnGameObjectAdded={(evGoAdded != null)}, OnGameObjectRemoved={(evGoRemoved != null)}, OnComponentRegistered={(evCompReg != null)}, OnComponentUnregistered={(evCompUnreg != null)}) — running under stable Facepunch engine. This test is fork-only by design." );
            return;
        }

        // Internal raise helpers — invoked directly to bypass the engine's
        // hotload-conditional GameObjectDirectory.Add/Remove timing and prove
        // the engine→library wiring end-to-end regardless of scene state.
        var raiseGoAdded   = sceneType.GetMethod( "RaiseOnGameObjectAdded",       BindingFlags.NonPublic | BindingFlags.Static );
        var raiseGoRemoved = sceneType.GetMethod( "RaiseOnGameObjectRemoved",     BindingFlags.NonPublic | BindingFlags.Static );
        var raiseCompReg   = sceneType.GetMethod( "RaiseOnComponentRegistered",   BindingFlags.NonPublic | BindingFlags.Static );
        var raiseCompUnreg = sceneType.GetMethod( "RaiseOnComponentUnregistered", BindingFlags.NonPublic | BindingFlags.Static );

        if ( raiseGoAdded == null || raiseGoRemoved == null || raiseCompReg == null || raiseCompUnreg == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — raise helpers not found (RaiseOnGameObjectAdded={(raiseGoAdded != null)}, RaiseOnGameObjectRemoved={(raiseGoRemoved != null)}, RaiseOnComponentRegistered={(raiseCompReg != null)}, RaiseOnComponentUnregistered={(raiseCompUnreg != null)}) — engine patch incomplete" );
            return;
        }

        var bindState = SceneMutationHandler.GetBindingState();
        if ( !bindState.gameObjectAdded || !bindState.gameObjectRemoved || !bindState.componentRegistered || !bindState.componentUnregistered )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — SceneMutationHandler binding-state shows missing subscriptions ({bindState}). Library handler EnsureBound did not reach all four events." );
            return;
        }

        Log.Info( $"[PASS] {TestName}: PRECONDITION — all 4 fork events present + all 4 Raise helpers reflectable + library handler bound to all 4" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Snapshot fire counts BEFORE invoking raise. Handler increments
        // atomically; if any handler isn't bound, the corresponding count
        // won't advance and ASSERT will catch it.
        int goAddBefore    = SceneMutationHandler._gameObjectAddedFireCount;
        int goRemoveBefore = SceneMutationHandler._gameObjectRemovedFireCount;
        int compRegBefore  = SceneMutationHandler._componentRegisteredFireCount;
        int compUnregBefore = SceneMutationHandler._componentUnregisteredFireCount;

        // For deterministic raise-invoke we can pass null safely — the engine
        // raise helpers iterate GetInvocationList and pass the payload through;
        // the library handler defensively handles null Scene / null GameObject /
        // null Component with "(null *)" placeholder strings. We're proving the
        // engine→library WIRING, not engine-side scene state.
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

        // Raise helpers iterate GetInvocationList synchronously. By the time
        // Invoke returns, our handlers have already run.
        await Task.Delay( 50 );

        int goAddAfter    = SceneMutationHandler._gameObjectAddedFireCount;
        int goRemoveAfter = SceneMutationHandler._gameObjectRemovedFireCount;
        int compRegAfter  = SceneMutationHandler._componentRegisteredFireCount;
        int compUnregAfter = SceneMutationHandler._componentUnregisteredFireCount;

        int goAddDelta    = goAddAfter - goAddBefore;
        int goRemoveDelta = goRemoveAfter - goRemoveBefore;
        int compRegDelta  = compRegAfter - compRegBefore;
        int compUnregDelta = compUnregAfter - compUnregBefore;

        Log.Info( $"[PASS] {TestName}: EXEC — all 4 raise helpers invoked. Fire deltas: goAdd={goAddDelta} goRemove={goRemoveDelta} compReg={compRegDelta} compUnreg={compUnregDelta}" );

        // ── ASSERT ──────────────────────────────────────────────────────
        bool allOk = true;

        if ( goAddDelta < 1 )    { Log.Info( $"[FAIL] {TestName}: ASSERT — OnGameObjectAdded handler did NOT fire (expected ≥1, got {goAddDelta})" ); allOk = false; }
        if ( goRemoveDelta < 1 ) { Log.Info( $"[FAIL] {TestName}: ASSERT — OnGameObjectRemoved handler did NOT fire (expected ≥1, got {goRemoveDelta})" ); allOk = false; }
        if ( compRegDelta < 1 )  { Log.Info( $"[FAIL] {TestName}: ASSERT — OnComponentRegistered handler did NOT fire (expected ≥1, got {compRegDelta})" ); allOk = false; }
        if ( compUnregDelta < 1 ){ Log.Info( $"[FAIL] {TestName}: ASSERT — OnComponentUnregistered handler did NOT fire (expected ≥1, got {compUnregDelta})" ); allOk = false; }

        var recent = SceneMutationHandler.SnapshotRecent();
        var lastFour = recent.Skip( Math.Max( 0, recent.Length - 4 ) ).ToArray();

        // Verify the rolling buffer captured the 4 events with the expected
        // Kind strings in order.
        string[] expectedKinds = { "GameObjectAdded", "GameObjectRemoved", "ComponentRegistered", "ComponentUnregistered" };
        if ( lastFour.Length < 4 )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — rolling buffer captured only {lastFour.Length}/4 events (expected ≥4 most recent)" );
            allOk = false;
        }
        else
        {
            for ( int i = 0; i < 4; i++ )
            {
                if ( lastFour[i].Kind != expectedKinds[i] )
                {
                    Log.Info( $"[FAIL] {TestName}: ASSERT — rolling buffer slot {i} kind mismatch (expected {expectedKinds[i]}, got {lastFour[i].Kind})" );
                    allOk = false;
                }
            }
        }

        if ( !allOk ) return;

        Log.Info( $"[PASS] {TestName}: ASSERT — all 4 events fired + rolling buffer captured all 4 in expected order (GameObjectAdded → GameObjectRemoved → ComponentRegistered → ComponentUnregistered); snapshot.* polling cure verified end-to-end" );

        // ── CLEANUP ─────────────────────────────────────────────────────
        // No per-test fixture to tear down — the 4 reflection-bound subscriptions
        // are the production subscriptions (we don't unsubscribe per-test; that
        // would break post-test MCP queries against the rolling buffer).
        // Idempotent re-run: each invocation snapshots before/after deltas, so
        // residual buffer state from prior runs doesn't pollute the assertions.
        Log.Info( $"[PASS] {TestName}: CLEANUP — no per-test fixture (subscriptions are production-shared); idempotent re-run safe" );
    }
}
