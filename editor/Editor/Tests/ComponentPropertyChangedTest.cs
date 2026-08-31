// [HANDOFF-TRUST: 4-gate regression test for Phase 1.5a Component.OnPropertyChanged event; deterministic raise-invoke pattern (matches 1.4/1.5 test design); no inherited-handoff hypothesis dependency]
//
// [PERF-OK: regression test, not perf-optimization work. The perf-gate matched
//  the phrase "high-frequency" in the rationale below; this file adds a
//  sbep.test_* ConCmd and changes no production code path.]
//
// engine-fork-elite-leverage Phase 1.5a — durable regression gate for the
// fork-only Component property-mutation event:
//
//   Sandbox.Component.OnPropertyChanged(Component)
//
// Probes the event via REFLECTION so this file compiles identically against
// both fork and stable engines. Under stable Facepunch engine the reflection
// probe returns null and the test emits a single [SKIP] log line (no PASS/FAIL
// gates fire — PRECONDITION returns early on the null path, matching the
// established 1.1/1.2/1.3/1.4/1.5 resilience-seam pattern).
//
// Under fork engine: invokes the internal Component.RaiseOnPropertyChanged
// helper directly (1.4/1.5 pattern — deterministic, independent of the engine's
// CallbackBatch dirty/validate timing) THREE times and asserts the library
// handler's fire counter advanced by exactly 3 and the rolling buffer captured
// all 3 with a sane payload. Three raises (not one) exercises the high-
// frequency counter the Phase 1.5a brief called out as the reason this event
// was split off from the four structural Phase 1.5 events.

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class ComponentPropertyChangedTest
{
    [ConCmd( "sbep.test_component_property_changed" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_component_property_changed";
        const int RaiseCount = 3;

        // ── PRECONDITION ────────────────────────────────────────────────
        await GameTask.MainThread();

        var componentType = typeof( Component );
        var evPropertyChanged = componentType.GetEvent( "OnPropertyChanged", BindingFlags.Public | BindingFlags.Static );

        if ( evPropertyChanged == null )
        {
            Log.Info( $"[SKIP] {TestName}: fork-only event Component.OnPropertyChanged not found — running under stable Facepunch engine. This test is fork-only by design." );
            return;
        }

        // Internal raise helper — invoked directly to bypass the engine's
        // CallbackBatch dirty/validate timing and prove the engine→library
        // wiring end-to-end regardless of scene state.
        var raisePropertyChanged = componentType.GetMethod( "RaiseOnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Static );

        if ( raisePropertyChanged == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — raise helper RaiseOnPropertyChanged not found — engine patch incomplete" );
            return;
        }

        if ( !ComponentPropertyChangedHandler.GetBindingState() )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — ComponentPropertyChangedHandler binding-state shows the subscription did not bind. Library handler EnsureBound did not reach the event." );
            return;
        }

        Log.Info( $"[PASS] {TestName}: PRECONDITION — fork event present + Raise helper reflectable + library handler bound" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Snapshot fire count BEFORE invoking raise. Handler increments
        // atomically; if the handler isn't bound the count won't advance and
        // ASSERT will catch it.
        int fireBefore = ComponentPropertyChangedHandler._propertyChangedFireCount;

        // For deterministic raise-invoke we can pass null safely — the engine
        // raise helper iterates GetInvocationList and passes the payload
        // through; the library handler null-defends the Component with
        // "(null *)" placeholder strings. We're proving the engine→library
        // WIRING, not engine-side component state.
        try
        {
            for ( int i = 0; i < RaiseCount; i++ )
                raisePropertyChanged.Invoke( null, new object[] { null } );
        }
        catch ( Exception ex )
        {
            Log.Info( $"[FAIL] {TestName}: EXEC — raise invoke threw {ex.GetType().Name}: {ex.Message}" );
            return;
        }

        // Raise helper iterates GetInvocationList synchronously. By the time
        // Invoke returns, our handler has already run.
        await Task.Delay( 50 );

        int fireAfter = ComponentPropertyChangedHandler._propertyChangedFireCount;
        int fireDelta = fireAfter - fireBefore;

        Log.Info( $"[PASS] {TestName}: EXEC — raise helper invoked {RaiseCount}×. Fire delta: {fireDelta}" );

        // ── ASSERT ──────────────────────────────────────────────────────
        bool allOk = true;

        if ( fireDelta != RaiseCount )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — OnPropertyChanged handler fire count delta mismatch (expected {RaiseCount}, got {fireDelta})" );
            allOk = false;
        }

        var recent = ComponentPropertyChangedHandler.SnapshotRecent();
        var lastN = recent.Skip( Math.Max( 0, recent.Length - RaiseCount ) ).ToArray();

        if ( lastN.Length < RaiseCount )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — rolling buffer captured only {lastN.Length}/{RaiseCount} events (expected ≥{RaiseCount} most recent)" );
            allOk = false;
        }
        else
        {
            // The deterministic raise passed null, so the handler recorded the
            // "(null component)" placeholder — proves the payload reached the
            // handler's record-construction path intact.
            for ( int i = 0; i < RaiseCount; i++ )
            {
                if ( lastN[i].ComponentType != "(null component)" )
                {
                    Log.Info( $"[FAIL] {TestName}: ASSERT — rolling buffer slot {i} ComponentType mismatch (expected '(null component)', got '{lastN[i].ComponentType}')" );
                    allOk = false;
                }
            }
        }

        if ( !allOk ) return;

        Log.Info( $"[PASS] {TestName}: ASSERT — event fired {RaiseCount}× + rolling buffer captured all {RaiseCount}; high-frequency Component property-mutation observability verified end-to-end (engine RaiseOnPropertyChanged → reflection-bound subscriber → atomic counter + rolling buffer)" );

        // ── CLEANUP ─────────────────────────────────────────────────────
        // No per-test fixture to tear down — the reflection-bound subscription
        // is the production subscription (we don't unsubscribe per-test; that
        // would break post-test MCP queries against the rolling buffer).
        // Idempotent re-run: each invocation snapshots before/after deltas, so
        // residual buffer state from prior runs doesn't pollute the assertions.
        Log.Info( $"[PASS] {TestName}: CLEANUP — no per-test fixture (subscription is production-shared); idempotent re-run safe" );
    }
}
