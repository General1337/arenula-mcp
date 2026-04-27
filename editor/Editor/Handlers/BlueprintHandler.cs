using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// blueprint tool: create complex object hierarchies from a declarative JSON spec.
/// Reduces sequential MCP calls to a single operation for setting up multi-object,
/// multi-component configurations (NPCs, vehicles, interactables, etc.).
/// </summary>
internal static class BlueprintHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create"   => Create( args ),
                "validate" => Validate( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'blueprint'.", action,
                    "Valid actions: create, validate" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── create ───────────────────────────────────────────────────────

    private static object Create( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create" );

        var specJson = HandlerBase.GetString( args, "spec" );
        if ( string.IsNullOrEmpty( specJson ) )
            return HandlerBase.Error( "Missing required 'spec' parameter.", "create" );

        var dryRun = HandlerBase.GetBool( args, "dry_run", false );

        JsonElement spec;
        try
        {
            using var doc = JsonDocument.Parse( specJson );
            spec = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Error( $"Failed to parse 'spec' JSON: {ex.Message}", "create" );
        }

        // Resolve optional parent
        GameObject parent = null;
        var parentId = HandlerBase.GetString( args, "parent_id" );
        if ( !string.IsNullOrEmpty( parentId ) && parentId != "null" && parentId != "root" )
            parent = SceneHelpers.FindByIdOrThrow( scene, parentId, "create" );

        // Parse optional root position/rotation overrides
        var posStr = HandlerBase.GetString( args, "position" );
        var rotStr = HandlerBase.GetString( args, "rotation" );

        if ( dryRun )
            return ValidateSpec( spec );

        // Track all created objects for rollback on failure
        var createdObjects = new List<GameObject>();
        var createdInfo = new List<object>();
        var componentInfo = new List<object>();

        try
        {
            var rootGo = CreateFromSpec( scene, spec, parent, createdObjects, createdInfo, componentInfo );

            // Apply root position/rotation overrides if provided
            if ( !string.IsNullOrEmpty( posStr ) )
                rootGo.WorldPosition = HandlerBase.ParseVector3( posStr );
            if ( !string.IsNullOrEmpty( rotStr ) )
            {
                var r = HandlerBase.ParseVector3( rotStr );
                rootGo.WorldRotation = Rotation.From( r.x, r.y, r.z );
            }

            return HandlerBase.Success( new
            {
                root = new { id = rootGo.Id.ToString(), name = rootGo.Name },
                created_objects = createdInfo,
                created_components = componentInfo,
                message = $"Created blueprint '{rootGo.Name}' with {createdInfo.Count} object(s) and {componentInfo.Count} component(s)."
            } );
        }
        catch ( Exception ex )
        {
            // Rollback: destroy all created objects on failure
            foreach ( var go in createdObjects )
            {
                try { go.Destroy(); } catch { }
            }

            return HandlerBase.Error( $"Blueprint creation failed (rolled back {createdObjects.Count} objects): {ex.Message}", "create" );
        }
    }

    /// <summary>
    /// Recursively create a GameObject hierarchy from a spec node.
    /// </summary>
    private static GameObject CreateFromSpec(
        Scene scene,
        JsonElement node,
        GameObject parent,
        List<GameObject> createdObjects,
        List<object> createdInfo,
        List<object> componentInfo )
    {
        var name = "Blueprint Object";
        if ( node.TryGetProperty( "name", out var nameProp ) )
            name = nameProp.GetString() ?? name;

        var go = scene.CreateObject();
        go.Name = name;
        createdObjects.Add( go );

        if ( parent != null )
            go.SetParent( parent );

        // Tags
        if ( node.TryGetProperty( "tags", out var tagsProp ) && tagsProp.ValueKind == JsonValueKind.Array )
        {
            foreach ( var tag in tagsProp.EnumerateArray() )
            {
                var tagStr = tag.GetString();
                if ( !string.IsNullOrEmpty( tagStr ) )
                    go.Tags.Add( tagStr );
            }
        }
        else if ( node.TryGetProperty( "tags", out var tagsStr ) && tagsStr.ValueKind == JsonValueKind.String )
        {
            foreach ( var tag in tagsStr.GetString().Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
                go.Tags.Add( tag );
        }

        // Transform (local relative to parent)
        if ( node.TryGetProperty( "position", out var posProp ) )
        {
            var pos = posProp.ValueKind == JsonValueKind.String
                ? HandlerBase.ParseVector3( posProp.GetString() )
                : Vector3.Zero;
            if ( parent != null )
                go.LocalPosition = pos;
            else
                go.WorldPosition = pos;
        }

        if ( node.TryGetProperty( "rotation", out var rotProp ) )
        {
            var angles = rotProp.ValueKind == JsonValueKind.String
                ? HandlerBase.ParseVector3( rotProp.GetString() )
                : Vector3.Zero;
            if ( parent != null )
                go.LocalRotation = Rotation.From( angles.x, angles.y, angles.z );
            else
                go.WorldRotation = Rotation.From( angles.x, angles.y, angles.z );
        }

        if ( node.TryGetProperty( "scale", out var scaleProp ) )
        {
            if ( scaleProp.ValueKind == JsonValueKind.String )
                go.LocalScale = HandlerBase.ParseVector3( scaleProp.GetString() );
            else if ( scaleProp.ValueKind == JsonValueKind.Number )
            {
                var s = scaleProp.GetSingle();
                go.LocalScale = new Vector3( s, s, s );
            }
        }

        // Enabled
        if ( node.TryGetProperty( "enabled", out var enabledProp ) )
            go.Enabled = enabledProp.GetBoolean();

        createdInfo.Add( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            path = SceneHelpers.GetObjectPath( go )
        } );

        // Components
        if ( node.TryGetProperty( "components", out var compArray ) && compArray.ValueKind == JsonValueKind.Array )
        {
            foreach ( var compSpec in compArray.EnumerateArray() )
            {
                if ( !compSpec.TryGetProperty( "type", out var typeProp ) )
                    throw new ArgumentException( $"Component spec on '{name}' is missing 'type' field." );

                var typeName = typeProp.GetString();
                var td = ComponentHandler.FindComponentTypeDescription( typeName );
                if ( td == null )
                    throw new ArgumentException( $"Component type '{typeName}' not found on object '{name}'." );

                var comp = go.Components.Create( td );

                // Set properties if provided
                if ( compSpec.TryGetProperty( "properties", out var propObj ) && propObj.ValueKind == JsonValueKind.Object )
                {
                    foreach ( var kvp in propObj.EnumerateObject() )
                    {
                        var prop = comp.GetType().GetProperty( kvp.Name,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );

                        if ( prop == null || !prop.CanWrite ) continue;

                        try
                        {
                            var converted = ComponentHandler.ConvertJsonValue( kvp.Value, prop.PropertyType );
                            prop.SetValue( comp, converted );
                        }
                        catch
                        {
                            // Non-fatal: skip property that fails to convert
                        }
                    }
                }

                // Set enabled state if specified
                if ( compSpec.TryGetProperty( "enabled", out var compEnabled ) )
                    comp.Enabled = compEnabled.GetBoolean();

                componentInfo.Add( new
                {
                    object_id = go.Id.ToString(),
                    component_id = comp.Id.ToString(),
                    type = comp.GetType().Name
                } );
            }
        }

        // Recursively create children
        if ( node.TryGetProperty( "children", out var childArray ) && childArray.ValueKind == JsonValueKind.Array )
        {
            foreach ( var childSpec in childArray.EnumerateArray() )
            {
                CreateFromSpec( scene, childSpec, go, createdObjects, createdInfo, componentInfo );
            }
        }

        return go;
    }

    // ── validate ─────────────────────────────────────────────────────

    private static object Validate( JsonElement args )
    {
        var specJson = HandlerBase.GetString( args, "spec" );
        if ( string.IsNullOrEmpty( specJson ) )
            return HandlerBase.Error( "Missing required 'spec' parameter.", "validate" );

        JsonElement spec;
        try
        {
            using var doc = JsonDocument.Parse( specJson );
            spec = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Error( $"Failed to parse 'spec' JSON: {ex.Message}", "validate" );
        }

        return ValidateSpec( spec );
    }

    /// <summary>
    /// Walk the spec tree and validate all component types and property names
    /// without creating anything.
    /// </summary>
    private static object ValidateSpec( JsonElement spec )
    {
        var issues = new List<Dictionary<string, string>>();
        ValidateNode( spec, "", issues );

        var hasErrors = issues.Any( i => i["severity"] == "error" );

        return HandlerBase.Success( new
        {
            valid = !hasErrors,
            issueCount = issues.Count,
            issues,
            message = hasErrors
                ? $"Blueprint has {issues.Count} issue(s) — fix errors before creating."
                : issues.Count > 0
                    ? $"Blueprint is valid with {issues.Count} warning(s)."
                    : "Blueprint is valid."
        } );
    }

    private static void ValidateNode( JsonElement node, string path, List<Dictionary<string, string>> issues )
    {
        var name = "unnamed";
        if ( node.TryGetProperty( "name", out var nameProp ) )
            name = nameProp.GetString() ?? name;

        var nodePath = string.IsNullOrEmpty( path ) ? name : $"{path}/{name}";

        if ( node.TryGetProperty( "components", out var compArray ) && compArray.ValueKind == JsonValueKind.Array )
        {
            foreach ( var compSpec in compArray.EnumerateArray() )
            {
                if ( !compSpec.TryGetProperty( "type", out var typeProp ) )
                {
                    issues.Add( new Dictionary<string, string> { ["severity"] = "error", ["path"] = nodePath, ["message"] = "Component spec missing 'type' field." } );
                    continue;
                }

                var typeName = typeProp.GetString();
                var td = ComponentHandler.FindComponentTypeDescription( typeName );
                if ( td == null )
                {
                    var suggestions = TypeLibrary.GetTypes<Component>()
                        .Where( t => !t.IsAbstract && t.Name.IndexOf( typeName, StringComparison.OrdinalIgnoreCase ) >= 0 )
                        .Select( t => t.Name )
                        .Take( 5 )
                        .ToList();

                    var hint = suggestions.Count > 0 ? $" Similar: {string.Join( ", ", suggestions )}" : "";
                    issues.Add( new Dictionary<string, string> { ["severity"] = "error", ["path"] = nodePath, ["message"] = $"Component type '{typeName}' not found.{hint}" } );
                    continue;
                }

                if ( compSpec.TryGetProperty( "properties", out var propObj ) && propObj.ValueKind == JsonValueKind.Object )
                {
                    foreach ( var kvp in propObj.EnumerateObject() )
                    {
                        var prop = td.TargetType.GetProperty( kvp.Name,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );
                        if ( prop == null )
                            issues.Add( new Dictionary<string, string> { ["severity"] = "warning", ["path"] = nodePath, ["message"] = $"Property '{kvp.Name}' not found on '{typeName}'." } );
                        else if ( !prop.CanWrite )
                            issues.Add( new Dictionary<string, string> { ["severity"] = "warning", ["path"] = nodePath, ["message"] = $"Property '{kvp.Name}' on '{typeName}' is read-only." } );
                    }
                }
            }
        }

        // Recurse children
        if ( node.TryGetProperty( "children", out var childArray ) && childArray.ValueKind == JsonValueKind.Array )
        {
            foreach ( var child in childArray.EnumerateArray() )
                ValidateNode( child, nodePath, issues );
        }
    }
}
