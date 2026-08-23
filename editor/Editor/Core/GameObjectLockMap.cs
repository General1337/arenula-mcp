// [HANDOFF-TRUST: greenfield primitive authored from docs/ai/initiatives/engine-fork-elite-leverage/phase3.1-design.md §"Library MVP" reference shape, not a fix on any inherited handoff hypothesis — see Phase 1 re-verification report at docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md and falsification sentinel at .claude/session-state/agents/f879d224-841c-4ab0-84f5-5653fa7cac0d/falsification-passed/engine-fork-phase3-1-library-mvp.json which falsified the RFC's editor-mutex-layer claim and pinned Path C as the elite-tier elected design.]
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula;

/// <summary>
/// Per-GameObject ownership lock map. Cross-call durability surface for per-key
/// scene-edit operations that span multiple Coordinator leases (e.g.
/// transaction.apply mutating N GameObjects across an internal op sequence,
/// or future explicit gameobject.acquire / release actions).
///
/// Process-scoped to the MCP server. NO disk persistence. Cleared on SSE
/// session close (ReleaseAll by agent), Scene.Load, and editor.play entry.
///
/// Note: the AUTHORITATIVE per-call contention check lives in
/// McpOperationCoordinator (key-aware FindConflict). This map exists for the
/// narrower "I want to hold these GOs across multiple separate calls" use
/// case. No live consumer wires TryAcquire yet (2026-06-11 audit) — the API is
/// forward-facing; the reap/snapshot paths run via McpLockLifecycle + the
/// operation.locks_held query.
/// </summary>
internal record LockEntry(
    string AgentId,
    string AgentSlug,
    DateTime AcquiredUtc,
    DateTime LastSeenUtc,
    string Operation,
    TimeSpan Ttl );

internal static class GameObjectLockMap
{
    private static readonly ConcurrentDictionary<Guid, LockEntry> _locks = new();

    // mcp-operation-lifecycle-hardening (audit finding #3): lifecycle-class
    // operations (compile cycles, play-mode transitions, suite dispatch)
    // legitimately hold cross-call locks far longer than a scene edit — the
    // flat 120s TTL reaped them mid-flight, and silently, producing the
    // "locked by unknown holder" confusion. Per-entry TTL + a warning on reap.
    private static readonly TimeSpan _defaultTtl = TimeSpan.FromSeconds( 120 );
    private static readonly TimeSpan _lifecycleTtl = TimeSpan.FromSeconds( 600 );

    /// <summary>
    /// Returns null on success, or the existing holder on contention. Pass
    /// lifecycleClass=true when the acquiring operation is lifecycle-class
    /// (ToolActionMetadata.LifecycleKind == "lifecycle") — grants the longer
    /// 600s TTL instead of the default 120s.
    /// </summary>
    internal static LockEntry TryAcquire( Guid id, string agentId, string slug, string op, bool lifecycleClass = false )
    {
        if ( string.IsNullOrEmpty( agentId ) )
            return null; // anonymous: degraded mode — skip lock check
        ReapExpired();
        var ttl = lifecycleClass ? _lifecycleTtl : _defaultTtl;
        var entry = new LockEntry( agentId, slug ?? "", DateTime.UtcNow, DateTime.UtcNow, op ?? "", ttl );
        if ( _locks.TryAdd( id, entry ) ) return null;
        if ( _locks.TryGetValue( id, out var existing ) && existing.AgentId == agentId )
        {
            // Same agent: reentrant refresh. TTL only ever ratchets up — a
            // lifecycle-class refresh extends a default-class hold, never trims it.
            _locks[id] = existing with
            {
                LastSeenUtc = DateTime.UtcNow,
                Operation = op ?? existing.Operation,
                Ttl = ttl > existing.Ttl ? ttl : existing.Ttl
            };
            return null;
        }
        return existing;
    }

    internal static void Release( Guid id, string agentId )
    {
        if ( string.IsNullOrEmpty( agentId ) ) return;
        if ( _locks.TryGetValue( id, out var e ) && e.AgentId == agentId )
            _locks.TryRemove( id, out _ );
    }

    /// <summary>Called from ArenulaMcpServer on SSE session close.</summary>
    internal static int ReleaseAll( string agentId )
    {
        if ( string.IsNullOrEmpty( agentId ) ) return 0;
        var removed = 0;
        foreach ( var kv in _locks )
        {
            if ( kv.Value.AgentId == agentId && _locks.TryRemove( kv.Key, out _ ) )
                removed++;
        }
        return removed;
    }

    /// <summary>Called from Scene.Load and editor.play entry hooks.</summary>
    internal static int ClearAll()
    {
        var n = _locks.Count;
        _locks.Clear();
        return n;
    }

    /// <summary>
    /// Expire entries idle past their per-entry TTL. Each reap logs a warning —
    /// a silent reap is how "locked by unknown holder" confusion is born: the
    /// next holder acquires a lock the previous agent believes it still owns.
    /// Returns the count reaped. Called on every TryAcquire and periodically
    /// from McpLockLifecycle's frame sweep.
    /// </summary>
    internal static int ReapExpired()
    {
        var now = DateTime.UtcNow;
        var reaped = 0;
        foreach ( var kv in _locks )
        {
            var idle = now - kv.Value.LastSeenUtc;
            if ( idle <= kv.Value.Ttl ) continue;
            if ( !_locks.TryRemove( kv.Key, out var e ) ) continue;
            reaped++;
            var shortAgent = string.IsNullOrEmpty( e.AgentId ) ? "anon" : e.AgentId[..Math.Min( 8, e.AgentId.Length )];
            Log.Warning(
                $"[Arenula] GameObjectLockMap TTL-reaped lock on {kv.Key} held by agent {shortAgent}"
                + $"{(string.IsNullOrEmpty( e.AgentSlug ) ? "" : $" (slug '{e.AgentSlug}')")}"
                + $", op '{e.Operation}', idle {idle.TotalSeconds:F0}s > ttl {e.Ttl.TotalSeconds:F0}s. "
                + "The holder may still believe it owns this GameObject." );
        }
        return reaped;
    }

    /// <summary>Read-only snapshot for diagnostics / observation tools.</summary>
    internal static IReadOnlyDictionary<Guid, LockEntry> Snapshot()
        => _locks.ToDictionary( kv => kv.Key, kv => kv.Value );

    /// <summary>
    /// Structured holder listing for the operation.locks_held query: who holds
    /// which GameObject, for how long, and when the TTL reaper will take it.
    /// </summary>
    internal static object LocksHeldSnapshot()
    {
        var now = DateTime.UtcNow;
        var locks = _locks
            .Select( kv => new
            {
                gameobject_id = kv.Key.ToString(),
                agent_id = kv.Value.AgentId,
                agent_slug = kv.Value.AgentSlug,
                operation = kv.Value.Operation,
                acquired_utc = kv.Value.AcquiredUtc.ToString( "o" ),
                held_s = (int)(now - kv.Value.AcquiredUtc).TotalSeconds,
                idle_s = (int)(now - kv.Value.LastSeenUtc).TotalSeconds,
                ttl_s = (int)kv.Value.Ttl.TotalSeconds,
                expires_in_s = (int)Math.Max( 0, (kv.Value.Ttl - (now - kv.Value.LastSeenUtc)).TotalSeconds )
            } )
            .OrderBy( l => l.expires_in_s )
            .ToArray();
        return new { count = locks.Length, locks };
    }

    internal static int Count => _locks.Count;
}
