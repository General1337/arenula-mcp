using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

// [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — additive 'identify' action + McpSession threading; no inherited handoff hypothesis applies, per Phase 1 falsification at docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md.]
/// <summary>
/// session tool: list, set_active, load_scene, identify.
/// Manages editor sessions (scene/prefab tabs) and per-connection agent identity
/// (engine-fork-phase3-1-library-mvp). Ported from Ozmium SessionToolHandlers.
/// </summary>
internal static class SessionHandler
{
    internal static object Handle( string action, JsonElement args, McpSession session )
    {
        try
        {
            return action switch
            {
                "list"       => List(),
                "set_active" => SetActive( args ),
                "load_scene" => LoadScene( args ),
                "identify"   => Identify( args, session ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: list, set_active, load_scene, identify" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── identify ──────────────────────────────────────────────────────────
    // Stamp the SSE-session-scoped agent identity used by McpOperationCoordinator
    // for the per-key GAMEOBJECT-LOCKED-BY-<agent[:8]> contention error and by
    // GameObjectLockMap.ReleaseAll for SSE-close eviction. Idempotent:
    // re-identify with the same values is a no-op; re-identify with different
    // values mutates and logs an info line for audit.
    //
    // mcp-batch-async-session-identify (findings.md #7, 2026-06-11): identity is
    // ALSO stamped into AgentIdentityRegistry as one atomic (agent_id, slug)
    // write, and the response is structured — {agent_id, agent_slug, resolved_as}
    // plus the legacy Phase 3 fields — so the coordinator can fresh-resolve
    // identity instead of trusting the dispatcher's TryBegin-time copy (the
    // GAMEOBJECT-LOCKED-BY-anon race).

    private static object Identify( JsonElement args, McpSession session )
    {
        var agentId = HandlerBase.GetString( args, "agent_id" );
        var slug = HandlerBase.GetString( args, "slug" );
        if ( string.IsNullOrWhiteSpace( agentId ) || string.IsNullOrWhiteSpace( slug ) )
            return HandlerBase.Error(
                "agent_id and slug are required.",
                "identify",
                "Pass agent_id (harness session_id GUID) and slug (current session folder slug). Both non-empty." );

        if ( session == null )
            return HandlerBase.Error( "No session context available.", "identify" );

        var prevAgentId = session.AgentId;
        var prevSlug = session.AgentSlug;
        var changed = ( prevAgentId != agentId ) || ( prevSlug != slug );
        var resolvedAs = string.IsNullOrEmpty( prevAgentId ) ? "identified"
            : changed ? "reidentified"
            : "unchanged";

        // Atomic stamp FIRST — one lock-guarded write of BOTH values — so the
        // coordinator's fresh-resolve path can never observe a torn or stale
        // pair while the two unsynchronized McpSession setters below land.
        AgentIdentityRegistry.Stamp( session.SessionId, agentId, slug );

        session.AgentId = agentId;
        session.AgentSlug = slug;

        if ( changed && !string.IsNullOrEmpty( prevAgentId ) )
        {
            // Log audit line — agent identity mutated on an already-identified session.
            Log.Info(
                $"[mcp.session.identify] AgentId changed on session " +
                $"{session.SessionId?[..Math.Min(8, session.SessionId.Length)]}: " +
                $"{prevAgentId[..Math.Min(8, prevAgentId.Length)]} ({prevSlug}) " +
                $"-> {agentId[..Math.Min(8, agentId.Length)]} ({slug})" );
        }

        // engine-fork-elite-leverage Phase 3.3 — pre-spawn this agent's scratch
        // container so runtime experiments land in an isolated, NotSaved sandbox
        // the moment the session is identified. session.identify IS the MCP
        // session-start signal, and SessionHandler.Handle runs on the dispatcher's
        // MainThread branch, so scene mutation here is thread-safe. Best-effort:
        // the try/catch guarantees identity stamping never fails because of
        // scratch creation.
        try
        {
            ScratchScenePool.EnsureContainer( SceneHelpers.ResolveScene(), agentId, slug, out _ );
        }
        catch ( Exception ex )
        {
            Log.Warning( $"[session.identify] scratch pre-spawn failed: {ex.Message}" );
        }

        return HandlerBase.Success( new
        {
            agent_id = agentId,
            agent_slug = slug,
            resolved_as = resolvedAs,
            // Legacy Phase 3 fields retained for existing consumers.
            slug,
            acknowledged_at = DateTime.UtcNow.ToString( "o" ),
            changed
        } );
    }

    // ── list ──────────────────────────────────────────────────────────────
    // Ported from SessionToolHandlers.GetEditorSessions

    private static object List()
    {
        var sessions = SceneEditorSession.All ?? new List<SceneEditorSession>();
        var active = SceneEditorSession.Active;
        var results = new List<object>();

        for ( int i = 0; i < sessions.Count; i++ )
        {
            var s = sessions[i];
            results.Add( new
            {
                index = i,
                name = s.Scene?.Name ?? "(unnamed)",
                isActive = s == active,
                isPrefabSession = s.IsPrefabSession,
                hasUnsavedChanges = s.HasUnsavedChanges
            } );
        }

        return HandlerBase.Success( new
        {
            count = results.Count,
            sessions = results
        } );
    }

    // ── set_active ────────────────────────────────────────────────────────
    // Ported from SessionToolHandlers.SetActiveSession

    private static object SetActive( JsonElement args )
    {
        var sessionId = HandlerBase.GetString( args, "session_id" );
        if ( string.IsNullOrEmpty( sessionId ) )
            return HandlerBase.Error( "Missing required 'session_id' parameter.", "set_active",
                "Provide the session name or index from session.list." );

        var sessions = SceneEditorSession.All ?? new List<SceneEditorSession>();
        if ( sessions.Count == 0 )
            return HandlerBase.Error( "No editor sessions open.", "set_active" );

        SceneEditorSession target = null;

        // Try parsing as index first
        if ( int.TryParse( sessionId, out var index ) )
        {
            if ( index >= 0 && index < sessions.Count )
                target = sessions[index];
            else
                return HandlerBase.Error(
                    $"Index {index} out of range. {sessions.Count} sessions available (0-{sessions.Count - 1}).",
                    "set_active" );
        }
        else
        {
            // Try matching by name (partial, case-insensitive)
            target = sessions.FirstOrDefault( s =>
                s.Scene?.Name?.Contains( sessionId, StringComparison.OrdinalIgnoreCase ) == true );
            if ( target == null )
                return HandlerBase.Error(
                    $"No session found matching '{sessionId}'. Use session.list to see available sessions.",
                    "set_active" );
        }

        target.MakeActive();
        return HandlerBase.Confirm( $"Activated session: '{target.Scene?.Name ?? "(unnamed)"}'." );
    }

    // ── load_scene ────────────────────────────────────────────────────────
    // Ported from SessionToolHandlers.LoadScene

    private static object LoadScene( JsonElement args )
    {
        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "load_scene" );

        // CreateFromPath handles both .scene and .prefab files.
        // It reuses an existing session if the file is already open.
        var session = SceneEditorSession.CreateFromPath( path );

        if ( session == null )
        {
            // Try with common path variations
            if ( !path.EndsWith( ".scene" ) && !path.EndsWith( ".prefab" ) )
                session = SceneEditorSession.CreateFromPath( path + ".scene" );

            if ( session == null )
                return HandlerBase.Error(
                    $"Could not open '{path}'. Ensure the file exists and is a .scene or .prefab.",
                    "load_scene",
                    "Use asset_query.search with type 'scene' to list available scenes." );
        }

        session.MakeActive();

        return HandlerBase.Success( new
        {
            message = $"Opened and activated: '{session.Scene?.Name ?? path}'.",
            name = session.Scene?.Name,
            isPrefabSession = session.IsPrefabSession
        } );
    }
}
