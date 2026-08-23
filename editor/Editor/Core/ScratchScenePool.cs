// [HANDOFF-TRUST: greenfield primitive authored from docs/ai/sessions/2026-05-20-engine-fork-phase3-3-scratch-scene-pool/plan.md §2.1, sibling shape of Core/GameObjectLockMap.cs. Not a fix on any inherited handoff hypothesis — engine-fork-elite-leverage Phase 3.3 is a clean greenfield feature with no prior failure-memory entries for this domain+target (see packet.md §"Prior Failed Approaches").]
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula;

/// <summary>
/// One live scratch container, returned by <see cref="ScratchScenePool.Snapshot"/>.
/// </summary>
internal sealed record ScratchEntry( string AgentId, Guid Id, int ChildCount, string Slug );

/// <summary>
/// Per-agent scratch-scene pool. Gives every AI agent an isolated, never-persisted
/// <c>agent_&lt;id&gt;_scratch</c> GameObject container so runtime experiments
/// (spawned ships, instantiated components, physics tests) land in a per-agent
/// sandbox instead of polluting the live <c>solar_system.scene</c>.
///
/// Sibling of <see cref="GameObjectLockMap"/>: a process-scoped static primitive,
/// NO disk persistence. The headline correctness invariant is
/// <see cref="GameObjectFlags.NotSaved"/> on BOTH the pool root and every
/// container — scratch objects are excluded from scene serialization, so saving
/// <c>solar_system.scene</c> with containers live never writes <c>agent_*_scratch</c>
/// objects to disk. <see cref="GameObjectFlags.NotNetworked"/> keeps them local.
///
/// Scene layout:
/// <code>
/// &lt;scene root&gt;
///   __mcp_scratch_pool          ← single pool-root GameObject, NotSaved|NotNetworked
///     agent_&lt;id-A&gt;_scratch      ← one container per agent, NotSaved|NotNetworked
///     agent_&lt;id-B&gt;_scratch
/// </code>
///
/// The <b>scene is the source of truth.</b> The agentId→GUID dictionary is a cache
/// only — every accessor re-validates <see cref="GameObject.IsValid"/> and falls
/// back to a name lookup, so the pool survives external destruction / scene reload
/// without desync.
///
/// All scene-touching methods are MainThread-only (callers already are — MCP
/// handlers run on the dispatcher's MainThread branch, ConCmds on the editor main
/// thread). The one exception is <see cref="MarkForReap"/>, which is in-memory
/// only and safe from the SSE-close path: <see cref="GameObject.DestroyImmediate"/>
/// requires the MainThread, so SSE close merely flags the agent and the actual
/// destroy happens lazily on the next MainThread pool op.
/// </summary>
internal static class ScratchScenePool
{
    /// <summary>Name of the single pool-root GameObject at the scene root.</summary>
    internal const string PoolRootName = "__mcp_scratch_pool";

    private const string ContainerPrefix = "agent_";
    private const string ContainerSuffix = "_scratch";

    /// <summary>
    /// Stamped on the pool root and every container at creation and re-asserted on
    /// every <see cref="EnsureContainer"/> call. <c>NotSaved</c> is the disk-safety
    /// invariant; <c>NotNetworked</c> keeps scratch objects out of the scene
    /// network snapshot.
    /// </summary>
    private const GameObjectFlags ScratchFlags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

    // agentId → container GUID. Cache only; the scene is the source of truth and
    // every accessor re-validates against it.
    private static readonly ConcurrentDictionary<string, Guid> _containers = new();

    // agentId → slug, surfaced by `scratch list` for human-readable diagnostics.
    private static readonly ConcurrentDictionary<string, string> _slugs = new();

    // agentIds whose SSE session closed — destroyed lazily on the next MainThread
    // pool op (EnsureContainer / Snapshot reap before doing their work). The value
    // byte is unused; ConcurrentDictionary has no concurrent-set type.
    private static readonly ConcurrentDictionary<string, byte> _reapMarks = new();

    /// <summary>The canonical container name for an agent id.</summary>
    internal static string ContainerName( string agentId ) => $"{ContainerPrefix}{agentId}{ContainerSuffix}";

    // ── Spatial slots ───────────────────────────────────────────────────
    //
    // The pool hands every agent a private VOLUME as well as a private branch of the
    // hierarchy. Slots are carved out of the `deep-range-high` free-space region — the
    // same volume `SBEP.World.PlacementAuthority` offers as the default answer to
    // "somewhere empty", and one measured clear across its full extents on 2026-08-16.
    //
    // Keep these in step with PlacementAuthority.FreeSpaceCandidates[0]. They are
    // duplicated rather than referenced so the pool stays usable when the game assembly
    // is mid-hotload, which is exactly when an agent is most likely to call scratch.ensure.
    // `Tools/test-world-atlas.py` gate G7 parses both and fails if they diverge.

    private static readonly Vector3 SlotRegionCenter = new( 0f, 0f, 40000f );
    private static readonly Vector3 SlotRegionHalf = new( 12000f, 12000f, 6000f );

    /// <summary>Edge length of one agent's private cube, in world units.</summary>
    internal const float SlotSize = 4000f;

    /// <summary>Slots per axis, derived so the grid always fits inside the region.</summary>
    private static int SlotsPerAxis( float halfExtent ) => Math.Max( 1, (int)(halfExtent * 2f / SlotSize) );

    /// <summary>World centre of slot <paramref name="index"/>, wrapping through the grid.</summary>
    internal static Vector3 SlotCenter( int index )
    {
        var nx = SlotsPerAxis( SlotRegionHalf.x );
        var ny = SlotsPerAxis( SlotRegionHalf.y );
        var nz = SlotsPerAxis( SlotRegionHalf.z );
        var total = nx * ny * nz;
        var i = ((index % total) + total) % total;

        var ix = i % nx;
        var iy = (i / nx) % ny;
        var iz = i / (nx * ny);

        // Corner of the grid, then step to the middle of the addressed cell.
        var origin = SlotRegionCenter - new Vector3( nx, ny, nz ) * (SlotSize * 0.5f);
        return origin + new Vector3( ix + 0.5f, iy + 0.5f, iz + 0.5f ) * SlotSize;
    }

    /// <summary>
    /// Deterministic first choice of slot for an agent, so the same agent lands in the
    /// same place across reconnects and its screenshots stay comparable.
    /// </summary>
    internal static int PreferredSlot( string agentId )
    {
        unchecked
        {
            var h = 2166136261u;
            foreach ( var c in agentId ?? "" )
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)(h & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// Move the container into a slot no other live container occupies. Linear probing
    /// from the agent's preferred slot — with at most a handful of concurrent agents
    /// against a 108-cell grid, this settles on the first try nearly always.
    /// </summary>
    private static void AssignSlot( GameObject poolRoot, GameObject container, string agentId )
    {
        var taken = new HashSet<Guid>();
        var occupied = new List<Vector3>();
        foreach ( var sibling in poolRoot.Children )
        {
            if ( sibling == null || sibling == container || !taken.Add( sibling.Id ) ) continue;
            occupied.Add( sibling.WorldPosition );
        }

        var start = PreferredSlot( agentId );
        for ( var probe = 0; probe < 128; probe++ )
        {
            var candidate = SlotCenter( start + probe );
            var clash = occupied.Any( o => Vector3.DistanceBetween( o, candidate ) < SlotSize * 0.5f );
            if ( clash ) continue;
            container.WorldPosition = candidate;
            return;
        }

        // Grid full — better to stack two agents in deep space than to silently drop
        // one back onto the world origin, which is inside a planet.
        container.WorldPosition = SlotCenter( start );
    }

    /// <summary>The slot volume an agent currently owns, for `scratch get` / `scratch list`.</summary>
    internal static object SlotInfo( GameObject container ) => container == null ? null : new
    {
        center = new { x = container.WorldPosition.x, y = container.WorldPosition.y, z = container.WorldPosition.z },
        size = SlotSize,
        region = "deep-range-high",
        note = "Private volume in deep space. Objects created under this container start "
             + "here instead of at the world origin, which is inside Earth. Verify any "
             + "position you set explicitly with `place action=check`.",
    };

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Get-or-create the agent's scratch container (and the pool root). Idempotent —
    /// a second call with the same agent id returns the same GameObject. Reaps any
    /// SSE-closed agents first, stamps <see cref="ScratchFlags"/> on the pool root
    /// and the container, and refreshes the agentId→GUID cache.
    /// </summary>
    /// <param name="created">True when this call created the container; false when it already existed.</param>
    internal static GameObject EnsureContainer( Scene scene, string agentId, string agentSlug, out bool created )
    {
        created = false;
        if ( scene == null ) throw new ArgumentNullException( nameof( scene ) );
        if ( string.IsNullOrWhiteSpace( agentId ) )
            throw new ArgumentException( "agentId is required.", nameof( agentId ) );

        ReapStale( scene );

        var poolRoot = EnsurePoolRoot( scene );

        var container = FindContainer( poolRoot, agentId );
        if ( container == null )
        {
            container = scene.CreateObject();
            container.Name = ContainerName( agentId );
            container.SetParent( poolRoot );
            created = true;
        }

        // Spatial isolation, not just hierarchy isolation. Until 2026-08-16 the container
        // was parented and nothing else, so every agent's scratch sat at the same world
        // point — which is the world origin, which is the centre of Earth. Two agents
        // spawning "at the scratch container" therefore got two ships inside each other,
        // inside a planet. Give each agent its own slot in deep space instead.
        AssignSlot( poolRoot, container, agentId );

        // Re-assert the flags every call — cheap, idempotent, and defends against
        // an external editor mutation clearing them.
        container.Flags |= ScratchFlags;

        _containers[agentId] = container.Id;
        if ( !string.IsNullOrEmpty( agentSlug ) )
            _slugs[agentId] = agentSlug;
        _reapMarks.TryRemove( agentId, out _ );

        return container;
    }

    /// <summary>
    /// Return the agent's existing container, or null if none exists. Never creates.
    /// </summary>
    internal static GameObject GetContainer( Scene scene, string agentId )
    {
        if ( scene == null || string.IsNullOrWhiteSpace( agentId ) ) return null;
        var poolRoot = FindPoolRoot( scene );
        return poolRoot == null ? null : FindContainer( poolRoot, agentId );
    }

    /// <summary>
    /// Destroy every child of the agent's container, keeping the container itself.
    /// Returns the number of children removed (0 if no container exists).
    /// </summary>
    internal static int ClearContainer( Scene scene, string agentId )
    {
        var container = GetContainer( scene, agentId );
        if ( container == null ) return 0;

        // Snapshot first — DestroyImmediate mutates the parent's Children list.
        var children = container.Children.ToList();
        foreach ( var child in children )
            child.DestroyImmediate();
        return children.Count;
    }

    /// <summary>
    /// Destroy the agent's container entirely (and its cache + slug entries).
    /// Returns true when a container existed and was destroyed.
    /// </summary>
    internal static bool ReleaseContainer( Scene scene, string agentId )
    {
        var container = GetContainer( scene, agentId );
        if ( !string.IsNullOrWhiteSpace( agentId ) )
        {
            _containers.TryRemove( agentId, out _ );
            _slugs.TryRemove( agentId, out _ );
            _reapMarks.TryRemove( agentId, out _ );
        }
        if ( container == null ) return false;
        container.DestroyImmediate();
        return true;
    }

    /// <summary>
    /// Flag an agent whose SSE session closed for lazy reaping. In-memory ONLY —
    /// safe to call off the MainThread (the SSE-close path is not guaranteed to be
    /// on it). The container is actually destroyed by the next MainThread pool op.
    /// </summary>
    internal static void MarkForReap( string agentId )
    {
        if ( string.IsNullOrWhiteSpace( agentId ) ) return;
        _reapMarks[agentId] = 1;
    }

    /// <summary>
    /// Destroy the whole <see cref="PoolRootName"/> root and clear all caches.
    /// The explicit "nuke everything" primitive — for Scene.Load / play-entry
    /// resets. Returns the container count that was under the pool root.
    ///
    /// Not wired to a lifecycle event in Phase 3.3 (plan.md §4 — a Scene.Load hook
    /// is a nice-to-have, not a blocker: the <c>NotSaved</c> flag already guarantees
    /// disk safety and the per-op lazy reap handles staleness). Callers that DO use
    /// it must accept that it destroys every agent's container, not just their own.
    /// </summary>
    internal static int ClearPool( Scene scene )
    {
        _containers.Clear();
        _slugs.Clear();
        _reapMarks.Clear();
        if ( scene == null ) return 0;
        var poolRoot = FindPoolRoot( scene );
        if ( poolRoot == null ) return 0;
        int containerCount = poolRoot.Children.Count;
        poolRoot.DestroyImmediate();
        return containerCount;
    }

    /// <summary>
    /// Read-only snapshot of every live container, for <c>scratch list</c>. Reaps
    /// SSE-closed agents first so the result never lists a stale container.
    /// </summary>
    internal static IReadOnlyList<ScratchEntry> Snapshot( Scene scene )
    {
        var result = new List<ScratchEntry>();
        if ( scene == null ) return result;

        ReapStale( scene );

        var poolRoot = FindPoolRoot( scene );
        if ( poolRoot == null ) return result;

        foreach ( var child in poolRoot.Children )
        {
            if ( !child.IsValid() ) continue;
            var name = child.Name;
            if ( name == null
                || !name.StartsWith( ContainerPrefix, StringComparison.Ordinal )
                || !name.EndsWith( ContainerSuffix, StringComparison.Ordinal )
                || name.Length <= ContainerPrefix.Length + ContainerSuffix.Length )
                continue;

            var agentId = name.Substring(
                ContainerPrefix.Length,
                name.Length - ContainerPrefix.Length - ContainerSuffix.Length );
            _slugs.TryGetValue( agentId, out var slug );
            result.Add( new ScratchEntry( agentId, child.Id, child.Children.Count, slug ?? "" ) );
        }
        return result;
    }

    // ── Internals ───────────────────────────────────────────────────────

    /// <summary>Find the single pool-root GameObject at the scene root, or null.</summary>
    internal static GameObject FindPoolRoot( Scene scene )
    {
        if ( scene == null ) return null;
        foreach ( var root in scene.Children )
            if ( root.IsValid() && root.Name == PoolRootName )
                return root;
        return null;
    }

    /// <summary>Get-or-create the pool root, re-asserting <see cref="ScratchFlags"/>.</summary>
    private static GameObject EnsurePoolRoot( Scene scene )
    {
        var poolRoot = FindPoolRoot( scene );
        if ( poolRoot == null )
        {
            poolRoot = scene.CreateObject();
            poolRoot.Name = PoolRootName;
        }
        poolRoot.Flags |= ScratchFlags;
        return poolRoot;
    }

    /// <summary>
    /// Find an agent's container under the pool root. Tries the agentId→GUID cache
    /// fast-path first (re-validated), then falls back to a name lookup — which
    /// also repairs the cache.
    /// </summary>
    private static GameObject FindContainer( GameObject poolRoot, string agentId )
    {
        if ( poolRoot == null || !poolRoot.IsValid() ) return null;
        var name = ContainerName( agentId );

        // Cache fast-path — re-validate the cached GUID against the live scene.
        if ( _containers.TryGetValue( agentId, out var cachedId ) )
        {
            foreach ( var child in poolRoot.Children )
                if ( child.IsValid() && child.Id == cachedId && child.Name == name )
                    return child;
        }

        // Name fallback — survives external destruction / scene reload; repairs cache.
        foreach ( var child in poolRoot.Children )
        {
            if ( child.IsValid() && child.Name == name )
            {
                _containers[agentId] = child.Id;
                return child;
            }
        }
        return null;
    }

    /// <summary>
    /// Destroy the containers of any SSE-closed agents flagged via
    /// <see cref="MarkForReap"/>. Runs at the head of every MainThread pool op.
    /// </summary>
    private static void ReapStale( Scene scene )
    {
        if ( _reapMarks.IsEmpty ) return;
        var poolRoot = FindPoolRoot( scene );
        foreach ( var agentId in _reapMarks.Keys.ToList() )
        {
            if ( poolRoot != null )
                FindContainer( poolRoot, agentId )?.DestroyImmediate();
            _containers.TryRemove( agentId, out _ );
            _slugs.TryRemove( agentId, out _ );
            _reapMarks.TryRemove( agentId, out _ );
        }
    }
}
