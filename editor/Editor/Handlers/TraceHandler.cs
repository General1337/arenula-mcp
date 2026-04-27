using System;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// trace tool: ray, box, ground.
/// Scene trace (raycast) operations for finding ground level,
/// checking clearance, and detecting geometry at positions.
/// </summary>
internal static class TraceHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "ray"    => Ray( args ),
                "box"    => Box( args ),
                "ground" => Ground( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: ray, box, ground" )
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

        var trace = scene.Trace.Ray( from, to );
        trace = ApplyExcludeTags( trace, args );
        var tr = trace.Run();

        return HandlerBase.Success( new
        {
            hit = tr.Hit,
            position = HandlerBase.V3( tr.EndPosition ),
            normal = HandlerBase.V3( tr.Normal ),
            distance = MathF.Round( tr.Distance, 2 ),
            hit_object_id = tr.GameObject?.Id.ToString(),
            hit_object_name = tr.GameObject?.Name,
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

        var trace = scene.Trace.Box( bbox, position, position );
        trace = ApplyExcludeTags( trace, args );
        var tr = trace.Run();

        return HandlerBase.Success( new
        {
            hit = tr.Hit,
            hit_object_name = tr.GameObject?.Name,
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

        var trace = scene.Trace.Ray( from, to );
        trace = ApplyExcludeTags( trace, args );
        var tr = trace.Run();

        return HandlerBase.Success( new
        {
            found = tr.Hit,
            position = HandlerBase.V3( tr.EndPosition ),
            normal = HandlerBase.V3( tr.Normal ),
            surface_material = tr.Surface?.ResourceName,
        } );
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static SceneTrace ApplyExcludeTags( SceneTrace trace, JsonElement args )
    {
        var tagsStr = HandlerBase.GetString( args, "tags" );
        if ( !string.IsNullOrEmpty( tagsStr ) )
        {
            var tags = tagsStr.Split( ',' );
            foreach ( var tag in tags )
            {
                var trimmed = tag.Trim();
                if ( !string.IsNullOrEmpty( trimmed ) )
                    trace = trace.WithoutTags( trimmed );
            }
        }
        return trace;
    }
}
