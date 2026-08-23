// Editor/Handlers/EffectsHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// effects tool: create, configure_particle, configure_post_processing.
/// Consolidates Ozmium CreateParticleEffect, CreateFogVolume, CreateBeamEffect,
/// CreateVerletRope, CreateRadiusDamage, CreateRenderEntity into 'create' with type param.
/// Ported from EffectToolHandlers.cs (641 lines, minus CreateJoint moved to physics).
/// </summary>
internal static class EffectsHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create"                   => Create( args ),
                "configure_particle"       => ConfigureParticle( args ),
                "configure_post_processing" => ConfigurePostProcessing( args ),
                "configure_sprite"         => ConfigureSprite( args ),
                "configure_prop"           => ConfigureProp( args ),
                "configure_world_panel"    => ConfigureWorldPanel( args ),
                "participants"             => Participants( args ),
                "effects_list"             => EffectsList( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: create, configure_particle, configure_post_processing, configure_sprite, configure_prop, configure_world_panel, participants, effects_list" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── create ────────────────────────────────────────────────────────
    // Consolidates: CreateParticleEffect, CreateFogVolume, CreateBeamEffect,
    //               CreateVerletRope, CreateRadiusDamage, CreateRenderEntity

    private static object Create( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create" );

        var type = HandlerBase.GetString( args, "type", "particle" );
        var posStr = HandlerBase.GetString( args, "position" );
        var position = posStr != null ? HandlerBase.ParseVector3( posStr ) : Vector3.Zero;

        return type.ToLowerInvariant() switch
        {
            "particle"       => CreateParticle( scene, args, position ),
            "fog"            => CreateFog( scene, args, position ),
            "beam"           => CreateBeam( scene, args, position ),
            "rope"           => CreateRope( scene, args, position ),
            "radius_damage"  => CreateRadiusDamage( scene, args, position ),
            "render_entity"  => CreateRenderEntity( scene, args, position ),
            "sprite"         => CreateSprite( scene, args, position ),
            "prop"           => CreateProp( scene, args, position ),
            "world_panel"    => CreateWorldPanel( scene, args, position ),
            _ => HandlerBase.Error(
                $"Unknown effect type '{type}'.",
                "create",
                "Valid types: particle, fog, beam, rope, radius_damage, render_entity, sprite, prop, world_panel" )
        };
    }

    private static object CreateParticle( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Particle Effect";
        go.WorldPosition = position;

        var pe = go.Components.Create<ParticleEffect>();
        pe.MaxParticles = HandlerBase.GetInt( args, "max_particles", 1000 );
        if ( args.TryGetProperty( "lifetime", out var ltEl ) && ltEl.ValueKind == JsonValueKind.Number )
            pe.Lifetime = ltEl.GetSingle();
        if ( args.TryGetProperty( "time_scale", out var tsEl ) && tsEl.ValueKind == JsonValueKind.Number )
            pe.TimeScale = tsEl.GetSingle();
        if ( args.TryGetProperty( "pre_warm", out var pwEl ) && pwEl.ValueKind == JsonValueKind.Number )
            pe.PreWarm = pwEl.GetSingle();

        return HandlerBase.Success( new
        {
            message = $"Created ParticleEffect '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateFog( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Fog Volume";
        go.WorldPosition = position;

        var fogType = HandlerBase.GetString( args, "fog_type", "gradient" );

        if ( fogType.Equals( "volumetric", StringComparison.OrdinalIgnoreCase ) )
        {
            var vf = go.Components.Create<VolumetricFogVolume>();
            if ( args.TryGetProperty( "strength", out var sEl ) && sEl.ValueKind == JsonValueKind.Number )
                vf.Strength = sEl.GetSingle();
            if ( args.TryGetProperty( "falloff_exponent", out var feEl ) && feEl.ValueKind == JsonValueKind.Number )
                vf.FalloffExponent = feEl.GetSingle();
            vf.Bounds = BBox.FromPositionAndSize( 0, 300 );
        }
        else
        {
            var gf = go.Components.Create<GradientFog>();
            gf.Color = Color.White;
            if ( args.TryGetProperty( "height", out var hEl ) && hEl.ValueKind == JsonValueKind.Number )
                gf.Height = hEl.GetSingle();
            if ( args.TryGetProperty( "start_distance", out var sdEl ) && sdEl.ValueKind == JsonValueKind.Number )
                gf.StartDistance = sdEl.GetSingle();
            if ( args.TryGetProperty( "end_distance", out var edEl ) && edEl.ValueKind == JsonValueKind.Number )
                gf.EndDistance = edEl.GetSingle();
            if ( args.TryGetProperty( "falloff_exponent", out var feEl ) && feEl.ValueKind == JsonValueKind.Number )
                gf.FalloffExponent = feEl.GetSingle();
            if ( args.TryGetProperty( "vertical_falloff_exponent", out var vfeEl ) && vfeEl.ValueKind == JsonValueKind.Number )
                gf.VerticalFalloffExponent = vfeEl.GetSingle();
            var colorStr = HandlerBase.GetString( args, "color" );
            if ( !string.IsNullOrEmpty( colorStr ) )
            {
                try { gf.Color = Color.Parse( colorStr ) ?? default; } catch { }
            }
        }

        return HandlerBase.Success( new
        {
            message = $"Created {fogType} fog on '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateBeam( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Beam Effect";
        go.WorldPosition = position;

        var beam = go.Components.Create<BeamEffect>();
        if ( args.TryGetProperty( "scale", out var scEl ) && scEl.ValueKind == JsonValueKind.Number )
            beam.Scale = scEl.GetSingle();
        if ( args.TryGetProperty( "beams_per_second", out var bpsEl ) && bpsEl.ValueKind == JsonValueKind.Number )
            beam.BeamsPerSecond = bpsEl.GetSingle();
        beam.MaxBeams = HandlerBase.GetInt( args, "max_beams", 1 );
        beam.Looped = HandlerBase.GetBool( args, "looped", false );

        var targetPosStr = HandlerBase.GetString( args, "target_position" );
        if ( targetPosStr != null )
            beam.TargetPosition = HandlerBase.ParseVector3( targetPosStr );

        var targetId = HandlerBase.GetString( args, "target_id" );
        if ( !string.IsNullOrEmpty( targetId ) )
        {
            var targetGo = SceneHelpers.FindById( scene, targetId );
            if ( targetGo != null ) beam.TargetGameObject = targetGo;
        }

        return HandlerBase.Success( new
        {
            message = $"Created BeamEffect '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateRope( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Verlet Rope";
        go.WorldPosition = position;

        var rope = go.Components.Create<VerletRope>();
        rope.SegmentCount = HandlerBase.GetInt( args, "segment_count", 16 );
        if ( args.TryGetProperty( "slack", out var slEl ) && slEl.ValueKind == JsonValueKind.Number )
            rope.Slack = slEl.GetSingle();
        if ( args.TryGetProperty( "radius", out var rEl ) && rEl.ValueKind == JsonValueKind.Number )
            rope.Radius = rEl.GetSingle();
        if ( args.TryGetProperty( "stiffness", out var stEl ) && stEl.ValueKind == JsonValueKind.Number )
            rope.Stiffness = stEl.GetSingle();
        if ( args.TryGetProperty( "damping_factor", out var dfEl ) && dfEl.ValueKind == JsonValueKind.Number )
            rope.DampingFactor = dfEl.GetSingle();

        var attachId = HandlerBase.GetString( args, "attachment_id" );
        if ( !string.IsNullOrEmpty( attachId ) )
        {
            var attachGo = SceneHelpers.FindById( scene, attachId );
            if ( attachGo != null ) rope.Attachment = attachGo;
        }

        return HandlerBase.Success( new
        {
            message = $"Created VerletRope '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateRadiusDamage( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Radius Damage";
        go.WorldPosition = position;

        var rd = go.Components.Create<RadiusDamage>();
        if ( args.TryGetProperty( "radius", out var rEl ) && rEl.ValueKind == JsonValueKind.Number )
            rd.Radius = rEl.GetSingle();
        if ( args.TryGetProperty( "damage_amount", out var daEl ) && daEl.ValueKind == JsonValueKind.Number )
            rd.DamageAmount = daEl.GetSingle();
        if ( args.TryGetProperty( "physics_force_scale", out var pfsEl ) && pfsEl.ValueKind == JsonValueKind.Number )
            rd.PhysicsForceScale = pfsEl.GetSingle();
        rd.DamageOnEnabled = HandlerBase.GetBool( args, "damage_on_enabled", true );
        rd.Occlusion = HandlerBase.GetBool( args, "occlusion", true );

        var damageTags = HandlerBase.GetString( args, "damage_tags" );
        if ( !string.IsNullOrEmpty( damageTags ) )
        {
            foreach ( var tag in damageTags.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
                rd.DamageTags.Add( tag.Trim() );
        }

        return HandlerBase.Success( new
        {
            message = $"Created RadiusDamage '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition ),
            radius = rd.Radius,
            damage = rd.DamageAmount
        } );
    }

    private static object CreateRenderEntity( Scene scene, JsonElement args, Vector3 position )
    {
        // Delegates to RenderingToolHandlers-equivalent logic
        // Supports: text, line, sprite, trail, model_renderer, skinned_model, screen_panel
        var renderType = HandlerBase.GetString( args, "render_type", "" );

        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Render Entity";
        go.WorldPosition = position;

        switch ( renderType.ToLowerInvariant() )
        {
            case "text":
            {
                var tr = go.Components.Create<TextRenderer>();
                tr.Text = HandlerBase.GetString( args, "text", tr.Text );
                var colorStr = HandlerBase.GetString( args, "color" );
                if ( !string.IsNullOrEmpty( colorStr ) )
                {
                    try { tr.Color = Color.Parse( colorStr ) ?? default; } catch { }
                }
                break;
            }
            case "trail":
            {
                var trail = go.Components.Create<TrailRenderer>();
                trail.MaxPoints = HandlerBase.GetInt( args, "max_points", trail.MaxPoints );
                trail.Emitting = HandlerBase.GetBool( args, "emitting", trail.Emitting );
                break;
            }
            case "line":
                go.Components.Create<LineRenderer>();
                break;
            case "model_renderer":
            {
                var mr = go.Components.Create<ModelRenderer>();
                var modelPath = HandlerBase.GetString( args, "model_path" );
                if ( !string.IsNullOrEmpty( modelPath ) )
                {
                    var model = Model.Load( modelPath );
                    if ( model != null ) mr.Model = model;
                }
                break;
            }
            case "skinned_model":
            {
                var sk = go.Components.Create<SkinnedModelRenderer>();
                var modelPath = HandlerBase.GetString( args, "model_path" );
                if ( !string.IsNullOrEmpty( modelPath ) )
                {
                    var model = Model.Load( modelPath );
                    if ( model != null ) sk.Model = model;
                }
                break;
            }
            case "screen_panel":
            {
                var sp = go.Components.Create<ScreenPanel>();
                if ( args.TryGetProperty( "opacity", out var opEl ) && opEl.ValueKind == JsonValueKind.Number )
                    sp.Opacity = opEl.GetSingle();
                break;
            }
            default:
                go.Destroy();
                return HandlerBase.Error(
                    $"Unknown render_type '{renderType}'.",
                    "create",
                    "For render_entity type, valid render_types: text, line, trail, model_renderer, skinned_model, screen_panel" );
        }

        return HandlerBase.Success( new
        {
            message = $"Created {renderType} render entity '{go.Name}'.",
            id = go.Id.ToString(),
            name = go.Name,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    // ── configure_particle ────────────────────────────────────────────
    // Ported from EffectToolHandlers.ConfigureParticleEffect

    private static object ConfigureParticle( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "configure_particle" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_particle" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "configure_particle" );
        var pe = go.Components.Get<ParticleEffect>();
        if ( pe == null )
            return HandlerBase.Error( $"No ParticleEffect component found on '{go.Name}'.", "configure_particle" );

        if ( args.TryGetProperty( "max_particles", out var mpEl ) && mpEl.ValueKind == JsonValueKind.Number )
            pe.MaxParticles = mpEl.GetInt32();
        if ( args.TryGetProperty( "lifetime", out var ltEl ) && ltEl.ValueKind == JsonValueKind.Number )
            pe.Lifetime = ltEl.GetSingle();
        if ( args.TryGetProperty( "time_scale", out var tsEl ) && tsEl.ValueKind == JsonValueKind.Number )
            pe.TimeScale = tsEl.GetSingle();
        if ( args.TryGetProperty( "pre_warm", out var pwEl ) && pwEl.ValueKind == JsonValueKind.Number )
            pe.PreWarm = pwEl.GetSingle();

        return HandlerBase.Confirm( $"Configured ParticleEffect on '{go.Name}'." );
    }

    // ── configure_post_processing ─────────────────────────────────────
    // Ported from EffectToolHandlers.ConfigurePostProcessing

    private static object ConfigurePostProcessing( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "configure_post_processing" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "configure_post_processing" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "configure_post_processing" );

        // ConfigurePostProcessing in Ozmium creates a new PostProcessVolume if not present
        var pp = go.Components.Get<PostProcessVolume>();
        if ( pp == null )
            pp = go.Components.Create<PostProcessVolume>();

        pp.Priority = HandlerBase.GetInt( args, "priority", pp.Priority );
        if ( args.TryGetProperty( "blend_weight", out var bwEl ) && bwEl.ValueKind == JsonValueKind.Number )
            pp.BlendWeight = bwEl.GetSingle();
        if ( args.TryGetProperty( "blend_distance", out var bdEl ) && bdEl.ValueKind == JsonValueKind.Number )
            pp.BlendDistance = bdEl.GetSingle();
        pp.EditorPreview = HandlerBase.GetBool( args, "editor_preview", pp.EditorPreview );

        return HandlerBase.Confirm( $"Configured PostProcessVolume on '{go.Name}'." );
    }

    // ── sprite / prop / world_panel ───────────────────────────────────
    //
    // Ported from upstream Arenula 68bc5b0 (2026-08-05), adapted to this repo's
    // idiom: scene.CreateObject() + Components.Get<T>() rather than upstream's
    // CreateChildObject / GetComponentByIdOrFirst helpers, which arrived with the
    // refactor-card batch this fork deliberately did not take.

    private static object CreateSprite( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Sprite";
        go.WorldPosition = position;

        var sr = go.Components.Create<SpriteRenderer>();

        var spritePath = HandlerBase.GetString( args, "sprite" );
        if ( !string.IsNullOrEmpty( spritePath ) )
        {
            var sprite = ResourceLibrary.Get<Sprite>( spritePath );
            if ( sprite == null )
            {
                // Leave nothing half-built behind — a nameless empty GameObject
                // in the scene is worse than the error.
                go.Destroy();
                return HandlerBase.Error(
                    $"Sprite not found at '{spritePath}'.", "create",
                    "User-created sprites live under 'Assets/'; the path is relative to Assets/ with no 'Assets/' prefix.",
                    null, McpErrorCode.NotFound );
            }
            sr.Sprite = sprite;
        }

        ApplySpriteProperties( sr, args );

        return HandlerBase.Success( new
        {
            message = $"Created SpriteRenderer '{go.Name}'.",
            id = go.Id.ToString(),
            component_id = sr.Id.ToString(),
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateProp( Scene scene, JsonElement args, Vector3 position )
    {
        var modelPath = HandlerBase.GetString( args, "model" );
        if ( string.IsNullOrEmpty( modelPath ) )
            return HandlerBase.Error( "Missing required 'model' parameter for the 'prop' type.", "create" );

        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "Prop";
        go.WorldPosition = position;

        var prop = go.Components.Create<Prop>();

        var model = HandlerBase.ResolveModel( modelPath, "create_prop" );
        if ( model == null || model.IsError )
        {
            go.Destroy();
            return HandlerBase.Error(
                $"Model '{modelPath}' did not resolve{( model?.IsError == true ? " (the path resolved to the engine's error model)" : "" )}.",
                "create",
                "Check the .vmdl path. Model.Load returns the error model rather than null for a bad path, so an unchecked assignment would have looked like success.",
                null, McpErrorCode.NotFound );
        }
        prop.Model = model;

        ApplyPropProperties( prop, args );

        return HandlerBase.Success( new
        {
            message = $"Created Prop '{go.Name}'.",
            id = go.Id.ToString(),
            component_id = prop.Id.ToString(),
            model = modelPath,
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object CreateWorldPanel( Scene scene, JsonElement args, Vector3 position )
    {
        var go = scene.CreateObject();
        go.Name = HandlerBase.GetString( args, "name" ) ?? "World Panel";
        go.WorldPosition = position;

        var wp = go.Components.Create<WorldPanel>();
        ApplyWorldPanelProperties( wp, args, "create" );

        return HandlerBase.Success( new
        {
            message = $"Created WorldPanel '{go.Name}'.",
            id = go.Id.ToString(),
            component_id = wp.Id.ToString(),
            panel_size = new { x = wp.PanelSize.x, y = wp.PanelSize.y },
            position = HandlerBase.V3( go.WorldPosition )
        } );
    }

    private static object ConfigureSprite( JsonElement args )
    {
        var (go, sr, error) = Resolve<SpriteRenderer>( args, "configure_sprite" );
        if ( sr == null ) return error;

        var spritePath = HandlerBase.GetString( args, "sprite" );
        if ( !string.IsNullOrEmpty( spritePath ) )
        {
            var sprite = ResourceLibrary.Get<Sprite>( spritePath );
            if ( sprite == null )
                return HandlerBase.Error(
                    $"Sprite not found at '{spritePath}'.", "configure_sprite",
                    "User-created sprites live under 'Assets/'; the path is relative to Assets/ with no 'Assets/' prefix.",
                    null, McpErrorCode.NotFound );
            sr.Sprite = sprite;
        }

        ApplySpriteProperties( sr, args );

        var filter = HandlerBase.ResolveEnum<Sandbox.Rendering.FilterMode>(
            HandlerBase.GetString( args, "texture_filter" ), "texture_filter", "configure_sprite" );
        if ( filter.HasValue ) sr.TextureFilter = filter.Value;

        if ( HandlerBase.TryGetFloat( args, "depth_feather", out var df ) ) sr.DepthFeather = df;
        if ( HandlerBase.TryGetFloat( args, "fog_strength", out var fs ) ) sr.FogStrength = fs;
        if ( HandlerBase.TryGetFloat( args, "alpha_cutoff", out var ac ) ) sr.AlphaCutoff = ac;

        return HandlerBase.Confirm( $"Configured SpriteRenderer on '{go.Name}'." );
    }

    private static object ConfigureProp( JsonElement args )
    {
        var (go, prop, error) = Resolve<Prop>( args, "configure_prop" );
        if ( prop == null ) return error;

        var modelPath = HandlerBase.GetString( args, "model" );
        if ( !string.IsNullOrEmpty( modelPath ) )
        {
            var model = HandlerBase.ResolveModel( modelPath, "configure_prop" );
            if ( model == null || model.IsError )
                return HandlerBase.Error(
                    $"Model '{modelPath}' did not resolve — the prop's model was NOT changed.",
                    "configure_prop",
                    "Check the .vmdl path. Model.Load returns the error model rather than null for a bad path.",
                    null, McpErrorCode.NotFound );
            prop.Model = model;
        }

        ApplyPropProperties( prop, args );

        if ( args.TryGetProperty( "body_groups", out var bgEl ) && bgEl.ValueKind == JsonValueKind.Number )
            prop.BodyGroups = bgEl.GetUInt64();

        return HandlerBase.Confirm( $"Configured Prop on '{go.Name}'." );
    }

    private static object ConfigureWorldPanel( JsonElement args )
    {
        var (go, wp, error) = Resolve<WorldPanel>( args, "configure_world_panel" );
        if ( wp == null ) return error;

        ApplyWorldPanelProperties( wp, args, "configure_world_panel" );
        return HandlerBase.Confirm( $"Configured WorldPanel on '{go.Name}'." );
    }

    // ── Shared property application ───────────────────────────────────
    // Create and configure set the same fields; keeping one copy stops the two
    // paths drifting so that a property works on configure but not on create.

    private static void ApplySpriteProperties( SpriteRenderer sr, JsonElement args )
    {
        var sizeStr = HandlerBase.GetString( args, "size" );
        if ( sizeStr != null ) sr.Size = HandlerBase.ParseVector2( sizeStr );

        HandlerBase.ApplyColor( args, "color", c => sr.Color = c );

        var billboard = HandlerBase.ResolveEnum<SpriteRenderer.BillboardMode>(
            HandlerBase.GetString( args, "billboard" ), "billboard", "sprite" );
        if ( billboard.HasValue ) sr.Billboard = billboard.Value;

        if ( HandlerBase.TryGetBool( args, "lighting", out var lit ) ) sr.Lighting = lit;
        if ( HandlerBase.TryGetBool( args, "shadows", out var sh ) ) sr.Shadows = sh;
        if ( HandlerBase.TryGetBool( args, "opaque", out var op ) ) sr.Opaque = op;
        if ( HandlerBase.TryGetBool( args, "flip_horizontal", out var fh ) ) sr.FlipHorizontal = fh;
        if ( HandlerBase.TryGetBool( args, "flip_vertical", out var fv ) ) sr.FlipVertical = fv;
        if ( HandlerBase.TryGetFloat( args, "playback_speed", out var ps ) ) sr.PlaybackSpeed = ps;

        var animation = HandlerBase.GetString( args, "animation" );
        if ( !string.IsNullOrEmpty( animation ) ) sr.StartingAnimationName = animation;
    }

    private static void ApplyPropProperties( Prop prop, JsonElement args )
    {
        HandlerBase.ApplyColor( args, "tint", c => prop.Tint = c );

        if ( HandlerBase.TryGetFloat( args, "health", out var h ) ) prop.Health = h;
        if ( HandlerBase.TryGetBool( args, "is_static", out var isStatic ) ) prop.IsStatic = isStatic;
        if ( HandlerBase.TryGetBool( args, "start_asleep", out var sa ) ) prop.StartAsleep = sa;

        var materialGroup = HandlerBase.GetString( args, "material_group" );
        if ( !string.IsNullOrEmpty( materialGroup ) ) prop.MaterialGroup = materialGroup;
    }

    private static void ApplyWorldPanelProperties( WorldPanel wp, JsonElement args, string action )
    {
        var panelSizeStr = HandlerBase.GetString( args, "panel_size" );
        if ( panelSizeStr != null ) wp.PanelSize = HandlerBase.ParseVector2( panelSizeStr );

        if ( HandlerBase.TryGetBool( args, "look_at_camera", out var lac ) ) wp.LookAtCamera = lac;
        if ( HandlerBase.TryGetFloat( args, "render_scale", out var rs ) ) wp.RenderScale = rs;
        if ( HandlerBase.TryGetFloat( args, "interaction_range", out var ir ) ) wp.InteractionRange = ir;

        var hAlign = HandlerBase.ResolveEnum<WorldPanel.HAlignment>(
            HandlerBase.GetString( args, "horizontal_align" ), "horizontal_align", action );
        if ( hAlign.HasValue ) wp.HorizontalAlign = hAlign.Value;

        var vAlign = HandlerBase.ResolveEnum<WorldPanel.VAlignment>(
            HandlerBase.GetString( args, "vertical_align" ), "vertical_align", action );
        if ( vAlign.HasValue ) wp.VerticalAlign = vAlign.Value;
    }

    /// <summary>
    /// Resolve the GameObject named by 'id' and the component of type T on it.
    /// Returns a null component plus the error response to hand back.
    /// </summary>
    private static (GameObject go, T component, object error) Resolve<T>( JsonElement args, string action )
        where T : Component
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return ( null, null, HandlerBase.Error( "No active scene.", action ) );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return ( null, null, HandlerBase.Error( "Missing required 'id' parameter.", action ) );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, action );
        var component = go.Components.Get<T>();
        if ( component == null )
            return ( go, null, HandlerBase.Error(
                $"No {typeof( T ).Name} on '{go.Name}'.", action,
                $"Add one with component.add type='{typeof( T ).Name}'.",
                null, McpErrorCode.NotFound ) );

        return ( go, component, null );
    }

    // ── EFFE-M53: the participant registry, read through the game-side facade ──────────────
    //
    // The registry (FxLiveEffects, Libraries/sbep_vfx) lives in the game assembly, so the plugin
    // reaches it the way QaHandler reaches QaSuiteBridge: resolve the bridge type from the CURRENT
    // game-assembly generation and invoke its static string-returning methods. The facade returns
    // JSON with an "ok" boolean; ok:false becomes an MCP error with the facade's own message.

    private const string ParticipantsBridgeType = "SBEP.Diagnostics.FxParticipantsBridge";

    /// <summary>effects action=participants effect=&lt;id|last|name&gt; — every registered piece of one effect.</summary>
    private static object Participants( JsonElement args )
    {
        var effect = args.TryGetProperty( "effect", out var e ) && e.ValueKind == JsonValueKind.String ? e.GetString() : "last";
        return InvokeParticipantsBridge( "Get", new[] { typeof( string ) }, new object[] { string.IsNullOrWhiteSpace( effect ) ? "last" : effect }, "participants" );
    }

    /// <summary>effects action=effects_list — every live and recently ended effect the registry knows.</summary>
    private static object EffectsList( JsonElement args )
        => InvokeParticipantsBridge( "List", Type.EmptyTypes, Array.Empty<object>(), "effects_list" );

    private static object InvokeParticipantsBridge( string methodName, Type[] paramTypes, object[] invokeArgs, string action )
    {
        if ( Game.ActiveScene is null )
            return HandlerBase.Error( "No active scene.", action, "The registry fills while an effect plays — start play mode, fire an effect, then read it." );

        var type = QaHandler.ResolveGameType( ParticipantsBridgeType );
        if ( type == null )
            return HandlerBase.Error( $"Game-side bridge type '{ParticipantsBridgeType}' not found.", action,
                "Game code may be uncompiled or stale — compile the project and retry." );

        var method = type.GetMethod( methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, paramTypes, null );
        if ( method == null )
            return HandlerBase.Error( $"Bridge method '{ParticipantsBridgeType}.{methodName}' not found.", action,
                "Editor plugin and game code are out of sync — recompile both." );

        string json;
        try { json = method.Invoke( null, invokeArgs ) as string; }
        catch ( System.Reflection.TargetInvocationException tex )
        {
            return HandlerBase.Error( $"Bridge threw: {tex.InnerException?.Message ?? tex.Message}", action );
        }
        if ( string.IsNullOrEmpty( json ) )
            return HandlerBase.Error( "Bridge returned an empty response.", action );

        using var doc = JsonDocument.Parse( json );
        var root = doc.RootElement;
        if ( root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "ok", out var okEl ) && okEl.ValueKind == JsonValueKind.False )
        {
            var msg = root.TryGetProperty( "error", out var eEl ) ? eEl.GetString() : $"{action} failed.";
            return HandlerBase.Error( msg, action, "Pass effect=<id> from effects_list, or 'last' for the most recent effect." );
        }
        return HandlerBase.Text( json );
    }
}
