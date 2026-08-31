// [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 4 — lock-key-aware McpOperationCoordinator rewrite per the Path C committed design (docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md §"Path C — committed design"). Phase 1 falsified the design RFC's editor-mutex-layer claim (falsification sentinel engine-fork-phase3-1-library-mvp.json); this Coordinator IS the real cross-session serialization surface. No inherited handoff hypothesis applies — banned approaches Path A (editor-mutex bypass) and Path B (in-handler-only) are NOT re-attempted here.]
// [PERF-OK: not a hot-path optimization. FindConflict gains one LockKeyStrategy enum compare per active op plus a bounded Guid-set overlap scan; the Active dict holds one entry per in-flight MCP op (realistically <10). The per-key check REMOVES false cross-session contention — it does not add a measured bottleneck and is not optimizing any profiled path.]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox;

namespace Arenula;

internal sealed class McpOperationLease : IDisposable
{
    private readonly string _operationId;
    private bool _disposed;

    internal McpOperationLease( string operationId )
    {
        _operationId = operationId;
    }

    public void Dispose()
    {
        if ( _disposed ) return;
        _disposed = true;
        McpOperationCoordinator.Release( _operationId );
    }
}

/// <summary>
/// Coordinates editor-facing MCP operations across independent agent sessions.
///
/// This is the AUTHORITATIVE cross-session contention point for the MCP plugin.
/// Phase 1 of engine-fork-phase3-1-library-mvp falsified the design RFC's claim
/// that the editor-mutex layer serializes scene writes — it does not;
/// gameobject.* / component.* actions route to default_class=readonly
/// skip_lock=true at that layer. The real serialization happens HERE.
///
/// Conflict policy (Path C committed design, phase-01.md item 4):
///   • ReadOnly       — never conflicts with ReadOnly or per-key writes;
///                      conflicts only with a scene-wide write.
///   • PerKeyWrite    — a non-readonly op whose ToolActionMetadata.LockKey is
///                      not LockKeyStrategy.None. Conflicts with another
///                      per-key write ONLY when their extracted Guid key sets
///                      overlap; always conflicts with a scene-wide write.
///   • SceneWideWrite — a non-readonly op with LockKey == None. Conflicts with
///                      everything (legacy blanket-serialize behavior).
/// Same-session ops never conflict with each other (reentry allowed) — a nested
/// call from a session already holding a lease cannot block itself.
///
/// Classification is purely metadata-driven (LockKey strategy), independent of
/// agent identity: an anonymous session (McpSession.AgentId == null) still gets
/// correct per-key contention because the check keys on SessionId + extracted
/// lock keys, never on AgentId. AgentId is used only for the human-readable
/// GAMEOBJECT-LOCKED-BY-&lt;agent&gt; error prefix.
/// </summary>
internal static class McpOperationCoordinator
{
    private enum OpKind { ReadOnly, PerKeyWrite, SceneWideWrite }

    private sealed class ActiveOperation
    {
        public string OperationId { get; init; }
        public string SessionId { get; init; }
        public string AgentId { get; init; }
        public string AgentSlug { get; init; }
        public string Tool { get; init; }
        public string Action { get; init; }
        public ToolRegistry.ToolActionMetadata Metadata { get; init; }
        public IReadOnlyList<Guid> LockKeys { get; init; } = Array.Empty<Guid>();
        public DateTime StartedUtc { get; init; }
        public McpLeaseGuard.OuterLeaseBinding OuterLease { get; init; }
        public McpLeaseGuard.HolderObservation HolderObservation { get; init; }
        public bool ZombieWarned { get; set; }

        /// <summary>
        /// Stamped by RpcDispatcher the moment the handler returns, BEFORE the
        /// lease is released. A non-null value with no matching Release means
        /// the release path was lost (dead client connection tearing down the
        /// continuation) — the zombie sweep evicts such entries after a short
        /// grace instead of wedging the coordinator forever.
        /// </summary>
        public DateTime? CompletedUtc { get; set; }

        public OpKind Kind => ClassifyOp( Metadata );
    }

    private sealed class OperationScope
    {
        public string OperationId { get; init; }
        public string SessionId { get; init; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveOperation> Active = new();
    private static readonly AsyncLocal<OperationScope> Current = new();

    // Zombie-sweep policy (mcp-operation-lifecycle-hardening, audit finding #3).
    // A documented 65-min stuck lease + a 700s+ past-TTL zombie blocked ALL MCP
    // calls for every agent until a cold editor restart — Active had no TTL.
    //   • CompletedReleaseGrace: a handler stamped CompletedUtc but Release never
    //     ran (dead client). Release follows completion within the same dispatch
    //     frame, so anything older than this grace is a leak, not a race.
    //   • Hard TTLs bound ops whose handler never completes (wedged await).
    //     Eviction releases the LEASE only — a still-running handler is detached
    //     and its eventual Release becomes a no-op. Lifecycle-class ops
    //     (compiles, console runs, suite dispatch) legitimately run long, so
    //     they get double the default budget.
    private static readonly TimeSpan CompletedReleaseGrace = TimeSpan.FromSeconds( 30 );
    private static readonly TimeSpan DefaultZombieTtl = TimeSpan.FromMinutes( 10 );
    private static readonly TimeSpan LifecycleZombieTtl = TimeSpan.FromMinutes( 20 );

    internal static string CurrentOperationId => Current.Value?.OperationId;
    internal static string CurrentSessionId => Current.Value?.SessionId;
    internal static int EditorRevision { get; private set; }

    private static OpKind ClassifyOp( ToolRegistry.ToolActionMetadata metadata )
    {
        if ( metadata is null || metadata.ReadOnly ) return OpKind.ReadOnly;
        return metadata.LockKey == LockKeyStrategy.None
            ? OpKind.SceneWideWrite
            : OpKind.PerKeyWrite;
    }

    internal static bool TryBegin(
        string sessionId,
        string agentId,
        string agentSlug,
        string tool,
        string action,
        IReadOnlyList<Guid> lockKeys,
        ToolRegistry.ToolActionMetadata metadata,
        out McpOperationLease lease,
        out object busyError )
    {
        metadata ??= ToolRegistry.GetOperationMetadata( tool, action );
        lockKeys ??= Array.Empty<Guid>();

        // mcp-batch-async-session-identify (findings.md #7): prefer the atomic
        // identity registry over the caller-supplied values — RpcDispatcher reads
        // session.AgentId at call time, which can be stale (or torn) when this op
        // races a concurrent session.identify on the same SSE session.
        // session.identify is itself what writes the fresh registry entry. Its
        // request envelope is the only current identity before the handler runs,
        // so do not replace it with a prior/null registry value at admission.
        if ( tool != "session" || action != "identify" )
            (agentId, agentSlug) = AgentIdentityRegistry.ResolveFresh(
                sessionId, agentId, agentSlug );
        var operationId = "op_" + Guid.NewGuid().ToString( "N" )[..12];
        lease = null;
        busyError = null;

        // Wave 3 — enforce the editor lease AT THE RESOURCE, not at the caller.
        //
        // Everything above this line assumes the caller cooperated: that it ran a
        // PreToolUse hook and took the editor mutex first. Claude Code cannot skip
        // its hooks; Codex is merely asked to call its dispatcher; a Gemini /
        // Cursor / Copilot agent or a bare script runs nothing at all — and until
        // now the editor served them anyway, because it never checked.
        //
        // This is the check. An agent that never joined the coordination layer now
        // gets a structured refusal instead of a corrupted editor, with no adapter
        // needed on its side. It generalises exactly what the per-GameObject lock
        // below already does for GameObjects.
        //
        // DEFAULTS TO AUDIT (log, allow). See McpLeaseGuard for why: a false
        // positive here would take the editor away from every agent at once, and
        // McpSession.AgentId is still nullable by design.
        if ( McpLeaseGuard.ShouldReject( agentId, tool, action, metadata?.ReadOnly ?? true,
                                         out var outerLease, out var holderObservation,
                                         out var leaseError ) )
        {
            busyError = leaseError;
            return false;
        }

        lock ( Sync )
        {
            // Self-healing entry point: evict zombies BEFORE the conflict check so
            // a wedged lease clears on the very next incoming call instead of
            // requiring a cold editor restart. ArenulaMcpServer's cancellation-
            // bound background maintenance covers the no-incoming-calls case
            // without putting coordination locks or file I/O on tool.frame.
            SweepZombiesLocked();

            // Race closure: the daemon can publish a death fence after the
            // outer guard read but before this operation enters Active. Fence
            // processing takes this same lock, so re-checking here makes the
            // zero-active acknowledgement an atomic admission boundary.
            if ( McpLeaseGuard.IsFenceRequested( holderObservation ) )
            {
                busyError = McpLeaseGuard.BuildFenceError(
                    tool, action, holderObservation );
                return false;
            }

            var conflict = FindConflict( sessionId, metadata, lockKeys );
            if ( conflict != null )
            {
                var incomingKind = ClassifyOp( metadata );
                busyError = incomingKind == OpKind.PerKeyWrite && conflict.Kind == OpKind.PerKeyWrite
                    ? BuildKeyedBusyError( tool, action, operationId, conflict, lockKeys )
                    : BuildBusyError( tool, action, operationId, conflict );
                // The dispatcher will return this typed refusal without ever
                // constructing an IDisposable lease. That is already a terminal
                // outcome for the exact outer call, so prove it here instead of
                // depending on a post-hook that may never run.
                McpLeaseGuard.RecordTerminal(
                    outerLease, operationId, tool, action );
                return false;
            }

            Active[operationId] = new ActiveOperation
            {
                OperationId = operationId,
                SessionId = sessionId,
                AgentId = agentId,
                AgentSlug = agentSlug,
                Tool = tool,
                Action = action,
                Metadata = metadata,
                LockKeys = lockKeys,
                StartedUtc = DateTime.UtcNow,
                OuterLease = outerLease,
                HolderObservation = holderObservation
            };
            Current.Value = new OperationScope { OperationId = operationId, SessionId = sessionId };
            lease = new McpOperationLease( operationId );
            return true;
        }
    }

    /// <summary>
    /// Returns the oldest active operation that conflicts with the incoming op,
    /// or null if the incoming op may proceed. Caller must hold <see cref="Sync"/>.
    /// </summary>
    private static ActiveOperation FindConflict(
        string sessionId,
        ToolRegistry.ToolActionMetadata metadata,
        IReadOnlyList<Guid> lockKeys )
    {
        var incomingKind = ClassifyOp( metadata );
        HashSet<Guid> incomingKeys = null;

        foreach ( var op in Active.Values.OrderBy( o => o.StartedUtc ) )
        {
            // Same session: reentry allowed — a nested call from the session
            // that already holds a lease never blocks itself.
            if ( op.SessionId == sessionId ) continue;

            var activeKind = op.Kind;
            bool conflict;
            switch ( incomingKind )
            {
                case OpKind.ReadOnly:
                    conflict = activeKind == OpKind.SceneWideWrite;
                    break;
                case OpKind.SceneWideWrite:
                    conflict = true;
                    break;
                case OpKind.PerKeyWrite:
                default:
                    if ( activeKind == OpKind.ReadOnly )
                        conflict = false;
                    else if ( activeKind == OpKind.SceneWideWrite )
                        conflict = true;
                    else
                    {
                        incomingKeys ??= new HashSet<Guid>( lockKeys ?? Array.Empty<Guid>() );
                        conflict = KeysOverlap( incomingKeys, op.LockKeys );
                    }
                    break;
            }

            if ( conflict ) return op;
        }

        return null;
    }

    private static bool KeysOverlap( HashSet<Guid> incoming, IReadOnlyList<Guid> active )
    {
        if ( incoming.Count == 0 || active is null || active.Count == 0 ) return false;
        foreach ( var g in active )
            if ( incoming.Contains( g ) ) return true;
        return false;
    }

    private static string ShortAgent( string agentId )
        => string.IsNullOrEmpty( agentId ) ? "anon" : agentId[..Math.Min( 8, agentId.Length )];

    /// <summary>
    /// Freshest identity for an active operation: the registry entry for its
    /// session when one exists (session.identify may have landed AFTER this op
    /// was admitted with null identity), else the values stamped at TryBegin.
    /// This is what kills the spurious GAMEOBJECT-LOCKED-BY-anon error — the
    /// conflict error is built strictly later than the identify the holding
    /// agent already awaited, so the registry read here is always fresh.
    /// </summary>
    private static (string AgentId, string AgentSlug) ResolveHolderIdentity( ActiveOperation op )
        => AgentIdentityRegistry.ResolveFresh( op.SessionId, op.AgentId, op.AgentSlug );

    /// <summary>
    /// Per-GameObject contention error. The message form mirrors editor-mutex
    /// Rule 6 ("...: WAIT-AND-RETRY required") so the agent harness re-issues the
    /// exact same call instead of substituting an alternate approach.
    /// </summary>
    private static object BuildKeyedBusyError(
        string tool, string action, string rejectedOperationId,
        ActiveOperation holder, IReadOnlyList<Guid> incomingKeys )
    {
        var elapsed = DateTime.UtcNow - holder.StartedUtc;
        var holderKeys = new HashSet<Guid>( holder.LockKeys ?? Array.Empty<Guid>() );
        var overlapping = ( incomingKeys ?? Array.Empty<Guid>() )
            .Where( holderKeys.Contains ).Distinct().Select( g => g.ToString() ).ToArray();
        var (holderAgentId, holderAgentSlug) = ResolveHolderIdentity( holder );
        var holderName = string.IsNullOrEmpty( holderAgentSlug ) ? holder.SessionId : holderAgentSlug;

        return HandlerBase.Error(
            $"GAMEOBJECT-LOCKED-BY-{ShortAgent( holderAgentId )}: WAIT-AND-RETRY required",
            $"{tool}.{action}",
            $"GameObject(s) [{string.Join( ", ", overlapping )}] held by {holderName} "
            + $"(age {elapsed.TotalSeconds:F1}s, op {holder.Tool}.{holder.Action}). Re-issue the exact same call.",
            new
            {
                code = "gameobject_locked",
                rejected_operation_id = rejectedOperationId,
                locked_keys = overlapping,
                holder = new
                {
                    operation_id = holder.OperationId,
                    session_id = holder.SessionId,
                    agent_id = holderAgentId,
                    agent_slug = holderAgentSlug,
                    tool = holder.Tool,
                    action = holder.Action,
                    elapsed_ms = (int)elapsed.TotalMilliseconds
                }
            } );
    }

    /// <summary>Scene-wide contention error (legacy blanket-serialize form).</summary>
    private static object BuildBusyError( string tool, string action, string rejectedOperationId, ActiveOperation active )
    {
        var elapsed = DateTime.UtcNow - active.StartedUtc;
        var (activeAgentId, activeAgentSlug) = ResolveHolderIdentity( active );
        return HandlerBase.Error(
            "Another editor operation is already running.",
            $"{tool}.{action}",
            "Retry after the active operation completes. Read-only tools can run concurrently unless an exclusive editor operation is active.",
            new
            {
                code = "mcp_busy",
                rejected_operation_id = rejectedOperationId,
                active_operation = new
                {
                    operation_id = active.OperationId,
                    session_id = active.SessionId,
                    agent_id = activeAgentId,
                    agent_slug = activeAgentSlug,
                    tool = active.Tool,
                    action = active.Action,
                    read_only = active.Metadata.ReadOnly,
                    write_kind = active.Metadata.WriteKind,
                    lifecycle_kind = active.Metadata.LifecycleKind,
                    elapsed_ms = (int)elapsed.TotalMilliseconds
                }
            } );
    }

    /// <summary>
    /// Stamps the operation as handler-complete. Called by RpcDispatcher the
    /// moment the handler returns, before the finally releases the lease. In the
    /// normal path the entry is removed milliseconds later; if the release is
    /// lost (dead client), the sweep evicts it after <see cref="CompletedReleaseGrace"/>.
    /// </summary>
    internal static void MarkCompleted( string operationId )
    {
        if ( string.IsNullOrEmpty( operationId ) ) return;
        lock ( Sync )
        {
            if ( Active.TryGetValue( operationId, out var op ) )
                op.CompletedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Evicts zombie operations: (a) handler completed but the lease was never
    /// released (dead client connection), past <see cref="CompletedReleaseGrace"/>;
    /// (b) operations past their hard TTL (handler wedged and will never
    /// complete). Returns the count evicted. Called opportunistically at every
    /// TryBegin and periodically from McpLockLifecycle's frame sweep.
    /// </summary>
    internal static int SweepZombies()
    {
        lock ( Sync ) return SweepZombiesLocked();
    }

    /// <summary>Caller must hold <see cref="Sync"/>.</summary>
    private static int SweepZombiesLocked()
    {
        if ( Active.Count == 0 ) return 0;

        var now = DateTime.UtcNow;
        List<(ActiveOperation Op, string Reason)> doomed = null;

        foreach ( var op in Active.Values )
        {
            string reason = null;
            if ( op.CompletedUtc is { } completed && now - completed > CompletedReleaseGrace )
            {
                reason = $"handler completed {(now - completed).TotalSeconds:F0}s ago but the lease was never released (dead client connection?)";
            }
            else
            {
                var ttl = op.Metadata?.LifecycleKind == "lifecycle" ? LifecycleZombieTtl : DefaultZombieTtl;
                if ( now - op.StartedUtc > ttl )
                    reason = $"exceeded hard TTL {ttl.TotalSeconds:F0}s (handler presumed wedged)";
            }

            if ( reason != null )
            {
                // A mutating handler is still authoritative until its real
                // finally path releases it. Timers can alarm, never detach it
                // and admit a successor that the old handler could still hit.
                if ( !op.Metadata.ReadOnly )
                {
                    if ( !op.ZombieWarned )
                    {
                        op.ZombieWarned = true;
                        Log.Warning(
                            $"[Arenula] Coordinator retained mutating operation {op.OperationId} "
                            + $"({op.Tool}.{op.Action}): {reason}. Timer is alarm-only; "
                            + "release waits for the handler terminal path." );
                    }
                    continue;
                }
                (doomed ??= new()).Add( (op, reason) );
            }
        }

        if ( doomed is null ) return 0;

        foreach ( var (op, reason) in doomed )
        {
            Active.Remove( op.OperationId );
            if ( !op.Metadata.ReadOnly )
                EditorRevision++;
            var (sweepAgentId, sweepAgentSlug) = ResolveHolderIdentity( op );
            Log.Warning(
                $"[Arenula] Coordinator zombie sweep evicted {op.OperationId} "
                + $"({op.Tool}.{op.Action}, agent {ShortAgent( sweepAgentId )}"
                + $"{(string.IsNullOrEmpty( sweepAgentSlug ) ? "" : $" slug '{sweepAgentSlug}'")}, "
                + $"age {(now - op.StartedUtc).TotalSeconds:F0}s): {reason}. "
                + "If the handler task is still running it is now detached; its eventual release is a no-op." );
        }

        return doomed.Count;
    }

    /// <summary>
    /// Force-releases an in-flight lease by id (operation.cancel). Returns a
    /// structured description of the canceled operation, or null when no such
    /// operation is active. Does NOT abort the underlying handler task — it
    /// releases the serialization lease so other agents unblock; a still-running
    /// handler is detached and its eventual release becomes a no-op.
    /// </summary>
    internal static object CancelOperation( string operationId, string requestedByAgent )
    {
        if ( string.IsNullOrEmpty( operationId ) ) return null;
        lock ( Sync )
        {
            if ( !Active.TryGetValue( operationId, out var op ) )
                return null;

            var retained = !op.Metadata.ReadOnly;
            if ( !retained )
                Active.Remove( operationId );

            var elapsed = DateTime.UtcNow - op.StartedUtc;
            var (heldByAgentId, heldByAgentSlug) = ResolveHolderIdentity( op );
            Log.Warning(
                $"[Arenula] operation.cancel: lease {operationId} ({op.Tool}.{op.Action}, "
                + $"held by agent {ShortAgent( heldByAgentId )}, age {elapsed.TotalSeconds:F0}s) "
                + $"cancel requested by agent {ShortAgent( requestedByAgent )}. "
                + (retained
                    ? "Mutating handler retained until its real terminal path; no unsafe detach occurred."
                    : "Read-only operation lease released." ) );

            return new
            {
                operation_id = op.OperationId,
                session_id = op.SessionId,
                agent_id = heldByAgentId,
                agent_slug = heldByAgentSlug,
                tool = op.Tool,
                action = op.Action,
                elapsed_ms = (int)elapsed.TotalMilliseconds,
                handler_had_completed = op.CompletedUtc != null,
                retained_until_terminal = retained
            };
        }
    }

    internal static void Release( string operationId )
    {
        ActiveOperation released = null;
        lock ( Sync )
        {
            if ( Active.TryGetValue( operationId, out var op ) )
            {
                released = op;
                Active.Remove( operationId );
                if ( !op.Metadata.ReadOnly )
                    EditorRevision++;
            }

            if ( Current.Value?.OperationId == operationId )
                Current.Value = null;
        }

        // Only the real IDisposable/finally path reaches here with the operation
        // still active. Cancel, session eviction, scene reset, and zombie sweeps
        // deliberately remove it first because their handler may still execute.
        if ( released != null )
            McpLeaseGuard.RecordTerminal(
                released.OuterLease, released.OperationId,
                released.Tool, released.Action );
    }

    /// <summary>
    /// Drops every active operation owned by <paramref name="sessionId"/>. Called
    /// from the SSE session-close path so a disconnected agent's in-flight leases
    /// do not strand the Coordinator. Returns the count evicted.
    /// </summary>
    internal static int EvictBySession( string sessionId )
    {
        if ( string.IsNullOrEmpty( sessionId ) ) return 0;
        lock ( Sync )
        {
            var doomed = Active.Where( kv => kv.Value.SessionId == sessionId
                                            && kv.Value.Metadata.ReadOnly )
                               .Select( kv => kv.Key ).ToList();
            foreach ( var id in doomed )
            {
                if ( Active.TryGetValue( id, out var op ) )
                {
                    Active.Remove( id );
                    if ( !op.Metadata.ReadOnly )
                        EditorRevision++;
                }
                if ( Current.Value?.OperationId == id )
                    Current.Value = null;
            }

            // SSE close also retires the session's stamped identity — the sole
            // caller of EvictBySession is ArenulaMcpServer's session teardown,
            // so the registry never grows past the set of live sessions.
            AgentIdentityRegistry.Evict( sessionId );
            return doomed.Count;
        }
    }

    /// <summary>
    /// Drops every active operation. Called from Scene.Load and editor.play
    /// entry hooks where outstanding leases reference a scene that no longer
    /// exists. Returns the count cleared.
    /// </summary>
    internal static int ClearAll()
    {
        lock ( Sync )
        {
            var doomed = Active.Where( kv => kv.Value.Metadata.ReadOnly )
                               .Select( kv => kv.Key ).ToList();
            var n = doomed.Count;
            if ( n > 0 )
            {
                foreach ( var id in doomed ) Active.Remove( id );
            }
            Current.Value = null;
            return n;
        }
    }

    internal static void ProcessRuntimeFence()
    {
        lock ( Sync )
        {
            if ( !McpLeaseGuard.TryReadCurrentHolderObservation(
                     out var observation )
                 || !McpLeaseGuard.TryReadFenceRequest(
                     observation, out var request ) )
                return;

            var active = Active.Values
                .Where( op => !op.Metadata.ReadOnly
                    && op.HolderObservation.RequestId == request.RequestId
                    && op.HolderObservation.RuntimeBootNonce
                        == request.RuntimeBootNonce )
                .Select( op => op.OperationId )
                .ToArray();
            McpLeaseGuard.WriteFenceAck( request, active );
        }
    }

    internal static object Snapshot()
    {
        lock ( Sync )
        {
            return new
            {
                editor_revision = EditorRevision,
                zombie_policy = new
                {
                    completed_release_grace_s = (int)CompletedReleaseGrace.TotalSeconds,
                    default_zombie_ttl_s = (int)DefaultZombieTtl.TotalSeconds,
                    lifecycle_zombie_ttl_s = (int)LifecycleZombieTtl.TotalSeconds
                },
                active = Active.Values
                    .OrderBy( op => op.StartedUtc )
                    .Select( op =>
                    {
                        var (freshAgentId, freshAgentSlug) = ResolveHolderIdentity( op );
                        return new
                        {
                        operation_id = op.OperationId,
                        session_id = op.SessionId,
                        agent_id = freshAgentId,
                        agent_slug = freshAgentSlug,
                        tool = op.Tool,
                        action = op.Action,
                        read_only = op.Metadata.ReadOnly,
                        write_kind = op.Metadata.WriteKind,
                        lifecycle_kind = op.Metadata.LifecycleKind,
                        lock_key = op.Metadata.LockKey.ToString(),
                        lock_keys = op.LockKeys.Select( g => g.ToString() ).ToArray(),
                        op_kind = op.Kind.ToString(),
                        elapsed_ms = (int)(DateTime.UtcNow - op.StartedUtc).TotalMilliseconds,
                        handler_completed = op.CompletedUtc != null,
                        completed_ms_ago = op.CompletedUtc is { } c ? (int)(DateTime.UtcNow - c).TotalMilliseconds : (int?)null
                        };
                    } )
                    .ToArray()
            };
        }
    }
}
