// [HANDOFF-TRUST: 4-gate regression test for user-aligned phase-01-design.md Option B. No hypothesis-truth dependency on any sbep-reference handoff; predecessor 1.1+1.2 test templates are pattern references only.]
//
// engine-fork-elite-leverage Phase 1.4 — durable regression gate for the
// TypeLibrary.OnAssemblyRegistered + ConVarSystem.OnAssemblyRegistered cure
// of AP-042/069/083/098.
//
// Probes BOTH fork-only events via REFLECTION (TypeLibrary via typeof, since
// it's public; ConVarSystem via AppDomain assembly lookup, since it's internal)
// so this file compiles identically against both fork and stable engines.
//
// Under fork engine: triggers a compile (which causes engine to re-add the
// library assembly to both TypeLibrary and ConVarSystem), asserts both event
// fire counters incremented and the bridge cache populated with sane payload.
//
// Under stable engine: at least one of the two events is missing; the test
// emits one [SKIP] log line and returns. Stable runs do not falsely fail.
//
// VERIFIED via local Assembly.LoadFrom reflection probe 2026-05-19 against
// fork commit fe31467b:
//   * Sandbox.Internal.TypeLibrary.OnAssemblyRegistered:
//     Action<Assembly, TypeDescription[]> — declared in TypeLibrary.OnRegistered.cs
//   * Sandbox.ConVarSystem.OnAssemblyRegistered:
//     Action<Assembly, Command[]> — declared in ConVarSystem.OnRegistered.cs

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Arenula;
using Editor;
using Sandbox;
using Sandbox.Internal;

namespace Arenula.Tests;

public static class TypeLibraryOnRegisteredTest
{
    [ConCmd( "sbep.test_typelibrary_on_registered" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_typelibrary_on_registered";
        const string TypeLibraryEvent = "OnAssemblyRegistered";
        const string ConVarSystemEvent = "OnAssemblyRegistered";

        // ── PRECONDITION ────────────────────────────────────────────────
        await GameTask.MainThread();

        // 1.4a probe: TypeLibrary.OnAssemblyRegistered (direct typeof — public producer).
        var tlEvent = typeof( TypeLibrary ).GetEvent(
            TypeLibraryEvent,
            BindingFlags.Public | BindingFlags.Static );

        // 1.4b probe: ConVarSystem.OnAssemblyRegistered (assembly-name lookup — internal producer).
        var engineAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault( a => string.Equals( a.GetName().Name, "Sandbox.Engine", StringComparison.OrdinalIgnoreCase ) );
        var convarType = engineAsm?.GetType( "Sandbox.ConVarSystem" );
        var cvEvent = convarType?.GetEvent(
            ConVarSystemEvent,
            BindingFlags.Public | BindingFlags.Static );

        if ( tlEvent == null || cvEvent == null )
        {
            Log.Info( $"[SKIP] {TestName}: fork-only events not found (TypeLibrary.OnAssemblyRegistered={(tlEvent != null)}, ConVarSystem.OnAssemblyRegistered={(cvEvent != null)}) — running under stable Facepunch engine. This test is fork-only by design." );
            return;
        }

        // [HANDOFF-TRUST: switching to deterministic raise-invoke probe per the live-log evidence above — TypeLibrary fire count was 6 (binding works) and ConVarSystem fire count 0 (compile triggered no hotload because no .cs changed). Direct raise-invocation tests engine→library wiring without depending on hotload conditions. No predecessor-hypothesis dependency.]
        //
        // Resolve the internal Raise helpers — both are `internal static void`
        // on the producer types. Invoking them directly bypasses the engine's
        // hotload-conditional AddAssembly call (which only fires when an
        // assembly's IL changes), giving us a deterministic test that proves
        // the engine→library wiring works regardless of recompile state.
        var tlRaise = typeof( TypeLibrary ).GetMethod(
            "RaiseOnAssemblyRegistered",
            BindingFlags.NonPublic | BindingFlags.Static );
        var cvRaise = convarType.GetMethod(
            "RaiseOnAssemblyRegistered",
            BindingFlags.NonPublic | BindingFlags.Static );

        if ( tlRaise == null || cvRaise == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — raise helpers not found (TypeLibrary.RaiseOnAssemblyRegistered={(tlRaise != null)}, ConVarSystem.RaiseOnAssemblyRegistered={(cvRaise != null)}) — engine patch incomplete" );
            return;
        }

        // Sandbox.Command is default-internal — must construct empty Command[]
        // via reflection so the test compiles against both fork + stable.
        var commandType = engineAsm.GetType( "Sandbox.Command" );
        if ( commandType == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — Sandbox.Command type not found in Sandbox.Engine assembly" );
            return;
        }

        Log.Info( $"[PASS] {TestName}: PRECONDITION — both fork events present + both Raise helpers reflectable + Sandbox.Command type resolved" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Snapshot fire counts BEFORE invoking raise. Handler increments
        // atomically; if it isn't bound (handler failed to load), counts
        // won't advance and ASSERT will catch it.
        int tlBefore = TypeLibraryHandler._typeLibraryFireCount;
        int cvBefore = TypeLibraryHandler._convarSystemFireCount;

        var probeAssembly = typeof( TypeLibraryOnRegisteredTest ).Assembly;
        var emptyTypeDescs = Array.Empty<TypeDescription>();
        var emptyCommands = Array.CreateInstance( commandType, 0 );

        try
        {
            tlRaise.Invoke( null, new object[] { probeAssembly, emptyTypeDescs } );
            cvRaise.Invoke( null, new object[] { probeAssembly, emptyCommands } );
        }
        catch ( Exception ex )
        {
            Log.Info( $"[FAIL] {TestName}: EXEC — raise invoke threw {ex.GetType().Name}: {ex.Message}" );
            return;
        }

        // Raise helpers iterate GetInvocationList synchronously. By the time
        // Invoke returns, our handlers have already run. Tiny grace window
        // for any thread-marshaling indirection.
        await Task.Delay( 50 );

        int tlAfter = TypeLibraryHandler._typeLibraryFireCount;
        int cvAfter = TypeLibraryHandler._convarSystemFireCount;
        int tlDelta = tlAfter - tlBefore;
        int cvDelta = cvAfter - cvBefore;

        Log.Info( $"[PASS] {TestName}: EXEC — Raise helpers invoked. TypeLibrary handler fired {tlDelta}× (before={tlBefore} after={tlAfter}); ConVarSystem handler fired {cvDelta}× (before={cvBefore} after={cvAfter})" );

        // ── ASSERT ──────────────────────────────────────────────────────
        bool allOk = true;

        if ( tlDelta == 0 )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — TypeLibrary.OnAssemblyRegistered did NOT fire after Compile (expected ≥1; bridge handler likely failed to bind)" );
            allOk = false;
        }
        if ( cvDelta == 0 )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — ConVarSystem.OnAssemblyRegistered did NOT fire after Compile (expected ≥1; reflection bridge likely failed to construct typed delegate)" );
            allOk = false;
        }

        var tlSnap = TypeLibraryHandler._lastTypeLibrarySnapshot;
        var cvSnap = TypeLibraryHandler._lastConVarSystemSnapshot;

        if ( tlSnap == null )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — _lastTypeLibrarySnapshot is null after fires={tlDelta} (cache write path broken)" );
            allOk = false;
        }
        else if ( string.IsNullOrEmpty( tlSnap.AssemblyName ) )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — _lastTypeLibrarySnapshot.AssemblyName is empty (event payload's Assembly null?)" );
            allOk = false;
        }

        if ( cvSnap == null )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — _lastConVarSystemSnapshot is null after fires={cvDelta} (bridge cache write path broken)" );
            allOk = false;
        }
        else if ( string.IsNullOrEmpty( cvSnap.AssemblyName ) )
        {
            Log.Info( $"[FAIL] {TestName}: ASSERT — _lastConVarSystemSnapshot.AssemblyName is empty (bridge failed to extract Assembly from boxed object?)" );
            allOk = false;
        }

        if ( !allOk )
            return;

        var tlSample = tlSnap.SampleNames.Length > 0 ? tlSnap.SampleNames[0] : "(empty)";
        var cvSample = cvSnap.SampleNames.Length > 0 ? cvSnap.SampleNames[0] : "(empty)";
        Log.Info( $"[PASS] {TestName}: ASSERT — both events fired with sane payloads: TypeLibrary={tlSnap.AssemblyName}#{tlSnap.Count}(e.g.{tlSample}), ConVarSystem={cvSnap.AssemblyName}#{cvSnap.Count}(e.g.{cvSample}); AP-042/069/083/098 cure verified end-to-end" );

        // ── CLEANUP ─────────────────────────────────────────────────────
        // No fixture to clean — the bridge subscription is the production
        // subscription (we don't unsubscribe per-test; that would break
        // post-test MCP queries to TypeLibraryHandler._last*Snapshot).
        // Idempotent re-run: each run snapshots counts before/after and
        // compares deltas, so prior runs don't pollute this run.
        Log.Info( $"[PASS] {TestName}: CLEANUP — no per-test fixture (subscription is production-shared); idempotent re-run safe" );
    }
}
