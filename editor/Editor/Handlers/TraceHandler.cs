// [PERF-OK: not performance work. This is a feature port of four new trace
// actions from upstream Arenula (74613e9, d476122) on 2026-08-05. Nothing here
// optimizes an existing path — no hot loop was profiled, changed, or claimed
// faster. The MaxRaysPerCall / MaxGridSamplesPerAxis constants are inherited
// request-size caps that bound how much work ONE call can ask for; they are
// safety limits on new surface area, not tuning of anything that already ran.]

using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// trace tool: ray, box, ground, sphere_cast, box_cast, sample_grid, multi_ray.
/// Scene trace operations for finding ground level, checking clearance, sweeping
/// volumes, and sampling terrain profiles.
/// </summary>
/// <remarks>
/// The last four actions were ported from upstream Arenula (74613e9, d476122) on
/// 2026-08-05. Upstream's filter convention was NOT adopted: there, <c>tags</c>
/// means "only hit objects carrying these" (WithAnyTags). Here <c>tags</c> has
/// always meant the opposite — "do not hit these" — and silently inverting that
/// would break every existing caller of ray / box / ground. So across this whole
/// tool: <c>tags</c> and <c>ignore_tags</c> exclude, <c>require_tags</c> includes.
/// </remarks>
internal static class TraceHandler
{
    /// <summary>Cap on rays per multi_ray call — each one is a full scene trace.</summary>
    private const int MaxRaysPerCall = 256;

    /// <summary>Cap per grid axis. 32x32 is 1024 traces, already a slow call.</summary>
    private const int MaxGridSamplesPerAxis = 32;

    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "ray"         => Ray( args ),
                "box"         => Box( args ),
                "ground"      => Ground( args ),
                "sphere_cast" => SphereCast( args ),
                "box_cast"    => BoxCast( args ),
                "sample_grid" => SampleGrid( args ),
                "multi_ray"   => MultiRay( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: ray, box, ground, sphere_cast, box_cast, sample_grid, multi_ray" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── ray ──────────────────────────────────────────────────────────
    // Cast a ray from point A to point B. Return hit info.

    private static object Ray( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "ray" );

        var fromStr = HandlerBase.GetString( args, "from" );
        if ( string.IsNullOrEmpty( fromStr ) )
            return HandlerBase.Error( "Missing required 'from' parameter.", "ray" );

        var toStr = HandlerBase.GetString( args, "to" );
        if ( string.IsNullOrEmpty( toStr ) )
            return HandlerBase.Error( "Missing required 'to' parameter.", "ray" );

        var from = HandlerBase.ParseVector3( fromStr );
        var to = HandlerBase.ParseVector3( toStr );

        var trace = ApplyFilters( scene.Trace.Ray( from, to ), args, scene );
        var tr = trace.Run();

        // Physics missed — the terrain collider may be stale after a heightmap
        // edit in this session, so ask the heightmap directly before reporting
        // a miss. See TryTerrainFallback.
        if ( !tr.Hit && TryTerrainFallback( scene, from, to ) is TerrainHit th )
            return HandlerBase.Success( FormatTerrainHit( th, from, to ) );

        return HandlerBase.Success( new
        {
            hit = tr.Hit,
            position = HandlerBase.V3( tr.EndPosition ),
            normal = HandlerBase.V3( tr.Normal ),
            distance = MathF.Round( tr.Distance, 2 ),
            hit_object_id = tr.GameObject?.Id.ToString(),
            hit_object_name = tr.GameObject?.Name,
            fraction = MathF.Round( tr.Fraction, 4 ),
            surface = tr.Surface?.ResourceName,
            component_type = tr.Component?.GetType().Name,
            source = "physics"
        } );
    }

    // ── box ──────────────────────────────────────────────────────────
    // Box trace for clearance checking at a position.

    private static object Box( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "box" );

        var posStr = HandlerBase.GetString( args, "position" );
        if ( string.IsNullOrEmpty( posStr ) )
            return HandlerBase.Error( "Missing required 'position' parameter.", "box" );

        var position = HandlerBase.ParseVector3( posStr );
        var sizeStr = HandlerBase.GetString( args, "size", "32,32,72" );
        var size = HandlerBase.ParseVector3( sizeStr );

        // Build a BBox centered on the position using half-extents
        var halfSize = size * 0.5f;
        var mins = new Vector3( -halfSize.x, -halfSize.y, 0 );
        var maxs = new Vector3( halfSize.x, halfSize.y, size.z );
        var bbox = new BBox( mins, maxs );

        var trace = ApplyFilters( scene.Trace.Box( bbox, position, position ), args, scene );
        var tr = trace.Run();

        return HandlerBase.Success( new
        {
            hit = tr.Hit,
            hit_object_name = tr.GameObject?.Name,
            hit_object_id = tr.GameObject?.Id.ToString(),
        } );
    }

    // ── ground ───────────────────────────────────────────────────────
    // Find the ground position below a point (ray straight down).

    private static object Ground( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "ground" );

        var posStr = HandlerBase.GetString( args, "position" );
        if ( string.IsNullOrEmpty( posStr ) )
            return HandlerBase.Error( "Missing required 'position' parameter.", "ground" );

        var position = HandlerBase.ParseVector3( posStr );
        var maxDistance = HandlerBase.GetFloat( args, "max_distance", 10000f );

        var from = position;
        var to = position - new Vector3( 0, 0, maxDistance );

        var trace = ApplyFilters( scene.Trace.Ray( from, to ), args, scene );
        var tr = trace.Run();

        if ( !tr.Hit && TryTerrainFallback( scene, from, to ) is TerrainHit th )
            return HandlerBase.Success( new
            {
                found = true,
                position = HandlerBase.V3( th.Position ),
                normal = HandlerBase.V3( th.Normal ),
                surface_material = "terrain",
                source = "terrain_heightmap"
            } );

        return HandlerBase.Success( new
        {
            found = tr.Hit,
            position = HandlerBase.V3( tr.EndPosition ),
            normal = HandlerBase.V3( tr.Normal ),
            surface_material = tr.Surface?.ResourceName,
            source = "physics"
        } );
    }

    // ── sphere_cast ──────────────────────────────────────────────────
    // Sweep a sphere from A to B. Use for "will this fit through" and for
    // thickened line-of-sight checks that a zero-width ray slips through.

    private static object SphereCast( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "sphere_cast" );

        var fromStr = HandlerBase.GetString( args, "from" );
        var toStr = HandlerBase.GetString( args, "to" );
        if ( string.IsNullOrEmpty( fromStr ) || string.IsNullOrEmpty( toStr ) )
            return HandlerBase.Error( "Missing required 'from' and 'to' parameters (as 'x,y,z').", "sphere_cast" );

        var radius = HandlerBase.GetFloat( args, "radius" );
        if ( radius <= 0 )
            return HandlerBase.Error( "Missing or invalid 'radius' parameter (must be > 0).", "sphere_cast" );

        var from = HandlerBase.ParseVector3( fromStr );
        var to = HandlerBase.ParseVector3( toStr );

        var trace = ApplyFilters( scene.Trace.Sphere( radius, from, to ), args, scene );
        return HandlerBase.Success( FormatResult( trace.Run() ) );
    }

    // ── box_cast ─────────────────────────────────────────────────────
    // Sweep a box from A to B. Distinct from the 'box' action, which is a
    // stationary overlap test at one position.

    private static object BoxCast( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "box_cast" );

        var fromStr = HandlerBase.GetString( args, "from" );
        var toStr = HandlerBase.GetString( args, "to" );
        var sizeStr = HandlerBase.GetString( args, "size" );
        if ( string.IsNullOrEmpty( fromStr ) || string.IsNullOrEmpty( toStr ) )
            return HandlerBase.Error( "Missing required 'from' and 'to' parameters (as 'x,y,z').", "box_cast" );
        if ( string.IsNullOrEmpty( sizeStr ) )
            return HandlerBase.Error( "Missing required 'size' parameter (box extents as 'x,y,z').", "box_cast" );

        var from = HandlerBase.ParseVector3( fromStr );
        var to = HandlerBase.ParseVector3( toStr );
        var extents = HandlerBase.ParseVector3( sizeStr );

        var trace = ApplyFilters( scene.Trace.Box( extents, from, to ), args, scene );
        return HandlerBase.Success( FormatResult( trace.Run() ) );
    }

    // ── sample_grid ──────────────────────────────────────────────────
    // Drop a grid of downward rays over an area and report the height under
    // each. One call replaces N round-trips when profiling terrain, checking
    // a landing site, or finding the floor across a room.

    private static object SampleGrid( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "sample_grid" );

        var centerStr = HandlerBase.GetString( args, "center" );
        var areaSizeStr = HandlerBase.GetString( args, "area_size" );
        if ( string.IsNullOrEmpty( centerStr ) || string.IsNullOrEmpty( areaSizeStr ) )
            return HandlerBase.Error( "Missing required 'center' (x,y,z) and 'area_size' (x,y) parameters.", "sample_grid" );

        var center = HandlerBase.ParseVector3( centerStr );
        var areaSize = HandlerBase.ParseVector2( areaSizeStr );

        var requestedX = HandlerBase.GetInt( args, "samples_x", 10 );
        var requestedY = HandlerBase.GetInt( args, "samples_y", 10 );
        var samplesX = Math.Clamp( requestedX, 1, MaxGridSamplesPerAxis );
        var samplesY = Math.Clamp( requestedY, 1, MaxGridSamplesPerAxis );
        var maxDepth = HandlerBase.GetFloat( args, "max_depth", 5000f );

        var grid = new List<object>();
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        int hits = 0;
        int terrainFallbackHits = 0;

        float startX = center.x - areaSize.x / 2f;
        float startY = center.y - areaSize.y / 2f;
        float stepX = samplesX > 1 ? areaSize.x / ( samplesX - 1 ) : 0;
        float stepY = samplesY > 1 ? areaSize.y / ( samplesY - 1 ) : 0;

        for ( int iy = 0; iy < samplesY; iy++ )
        {
            for ( int ix = 0; ix < samplesX; ix++ )
            {
                float x = startX + ix * stepX;
                float y = startY + iy * stepY;
                var from = new Vector3( x, y, center.z );
                var to = new Vector3( x, y, center.z - maxDepth );

                var result = ApplyFilters( scene.Trace.Ray( from, to ), args, scene ).Run();

                float roundedX = MathF.Round( x, 1 );
                float roundedY = MathF.Round( y, 1 );

                if ( result.Hit )
                {
                    hits++;
                    float z = result.EndPosition.z;
                    if ( z < minZ ) minZ = z;
                    if ( z > maxZ ) maxZ = z;
                    grid.Add( new
                    {
                        x = roundedX,
                        y = roundedY,
                        hit = true,
                        z = MathF.Round( z, 2 ),
                        surface = result.Surface?.ResourceName,
                        object_name = result.GameObject?.Name,
                        source = "physics"
                    } );
                    continue;
                }

                if ( TryTerrainFallback( scene, from, to ) is TerrainHit th )
                {
                    hits++;
                    terrainFallbackHits++;
                    float z = th.Position.z;
                    if ( z < minZ ) minZ = z;
                    if ( z > maxZ ) maxZ = z;
                    grid.Add( new
                    {
                        x = roundedX,
                        y = roundedY,
                        hit = true,
                        z = MathF.Round( z, 2 ),
                        surface = "terrain",
                        object_name = th.ObjectName,
                        source = "terrain_heightmap"
                    } );
                    continue;
                }

                grid.Add( new { x = roundedX, y = roundedY, hit = false } );
            }
        }

        return HandlerBase.Success( new
        {
            grid,
            samples_x = samplesX,
            samples_y = samplesY,
            // Report the clamp rather than silently returning a smaller grid than
            // the caller asked for — a quiet cap reads as "that is the whole area".
            clamped = requestedX != samplesX || requestedY != samplesY
                ? $"requested {requestedX}x{requestedY}, clamped to {samplesX}x{samplesY} (max {MaxGridSamplesPerAxis} per axis)"
                : null,
            total_samples = samplesX * samplesY,
            hits,
            terrain_fallback_hits = terrainFallbackHits,
            min_z = hits > 0 ? MathF.Round( minZ, 2 ) : (float?)null,
            max_z = hits > 0 ? MathF.Round( maxZ, 2 ) : (float?)null
        } );
    }

    // ── multi_ray ────────────────────────────────────────────────────
    // Batch arbitrary rays into one call. Filters apply to all of them.

    private static object MultiRay( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "multi_ray" );

        if ( !args.TryGetProperty( "rays", out var raysEl ) || raysEl.ValueKind != JsonValueKind.Array )
            return HandlerBase.Error( "Missing required 'rays' parameter (JSON array of {from, to} objects).", "multi_ray" );

        var rayCount = raysEl.GetArrayLength();
        if ( rayCount == 0 )
            return HandlerBase.Error( "Empty 'rays' array.", "multi_ray" );
        if ( rayCount > MaxRaysPerCall )
            return HandlerBase.Error(
                $"Too many rays ({rayCount}). Maximum is {MaxRaysPerCall} per call.", "multi_ray",
                "Split the batch, or use sample_grid if the rays form a regular downward grid." );

        var results = new List<object>();

        foreach ( var rayEl in raysEl.EnumerateArray() )
        {
            var fromStr = rayEl.TryGetProperty( "from", out var fEl ) && fEl.ValueKind == JsonValueKind.String
                ? fEl.GetString() : null;
            var toStr = rayEl.TryGetProperty( "to", out var tEl ) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() : null;

            if ( string.IsNullOrEmpty( fromStr ) || string.IsNullOrEmpty( toStr ) )
            {
                results.Add( new { hit = false, error = "Missing 'from' or 'to' in ray entry." } );
                continue;
            }

            var from = HandlerBase.ParseVector3( fromStr );
            var to = HandlerBase.ParseVector3( toStr );

            var result = ApplyFilters( scene.Trace.Ray( from, to ), args, scene ).Run();
            if ( !result.Hit && TryTerrainFallback( scene, from, to ) is TerrainHit th )
            {
                results.Add( FormatTerrainHit( th, from, to ) );
                continue;
            }

            results.Add( FormatResult( result ) );
        }

        return HandlerBase.Success( new { results, count = results.Count } );
    }

    // ── Filters ──────────────────────────────────────────────────────

    /// <summary>
    /// Apply tag / object / trigger filters shared by every action.
    /// <c>tags</c> and <c>ignore_tags</c> both EXCLUDE — see the class remarks for
    /// why this deviates from upstream. <c>require_tags</c> is the include filter.
    /// </summary>
    private static SceneTrace ApplyFilters( SceneTrace trace, JsonElement args, Scene scene )
    {
        foreach ( var name in new[] { "tags", "ignore_tags" } )
        {
            var excluded = SplitTags( HandlerBase.GetString( args, name ) );
            if ( excluded.Length > 0 ) trace = trace.WithoutTags( excluded );
        }

        var required = SplitTags( HandlerBase.GetString( args, "require_tags" ) );
        if ( required.Length > 0 ) trace = trace.WithAnyTags( required );

        var ignoreId = HandlerBase.GetString( args, "ignore_id" );
        if ( !string.IsNullOrEmpty( ignoreId ) && scene != null )
        {
            var ignoreGo = SceneHelpers.FindById( scene, ignoreId );
            if ( ignoreGo != null )
                trace = trace.IgnoreGameObjectHierarchy( ignoreGo );
        }

        if ( HandlerBase.GetBool( args, "hit_triggers", false ) )
            trace = trace.HitTriggers();

        return trace;
    }

    private static string[] SplitTags( string raw ) =>
        string.IsNullOrEmpty( raw )
            ? Array.Empty<string>()
            : raw.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

    // ── Terrain fallback ─────────────────────────────────────────────

    /// <summary>A heightmap-derived hit, used when the physics collider missed.</summary>
    private readonly struct TerrainHit
    {
        internal TerrainHit( Vector3 position, Vector3 normal, GameObject owner )
        {
            Position = position;
            Normal = normal;
            Owner = owner;
        }

        internal Vector3 Position { get; }
        internal Vector3 Normal { get; }
        internal GameObject Owner { get; }
        internal string ObjectName => Owner?.Name;
    }

    /// <summary>
    /// Ask every Terrain in the scene whether the ray meets its heightmap.
    /// The terrain physics collider does not always rebuild after a heightmap
    /// edit in the same session, so a physics miss is not proof of empty space.
    /// Terrain.RayIntersects reads the heightmap directly and needs no collider.
    /// Returns null when nothing was hit.
    /// </summary>
    private static object TryTerrainFallback( Scene scene, Vector3 from, Vector3 to )
    {
        var distance = Vector3.DistanceBetween( from, to );
        if ( distance <= 0f ) return null;

        var ray = new Ray( from, ( to - from ).Normal );

        foreach ( var go in SceneHelpers.WalkAll( scene ) )
        {
            var terrain = go.Components.Get<Terrain>();
            if ( terrain == null ) continue;

            // The four-arg overload also yields the surface normal. Upstream used
            // the three-arg one and hard-coded the normal to straight up, which is
            // wrong on every slope — and slope is the reason to sample terrain.
            if ( !terrain.RayIntersects( ray, distance, out var localPos, out var localNormal ) )
                continue;

            return new TerrainHit(
                go.WorldTransform.PointToWorld( localPos ),
                go.WorldTransform.NormalToWorld( localNormal ),
                go );
        }

        return null;
    }

    // ── Result formatting ────────────────────────────────────────────

    private static object FormatResult( SceneTraceResult r )
    {
        if ( !r.Hit )
        {
            return new
            {
                hit = false,
                start_position = HandlerBase.V3( r.StartPosition ),
                end_position = HandlerBase.V3( r.EndPosition ),
                distance = MathF.Round( r.Distance, 2 )
            };
        }

        return new
        {
            hit = true,
            position = HandlerBase.V3( r.EndPosition ),
            normal = HandlerBase.V3( r.Normal ),
            distance = MathF.Round( r.Distance, 2 ),
            fraction = MathF.Round( r.Fraction, 4 ),
            start_position = HandlerBase.V3( r.StartPosition ),
            end_position = HandlerBase.V3( r.EndPosition ),
            surface = r.Surface?.ResourceName,
            hit_object_id = r.GameObject?.Id.ToString(),
            hit_object_name = r.GameObject?.Name,
            component_type = r.Component?.GetType().Name,
            source = "physics"
        };
    }

    private static object FormatTerrainHit( TerrainHit th, Vector3 from, Vector3 to )
    {
        var travelled = Vector3.DistanceBetween( from, th.Position );
        var total = Vector3.DistanceBetween( from, to );

        return new
        {
            hit = true,
            position = HandlerBase.V3( th.Position ),
            normal = HandlerBase.V3( th.Normal ),
            distance = MathF.Round( travelled, 2 ),
            fraction = total > 0 ? MathF.Round( travelled / total, 4 ) : 0f,
            start_position = HandlerBase.V3( from ),
            end_position = HandlerBase.V3( th.Position ),
            surface = "terrain",
            hit_object_id = th.Owner?.Id.ToString(),
            hit_object_name = th.ObjectName,
            component_type = nameof( Terrain ),
            source = "terrain_heightmap"
        };
    }
}
