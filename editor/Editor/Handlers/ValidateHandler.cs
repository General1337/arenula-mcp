using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// validate tool: run diagnostics on scene objects, components, prefabs, and references.
/// All actions are read-only — inspects scene health without modifying anything.
/// </summary>
internal static class ValidateHandler
{
    private sealed class ValidationIssue
    {
        public string severity;
        public string category;
        public string object_id;
        public string object_name;
        public string object_path;
        public string component_type;
        public string component_id;
        public string property;
        public string message;
        public string suggestion;
    }

    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "scene"      => ValidateScene( args ),
                "component"  => ValidateComponent( args ),
                "prefab"     => ValidatePrefab( args ),
                "references" => ValidateReferences( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'validate'.", action,
                    "Valid actions: scene, component, prefab, references" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── scene ────────────────────────────────────────────────────────

    private static object ValidateScene( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "scene" );

        var severityFilter = HandlerBase.GetString( args, "severity_filter" );
        var issues = new List<ValidationIssue>();

        var activeCameras = new List<string>();

        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            var components = go.Components.GetAll().ToList();

            // Check: object with no components (potential empty container)
            if ( components.Count == 0 && go.Children.Count == 0 )
            {
                AddIssue( issues, "info", "empty_object", go,
                    $"'{go.Name}' has no components and no children.",
                    "Consider removing this empty object or adding components." );
            }

            // Check: disabled object with enabled children
            if ( !go.Enabled )
            {
                var enabledChildren = go.Children.Where( c => c.Enabled ).ToList();
                if ( enabledChildren.Count > 0 )
                {
                    AddIssue( issues, "warning", "disabled_parent", go,
                        $"'{go.Name}' is disabled but has {enabledChildren.Count} enabled children (they won't be active).",
                        "Enable the parent or disable the children." );
                }
            }

            // Check: duplicate component types
            var typeCounts = components.GroupBy( c => c.GetType().Name ).Where( g => g.Count() > 1 ).ToList();
            foreach ( var dup in typeCounts )
            {
                AddIssue( issues, "warning", "duplicate_component", go,
                    $"'{go.Name}' has {dup.Count()} '{dup.Key}' components.",
                    "Multiple instances of the same component type is unusual — verify this is intentional." );
            }

            foreach ( var comp in components )
            {
                var typeName = comp.GetType().Name;

                // Check: ModelRenderer with null Model
                if ( typeName == "ModelRenderer" || typeName == "SkinnedModelRenderer" )
                {
                    var modelProp = comp.GetType().GetProperty( "Model",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );
                    if ( modelProp != null )
                    {
                        try
                        {
                            var model = modelProp.GetValue( comp );
                            if ( model == null )
                            {
                                AddIssue( issues, "warning", "null_model", go,
                                    $"'{go.Name}' has a {typeName} with no Model set — object will be invisible.",
                                    "Set the Model property to a valid .vmdl asset path.", typeName );
                            }
                        }
                        catch { }
                    }
                }

                // Check: Collider without Rigidbody (for physics objects)
                if ( typeName.Contains( "Collider" ) && !typeName.Contains( "Character" ) )
                {
                    var hasRigidbody = components.Any( c =>
                        c.GetType().Name == "Rigidbody" || c.GetType().Name == "CharacterController" );
                    if ( !hasRigidbody )
                    {
                        AddIssue( issues, "info", "collider_no_rigidbody", go,
                            $"'{go.Name}' has a {typeName} but no Rigidbody. It will act as static geometry.",
                            "Add a Rigidbody if this object should be dynamic.", typeName );
                    }
                }

                // Check: CameraComponent — track active cameras
                if ( typeName == "CameraComponent" && comp.Enabled && go.Enabled )
                {
                    activeCameras.Add( go.Name );
                }
            }
        }

        // Check: multiple active cameras
        if ( activeCameras.Count > 1 )
        {
            issues.Insert( 0, new ValidationIssue
            {
                severity = "warning",
                category = "multiple_cameras",
                object_name = string.Join( ", ", activeCameras ),
                message = $"Multiple active cameras detected: {string.Join( ", ", activeCameras )}. Only one will be used at runtime.",
                suggestion = "Disable extra cameras or set them up for specific use cases."
            } );
        }

        // Filter by severity
        if ( !string.IsNullOrEmpty( severityFilter ) )
        {
            issues = issues.Where( i =>
                string.Equals( i.severity, severityFilter, StringComparison.OrdinalIgnoreCase )
            ).ToList();
        }

        return HandlerBase.Success( new
        {
            issueCount = issues.Count,
            errorCount = issues.Count( i => i.severity == "error" ),
            warningCount = issues.Count( i => i.severity == "warning" ),
            infoCount = issues.Count( i => i.severity == "info" ),
            filter = severityFilter,
            issues
        } );
    }

    // ── component ────────────────────────────────────────────────────

    private static object ValidateComponent( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "component" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "component" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "component" );
        var componentId = HandlerBase.GetString( args, "component_id" );

        var issues = new List<ValidationIssue>();
        var components = go.Components.GetAll().ToList();

        if ( !string.IsNullOrEmpty( componentId ) )
        {
            var comp = components.FirstOrDefault( c => c.Id.ToString() == componentId );
            if ( comp == null )
                return HandlerBase.Error( $"Component '{componentId}' not found on '{go.Name}'.", "component" );
            ValidateComponentProperties( go, comp, issues );
        }
        else
        {
            foreach ( var comp in components )
                ValidateComponentProperties( go, comp, issues );
        }

        return HandlerBase.Success( new
        {
            object_id = go.Id.ToString(),
            object_name = go.Name,
            issueCount = issues.Count,
            issues
        } );
    }

    private static void ValidateComponentProperties( GameObject go, Component comp, List<ValidationIssue> issues )
    {
        var compType = comp.GetType();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        foreach ( var prop in compType.GetProperties( flags ) )
        {
            if ( !prop.CanRead ) continue;

            // Check for [Property] attribute — these are serialized/configured properties
            var hasPropertyAttr = prop.GetCustomAttributes( typeof( Sandbox.PropertyAttribute ), true ).Length > 0;
            if ( !hasPropertyAttr ) continue;

            object val;
            try { val = prop.GetValue( comp ); }
            catch { continue; }

            // Null reference on a non-nullable reference-type property
            if ( val == null && prop.PropertyType.IsClass && prop.PropertyType != typeof( string ) )
            {
                AddIssue( issues, "warning", "null_property", go,
                    $"'{compType.Name}.{prop.Name}' is null (type: {prop.PropertyType.Name}).",
                    $"Set '{prop.Name}' to a valid value.", compType.Name, comp.Id.ToString(), prop.Name );
            }

            // Asset path properties that point to non-existent assets
            if ( val is string strVal && !string.IsNullOrEmpty( strVal ) )
            {
                if ( prop.PropertyType == typeof( string ) && ( prop.Name.Contains( "Path" ) || prop.Name.Contains( "Asset" ) ) )
                {
                    var asset = AssetSystem.FindByPath( strVal );
                    if ( asset == null )
                    {
                        AddIssue( issues, "warning", "broken_asset_path", go,
                            $"'{compType.Name}.{prop.Name}' references '{strVal}' which was not found in assets.",
                            "Check the asset path or use asset_query.search to find the correct path.", compType.Name, comp.Id.ToString(), prop.Name );
                    }
                }
            }
        }
    }

    // ── prefab ───────────────────────────────────────────────────────

    private static object ValidatePrefab( JsonElement args )
    {
        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "prefab" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Prefab not found: {path}", "prefab" );

        var issues = new List<ValidationIssue>();

        // Try to read the raw prefab JSON
        try
        {
            var prefabFile = ResourceLibrary.Get<PrefabFile>( path );
            if ( prefabFile == null )
            {
                issues.Add( new ValidationIssue
                {
                    severity = "error",
                    category = "prefab_load_failed",
                    message = $"Could not load prefab file: {path}",
                    suggestion = "The prefab file may be corrupted. Try recreating it."
                } );
            }
        }
        catch ( Exception ex )
        {
            issues.Add( new ValidationIssue
            {
                severity = "error",
                category = "prefab_parse_error",
                message = $"Error loading prefab: {ex.Message}",
                suggestion = "Check the prefab JSON for syntax errors."
            } );
        }

        // Validate component type references in the prefab JSON (read from disk)
        try
        {
            if ( !string.IsNullOrEmpty( asset.AbsolutePath ) && File.Exists( asset.AbsolutePath ) )
            {
                var jsonContent = File.ReadAllText( asset.AbsolutePath );
                if ( !string.IsNullOrEmpty( jsonContent ) )
                {
                    using var doc = JsonDocument.Parse( jsonContent );
                    ValidatePrefabNode( doc.RootElement, path, issues );
                }
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            path,
            issueCount = issues.Count,
            issues,
            message = issues.Count == 0 ? "Prefab is valid." : $"Found {issues.Count} issue(s)."
        } );
    }

    private static void ValidatePrefabNode( JsonElement node, string context, List<ValidationIssue> issues )
    {
        // Check component type references
        if ( node.TryGetProperty( "__type", out var typeProp ) )
        {
            var typeName = typeProp.GetString();
            if ( !string.IsNullOrEmpty( typeName ) )
            {
                var td = TypeLibrary.GetType( typeName );
                if ( td == null )
                {
                    issues.Add( new ValidationIssue
                    {
                        severity = "error",
                        category = "broken_script_reference",
                        message = $"Component type '{typeName}' not found in TypeLibrary.",
                        suggestion = "The script may have been renamed or deleted. Update the prefab."
                    } );
                }
            }
        }

        // Recurse into child properties
        if ( node.ValueKind == JsonValueKind.Object )
        {
            foreach ( var prop in node.EnumerateObject() )
            {
                if ( prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array )
                    ValidatePrefabNode( prop.Value, context, issues );
            }
        }
        else if ( node.ValueKind == JsonValueKind.Array )
        {
            foreach ( var item in node.EnumerateArray() )
            {
                if ( item.ValueKind == JsonValueKind.Object || item.ValueKind == JsonValueKind.Array )
                    ValidatePrefabNode( item, context, issues );
            }
        }
    }

    // ── references ───────────────────────────────────────────────────

    private static object ValidateReferences( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "references" );

        var issues = new List<ValidationIssue>();
        int scanned = 0;

        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
        {
            if ( scanned++ > 2000 ) break;

            foreach ( var comp in go.Components.GetAll() )
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                foreach ( var prop in comp.GetType().GetProperties( flags ) )
                {
                    if ( !prop.CanRead ) continue;

                    try
                    {
                        // Check GameObject references
                        if ( typeof( GameObject ).IsAssignableFrom( prop.PropertyType ) )
                        {
                            var refGo = prop.GetValue( comp ) as GameObject;
                            if ( refGo != null && !refGo.IsValid )
                            {
                                AddIssue( issues, "error", "broken_gameobject_ref", go,
                                    $"'{comp.GetType().Name}.{prop.Name}' references a destroyed GameObject.",
                                    "Clear the reference or set it to a valid object.",
                                    comp.GetType().Name, comp.Id.ToString(), prop.Name );
                            }
                        }

                        // Check Component references
                        if ( typeof( Component ).IsAssignableFrom( prop.PropertyType ) && prop.PropertyType != typeof( Component ) )
                        {
                            var refComp = prop.GetValue( comp ) as Component;
                            if ( refComp != null && !refComp.IsValid )
                            {
                                AddIssue( issues, "error", "broken_component_ref", go,
                                    $"'{comp.GetType().Name}.{prop.Name}' references a destroyed Component.",
                                    "Clear the reference or set it to a valid component.",
                                    comp.GetType().Name, comp.Id.ToString(), prop.Name );
                            }
                        }

                        // Check Model references
                        if ( prop.PropertyType == typeof( Model ) )
                        {
                            var model = prop.GetValue( comp ) as Model;
                            // Model.IsError or null check
                            if ( model != null )
                            {
                                // Model loaded but may point to missing asset — check via name
                                var modelName = model.Name;
                                if ( string.IsNullOrEmpty( modelName ) || modelName == "error" )
                                {
                                    AddIssue( issues, "error", "broken_model_ref", go,
                                        $"'{comp.GetType().Name}.{prop.Name}' references a model that failed to load.",
                                        "Check the model path or use asset_query.search to find the correct asset.",
                                        comp.GetType().Name, comp.Id.ToString(), prop.Name );
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return HandlerBase.Success( new
        {
            objectsScanned = scanned,
            issueCount = issues.Count,
            issues,
            message = issues.Count == 0
                ? $"No broken references found (scanned {scanned} objects)."
                : $"Found {issues.Count} broken reference(s) across {scanned} objects."
        } );
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static void AddIssue( List<ValidationIssue> issues, string severity, string category, GameObject go,
        string message, string suggestion, string componentType = null, string componentId = null, string property = null )
    {
        issues.Add( new ValidationIssue
        {
            severity = severity,
            category = category,
            object_id = go.Id.ToString(),
            object_name = go.Name,
            object_path = SceneHelpers.GetObjectPath( go ),
            component_type = componentType,
            component_id = componentId,
            property = property,
            message = message,
            suggestion = suggestion
        } );
    }
}
