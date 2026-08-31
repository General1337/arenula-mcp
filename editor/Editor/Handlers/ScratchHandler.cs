// Editor/Handlers/ScratchHandler.cs
//
// [HANDOFF-TRUST: greenfield engine-fork-elite-leverage Phase 3.3 — the `scratch` MCP tool, sibling of the Phase 3.1 GameObjectLockMap + Phase 2.6 agents.observe handler. No live hypothesis from any inherited handoff applies: Phase 3.3 is a clean greenfield library feature (packet.md §"Prior Failed Approaches" — none) and the bare 'phase3' substring does not inherit any prior phase's hypothesis.]
//
// engine-fork-elite-leverage Phase 3.3 — scratch-scene pool MCP tool.
//
// Wraps Core/ScratchScenePool.cs as a scanned [McpTool]. Five actions give every
// agent an isolated, never-persisted agent_<id>_scratch container:
//   ensure  — get-or-create this agent's container (pre-spawned at session.identify)
//   get     — return container info without creating
//   clear   — destroy every child of the container, keep the container
//   release — destroy the container entirely
//   list    — every live container across all agents
//
// Library-only — no engine fork patch, no fork-only symbol. Scanned via [McpTool]
// so RpcDispatcher routes it through InvokeScanned automatically (no switch case).
// Modern handler template: AgentObserveHandler.cs / AttentionHandler.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// <c>scratch</c> — per-agent scratch-scene pool. See <see cref="ScratchScenePool"/>
/// for the pool primitive. All actions are sync MainThread handlers; the dispatcher
/// awaits MainThread before invoking, so scene mutation here is thread-safe.
/// </summary>
[McpTool( "scratch",
	"Per-agent scratch-scene pool. Gives every AI agent an isolated, never-persisted agent_<id>_scratch GameObject container so runtime experiments (spawned ships, instantiated components, physics tests) land in a per-agent sandbox instead of polluting the live solar_system.scene. Each container is pre-spawned when the agent calls session.identify and carries GameObjectFlags.NotSaved — saving the scene with containers live never writes scratch objects to disk. Actions: 'ensure' get-or-creates this agent's container, 'get' returns its info without creating, 'clear' destroys all children but keeps the container, 'release' destroys the container entirely, 'list' returns every live container across all agents. The agent_id argument is optional and defaults to the calling session's identified agent (set via session.identify) — pass it explicitly only to operate on another agent's container.",
	ReadOnlyHint = false )]
internal static class ScratchHandler
{
    /// <summary>
    /// ensure — get-or-create this agent's scratch container. Returns
    /// <c>{ ok, agent_id, container_id, container_name, child_count, created }</c>.
    /// </summary>
    [McpAction( "ensure", WriteKind = "scene_write" )]
    internal static object HandleEnsure( JsonElement args, McpSession session )
    {
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "ensure" );

            var agentId = ResolveAgentId( args, session );
            if ( agentId == null ) return MissingIdentity( "ensure" );

            var container = ScratchScenePool.EnsureContainer(
                scene, agentId, session?.AgentSlug, out bool created );

            return HandlerBase.Success( new
            {
                ok = true,
                agent_id = agentId,
                container_id = container.Id.ToString(),
                container_name = container.Name,
                child_count = container.Children.Count,
                created
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "ensure" );
        }
    }

    /// <summary>
    /// get — return this agent's container info without creating it. Returns
    /// <c>{ ok, found:false, agent_id }</c> when no container exists.
    /// </summary>
    [McpAction( "get", ReadOnly = true )]
    internal static object HandleGet( JsonElement args, McpSession session )
    {
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "get" );

            var agentId = ResolveAgentId( args, session );
            if ( agentId == null ) return MissingIdentity( "get" );

            var container = ScratchScenePool.GetContainer( scene, agentId );
            if ( container == null )
                return HandlerBase.Success( new { ok = true, found = false, agent_id = agentId } );

            return HandlerBase.Success( new
            {
                ok = true,
                found = true,
                agent_id = agentId,
                container_id = container.Id.ToString(),
                container_name = container.Name,
                child_count = container.Children.Count
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "get" );
        }
    }

    /// <summary>
    /// clear — destroy every child of this agent's container, keep the container.
    /// Returns <c>{ ok, agent_id, removed }</c>.
    /// </summary>
    [McpAction( "clear", WriteKind = "scene_write" )]
    internal static object HandleClear( JsonElement args, McpSession session )
    {
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "clear" );

            var agentId = ResolveAgentId( args, session );
            if ( agentId == null ) return MissingIdentity( "clear" );

            var removed = ScratchScenePool.ClearContainer( scene, agentId );
            return HandlerBase.Success( new { ok = true, agent_id = agentId, removed } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "clear" );
        }
    }

    /// <summary>
    /// release — destroy this agent's container entirely. Returns
    /// <c>{ ok, agent_id, released }</c> (<c>released:false</c> when none existed).
    /// </summary>
    [McpAction( "release", WriteKind = "scene_write" )]
    internal static object HandleRelease( JsonElement args, McpSession session )
    {
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "release" );

            var agentId = ResolveAgentId( args, session );
            if ( agentId == null ) return MissingIdentity( "release" );

            var released = ScratchScenePool.ReleaseContainer( scene, agentId );
            return HandlerBase.Success( new { ok = true, agent_id = agentId, released } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "release" );
        }
    }

    /// <summary>
    /// list — every live container across all agents. Needs no agent identity.
    /// Returns <c>{ ok, count, pool_root_id, containers:[...] }</c>.
    /// </summary>
    [McpAction( "list", ReadOnly = true )]
    internal static object HandleList( JsonElement args )
    {
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "list" );

            var entries = ScratchScenePool.Snapshot( scene );
            var poolRoot = ScratchScenePool.FindPoolRoot( scene );

            return HandlerBase.Success( new
            {
                ok = true,
                count = entries.Count,
                pool_root_id = poolRoot?.Id.ToString(),
                containers = entries.Select( e => new
                {
                    agent_id = e.AgentId,
                    container_id = e.Id.ToString(),
                    child_count = e.ChildCount,
                    slug = e.Slug
                } ).ToList()
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "list" );
        }
    }

    /// <summary>
    /// tools/list inputSchema fragment. The scanner invokes this once at plugin
    /// load and caches it (plan.md §2.2).
    /// </summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new
            {
                type = "string",
                description = "The operation to perform.",
                @enum = new[] { "ensure", "get", "clear", "release", "list" }
            },
            ["agent_id"] = new
            {
                type = "string",
                description = "Target agent UUID. Optional — defaults to the calling session's identified agent (set via session.identify). Pass explicitly only to operate on another agent's container. Ignored by: list."
            }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    // ── Internals ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the agent id for a call: explicit <c>agent_id</c> arg wins, else the
    /// live session's identified <see cref="McpSession.AgentId"/>, else null.
    /// </summary>
    private static string ResolveAgentId( JsonElement args, McpSession session )
    {
        var explicitId = HandlerBase.GetString( args, "agent_id" );
        if ( !string.IsNullOrWhiteSpace( explicitId ) ) return explicitId;
        if ( session != null && !string.IsNullOrWhiteSpace( session.AgentId ) ) return session.AgentId;
        return null;
    }

    private static object MissingIdentity( string action ) => HandlerBase.Error(
        "No agent identity for the scratch operation.", action,
        "Call session.identify first, or pass an explicit 'agent_id' argument." );
}
