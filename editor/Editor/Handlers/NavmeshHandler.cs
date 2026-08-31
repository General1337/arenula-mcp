// Editor/Handlers/NavmeshHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// navmesh tool: create_agent, create_area, create_link, generate, get_status, query_path.
/// Ported from NavigationToolHandlers.cs (200 lines).
/// NEW actions: generate, get_status, query_path — research required.
/// </summary>
internal static class NavmeshHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create_agent" => CreateAgent( args ),
                "create_area"  => CreateArea( args ),
                "create_link"  => CreateLink( args ),
                "generate"     => Generate( args ),
                "get_status"   => GetStatus( args ),
                "query_path"   => QueryPath( args ),
                "generate_tile"         => GenerateTile( args ),
                "unload_tile"           => UnloadTile( args ),
                "set_defer_generation"  => SetDeferGeneration( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: create_agent, create_area, create_link, generate, get_status, query_path, generate_tile, unload_tile, set_defer_generation" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── create_agent ──────────────────────────────────────────────────
    // Ported from NavigationToolHandlers.CreateNavMeshAgent

    private static object CreateAgent( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create_agent" );

        // Can add to existing GO (via id) or create a new one
        var id = HandlerBase.GetString( args, "id" );
        GameObject go;

        if ( !string.IsNullOrEmpty( id ) )
        {
            go = SceneHelpers.FindByIdOrThrow( scene, id, "create_agent" );
        }
        else
        {
            go = scene.CreateObject();
            go.Name = HandlerBase.GetString( args, "name" ) ?? "NavMesh Agent";
            var posStr = HandlerBase.GetString( args, "position" );
            if ( posStr != null ) go.WorldPosition = HandlerBase.ParseVector3( posStr );
        }

        var agent = go.Components.Create<NavMeshAgent>();

        if ( args.TryGetProperty( "height", out var hEl ) && hEl.ValueKind == JsonValueKind.Number )
            agent.Height = hEl.GetSingle();
        if ( args.TryGetProperty( "radius", out var rEl ) && rEl.ValueKind == JsonValueKind.Number )
            agent.Radius = rEl.GetSingle();
        if ( args.TryGetProperty( "speed", out var sEl ) && sEl.ValueKind == JsonValueKind.Number )
            agent.MaxSpeed = sEl.GetSingle();
        if ( args.TryGetProperty( "acceleration", out var aEl ) && aEl.ValueKind == JsonValueKind.Number )
            agent.Acceleration = aEl.GetSingle();
        if ( args.TryGetProperty( "update_position", out var upEl ) &&
             ( upEl.ValueKind == JsonValueKind.True || upEl.ValueKind == JsonValueKind.False ) )
            agent.UpdatePosition = upEl.GetBoolean();
        if ( args.TryGetProperty( "update_rotation", out var urEl ) &&
             ( urEl.ValueKind == JsonValueKind.True || urEl.ValueKind == JsonValueKind.False ) )
            agent.UpdateRotation = urEl.GetBoolean();

        return HandlerBase.Success( new
        {
            message = $"Created NavMeshAgent on '{go.Name}'.",
            id = go.Id.ToString(),
            component_id = agent.Id.ToString(),
            speed = agent.MaxSpeed,
            radius = agent.Radius
        } );
    }

    // ── create_area ───────────────────────────────────────────────────
    // Ported from NavigationToolHandlers.CreateNavMeshArea

    private static object CreateArea( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create_area" );

        var posStr = HandlerBase.GetString( args, "position" );
        var position = posStr != null ? HandlerBase.ParseVector3( posStr ) : Vector3.Zero;

        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "NavMesh Area";
        go.WorldPosition = position;

        var area = go.Components.Create<NavMeshArea>();
        area.IsBlocker = HandlerBase.GetBool( args, "is_blocker", true );

        // Size via "size" param
        var sizeStr = HandlerBase.GetString( args, "size" );
        if ( sizeStr != null )
        {
            go.WorldScale = HandlerBase.ParseVector3( sizeStr );
        }

        return HandlerBase.Success( new
        {
            message = $"Created NavMeshArea '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition ),
            is_blocker = area.IsBlocker
        } );
    }

    // ── create_link ───────────────────────────────────────────────────
    // Ported from NavigationToolHandlers.CreateNavMeshLink

    private static object CreateLink( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create_link" );

        var startStr = HandlerBase.GetString( args, "start_position" );
        var endStr = HandlerBase.GetString( args, "end_position" );

        if ( string.IsNullOrEmpty( startStr ) || string.IsNullOrEmpty( endStr ) )
            return HandlerBase.Error( "Missing required 'start_position' and 'end_position' parameters (as 'x,y,z').", "create_link" );

        var startPos = HandlerBase.ParseVector3( startStr );
        var endPos = HandlerBase.ParseVector3( endStr );

        // Place the GO at the midpoint
        var midpoint = ( startPos + endPos ) / 2f;

        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "NavMesh Link";
        go.WorldPosition = midpoint;

        var link = go.Components.Create<NavMeshLink>();
        link.LocalStartPosition = startPos - midpoint;
        link.LocalEndPosition = endPos - midpoint;

        if ( args.TryGetProperty( "bidirectional", out var bdEl ) &&
             ( bdEl.ValueKind == JsonValueKind.True || bdEl.ValueKind == JsonValueKind.False ) )
            link.IsBiDirectional = bdEl.GetBoolean();

        return HandlerBase.Success( new
        {
            message = $"Created NavMeshLink '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition ),
            start = HandlerBase.V3( startPos ),
            end = HandlerBase.V3( endPos )
        } );
    }

    // ── generate ──────────────────────────────────────────────────────
    // NEW — trigger navmesh build.
    // Research: Use Scene.NavMesh.Generate() or NavArea static API.
    // If API not found, return informative error.

    private static object Generate( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "generate" );

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Sandbox.Navigation.NavMesh.BakeNavMesh();

            sw.Stop();
            return HandlerBase.Success( new
            {
                message = "Navmesh generation triggered.",
                generation_time_ms = sw.ElapsedMilliseconds
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"NavMesh generation failed: {ex.Message}", "generate" );
        }
    }

    // ── get_status ────────────────────────────────────────────────────
    // NEW — query navmesh state and agent count.

    private static object GetStatus( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "get_status" );

        var agents = SceneHelpers.WalkAll( scene )
            .SelectMany( go => go.Components.GetAll().OfType<NavMeshAgent>() )
            .ToList();

        var areas = SceneHelpers.WalkAll( scene )
            .SelectMany( go => go.Components.GetAll().OfType<NavMeshArea>() )
            .ToList();

        var links = SceneHelpers.WalkAll( scene )
            .SelectMany( go => go.Components.GetAll().OfType<NavMeshLink>() )
            .ToList();

        return HandlerBase.Success( new
        {
            agent_count = agents.Count,
            area_count = areas.Count,
            link_count = links.Count,
            agents = agents.Select( a => new
            {
                id = a.GameObject.Id.ToString(),
                name = a.GameObject.Name,
                speed = a.MaxSpeed,
                position = HandlerBase.V3( a.GameObject.WorldPosition )
            } )
        } );
    }

    // ── query_path ────────────────────────────────────────────────────
    // NEW — pathfind between two points.
    // Research: Use NavMesh.GetPath() or NavMesh.PathBuilder.

    private static object QueryPath( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "query_path" );

        var fromStr = HandlerBase.GetString( args, "from" );
        var toStr = HandlerBase.GetString( args, "to" );

        if ( string.IsNullOrEmpty( fromStr ) || string.IsNullOrEmpty( toStr ) )
            return HandlerBase.Error( "Missing required 'from' and 'to' parameters (as 'x,y,z').", "query_path" );

        var from = HandlerBase.ParseVector3( fromStr );
        var to = HandlerBase.ParseVector3( toStr );

        try
        {
            var navMesh = scene.NavMesh;
            if ( navMesh == null )
                return HandlerBase.Error( "No NavMesh available in the scene. Run navmesh.generate first.", "query_path" );

            var result = navMesh.CalculatePath( new Sandbox.Navigation.CalculatePathRequest
            {
                Start = from,
                Target = to
            } );

            if ( !result.IsValid || result.Points == null || result.Points.Count == 0 )
                return HandlerBase.Error( "No path found between the specified points.", "query_path" );

            var points = result.Points;
            float totalDist = 0;
            for ( int i = 1; i < points.Count; i++ )
                totalDist += Vector3.DistanceBetween( points[i - 1].Position, points[i].Position );

            return HandlerBase.Success( new
            {
                message = $"Path found: {points.Count} waypoints, {totalDist:F1} units.",
                waypoints = points.Select( p => HandlerBase.V3( p.Position ) ),
                distance = MathF.Round( totalDist, 1 ),
                waypoint_count = points.Count,
                status = result.Status.ToString()
            } );
        }
        catch ( NullReferenceException )
        {
            return HandlerBase.Error( "No navmesh data available. Ensure the scene has walkable geometry (e.g. a ground mesh with collider) and run navmesh.generate first.", "query_path" );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Path query failed: {ex.Message}", "query_path" );
        }
    }

    // ── Per-tile generation ───────────────────────────────────────────
    //
    // Ported from upstream Arenula 087f048 (2026-08-05). Whole-navmesh
    // 'generate' rebuilds everything, which is minutes on a large scene. These
    // three let you regenerate only the region you changed, and to turn off
    // generate-on-load entirely so a scene opens fast and you build tiles on
    // demand. All use the Request* (fire-and-forget, incremental over frames)
    // variants rather than the Task-returning ones, so no call blocks the editor.

    private static object GenerateTile( JsonElement args )
    {
        var navMesh = RequireNavMesh( "generate_tile", out var error );
        if ( navMesh == null ) return error;

        if ( TryGetBounds( args, out var bounds ) )
        {
            navMesh.RequestTilesGeneration( bounds );
            return HandlerBase.Confirm(
                $"Queued navmesh tile generation for bounds ({bounds.Mins} → {bounds.Maxs}). " +
                "Generation is incremental over subsequent frames — poll get_status for is_generating." );
        }

        var posStr = HandlerBase.GetString( args, "position" );
        if ( string.IsNullOrEmpty( posStr ) )
            return HandlerBase.Error(
                "Provide 'position' (x,y,z) or both 'bounds_min' and 'bounds_max' for the tiles to generate.",
                "generate_tile" );

        var position = HandlerBase.ParseVector3( posStr );
        navMesh.RequestTileGeneration( position );
        return HandlerBase.Confirm(
            $"Queued navmesh tile generation at {position}. " +
            "Generation is incremental over subsequent frames — poll get_status for is_generating." );
    }

    private static object UnloadTile( JsonElement args )
    {
        var navMesh = RequireNavMesh( "unload_tile", out var error );
        if ( navMesh == null ) return error;

        if ( TryGetBounds( args, out var bounds ) )
        {
            navMesh.UnloadTiles( bounds );
            return HandlerBase.Confirm( $"Unloaded navmesh tiles in bounds ({bounds.Mins} → {bounds.Maxs})." );
        }

        var posStr = HandlerBase.GetString( args, "position" );
        if ( string.IsNullOrEmpty( posStr ) )
            return HandlerBase.Error(
                "Provide 'position' (x,y,z) or both 'bounds_min' and 'bounds_max' for the tiles to unload.",
                "unload_tile" );

        var position = HandlerBase.ParseVector3( posStr );
        navMesh.UnloadTile( position );
        return HandlerBase.Confirm( $"Unloaded navmesh tile at {position}." );
    }

    private static object SetDeferGeneration( JsonElement args )
    {
        var navMesh = RequireNavMesh( "set_defer_generation", out var error );
        if ( navMesh == null ) return error;

        if ( !HandlerBase.TryGetBool( args, "enabled", out var enabled ) )
            return HandlerBase.Error(
                "Missing required 'enabled' parameter (true or false).", "set_defer_generation" );

        navMesh.DeferGeneration = enabled;

        return HandlerBase.Confirm( enabled
            ? "NavMesh DeferGeneration is ON — tiles will NOT auto-generate on scene load. Build them on demand with generate_tile."
            : "NavMesh DeferGeneration is OFF — tiles auto-generate on scene load." );
    }

    // ── Shared helpers for the tile actions ───────────────────────────

    /// <summary>
    /// Resolve the scene's NavMesh, or hand back the error response to return.
    /// Returns null when unavailable, with <paramref name="error"/> set.
    /// </summary>
    private static Sandbox.Navigation.NavMesh RequireNavMesh( string action, out object error )
    {
        error = null;
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
        {
            error = HandlerBase.Error( "No active scene.", action );
            return null;
        }

        var navMesh = scene.NavMesh;
        if ( navMesh == null )
        {
            error = HandlerBase.Error( "No NavMesh in the scene.", action,
                "Add a NavMesh to the scene, or run navmesh.generate first." );
            return null;
        }

        return navMesh;
    }

    /// <summary>True when the caller supplied BOTH bounds corners.</summary>
    private static bool TryGetBounds( JsonElement args, out BBox bounds )
    {
        bounds = default;
        var minStr = HandlerBase.GetString( args, "bounds_min" );
        var maxStr = HandlerBase.GetString( args, "bounds_max" );
        if ( string.IsNullOrEmpty( minStr ) || string.IsNullOrEmpty( maxStr ) ) return false;

        bounds = new BBox( HandlerBase.ParseVector3( minStr ), HandlerBase.ParseVector3( maxStr ) );
        return true;
    }
}
