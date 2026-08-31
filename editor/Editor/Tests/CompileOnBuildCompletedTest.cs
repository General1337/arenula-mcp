// engine-fork-elite-leverage Phase 1.1 — test ConCmd for CompileGroup.OnBuildCompleted
//
// Durable regression gate for the AP-100 cure. Probes the fork-only event
// via REFLECTION on Sandbox.CompileGroup (same pattern as the production
// CompileHandler subscription) so this file compiles identically against
// both fork and stable Facepunch engines.
//
// Under fork engine: subscribes via reflection, triggers a compile via
// EditorUtility.Projects.Compile (same path CompileHandler.Trigger uses),
// asserts the event fired with a non-empty CompilerOutput payload.
//
// Under stable engine: the event doesn't exist; the test emits one [SKIP]
// log line and returns. Stable runs do not falsely report failure.
//
// VERIFIED via engine source read 2026-05-18 (codex/hotload-full-fallback,
// engine/Sandbox.Compiling/CompileGroup.cs commit 27e634ae):
//   * OnBuildCompleted is declared `public static event Action<CompilerOutput[]>`
//     at CompileGroup.cs:46.
//   * Raised once per BuildAsync completion at CompileGroup.cs:400 (after
//     BuildResult publication, before per-instance OnCompileFinished).
//   * Per-subscriber try/catch in RaiseOnBuildCompleted shields the chain.
// Engine source: M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//                Sandbox.Compiling/CompileGroup.cs

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class CompileOnBuildCompletedTest
{
    /// <summary>
    /// Tier-3 regression test for the AP-100 cure.
    ///   PRECONDITION — an active, non-transient, code-bearing project loaded.
    ///                  Also: reflection-probe the fork-only event exists.
    ///                  Under stable engine the test [SKIP]s here.
    ///   EXEC         — subscribe (via reflection), trigger Compile, give 500ms
    ///                  for the event to fire (engine raises it inside BuildAsync
    ///                  before the awaiter resumes — should already be fired).
    ///   ASSERT       — eventFires in [1, 2] (multi-compiler tolerance),
    ///                  capturedOutputs non-null + non-empty.
    ///   CLEANUP      — unsubscribes regardless of pass/fail.
    /// </summary>
    [ConCmd( "sbep.test_compile_on_build_completed" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_compile_on_build_completed";
        const string EventName = "OnBuildCompleted";

        // ── PRECONDITION ────────────────────────────────────────────────
        await GameTask.MainThread();

        // Reflection probe for the fork-only event.
        var ev = typeof( CompileGroup ).GetEvent(
            EventName,
            BindingFlags.Public | BindingFlags.Static );
        if ( ev == null )
        {
            Log.Info( $"[SKIP] {TestName}: fork-only event CompileGroup.OnBuildCompleted not found — running under stable Facepunch engine. This test is fork-only by design." );
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

        Log.Info( $"[PASS] {TestName}: PRECONDITION — fork event present + compilable project '{compilable.Config?.Title ?? "?"}' loaded" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Subscribe via reflection. Atomic increment so any concurrent fire
        // (e.g. the editor's own file-watcher kicks a parallel build) can't
        // drop our count.
        int eventFires = 0;
        CompilerOutput[] capturedOutputs = null;

        Action<CompilerOutput[]> handler = outputs =>
        {
            Interlocked.Increment( ref eventFires );
            capturedOutputs = outputs;
        };

        // Build a delegate of the right type for the event signature. This
        // mirrors what CompileHandler.EnsureBound does in production — we
        // can't use `+=` directly without a literal `Action<CompilerOutput[]>`
        // reference (which we can write here since the type IS public), but
        // routing through reflection keeps the test's path identical to the
        // production subscription so a regression in either is caught.
        Delegate boundHandler = Delegate.CreateDelegate(
            ev.EventHandlerType,
            handler.Target,
            handler.Method );

        ev.AddEventHandler( null, boundHandler );
        try
        {
            await EditorUtility.Projects.Compile( compilable, null );

            // Engine raises the event synchronously inside BuildAsync's try block
            // BEFORE the awaiter resumes. By the time Compile returns it should
            // already be fired, but allow a small grace window for the rare case
            // where the build completion is dispatched onto a different context.
            await Task.Delay( 500 );

            Log.Info( $"[PASS] {TestName}: EXEC — Compile returned, event handler invoked {eventFires}× during window" );

            // ── ASSERT ──────────────────────────────────────────────────
            if ( eventFires == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — event did NOT fire after Compile (expected ≥1)" );
                return;
            }
            if ( eventFires > 2 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — event fired {eventFires}× (expected 1-2; >2 indicates duplicate subscription or runaway hotload)" );
                return;
            }
            if ( capturedOutputs == null )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — captured outputs is null" );
                return;
            }
            if ( capturedOutputs.Length == 0 )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — captured outputs array is empty" );
                return;
            }

            var anyOutputHasCompiler = capturedOutputs.Any( o => o?.Compiler != null );
            if ( !anyOutputHasCompiler )
            {
                Log.Info( $"[FAIL] {TestName}: ASSERT — captured outputs missing Compiler back-reference (event fired with malformed payload)" );
                return;
            }

            Log.Info( $"[PASS] {TestName}: ASSERT — event fired {eventFires}× with {capturedOutputs.Length} CompilerOutput(s); AP-100 cure verified end-to-end" );
        }
        finally
        {
            // ── CLEANUP ─────────────────────────────────────────────────
            try { ev.RemoveEventHandler( null, boundHandler ); } catch { }
            Log.Info( $"[PASS] {TestName}: CLEANUP — handler unsubscribed" );
        }
    }
}
