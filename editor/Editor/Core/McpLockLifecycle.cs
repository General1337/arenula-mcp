// [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 — greenfield lifecycle hook; clears Coordinator operation leases + per-GameObject locks at editor play-mode boundaries (Path C committed design, lifecycle reset Q3/Q5). No inherited handoff hypothesis applies — this is new plumbing, not a fix on any ranked hypothesis; Phase 1 already falsified the RFC editor-mutex-layer claim.]
using System;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Resets MCP lock state at editor play-mode boundaries.
///
/// McpOperationCoordinator leases are per-call (released by
/// McpOperationLease.Dispose) and GameObjectLockMap is process-scoped; clearing
/// on play enter/exit guarantees a clean slate across the boundary where scene
/// object identity churns the most, and is a backstop against a lease stranded
/// by an op that died mid-flight.
///
/// Note: S&Box exposes no dedicated scene-FILE-load editor event in this engine
/// version — only scene.startplay / scene.play / scene.stop / scene.saved (per
/// EditorScene.cs + SceneEditorSession.cs). The per-call lease model plus the
/// SSE-close eviction in ArenulaMcpServer.ReleaseSessionLocks already prevent
/// stranded state across a manual scene reload, so the play-mode boundaries are
/// the meaningful reset points.
/// </summary>
internal static class McpLockLifecycle
{
    // Periodic zombie sweep (mcp-operation-lifecycle-hardening, audit finding #3).
    // TryBegin sweeps opportunistically on every incoming call, but a fully
    // wedged editor sees no incoming calls that survive the conflict check —
    // this frame-driven sweep guarantees zombies clear even then, and surfaces
    // GameObjectLockMap reap warnings promptly instead of at the next acquire.
    // Same contract as EditorHeartbeat.OnFrame: throttled, allocation-free on
    // the hot path, and never throws out of a frame handler.
    private const double SweepIntervalSeconds = 10.0;
    private static long _lastSweepUnixMs;

    [EditorEvent.Frame]
    public static void OnFrameSweep()
    {
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if ( nowMs - _lastSweepUnixMs < (long)(SweepIntervalSeconds * 1000) )
                return;
            _lastSweepUnixMs = nowMs;

            McpOperationCoordinator.SweepZombies();
            GameObjectLockMap.ReapExpired();
        }
        catch
        {
            // EditorEvent contract: never propagate out of a frame handler.
        }
    }

    [Event( "scene.startplay" )]
    private static void OnStartPlay() => Reset( "scene.startplay" );

    [Event( "scene.stop" )]
    private static void OnStopPlay() => Reset( "scene.stop" );

    private static void Reset( string reason )
    {
        // Editor event handlers must swallow exceptions — an unhandled throw
        // breaks the event dispatcher for every other subscriber.
        try
        {
            var leases = McpOperationCoordinator.ClearAll();
            var locks = GameObjectLockMap.ClearAll();
            if ( leases > 0 || locks > 0 )
                Log.Info( $"[Arenula] {reason}: cleared {leases} operation lease(s) + {locks} GameObject lock(s)." );
        }
        catch ( Exception ex )
        {
            Log.Warning( $"[Arenula] McpLockLifecycle.Reset({reason}) failed: {ex.Message}" );
        }
    }
}
