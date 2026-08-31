// Editor/Core/AgentIdentityRegistry.cs
//
// mcp-batch-async-session-identify (findings.md #7, 2026-06-11): atomic
// session-identity store closing the GAMEOBJECT-LOCKED-BY-anon race.
//
// The race: session.identify mutates McpSession.AgentId/AgentSlug on the main
// thread, but RpcDispatcher copies session.AgentId into the coordinator's
// ActiveOperation at TryBegin time (RpcDispatcher.cs:131-133). An operation
// admitted before — or concurrently with — identify's handler body carries
// null identity for its whole lifetime, so any contention error built against
// it later reports "LOCKED-BY-anon" even though the session identified long
// ago. The two McpSession setters are also separate unsynchronized writes, so
// a cross-thread reader can observe a torn (new agent_id, old slug) pair.
//
// Fix: identify stamps BOTH values here in one lock-guarded write (the
// "atomically" in findings #7), and McpOperationCoordinator re-resolves
// identity from this registry both at TryBegin and — for the conflict HOLDER —
// at error-build/snapshot time, which is always strictly later than the
// identify call the agent already awaited.

using System.Collections.Generic;
using System.Text.Json;

namespace Arenula;

internal static class AgentIdentityRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (string AgentId, string AgentSlug)> BySession = new();

    /// <summary>
    /// Atomically record (or overwrite) the identity for one SSE session. Both
    /// values land in a single lock-guarded write — a concurrent
    /// <see cref="TryResolve"/> sees either the full old pair or the full new
    /// pair, never a torn mix.
    /// </summary>
    internal static void Stamp( string sessionId, string agentId, string agentSlug )
    {
        if ( string.IsNullOrEmpty( sessionId ) ) return;
        lock ( Sync )
        {
            BySession[sessionId] = (agentId, agentSlug);
        }
    }

    /// <summary>Read the stamped identity pair for a session, if any.</summary>
    internal static bool TryResolve( string sessionId, out string agentId, out string agentSlug )
    {
        agentId = null;
        agentSlug = null;
        if ( string.IsNullOrEmpty( sessionId ) ) return false;
        lock ( Sync )
        {
            if ( !BySession.TryGetValue( sessionId, out var pair ) ) return false;
            agentId = pair.AgentId;
            agentSlug = pair.AgentSlug;
            return true;
        }
    }

    /// <summary>
    /// Resolve the freshest identity for a session: the registry entry when one
    /// exists (it is authoritative — written by session.identify itself),
    /// otherwise the caller-supplied fallback (the dispatcher's possibly-stale
    /// McpSession read, or the value stamped on an ActiveOperation at TryBegin).
    /// </summary>
    internal static (string AgentId, string AgentSlug) ResolveFresh(
        string sessionId, string fallbackAgentId, string fallbackAgentSlug )
    {
        return TryResolve( sessionId, out var agentId, out var agentSlug )
            ? (agentId, agentSlug)
            : (fallbackAgentId, fallbackAgentSlug);
    }

    /// <summary>
    /// Resolve the identity of the agent making the CURRENT tool call
    /// (mcp-multiagent-verdict-integrity, findings.md #4). Resolution order:
    /// <list type="number">
    ///   <item>Explicit <c>agent_id</c> (+ optional <c>agent_slug</c>) args on the
    ///     call itself — works today on every dispatcher path, no session
    ///     threading required.</item>
    ///   <item>The SSE session's registry-fresh identity (when the dispatcher
    ///     threads <see cref="McpSession"/> through — see the handoff's
    ///     RpcDispatcher/ToolDispatchSteps wire-up lines).</item>
    ///   <item><c>(null, null)</c> — anonymous; callers treat this as
    ///     "no per-agent view, fall back to shared/global behavior".</item>
    /// </list>
    /// </summary>
    internal static (string AgentId, string AgentSlug) ResolveCaller( JsonElement args, McpSession session )
    {
        string argAgentId = null;
        string argSlug = null;
        if ( args.ValueKind == JsonValueKind.Object )
        {
            if ( args.TryGetProperty( "agent_id", out var idEl ) && idEl.ValueKind == JsonValueKind.String )
                argAgentId = idEl.GetString();
            if ( args.TryGetProperty( "agent_slug", out var slugEl ) && slugEl.ValueKind == JsonValueKind.String )
                argSlug = slugEl.GetString();
        }

        if ( !string.IsNullOrWhiteSpace( argAgentId ) )
            return (argAgentId.Trim(), string.IsNullOrWhiteSpace( argSlug ) ? null : argSlug.Trim());

        if ( session != null )
        {
            var (agentId, agentSlug) = ResolveFresh( session.SessionId, session.AgentId, session.AgentSlug );
            if ( !string.IsNullOrWhiteSpace( agentId ) )
                return (agentId, agentSlug);
        }

        return (null, null);
    }

    /// <summary>
    /// Drop a session's identity. Called from the SSE-close eviction path
    /// (McpOperationCoordinator.EvictBySession — sole caller is
    /// ArenulaMcpServer's session teardown) so the map never grows past the
    /// set of live sessions. Returns true when an entry existed.
    /// </summary>
    internal static bool Evict( string sessionId )
    {
        if ( string.IsNullOrEmpty( sessionId ) ) return false;
        lock ( Sync )
        {
            return BySession.Remove( sessionId );
        }
    }
}
