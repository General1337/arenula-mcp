using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sandbox;

namespace Arenula;

/// <summary>
/// search tool: advanced scene search beyond name/tag/component matching.
/// Provides regex search, property-value queries, spatial context, and reference graph.
/// All actions are read-only.
/// </summary>
internal static class SearchHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "by_pattern"        => ByPattern( args ),
                "by_property_value" => ByPropertyValue( args ),
                "get_neighborhood"  => GetNeighborhood( args ),
                "get_related"       => GetRelated( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'search'.", action,
                    "Valid actions: by_pattern, by_property_value, get_neighborhood, get_related" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── by_pattern ───────────────────────────────────────────────────

    private static object ByPattern( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "by_pattern" );

        var pattern = HandlerBase.GetString( args, "pattern" );
        if ( string.IsNullOrEmpty( pattern ) )
            return HandlerBase.Error( "Missing required 'pattern' parameter.", "by_pattern" );

        var matchPath = HandlerBase.GetBool( args, "match_path", false );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 50 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 200 ) limit = 200;

        Regex regex;
        try
        {
            regex = new Regex( pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds( 500 ) );
        }
        catch ( ArgumentException ex )
        {
            return HandlerBase.Error( $"Invalid regex pattern: {ex.Message}", "by_pattern" );
        }

        var matches = new List<object>();
        int skipped = 0;

        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            var target = matchPath ? SceneHelpers.GetObjectPath( go ) : go.Name;

            try
            {
                if ( !regex.IsMatch( target ) ) continue;
            }
            catch ( RegexMatchTimeoutException )
            {
                return HandlerBase.Error( "Regex match timed out. Simplify the pattern.", "by_pattern" );
            }

            if ( skipped < offset ) { skipped++; continue; }
            if ( matches.Count >= limit ) break;

            matches.Add( new
            {
                id = go.Id.ToString(),
                name = go.Name,
                path = SceneHelpers.GetObjectPath( go ),
                enabled = go.Enabled,
                component_count = go.Components.GetAll().Count()
            } );
        }

        return HandlerBase.Success( new
        {
            pattern,
            matchPath,
            matchCount = matches.Count,
            offset,
            matches
        } );
    }

    // ── by_property_value ────────────────────────────────────────────

    private static object ByPropertyValue( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "by_property_value" );

        var componentType = HandlerBase.GetString( args, "component_type" );
        if ( string.IsNullOrEmpty( componentType ) )
            return HandlerBase.Error( "Missing required 'component_type' parameter.", "by_property_value" );

        var propertyName = HandlerBase.GetString( args, "property" );
        if ( string.IsNullOrEmpty( propertyName ) )
            return HandlerBase.Error( "Missing required 'property' parameter.", "by_property_value" );

        var targetValue = HandlerBase.GetString( args, "value" );
        if ( targetValue == null )
            return HandlerBase.Error( "Missing required 'value' parameter.", "by_property_value" );

        var op = HandlerBase.GetString( args, "operator" ) ?? "eq";
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 50 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 200 ) limit = 200;

        var matches = new List<object>();
        int skipped = 0;
        int scanned = 0;

        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            if ( scanned++ > 5000 ) break; // safety cap

            foreach ( var comp in go.Components.GetAll() )
            {
                if ( !comp.GetType().Name.Equals( componentType, StringComparison.OrdinalIgnoreCase ) )
                    continue;

                var prop = comp.GetType().GetProperty( propertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );
                if ( prop == null ) continue;

                object currentVal;
                try { currentVal = prop.GetValue( comp ); }
                catch { continue; }

                if ( !CompareValues( currentVal, targetValue, op ) ) continue;

                if ( skipped < offset ) { skipped++; continue; }
                if ( matches.Count >= limit ) break;

                matches.Add( new
                {
                    id = go.Id.ToString(),
                    name = go.Name,
                    path = SceneHelpers.GetObjectPath( go ),
                    component_id = comp.Id.ToString(),
                    component_type = comp.GetType().Name,
                    property = propertyName,
                    current_value = currentVal?.ToString()
                } );
            }

            if ( matches.Count >= limit ) break;
        }

        return HandlerBase.Success( new
        {
            component_type = componentType,
            property = propertyName,
            @operator = op,
            target_value = targetValue,
            matchCount = matches.Count,
            objectsScanned = scanned,
            matches
        } );
    }

    private static bool CompareValues( object current, string target, string op )
    {
        if ( current == null ) return target == "null" || string.IsNullOrEmpty( target );

        var currentStr = current.ToString();

        switch ( op )
        {
            case "eq":
                return currentStr.Equals( target, StringComparison.OrdinalIgnoreCase );

            case "contains":
                return currentStr.IndexOf( target, StringComparison.OrdinalIgnoreCase ) >= 0;

            case "starts_with":
                return currentStr.StartsWith( target, StringComparison.OrdinalIgnoreCase );

            case "gt":
            case "lt":
            case "gte":
            case "lte":
                if ( float.TryParse( currentStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var currentNum ) &&
                     float.TryParse( target, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var targetNum ) )
                {
                    return op switch
                    {
                        "gt"  => currentNum > targetNum,
                        "lt"  => currentNum < targetNum,
                        "gte" => currentNum >= targetNum,
                        "lte" => currentNum <= targetNum,
                        _     => false
                    };
                }
                return false;

            default:
                return currentStr.Equals( target, StringComparison.OrdinalIgnoreCase );
        }
    }

    // ── get_neighborhood ─────────────────────────────────────────────

    private static object GetNeighborhood( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "get_neighborhood" );

        var radius = HandlerBase.GetFloat( args, "radius", 500f );
        var limit = HandlerBase.GetInt( args, "limit", 30 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 100 ) limit = 100;

        // Get center position from id or position param
        Vector3 center;
        object targetDetail = null;

        var id = HandlerBase.GetString( args, "id" );
        var posStr = HandlerBase.GetString( args, "position" );

        if ( !string.IsNullOrEmpty( id ) )
        {
            var target = SceneHelpers.FindByIdOrThrow( scene, id, "get_neighborhood" );
            center = target.WorldPosition;
            targetDetail = SceneHelpers.BuildDetail( target );
        }
        else if ( !string.IsNullOrEmpty( posStr ) )
        {
            center = HandlerBase.ParseVector3( posStr );
        }
        else
        {
            return HandlerBase.Error( "Provide 'id' (GameObject GUID) or 'position' (x,y,z).", "get_neighborhood" );
        }

        // Find nearby objects
        var nearby = new List<(GameObject go, float dist)>();
        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            if ( !string.IsNullOrEmpty( id ) && go.Id.ToString() == id ) continue;

            var dist = ( go.WorldPosition - center ).Length;
            if ( dist <= radius )
                nearby.Add( (go, dist) );
        }

        nearby.Sort( ( a, b ) => a.dist.CompareTo( b.dist ) );
        var limited = nearby.Take( limit ).ToList();

        var neighbors = limited.Select( n =>
        {
            var delta = n.go.WorldPosition - center;
            return new
            {
                id = n.go.Id.ToString(),
                name = n.go.Name,
                path = SceneHelpers.GetObjectPath( n.go ),
                distance = MathF.Round( n.dist, 2 ),
                direction = GetCardinalDirection( delta ),
                position = HandlerBase.V3( n.go.WorldPosition ),
                enabled = n.go.Enabled,
                components = n.go.Components.GetAll().Select( c => c.GetType().Name ).ToList()
            };
        } ).ToList();

        return HandlerBase.Success( new
        {
            center = HandlerBase.V3( center ),
            radius,
            target = targetDetail,
            neighborCount = neighbors.Count,
            totalInRadius = nearby.Count,
            neighbors
        } );
    }

    private static string GetCardinalDirection( Vector3 delta )
    {
        if ( delta.Length < 0.1f ) return "same_position";

        var parts = new List<string>();

        // S&Box uses Z-up, X-forward, Y-left coordinate system
        if ( delta.z > 10 ) parts.Add( "above" );
        else if ( delta.z < -10 ) parts.Add( "below" );

        var horizontal = new Vector2( delta.x, delta.y );
        if ( horizontal.Length > 10 )
        {
            var angle = MathF.Atan2( delta.y, delta.x ) * ( 180f / MathF.PI );
            if ( angle >= -22.5f && angle < 22.5f ) parts.Add( "forward" );
            else if ( angle >= 22.5f && angle < 67.5f ) parts.Add( "forward-left" );
            else if ( angle >= 67.5f && angle < 112.5f ) parts.Add( "left" );
            else if ( angle >= 112.5f && angle < 157.5f ) parts.Add( "back-left" );
            else if ( angle >= 157.5f || angle < -157.5f ) parts.Add( "back" );
            else if ( angle >= -157.5f && angle < -112.5f ) parts.Add( "back-right" );
            else if ( angle >= -112.5f && angle < -67.5f ) parts.Add( "right" );
            else if ( angle >= -67.5f && angle < -22.5f ) parts.Add( "forward-right" );
        }

        return parts.Count > 0 ? string.Join( "-", parts ) : "nearby";
    }

    // ── get_related ──────────────────────────────────────────────────

    private static object GetRelated( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "get_related" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "get_related" );

        var target = SceneHelpers.FindByIdOrThrow( scene, id, "get_related" );
        var targetGuid = target.Id;
        var targetComponentIds = target.Components.GetAll().Select( c => c.Id ).ToHashSet();

        // What this object references
        var referencesTo = new List<object>();
        foreach ( var comp in target.Components.GetAll() )
        {
            foreach ( var prop in comp.GetType().GetProperties( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance ) )
            {
                try
                {
                    if ( typeof( GameObject ).IsAssignableFrom( prop.PropertyType ) )
                    {
                        var refGo = prop.GetValue( comp ) as GameObject;
                        if ( refGo != null && refGo.Id != targetGuid )
                        {
                            referencesTo.Add( new
                            {
                                via_component = comp.GetType().Name,
                                via_property = prop.Name,
                                target_id = refGo.Id.ToString(),
                                target_name = refGo.Name,
                                target_type = "GameObject"
                            } );
                        }
                    }
                    else if ( typeof( Component ).IsAssignableFrom( prop.PropertyType ) )
                    {
                        var refComp = prop.GetValue( comp ) as Component;
                        if ( refComp != null && !targetComponentIds.Contains( refComp.Id ) )
                        {
                            referencesTo.Add( new
                            {
                                via_component = comp.GetType().Name,
                                via_property = prop.Name,
                                target_id = refComp.GameObject.Id.ToString(),
                                target_name = $"{refComp.GameObject.Name}.{refComp.GetType().Name}",
                                target_type = "Component"
                            } );
                        }
                    }
                }
                catch { }
            }
        }

        // What references this object
        var referencedBy = new List<object>();
        int scanned = 0;

        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            if ( go.Id == targetGuid ) continue;
            if ( scanned++ > 1000 ) break; // safety cap

            foreach ( var comp in go.Components.GetAll() )
            {
                foreach ( var prop in comp.GetType().GetProperties( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance ) )
                {
                    try
                    {
                        if ( typeof( GameObject ).IsAssignableFrom( prop.PropertyType ) )
                        {
                            var refGo = prop.GetValue( comp ) as GameObject;
                            if ( refGo != null && refGo.Id == targetGuid )
                            {
                                referencedBy.Add( new
                                {
                                    from_id = go.Id.ToString(),
                                    from_name = go.Name,
                                    via_component = comp.GetType().Name,
                                    via_property = prop.Name,
                                    reference_type = "GameObject"
                                } );
                            }
                        }
                        else if ( typeof( Component ).IsAssignableFrom( prop.PropertyType ) )
                        {
                            var refComp = prop.GetValue( comp ) as Component;
                            if ( refComp != null && targetComponentIds.Contains( refComp.Id ) )
                            {
                                referencedBy.Add( new
                                {
                                    from_id = go.Id.ToString(),
                                    from_name = go.Name,
                                    via_component = comp.GetType().Name,
                                    via_property = prop.Name,
                                    reference_type = "Component"
                                } );
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return HandlerBase.Success( new
        {
            target_id = target.Id.ToString(),
            target_name = target.Name,
            references_to = referencesTo,
            referenced_by = referencedBy,
            objects_scanned = scanned,
            message = $"'{target.Name}' references {referencesTo.Count} object(s) and is referenced by {referencedBy.Count} object(s)."
        } );
    }
}
