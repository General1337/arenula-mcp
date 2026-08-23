// Editor/Tests/FuzzHandlerTest.cs
//
// [HANDOFF-TRUST: 5-gate regression test for the greenfield engine-fork-elite-
//  leverage Phase 6.4 `fuzz` MCP tool. No hypothesis-truth dependency on any
//  inherited handoff; TypeLibraryOnRegisteredTest.cs is the library-test shape
//  reference only.]
//
// engine-fork-elite-leverage Phase 6.4 — durable regression gate for FuzzHandler.
//
// `sbep.test_fuzz` exercises the in-engine fuzz executor end-to-end against
// three purpose-built fixture components (FuzzSelfCheckClean / Crash / Reject)
// so the crash-vs-clean-reject classification, scratch isolation, and component
// enumeration are all proven without depending on any real game component's
// robustness (which would make this test flaky as the game evolves).
//
// Library file (fork side) — it may freely reference FuzzHandler. It uses only
// stable engine API, so it runs identically on stable Facepunch S&Box. It is
// NOT under Code/SBEP/**, so the publish-compat gate does not apply; it stays
// fork-only-symbol-free by construction regardless.
//
// Verdict note: like its sibling sbep.test_typelibrary_on_registered, this
// library ConCmd records no SBEP.Tests.TestVerdictRegistry verdict — the
// library editor assembly cannot reference the game ident without inducing the
// type-ambiguity documented in memory feedback_library_assemblyrefs_to_game_
// ident_creates_type_ambiguity. It logs gate-by-gate PASS/FAIL plus one
// machine-readable summary line, exactly as the existing library tests do.
//
// [TEST-PARAMS-INTENTIONAL: no parameter overrides — fixtures are dedicated
//  self-check components, not production components; no production default is
//  dialled.]

using System;
using System.Linq;
using Arenula;
using Sandbox;

namespace Arenula.Tests;

// ── Self-check fixture components ───────────────────────────────────────────
// The FuzzSelfCheckClean / FuzzSelfCheckCrash / FuzzSelfCheckReject fixtures
// live GAME-SIDE in Code/SBEP/Tests/FuzzSelfCheckFixtures.cs. FuzzHandler
// resolves a component by string name through the live game TypeLibrary, which
// indexes only game-assembly types — Component subclasses declared inside this
// editor library assembly are invisible to it (Phase 3 verification proved this
// empirically). The fixtures are referenced here ONLY by string name, so this
// file keeps zero compile-time dependency on them; the "FuzzSelfCheck" prefix
// makes FuzzHandler.IsSelfCheckFixture exclude them from `fuzz.enumerate`.

/// <summary>
/// <c>sbep.test_fuzz</c> — durable regression gate for <see cref="FuzzHandler"/>.
/// Five gates: enumeration is non-empty and excludes the fixtures; a clean
/// component produces zero crashes; a crash-class throw is classified as a
/// crash; an ArgumentException throw is classified as a clean reject; the
/// scratch fixture is destroyed after every run.
///
/// <para>Inputs from user: 1 — type <c>sbep.test_fuzz</c> in the F1 console.</para>
/// </summary>
public static class FuzzHandlerTest
{
    private const string TestName = "sbep.test_fuzz";
    private const string TestAgentId = "fuzz-selftest";

    private static int _gate;
    private static int _pass;
    private static int _fail;

    [ConCmd( "sbep.test_fuzz" )]
    public static void Run()
    {
        _gate = 0;
        _pass = 0;
        _fail = 0;

        Log.Info( $"=== {TestName} ===" );

        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
        {
            Log.Error( $"{TestName}: FAIL (1 of 1) — PRECONDITION: no active scene to host the fuzz fixture." );
            return;
        }

        try
        {
            GateEnumerate();
            GateCleanComponent( scene );
            GateCrashCaught( scene );
            GateCleanReject( scene );
            GateScratchIsolation( scene );
        }
        finally
        {
            // Cleanup — release the self-check scratch container so re-runs are
            // idempotent. Best-effort; never fails the test.
            try { ScratchScenePool.ReleaseContainer( scene, TestAgentId ); }
            catch ( Exception ex ) { Log.Warning( $"{TestName}: cleanup ReleaseContainer threw {ex.GetType().Name}: {ex.Message}" ); }
        }

        int total = _pass + _fail;
        if ( _fail == 0 )
            Log.Info( $"{TestName}: PASS ({total} gates)" );
        else
            Log.Error( $"{TestName}: FAIL ({_fail} of {total})" );
    }

    // ── Gate 1 — enumeration returns a sane population, fixtures excluded ────
    private static void GateEnumerate()
    {
        int g = ++_gate;
        try
        {
            var names = FuzzHandler.EnumerateComponentNames();
            bool fixtureLeaked = names.Any( FuzzHandler.IsSelfCheckFixture );
            if ( names.Count < 20 )
                Fail( g, "enumerate_nonempty", $"only {names.Count} fuzzable components enumerated (floor 20)" );
            else if ( fixtureLeaked )
                Fail( g, "enumerate_nonempty", "a FuzzSelfCheck* fixture leaked into the enumerate list" );
            else
                Pass( g, "enumerate_nonempty", $"{names.Count} fuzzable components enumerated; self-check fixtures excluded" );
        }
        catch ( Exception ex )
        {
            Fail( g, "enumerate_nonempty", $"threw {ex.GetType().Name}: {ex.Message}" );
        }
    }

    // ── Gate 2 — a clean component fuzzes with zero crashes ─────────────────
    private static void GateCleanComponent( Scene scene )
    {
        int g = ++_gate;
        try
        {
            var r = FuzzHandler.FuzzComponentCore( scene, TestAgentId, "FuzzSelfCheckClean" );
            if ( !r.Ok )
                Fail( g, "clean_component_passes", $"executor error: {r.Error}" );
            else if ( r.PropertiesFuzzed < 1 )
                Fail( g, "clean_component_passes", "no [Property] fuzzed — CleanValue not found on the fixture" );
            else if ( r.CrashCount != 0 )
                Fail( g, "clean_component_passes", $"clean component produced {r.CrashCount} crash(es) — classifier false-positive" );
            else
                Pass( g, "clean_component_passes", $"{r.PropertiesFuzzed} property fuzzed, 0 crashes" );
        }
        catch ( Exception ex )
        {
            Fail( g, "clean_component_passes", $"threw {ex.GetType().Name}: {ex.Message}" );
        }
    }

    // ── Gate 3 — a crash-class throw is classified as a crash ───────────────
    private static void GateCrashCaught( Scene scene )
    {
        int g = ++_gate;
        try
        {
            var r = FuzzHandler.FuzzComponentCore( scene, TestAgentId, "FuzzSelfCheckCrash" );
            if ( !r.Ok )
            {
                Fail( g, "crash_is_caught", $"executor error: {r.Error}" );
                return;
            }
            if ( r.CrashCount < 1 )
            {
                Fail( g, "crash_is_caught", "deliberate crash throw was NOT classified as a crash" );
                return;
            }
            var crash = r.Crashes[0];
            if ( crash.ExceptionType != "NullReferenceException" )
                Fail( g, "crash_is_caught", $"crash recorded but exception_type='{crash.ExceptionType}' (expected NullReferenceException)" );
            else if ( string.IsNullOrEmpty( crash.StackTop ) )
                Fail( g, "crash_is_caught", "crash recorded but stack_top is empty" );
            else
                Pass( g, "crash_is_caught", $"{r.CrashCount} crash(es); first = {crash.Property} -> {crash.ExceptionType}" );
        }
        catch ( Exception ex )
        {
            Fail( g, "crash_is_caught", $"threw {ex.GetType().Name}: {ex.Message}" );
        }
    }

    // ── Gate 4 — an ArgumentException throw is a clean reject, not a crash ───
    private static void GateCleanReject( Scene scene )
    {
        int g = ++_gate;
        try
        {
            var r = FuzzHandler.FuzzComponentCore( scene, TestAgentId, "FuzzSelfCheckReject" );
            if ( !r.Ok )
                Fail( g, "clean_reject_not_a_crash", $"executor error: {r.Error}" );
            else if ( r.PropertiesFuzzed < 1 )
                Fail( g, "clean_reject_not_a_crash", "no [Property] fuzzed — RejectValue not found on the fixture" );
            else if ( r.CrashCount != 0 )
                Fail( g, "clean_reject_not_a_crash", $"ArgumentException reject mis-classified as {r.CrashCount} crash(es)" );
            else if ( !r.Gates.Any( ga => ga.Detail != null && ga.Detail.Contains( "rejected" ) ) )
                Fail( g, "clean_reject_not_a_crash", "no gate detail shows a 'rejected' outcome — setter may not have thrown" );
            else
                Pass( g, "clean_reject_not_a_crash", $"{r.PropertiesFuzzed} property fuzzed; ArgumentException classified as clean reject, 0 crashes" );
        }
        catch ( Exception ex )
        {
            Fail( g, "clean_reject_not_a_crash", $"threw {ex.GetType().Name}: {ex.Message}" );
        }
    }

    // ── Gate 5 — the fuzz fixture is destroyed after every run ──────────────
    private static void GateScratchIsolation( Scene scene )
    {
        int g = ++_gate;
        try
        {
            var container = ScratchScenePool.GetContainer( scene, TestAgentId );
            if ( container == null )
            {
                // No container at all also satisfies isolation (nothing leaked).
                Pass( g, "scratch_isolation", "no residual scratch container — every fixture cleaned up" );
                return;
            }
            int children = container.Children.Count;
            if ( children != 0 )
                Fail( g, "scratch_isolation", $"{children} fixture GameObject(s) leaked under the scratch container" );
            else
                Pass( g, "scratch_isolation", "scratch container is empty — all fuzz fixtures destroyed" );
        }
        catch ( Exception ex )
        {
            Fail( g, "scratch_isolation", $"threw {ex.GetType().Name}: {ex.Message}" );
        }
    }

    // ── Logging helpers — class-scope private static, never local functions ─
    private static void Pass( int g, string name, string detail )
    {
        Log.Info( $"  [PASS] gate{g} {name} — {detail}" );
        _pass++;
    }

    private static void Fail( int g, string name, string detail )
    {
        Log.Error( $"  [FAIL] gate{g} {name} — {detail}" );
        _fail++;
    }
}
