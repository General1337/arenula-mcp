// Editor/Tests/ScratchScenePoolTest.cs
//
// [HANDOFF-TRUST: greenfield engine-fork-elite-leverage Phase 3.3 — durable regression gate for the scratch-scene pool. No live hypothesis from any inherited handoff applies: Phase 3.3 is a clean greenfield feature (packet.md §"Prior Failed Approaches" — none). Sibling test shape: AgentObserveTest.cs.]
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission code for sbep.test_scratch_scene_pool; it does not CLAIM completion. The actual test run happens via editor.console_run, and the resulting PASS sentinel is written by the test-evidence hook on observing the SUMMARY log line.]
//
// Durable regression gate for the Phase 3.3 scratch-scene pool (ScratchScenePool +
// ScratchHandler). Library-only — no fork-only event, NO [SKIP] path: this test
// runs identically on the fork engine and on stable Facepunch, unlike the
// Phase-1.x event tests.
//
// Uses a fixed synthetic agent id ("test-agent-scratch-pool") that never belongs
// to a real agent, so a concurrent live agent's container is never touched. The
// shared __mcp_scratch_pool root is intentionally left alone unless it is empty.
//
// PRECONDITION  Scene resolvable; release any stale test-agent container so the
//               run starts from a known baseline.
// EXEC          EnsureContainer → add a child → re-EnsureContainer (idempotency)
//               → ClearContainer → ReleaseContainer → double-release.
// ASSERT        Twelve gates, one log line each: created flag, container name,
//               parenting under __mcp_scratch_pool, NotSaved + NotNetworked on
//               the container and the pool root, child reparenting, EnsureContainer
//               idempotency, Snapshot child count, ClearContainer, ReleaseContainer,
//               and release idempotency.
// CLEANUP       Confirm the test container is gone; exercise ClearPool only when
//               the pool root is empty (concurrent-safe); idempotent re-run.

using System;
using System.Linq;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class ScratchScenePoolTest
{
    /// <summary>Tier-3 regression test for the Phase 3.3 scratch-scene pool.</summary>
    [ConCmd( "sbep.test_scratch_scene_pool" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_scratch_scene_pool";
        const string TestAgentId = "test-agent-scratch-pool";
        const string TestSlug = "test";
        const string ExpectedContainerName = "agent_test-agent-scratch-pool_scratch";

        await GameTask.MainThread();
        Log.Info( $"[INFO] {TestName}: ENTER @ {DateTime.UtcNow:O}" );

        // ── PRECONDITION ────────────────────────────────────────────────
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
        {
            Log.Error( $"[FAIL] {TestName}: PRECONDITION — SceneHelpers.ResolveScene() returned null (no active editor session?)" );
            return;
        }

        // Release any stale test-agent container only — never the whole pool, so
        // concurrent agents' containers survive a re-run.
        ScratchScenePool.ReleaseContainer( scene, TestAgentId );
        if ( ScratchScenePool.GetContainer( scene, TestAgentId ) != null )
        {
            Log.Error( $"[FAIL] {TestName}: PRECONDITION — stale '{TestAgentId}' container still present after release" );
            return;
        }
        Log.Info( $"[PASS] {TestName}: PRECONDITION — scene resolved, no stale '{TestAgentId}' container" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Capture every value needed by ASSERT BEFORE the destroy steps — reading
        // a property off a DestroyImmediate'd GameObject is undefined.
        var container = ScratchScenePool.EnsureContainer( scene, TestAgentId, TestSlug, out bool created1 );
        var containerName = container.Name;
        var firstId = container.Id;
        var containerParent = container.Parent;
        var containerFlags = container.Flags;

        var poolRoot = ScratchScenePool.FindPoolRoot( scene );
        var poolRootFlags = poolRoot?.Flags ?? GameObjectFlags.None;

        var child = scene.CreateObject();
        child.Name = "scratch_test_child";
        child.SetParent( container );
        var childParentId = child.Parent?.Id;
        int childCountAfterAdd = container.Children.Count;

        var container2 = ScratchScenePool.EnsureContainer( scene, TestAgentId, TestSlug, out bool created2 );
        var secondId = container2.Id;

        var snapshot = ScratchScenePool.Snapshot( scene );
        var testEntry = snapshot.FirstOrDefault( e => e.AgentId == TestAgentId );

        int removed = ScratchScenePool.ClearContainer( scene, TestAgentId );
        var afterClear = ScratchScenePool.GetContainer( scene, TestAgentId );
        int childCountAfterClear = afterClear?.Children.Count ?? -1;

        bool releasedOk = ScratchScenePool.ReleaseContainer( scene, TestAgentId );
        var afterRelease = ScratchScenePool.GetContainer( scene, TestAgentId );
        bool doubleRelease = ScratchScenePool.ReleaseContainer( scene, TestAgentId );

        Log.Info( $"[PASS] {TestName}: EXEC — ensure → add child → re-ensure → clear → release → double-release executed" );

        // ── ASSERT ──────────────────────────────────────────────────────
        int failures = 0;

        // Gate 1: ensure reported created=true on first call.
        Gate( ref failures, TestName, "created_flag", created1,
            "EnsureContainer reported created=true on first call",
            $"expected created=true, got {created1}" );

        // Gate 2: container name.
        Gate( ref failures, TestName, "container_name", containerName == ExpectedContainerName,
            $"container name == '{ExpectedContainerName}'",
            $"expected '{ExpectedContainerName}', got '{containerName}'" );

        // Gate 3: parented under __mcp_scratch_pool.
        bool parentOk = containerParent != null && containerParent.Name == ScratchScenePool.PoolRootName;
        Gate( ref failures, TestName, "parented_under_pool", parentOk,
            $"container parented under '{ScratchScenePool.PoolRootName}'",
            $"expected parent '{ScratchScenePool.PoolRootName}', got '{containerParent?.Name ?? "(null)"}'" );

        // Gate 4: container carries NotSaved (the disk-safety invariant).
        Gate( ref failures, TestName, "container_not_saved", containerFlags.HasFlag( GameObjectFlags.NotSaved ),
            "container has GameObjectFlags.NotSaved",
            $"NotSaved missing — flags={containerFlags}" );

        // Gate 5: container carries NotNetworked.
        Gate( ref failures, TestName, "container_not_networked", containerFlags.HasFlag( GameObjectFlags.NotNetworked ),
            "container has GameObjectFlags.NotNetworked",
            $"NotNetworked missing — flags={containerFlags}" );

        // Gate 6: pool root carries NotSaved too.
        Gate( ref failures, TestName, "pool_root_not_saved", poolRootFlags.HasFlag( GameObjectFlags.NotSaved ),
            "pool root has GameObjectFlags.NotSaved",
            $"NotSaved missing on pool root — flags={poolRootFlags}" );

        // Gate 7: child reparented under the container.
        Gate( ref failures, TestName, "child_reparented", childParentId == firstId && childCountAfterAdd == 1,
            "test child parented under the container (child count == 1)",
            $"expected child.Parent.Id=={firstId} & count==1, got parent={childParentId} count={childCountAfterAdd}" );

        // Gate 8: EnsureContainer is idempotent — same GUID, created=false.
        Gate( ref failures, TestName, "ensure_idempotent", !created2 && secondId == firstId,
            "second EnsureContainer returned the same GUID with created=false",
            $"expected same GUID & created=false, got created={created2} id={secondId} (first={firstId})" );

        // Gate 9: Snapshot reflects the container with its one child.
        bool snapOk = testEntry != null && testEntry.Id == firstId && testEntry.ChildCount == 1;
        Gate( ref failures, TestName, "snapshot_child_count", snapOk,
            "Snapshot lists the container with child_count == 1",
            testEntry == null
                ? "Snapshot did not list the test container"
                : $"expected id={firstId} childCount=1, got id={testEntry.Id} childCount={testEntry.ChildCount}" );

        // Gate 10: ClearContainer removed the child but kept the container.
        bool clearOk = removed == 1 && afterClear != null && childCountAfterClear == 0;
        Gate( ref failures, TestName, "clear_keeps_container", clearOk,
            "ClearContainer removed the child (returned 1) and kept the container (0 children)",
            $"expected removed=1 & container alive with 0 children, got removed={removed} afterClear={(afterClear == null ? "null" : "alive")} childCount={childCountAfterClear}" );

        // Gate 11: ReleaseContainer destroyed the container.
        Gate( ref failures, TestName, "release_destroys", releasedOk && afterRelease == null,
            "ReleaseContainer destroyed the container (subsequent get returns null)",
            $"expected released=true & get==null, got released={releasedOk} afterRelease={(afterRelease == null ? "null" : "alive")}" );

        // Gate 12: a second ReleaseContainer is a safe no-op.
        Gate( ref failures, TestName, "release_idempotent", !doubleRelease,
            "second ReleaseContainer returned false (idempotent)",
            $"expected false, got {doubleRelease}" );

        // ── CLEANUP ─────────────────────────────────────────────────────
        // The test container was already released in EXEC. Exercise ClearPool
        // ONLY when the pool root is empty — destroying a pool root that still
        // holds concurrent agents' containers would violate isolation.
        var poolAfter = ScratchScenePool.FindPoolRoot( scene );
        if ( poolAfter == null )
        {
            Log.Info( $"[PASS] {TestName}: CLEANUP — pool root already absent; nothing to tidy. Idempotent re-run safe (PRECONDITION pre-clears)." );
        }
        else if ( poolAfter.Children.Count == 0 )
        {
            ScratchScenePool.ClearPool( scene );
            bool gone = ScratchScenePool.FindPoolRoot( scene ) == null;
            if ( gone )
                Log.Info( $"[PASS] {TestName}: CLEANUP — empty pool root removed via ClearPool. Idempotent re-run safe." );
            else
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: CLEANUP — ClearPool did not remove the empty pool root" );
            }
        }
        else
        {
            Log.Info( $"[PASS] {TestName}: CLEANUP — pool root has {poolAfter.Children.Count} concurrent-agent container(s); left intact (concurrent-safe). Idempotent re-run safe." );
        }

        // ── SUMMARY ─────────────────────────────────────────────────────
        if ( failures == 0 )
            Log.Info( $"[PASS] {TestName}: SUMMARY — 12/12 gates green; scratch-scene pool verified end-to-end (ensure/get/clear/release/list + NotSaved|NotNetworked invariant + idempotency)" );
        else
            Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of 12 total" );
    }

    /// <summary>Emit one PASS/FAIL log line for a gate and bump the failure count.</summary>
    private static void Gate( ref int failures, string testName, string gate, bool ok, string passMsg, string failMsg )
    {
        if ( ok )
        {
            Log.Info( $"[PASS] {testName}: ASSERT {gate} — {passMsg}" );
        }
        else
        {
            failures++;
            Log.Info( $"[FAIL] {testName}: ASSERT {gate} — {failMsg}" );
        }
    }
}
