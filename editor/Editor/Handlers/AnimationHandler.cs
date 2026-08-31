using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// animation tool: discover and configure animation parameters on SkinnedModelRenderer
/// and CitizenAnimationHelper components. Lists available parameters, sets values,
/// and enumerates model animation sequences.
/// </summary>
internal static class AnimationHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "list_parameters"     => ListParameters( args ),
                "set_parameters"      => SetParameters( args ),
                "get_model_animations" => GetModelAnimations( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'animation'.", action,
                    "Valid actions: list_parameters, set_parameters, get_model_animations" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── list_parameters ──────────────────────────────────────────────

    private static object ListParameters( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "list_parameters" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "list_parameters" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "list_parameters" );

        // Find SkinnedModelRenderer
        var smr = go.Components.GetAll()
            .FirstOrDefault( c => c.GetType().Name == "SkinnedModelRenderer" );

        if ( smr == null )
            return HandlerBase.Error(
                $"'{go.Name}' has no SkinnedModelRenderer component.",
                "list_parameters",
                "Add a SkinnedModelRenderer component first." );

        var parameters = new List<object>();

        // 1. Get SkinnedModelRenderer settable properties
        var smrProps = smr.GetType().GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );

        foreach ( var prop in smrProps )
        {
            if ( !prop.CanWrite ) continue;
            var typeName = prop.PropertyType.Name;
            // Filter to animation-relevant properties
            if ( typeName != "Single" && typeName != "Boolean" && typeName != "Int32"
                && typeName != "Vector3" && typeName != "Rotation" && typeName != "String"
                && !prop.PropertyType.IsEnum ) continue;

            object currentVal = null;
            try { currentVal = prop.GetValue( smr ); } catch { }

            parameters.Add( new
            {
                name = prop.Name,
                type = typeName,
                value = currentVal?.ToString(),
                source = "SkinnedModelRenderer"
            } );
        }

        // 2. Check for CitizenAnimationHelper
        var animHelper = go.Components.GetAll()
            .FirstOrDefault( c => c.GetType().Name == "CitizenAnimationHelper" );

        var helperParams = new List<object>();
        if ( animHelper != null )
        {
            var helperProps = animHelper.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly );

            foreach ( var prop in helperProps )
            {
                if ( !prop.CanWrite ) continue;

                object currentVal = null;
                try { currentVal = prop.GetValue( animHelper ); } catch { }

                string enumValues = null;
                if ( prop.PropertyType.IsEnum )
                {
                    enumValues = string.Join( ", ", Enum.GetNames( prop.PropertyType ) );
                }

                helperParams.Add( new
                {
                    name = prop.Name,
                    type = prop.PropertyType.Name,
                    value = currentVal?.ToString(),
                    source = "CitizenAnimationHelper",
                    enum_values = enumValues
                } );
            }
        }

        // 3. Try to get model animation graph parameters
        var modelParams = new List<object>();
        try
        {
            var modelProp = smr.GetType().GetProperty( "Model" );
            var model = modelProp?.GetValue( smr ) as Model;
            if ( model != null )
            {
                // Try to access animation parameters through reflection
                var boneCount = model.BoneCount;
                modelParams.Add( new { info = "model_loaded", boneCount } );
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            object_id = go.Id.ToString(),
            object_name = go.Name,
            has_skinned_model_renderer = true,
            has_citizen_animation_helper = animHelper != null,
            skinned_model_params = parameters,
            citizen_helper_params = helperParams,
            model_info = modelParams,
            message = $"Found {parameters.Count} SkinnedModelRenderer params" +
                      ( animHelper != null ? $" and {helperParams.Count} CitizenAnimationHelper params" : "" ) + "."
        } );
    }

    // ── set_parameters ───────────────────────────────────────────────

    private static object SetParameters( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "set_parameters" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "set_parameters" );

        var paramsJson = HandlerBase.GetString( args, "parameters" );
        if ( string.IsNullOrEmpty( paramsJson ) )
            return HandlerBase.Error( "Missing required 'parameters' parameter.", "set_parameters" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "set_parameters" );

        JsonElement paramsObj;
        try
        {
            using var doc = JsonDocument.Parse( paramsJson );
            paramsObj = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Error( $"Failed to parse 'parameters' JSON: {ex.Message}", "set_parameters" );
        }

        // Find animation components
        var smr = go.Components.GetAll()
            .FirstOrDefault( c => c.GetType().Name == "SkinnedModelRenderer" );
        var animHelper = go.Components.GetAll()
            .FirstOrDefault( c => c.GetType().Name == "CitizenAnimationHelper" );

        if ( smr == null )
            return HandlerBase.Error( $"'{go.Name}' has no SkinnedModelRenderer.", "set_parameters" );

        var setResults = new List<object>();
        var errors = new List<object>();

        foreach ( var kvp in paramsObj.EnumerateObject() )
        {
            var paramName = kvp.Name;
            bool found = false;

            // Try CitizenAnimationHelper first (higher-level API)
            if ( animHelper != null )
            {
                var helperProp = animHelper.GetType().GetProperty( paramName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );

                if ( helperProp != null && helperProp.CanWrite )
                {
                    try
                    {
                        var converted = ComponentHandler.ConvertJsonValue( kvp.Value, helperProp.PropertyType );
                        helperProp.SetValue( animHelper, converted );
                        var readback = helperProp.GetValue( animHelper );
                        setResults.Add( new { parameter = paramName, value = readback?.ToString(), source = "CitizenAnimationHelper" } );
                        found = true;
                    }
                    catch ( Exception ex )
                    {
                        errors.Add( new { parameter = paramName, error = ex.Message } );
                        found = true;
                    }
                }
            }

            // Fall back to SkinnedModelRenderer
            if ( !found )
            {
                var smrProp = smr.GetType().GetProperty( paramName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );

                if ( smrProp != null && smrProp.CanWrite )
                {
                    try
                    {
                        var converted = ComponentHandler.ConvertJsonValue( kvp.Value, smrProp.PropertyType );
                        smrProp.SetValue( smr, converted );
                        var readback = smrProp.GetValue( smr );
                        setResults.Add( new { parameter = paramName, value = readback?.ToString(), source = "SkinnedModelRenderer" } );
                        found = true;
                    }
                    catch ( Exception ex )
                    {
                        errors.Add( new { parameter = paramName, error = ex.Message } );
                        found = true;
                    }
                }
            }

            // Try SkinnedModelRenderer.Set() for animgraph parameters
            if ( !found )
            {
                try
                {
                    var setMethod = smr.GetType().GetMethod( "Set",
                        new[] { typeof( string ), typeof( object ) } );
                    if ( setMethod != null )
                    {
                        object val;
                        if ( kvp.Value.ValueKind == JsonValueKind.True ) val = true;
                        else if ( kvp.Value.ValueKind == JsonValueKind.False ) val = false;
                        else if ( kvp.Value.ValueKind == JsonValueKind.Number ) val = kvp.Value.GetSingle();
                        else val = kvp.Value.GetString();

                        setMethod.Invoke( smr, new object[] { paramName, val } );
                        setResults.Add( new { parameter = paramName, value = val?.ToString(), source = "AnimGraph" } );
                        found = true;
                    }
                }
                catch ( Exception ex )
                {
                    errors.Add( new { parameter = paramName, error = ex.Message } );
                    found = true;
                }
            }

            if ( !found )
            {
                errors.Add( new { parameter = paramName, error = $"Parameter '{paramName}' not found on any animation component." } );
            }
        }

        return HandlerBase.Success( new
        {
            object_id = go.Id.ToString(),
            object_name = go.Name,
            set_count = setResults.Count,
            error_count = errors.Count,
            set = setResults,
            errors,
            message = $"Set {setResults.Count} animation parameters, {errors.Count} errors."
        } );
    }

    // ── get_model_animations ─────────────────────────────────────────

    private static object GetModelAnimations( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "get_model_animations" );

        Model model = null;
        var id = HandlerBase.GetString( args, "id" );
        var modelPath = HandlerBase.GetString( args, "model_path" );

        if ( !string.IsNullOrEmpty( id ) )
        {
            var go = SceneHelpers.FindByIdOrThrow( scene, id, "get_model_animations" );
            var smr = go.Components.GetAll()
                .FirstOrDefault( c => c.GetType().Name == "SkinnedModelRenderer" );
            if ( smr == null )
                return HandlerBase.Error( $"'{go.Name}' has no SkinnedModelRenderer.", "get_model_animations" );

            var modelProp = smr.GetType().GetProperty( "Model" );
            model = modelProp?.GetValue( smr ) as Model;
        }
        else if ( !string.IsNullOrEmpty( modelPath ) )
        {
            model = Model.Load( modelPath );
        }
        else
        {
            return HandlerBase.Error( "Provide 'id' (GameObject GUID) or 'model_path'.", "get_model_animations" );
        }

        if ( model == null )
            return HandlerBase.Error( "Model could not be loaded.", "get_model_animations" );

        var animations = new List<object>();

        // Try to enumerate animation sequences via reflection
        try
        {
            // Model.Animations or similar API
            var animProp = model.GetType().GetProperty( "Animations" ) ??
                           model.GetType().GetProperty( "AnimationList" );

            if ( animProp != null )
            {
                var animList = animProp.GetValue( model );
                if ( animList is System.Collections.IEnumerable enumerable )
                {
                    foreach ( var anim in enumerable )
                    {
                        var nameProp = anim.GetType().GetProperty( "Name" );
                        var durationProp = anim.GetType().GetProperty( "Duration" );
                        var loopProp = anim.GetType().GetProperty( "Looping" ) ?? anim.GetType().GetProperty( "IsLooping" );

                        object duration = null;
                        if ( durationProp != null )
                        {
                            try { duration = Math.Round( Convert.ToDouble( durationProp.GetValue( anim ) ), 3 ); }
                            catch { }
                        }

                        animations.Add( new
                        {
                            name = nameProp?.GetValue( anim )?.ToString() ?? anim.ToString(),
                            duration,
                            looping = loopProp?.GetValue( anim )
                        } );
                    }
                }
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            model_name = model.Name,
            bone_count = model.BoneCount,
            animation_count = animations.Count,
            animations,
            message = animations.Count > 0
                ? $"Found {animations.Count} animation(s) on '{model.Name}'."
                : $"Model '{model.Name}' has {model.BoneCount} bones. Animation enumeration may require runtime API access."
        } );
    }
}
