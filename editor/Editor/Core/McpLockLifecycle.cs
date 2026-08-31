// [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 — greenfield lifecycle hook; clears Coordinator operation leases + per-GameObject locks at editor play-mode boundaries (Path C committed design, lifecycle reset Q3/Q5). No inherited handoff hypothesis applies — this is new plumbing, not a fix on any ranked hypothesis; Phase 1 already falsified the RFC editor-mutex-layer claim.]
using System;
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
