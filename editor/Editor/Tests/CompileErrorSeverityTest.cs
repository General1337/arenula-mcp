// engine-fork-elite-leverage Phase 1.2 — test ConCmd for CompileGroup.OnCompileFailed
//
// Durable regression gate for the AP-100 second-pass cure. Probes the fork-only
// OnCompileFailed event via REFLECTION on Sandbox.CompileGroup (same pattern as
// the production CompileHandler subscription) so this file compiles identically
// against both fork and stable Facepunch engines.
//
// Under fork engine: subscribes via reflection, drops a temporary .cs probe file
// containing a known CS0103 ("name does not exist"), triggers a compile via
// EditorUtility.Projects.Compile (same path CompileHandler.Trigger uses), asserts
// (1) OnCompileFailed fired with a non-empty failed CompilerOutput[] payload,
// (2) the payload's Diagnostics include a Severity=Error entry matching our
// probe sentinel, (3) the library's _lastOutputs cache (post-merge) carries the
// failed compiler's diagnostics, proving the engine→library wiring is intact.
//
// Under stable engine: the event doesn't exist; the test emits one [SKIP]
// log line and returns. Stable runs do not falsely report failure.
//
// VERIFIED via engine source read 2026-05-18 (codex/hotload-full-fallback,
// engine/Sandbox.Compiling/CompileGroup.cs commit e4165169):
//   * OnCompileFailed is declared `public static event Action<CompilerOutput[]>`
//     at CompileGroup.cs:103 (post-1.1, between OnBuildCompleted and static ctor).
//   * Raised once per BuildAsync completion at CompileGroup.cs:473 immediately
//     after RaiseOnBuildCompleted, before per-instance OnCompileFinished.
//   * Zero-emission early-return inside RaiseOnCompileFailed when failedOutputs
//     is null or empty (happy path produces no fire).
// Engine source: M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//                Sandbox.Compiling/CompileGroup.cs

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class CompileErrorSeverityTest
{
    /// <summary>
    /// Tier-3 regression test for the AP-100 second-pass cure.
    ///   PRECONDITION — an active, non-transient, code-bearing project loaded.
    ///                  Reflection-probe both 1.1 OnBuildCompleted (dependency)
    ///                  and 1.2 OnCompileFailed events exist. Under stable
    ///                  engine the test [SKIP]s here.
    ///   EXEC         — subscribe to OnCompileFailed (via reflection), drop
    ///                  a .cs probe file containing a known CS0103, trigger
    ///                  Compile, give 500ms grace.
    ///   ASSERT       — eventFires in [1, 3] (multi-compiler tolerance — sbox
    ///                  may rebuild dependents), capturedFailedOutputs non-null
    ///                  and non-empty, contains Severity=Error diagnostic with
    ///                  message referencing our probe sentinel, AND the
    ///                  library's _lastOutputs (post-merge) returns the same
    ///                  diagnostic via CompileHandler.GetErrors.
    ///   CLEANUP      — unsubscribes, deletes probe file, triggers one more
    ///                  Compile to restore green cache state. Idempotent on
    ///                  re-run.
    /// </summary>
    [ConCmd( "sbep.test_compile_error_severity" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_compile_error_severity";
        const string EventName = "OnCompileFailed";
        const string ProbeSentinel = "SbepTestCompileErrorSeverity_DoesNotExist_xyz782";
        const string ProbeRelPath = "Code/SBEP/Debug/_SbepTestCompileErrorSeverityProbe.cs";

        // ── PRECONDITION ────────────────────────────────────────────────
        await GameTask.MainThread();

        var ev = typeof( CompileGroup ).GetEvent(
            EventName,
            BindingFlags.Public | BindingFlags.Static );
        if ( ev == null )
        {
            Log.Info( $"[SKIP] {TestName}: fork-only event CompileGroup.OnCompileFailed not found — running under stable Facepunch engine. This test is fork-only by design." );
            return;
        }

        // 1.1 OnBuildCompleted is a dependency — the library's merge logic
        // relies on it firing first to populate _lastOutputs with successful
        // outputs before 1.2 appends failed outputs.
        var evBuildCompleted = typeof( CompileGroup ).GetEvent(
            "OnBuildCompleted",
            BindingFlags.Public | BindingFlags.Static );
        if ( evBuildCompleted == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — OnCompileFailed present but OnBuildCompleted (1.1) missing — fork engine state inconsistent" );
            return;
        }

        var projects = EditorUtility.Projects.GetAll();
        if ( projects == null || projects.Count == 0 )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — no projects loaded" );
            return;
        }

        var compilable = projects
            .FirstOrDefault( p => p.Active && !p.IsTransient && !p.IsBuiltIn && p.HasCodePath() );

        if ( compilable == null )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — no compilable project (need Active + non-transient + HasCodePath)" );
            return;
        }

        var projectRoot = compilable.RootDirectory?.FullName;
        if ( string.IsNullOrEmpty( projectRoot ) || !Directory.Exists( projectRoot ) )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — compilable project '{compilable.Config?.Title ?? "?"}' has no RootDirectory on disk" );
            return;
        }

        Log.Info( $"[PASS] {TestName}: PRECONDITION — fork events present (OnCompileFailed + OnBuildCompleted) + compilable project '{compilable.Config?.Title ?? "?"}' loaded at {projectRoot}" );

        // ── EXEC ────────────────────────────────────────────────────────
        int eventFires = 0;
        CompilerOutput[] capturedFailedOutputs = null;

        Action<CompilerOutput[]> handler = outputs =>
        {
            Interlocked.Increment( ref eventFires );
            capturedFailedOutputs = outputs;
        };

        Delegate boundHandler = Delegate.CreateDelegate(
            ev.EventHandlerType,
            handler.Target,
            handler.Method );

        ev.AddEventHandler( null, boundHandler );

        string probeFullPath = Path.Combine( projectRoot, ProbeRelPath );
        string probeContent =
$@"// Auto-generated probe file — {TestName} — DELETE ME if you see this checked in
// Induces CS0103 to trigger CompileGroup.OnCompileFailed via a real failed
// build. Cleanup runs in the test ConCmd's finally block; the file should
// never persist beyond a single test run.
namespace SBEP.Probes;

public class _SbepTestCompileErrorSeverityProbe
{{
    public void DoesNotCompile() => {ProbeSentinel}.NoSuchMethod();
}}
";

        try
        {
            Directory.CreateDirectory( Path.GetDirectoryName( probeFullPath ) );
            File.WriteAllText( probeFullPath, probeContent );

            // Trigger compile — engine raises OnCompileFailed synchronously
            // inside BuildAsync's try block BEFORE the awaiter resumes.
            // EditorUtility.Projects.Compile may throw OR return an error result
            // when the build fails (which is the expected outcome here since the
            // probe induces CS0103). Catch + record the exception locally so the
            // ASSERT phase can still validate the fired-event payload.
            string compileExceptionNote = "no exception";
            try
            {
                await EditorUtility.Projects.Compile( compilable, null );
            }
            catch ( Exception ex )
            {
                compileExceptionNote = "threw " + ex.GetType().Name + ": " + ( ex.Message ?? "" ).Replace( '\n', ' ' ).Replace( '\r', ' ' );
            }
            await Task.Delay( 500 );

            Log.Info( $"[PASS] {TestName}: EXEC — Compile returned (with probe file {ProbeRelPath}, {compileExceptionNote}), event handler invoked {eventFires}× during window" );

            // ── ASSERT ──────────────────────────────────────────────────
            if ( eventFires == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — OnCompileFailed did NOT fire (expected ≥1 since the probe introduces a real CS0103)" );
                return;
            }
            if ( eventFires > 3 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — OnCompileFailed fired {eventFires}× (expected 1-3; >3 indicates duplicate subscription or runaway recompile loop)" );
                return;
            }
            if ( capturedFailedOutputs == null || capturedFailedOutputs.Length == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — capturedFailedOutputs empty (event fired but payload was null/empty — should never happen since RaiseOnCompileFailed early-returns on empty)" );
                return;
            }

            var allDiags = capturedFailedOutputs
                .Where( o => o?.Diagnostics != null )
                .SelectMany( o => o.Diagnostics )
                .ToList();

            var errorDiags = allDiags
                .Where( d => d.Severity == DiagnosticSeverity.Error )
                .ToList();

            if ( errorDiags.Count == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — capturedFailedOutputs has {allDiags.Count} total diagnostics but ZERO with Severity=Error (the entire point of 1.2 is exposing severity-tagged errors)" );
                return;
            }

            var probeError = errorDiags.FirstOrDefault( d => d.GetMessage().Contains( ProbeSentinel ) );
            if ( probeError == null )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — captured errors do not include the probe sentinel '{ProbeSentinel}' (got {errorDiags.Count} error(s), first: '{errorDiags[0].Id}: {errorDiags[0].GetMessage()}'); did the compile not pick up the probe file?" );
                return;
            }

            // End-to-end cross-check: the library's _lastOutputs (post-merge)
            // must contain the failed compiler's diagnostics. This proves the
            // engine event → library merge → public cache path is unbroken.
            var compileHandlerType = Type.GetType( "Arenula.CompileHandler, sbox_mcp.editor", throwOnError: false )
                                  ?? Type.GetType( "Arenula.CompileHandler, sbox_mcp", throwOnError: false )
                                  ?? typeof( Arenula.CompileHandler );
            var lastOutputsField = compileHandlerType?.GetField(
                "_lastOutputs",
                BindingFlags.NonPublic | BindingFlags.Static );
            if ( lastOutputsField == null )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — could not reflect Arenula.CompileHandler._lastOutputs (library type/field rename?)" );
                return;
            }

            var libraryLastOutputs = lastOutputsField.GetValue( null ) as CompilerOutput[];
            if ( libraryLastOutputs == null || libraryLastOutputs.Length == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — library _lastOutputs is null/empty after a failed-build event (merge did not run; check OnEngineCompileFailed binding)" );
                return;
            }

            var libraryReachesProbeError = libraryLastOutputs
                .Where( o => o?.Diagnostics != null )
                .SelectMany( o => o.Diagnostics )
                .Any( d => d.Severity == DiagnosticSeverity.Error && d.GetMessage().Contains( ProbeSentinel ) );

            if ( !libraryReachesProbeError )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — library _lastOutputs has {libraryLastOutputs.Length} CompilerOutput(s) but does NOT contain the probe error (merge path broken; engine→library wiring regressed)" );
                return;
            }

            Log.Info( $"[PASS] {TestName}: ASSERT — OnCompileFailed fired {eventFires}× with {capturedFailedOutputs.Length} failed CompilerOutput(s); {errorDiags.Count} Error-severity diagnostic(s); probe error '{probeError.Id}' detected in BOTH event payload AND library _lastOutputs (engine→library wiring intact); AP-100 second-pass cure verified end-to-end" );
        }
        finally
        {
            // ── CLEANUP ─────────────────────────────────────────────────
            try { ev.RemoveEventHandler( null, boundHandler ); } catch { }

            if ( !string.IsNullOrEmpty( probeFullPath ) && File.Exists( probeFullPath ) )
            {
                try { File.Delete( probeFullPath ); } catch { }
            }

            // Restore a green cache: trigger one more clean compile so callers
            // re-running the test or running other tests after this one don't
            // inherit the failed cache state from our probe.
            try
            {
                if ( compilable != null )
                    await EditorUtility.Projects.Compile( compilable, null );
            }
            catch { /* best-effort cleanup; failures here don't invalidate test PASS */ }

            Log.Info( $"[PASS] {TestName}: CLEANUP — handler unsubscribed, probe file deleted, clean recompile triggered" );
        }
    }
}
