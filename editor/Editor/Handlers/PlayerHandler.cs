// editor/Editor/Handlers/PlayerHandler.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// player tool: create, configure_movement, configure_camera, configure_body, configure_interaction.
/// Curated access to PlayerController's 72 properties, grouped by concern.
/// </summary>
[McpTool( "player",
    "Create and configure PlayerController components for character setup. Use create to add a controller and optional body model. Configure movement, camera, body, and interaction in separate actions so omitted properties retain their current values. This tool mutates scene objects and is intended for editor-time character authoring; use the existing pawn tool instead when driving or observing a live play-mode player." )]
internal static class PlayerHandler
{
    [McpAction( "create", WriteKind = "scene_write" )]
    internal static object HandleCreate( JsonElement args ) => Handle( "create", args );

    [McpAction( "configure_movement", WriteKind = "scene_write" )]
    internal static object HandleConfigureMovement( JsonElement args ) => Handle( "configure_movement", args );

    [McpAction( "configure_camera", WriteKind = "scene_write" )]
    internal static object HandleConfigureCamera( JsonElement args ) => Handle( "configure_camera", args );

    [McpAction( "configure_body", WriteKind = "scene_write" )]
    internal static object HandleConfigureBody( JsonElement args ) => Handle( "configure_body", args );

    [McpAction( "configure_interaction", WriteKind = "scene_write" )]
    internal static object HandleConfigureInteraction( JsonElement args ) => Handle( "configure_interaction", args );

    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure_movement", "configure_camera", "configure_body", "configure_interaction" } },
            ["id"] = new { type = "string", description = "Target GameObject GUID. Required for configure actions; optional for create." },
            ["name"] = new { type = "string", description = "Object name. Used by create." },
            ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by create." },
            ["body_model"] = new { type = "string", description = "Body model path. Used by create." },
            ["walk_speed"] = new { type = "number", description = "Walking speed." },
            ["run_speed"] = new { type = "number", description = "Running speed." },
            ["ducked_speed"] = new { type = "number", description = "Crouching speed." },
            ["jump_speed"] = new { type = "number", description = "Jump velocity." },
            ["acceleration_time"] = new { type = "number", description = "Seconds to accelerate." },
            ["deacceleration_time"] = new { type = "number", description = "Seconds to decelerate." },
            ["air_friction"] = new { type = "number", description = "Air friction factor." },
            ["brake_power"] = new { type = "number", description = "Ground braking friction." },
            ["run_by_default"] = new { type = "boolean", description = "Run by default and hold to walk." },
            ["third_person"] = new { type = "boolean", description = "Enable third-person camera." },
            ["hide_body_in_first_person"] = new { type = "boolean", description = "Hide the body model in first person." },
            ["camera_offset"] = new { type = "string", description = "Camera offset as 'x,y,z'." },
            ["eye_distance_from_top"] = new { type = "number", description = "Eye height measured from body top." },
            ["pitch_clamp"] = new { type = "number", description = "Maximum pitch in degrees." },
            ["look_sensitivity"] = new { type = "number", description = "Look sensitivity." },
            ["use_fov_from_preferences"] = new { type = "boolean", description = "Use player FOV preferences." },
            ["body_height"] = new { type = "number", description = "Standing body height." },
            ["body_radius"] = new { type = "number", description = "Capsule radius." },
            ["body_mass"] = new { type = "number", description = "Body mass in kilograms." },
            ["ducked_height"] = new { type = "number", description = "Crouching body height." },
            ["collision_tags"] = new { type = "string", description = "Comma-separated collision tags." },
            ["enable_pressing"] = new { type = "boolean", description = "Enable the use/press interaction system." },
            ["use_button"] = new { type = "string", description = "Input action name for use." },
            ["reach_length"] = new { type = "number", description = "Interaction reach distance." }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create"                  => Create( args ),
                "configure_movement"      => ConfigureMovement( args ),
                "configure_camera"        => ConfigureCamera( args ),
                "configure_body"          => ConfigureBody( args ),
                "configure_interaction"   => ConfigureInteraction( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: create, configure_movement, configure_camera, configure_body, configure_interaction" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── Shared: find PlayerController on a GO ────────────────────────

    private static PlayerController FindController( Scene scene, string id, string action, out object error )
    {
        error = null;
        var go = SceneHelpers.FindByIdOrThrow( scene, id, action );
        var pc = go.Components.Get<PlayerController>();
        if ( pc == null )
            error = HandlerBase.Error( $"No PlayerController found on '{go.Name}'.", action,
                "Add a PlayerController first with player.create." );
        return pc;
    }

    // ── create ───────────────────────────────────────────────────────

    private static object Create( JsonElement args )
    {
        var scene = HandlerBase.RequireScene( "create" );

        var id = HandlerBase.GetString( args, "id" );
        GameObject go;

        if ( !string.IsNullOrEmpty( id ) )
        {
            go = SceneHelpers.FindByIdOrThrow( scene, id, "create" );
        }
        else
        {
            go = scene.CreateObject();
            go.Name = HandlerBase.GetString( args, "name" ) ?? "Player";
            var posStr = HandlerBase.GetString( args, "position" );
            if ( posStr != null ) go.WorldPosition = HandlerBase.ParseVector3( posStr );
        }

        var pc = go.Components.Create<PlayerController>();

        var bodyModel = HandlerBase.GetString( args, "body_model" );
        if ( !string.IsNullOrEmpty( bodyModel ) )
        {
            pc.CreateBodyRenderer();
            if ( pc.Renderer != null )
                pc.Renderer.Model = HandlerBase.RequireModel( bodyModel, "create_player" );
        }

        return HandlerBase.Success( new
        {
            message = $"Created PlayerController on '{go.Name}'.",
            id = go.Id.ToString(),
            component_id = pc.Id.ToString(),
            body_height = pc.BodyHeight,
            body_radius = pc.BodyRadius
        } );
    }

    // ── configure_movement ──────────────────────────────────────────

    private static object ConfigureMovement( JsonElement args )
    {
        var scene = HandlerBase.RequireScene( "configure_movement" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_movement" );

        var pc = FindController( scene, id, "configure_movement", out var error );
        if ( error != null ) return error;

        if ( HandlerBase.TryGetFloat( args, "walk_speed", out var ws ) ) pc.WalkSpeed = ws;
        if ( HandlerBase.TryGetFloat( args, "run_speed", out var rs ) ) pc.RunSpeed = rs;
        if ( HandlerBase.TryGetFloat( args, "ducked_speed", out var ds ) ) pc.DuckedSpeed = ds;
        if ( HandlerBase.TryGetFloat( args, "jump_speed", out var js ) ) pc.JumpSpeed = js;
        if ( HandlerBase.TryGetFloat( args, "acceleration_time", out var at ) ) pc.AccelerationTime = at;
        if ( HandlerBase.TryGetFloat( args, "deacceleration_time", out var dt ) ) pc.DeaccelerationTime = dt;
        if ( HandlerBase.TryGetFloat( args, "air_friction", out var af ) ) pc.AirFriction = af;
        if ( HandlerBase.TryGetFloat( args, "brake_power", out var bp ) ) pc.BrakePower = bp;
        if ( HandlerBase.TryGetBool( args, "run_by_default", out var rbd ) ) pc.RunByDefault = rbd;

        return HandlerBase.Confirm( $"Configured movement on '{pc.GameObject.Name}': walk={pc.WalkSpeed}, run={pc.RunSpeed}, jump={pc.JumpSpeed}." );
    }

    // ── configure_camera ────────────────────────────────────────────

    private static object ConfigureCamera( JsonElement args )
    {
        var scene = HandlerBase.RequireScene( "configure_camera" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_camera" );

        var pc = FindController( scene, id, "configure_camera", out var error );
        if ( error != null ) return error;

        if ( HandlerBase.TryGetBool( args, "third_person", out var tp ) ) pc.ThirdPerson = tp;
        if ( HandlerBase.TryGetBool( args, "hide_body_in_first_person", out var hb ) ) pc.HideBodyInFirstPerson = hb;
        var offsetStr = HandlerBase.GetString( args, "camera_offset" );
        if ( offsetStr != null )
            pc.CameraOffset = HandlerBase.ParseVector3( offsetStr );
        if ( HandlerBase.TryGetFloat( args, "eye_distance_from_top", out var ed ) ) pc.EyeDistanceFromTop = ed;
        if ( HandlerBase.TryGetFloat( args, "pitch_clamp", out var pcl ) ) pc.PitchClamp = pcl;
        if ( HandlerBase.TryGetFloat( args, "look_sensitivity", out var ls ) ) pc.LookSensitivity = ls;
        if ( HandlerBase.TryGetBool( args, "use_fov_from_preferences", out var uf ) ) pc.UseFovFromPreferences = uf;

        return HandlerBase.Confirm( $"Configured camera on '{pc.GameObject.Name}': third_person={pc.ThirdPerson}." );
    }

    // ── configure_body ──────────────────────────────────────────────

    private static object ConfigureBody( JsonElement args )
    {
        var scene = HandlerBase.RequireScene( "configure_body" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_body" );

        var pc = FindController( scene, id, "configure_body", out var error );
        if ( error != null ) return error;

        if ( HandlerBase.TryGetFloat( args, "body_height", out var bh ) ) pc.BodyHeight = bh;
        if ( HandlerBase.TryGetFloat( args, "body_radius", out var br ) ) pc.BodyRadius = br;
        if ( HandlerBase.TryGetFloat( args, "body_mass", out var bm ) ) pc.BodyMass = bm;
        if ( HandlerBase.TryGetFloat( args, "ducked_height", out var dh ) ) pc.DuckedHeight = dh;

        var collisionTags = HandlerBase.GetString( args, "collision_tags" );
        if ( !string.IsNullOrEmpty( collisionTags ) )
        {
            pc.BodyCollisionTags.RemoveAll();
            foreach ( var tag in collisionTags.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
                pc.BodyCollisionTags.Add( tag );
        }

        return HandlerBase.Confirm( $"Configured body on '{pc.GameObject.Name}': height={pc.BodyHeight}, radius={pc.BodyRadius}, mass={pc.BodyMass}." );
    }

    // ── configure_interaction ───────────────────────────────────────

    private static object ConfigureInteraction( JsonElement args )
    {
        var scene = HandlerBase.RequireScene( "configure_interaction" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_interaction" );

        var pc = FindController( scene, id, "configure_interaction", out var error );
        if ( error != null ) return error;

        if ( HandlerBase.TryGetBool( args, "enable_pressing", out var ep ) ) pc.EnablePressing = ep;
        var useButton = HandlerBase.GetString( args, "use_button" );
        if ( useButton != null )
            pc.UseButton = useButton;
        if ( HandlerBase.TryGetFloat( args, "reach_length", out var rl ) ) pc.ReachLength = rl;

        return HandlerBase.Confirm( $"Configured interaction on '{pc.GameObject.Name}': pressing={pc.EnablePressing}, reach={pc.ReachLength}." );
    }
}
