// Editor/Handlers/EyeHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 2.2 of mcp-agent-capability-system — sibling to Wave 2.1 pawn.* SHIPPED this session and Wave 1 SHIPPED 2026-05-15. NOT a continuation of any thruster-vfx wave2 handoff (haze-shake, shock-diamonds, smoke-embergen); zero file overlap with Code/SBEP/Vfx/** or Assets/shaders/thruster_*.shader.]
//
// MCP handler for the 'eye' tool namespace. v1 ships six actions —
// a structured agent-owned scene-camera-pose holder with snapshot + depth-pick:
//   eye.attach     — create-or-bind a persistent tagged 'mcp_eye' GameObject
//   eye.move       — set world pose (position, optional look_at)
//   eye.orbit      — spherical-coord pose around a target
//   eye.frame      — auto-fit bbox of a target with margin
//   eye.snapshot   — JPEG + manifest via CaptureAnnotatedHelper at eye pose+fov
//   eye.depth_pick — world-space hit via Scene.Trace.Ray at normalized u,v
//
// v1 SCOPE CUTS (deferred to v1.1):
//   - eye.bind_editor_view — requires hooking the editor scene-viewport pose
//                            and re-broadcasting it on the play eye each tick.
//                            AttentionHandler already exposes the editor pose
//                            for read; v1.1 will mirror it as writable.
//   - eye.snapshot.with_depth / with_normals — RT depth/normal readback path
//                            needs ViewSetup integration; deferred to v2.
//
// Design notes:
// - The 'mcp_eye' is a pose-holding GameObject only (no CameraComponent).
//   eye.snapshot reuses CaptureAnnotatedHelper.Build with the main scene
//   camera + eye pose — same pattern as CaptureHandler.Annotated. This
//   avoids the GameObjectSystem<T> + CameraComponent persistence
//   complexity that Wave 1.2's Path A hit (memory:
//   feedback_sceneobject_null_in_editor_context + Wave 1.2 handoff §pivot).
// - fov is stored as static handler state, not on the GameObject — there's
//   no FOV property on a bare GameObject and we don't want to spawn a
//   CameraComponent we'd then have to keep Enabled=false.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

internal static class EyeHandler
{
	private const string EyeTag = "mcp_eye";
	private const string EyeName = "mcp_eye";
	private const float DefaultFov = 75f;
	private const float MinDist = 16f;

	// Eye fov is held as static handler state — survives method calls but
	// resets on plugin hot-reload. v1 acceptable behaviour per design notes.
	private static float _eyeFov = DefaultFov;

	internal static object Handle( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				"attach"     => Attach( args ),
				"move"       => Move( args ),
				"orbit"      => Orbit( args ),
				"frame"      => Frame( args ),
				"snapshot"   => Snapshot( args ),
				"depth_pick" => DepthPick( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Valid actions: attach, move, orbit, frame, snapshot, depth_pick (v1). bind_editor_view ships in v1.1." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ── Resolution ───────────────────────────────────────────────────

	private static Scene ResolveScene()
	{
		return SceneHelpers.ResolveScene();
	}

	private static GameObject ResolveEye( Scene scene )
	{
		if ( scene == null ) return null;
		// Tag-based lookup so the eye survives stop/start-play cycles —
		// the play-scene clone keeps tags.
		foreach ( var go in scene.GetAllObjects( false ) )
		{
			if ( go.Tags != null && go.Tags.Has( EyeTag ) )
				return go;
		}
		return null;
	}

	private static CameraComponent ResolveMainCamera( Scene scene )
	{
		foreach ( var c in scene.GetAllComponents<CameraComponent>() )
		{
			if ( c.IsMainCamera && c.Enabled ) return c;
		}
		return scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
	}

	// ── Actions ──────────────────────────────────────────────────────

	private static object Attach( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "attach" );

		var existing = ResolveEye( scene );
		if ( existing != null )
		{
			return HandlerBase.Success( new
			{
				ok = true,
				action = "attach",
				attached = false,
				reused = true,
				id = existing.Id.ToString(),
				name = existing.Name,
				pos = HandlerBase.V3( existing.WorldPosition ),
				angles = HandlerBase.Rot( existing.WorldRotation ),
				fov = _eyeFov
			} );
		}

		var go = scene.CreateObject( true );
		go.Name = EyeName;
		go.Tags.Add( EyeTag );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.WorldPosition = new Vector3( 0, 0, 200 );
		go.WorldRotation = Rotation.From( -15f, 0f, 0f );
		_eyeFov = DefaultFov;

		return HandlerBase.Success( new
		{
			ok = true,
			action = "attach",
			attached = true,
			reused = false,
			id = go.Id.ToString(),
			name = go.Name,
			pos = HandlerBase.V3( go.WorldPosition ),
			angles = HandlerBase.Rot( go.WorldRotation ),
			fov = _eyeFov,
			note = "v1: 'eye' is a pose-only GameObject (tag=mcp_eye). eye.snapshot reuses the main camera at this pose via CaptureAnnotatedHelper."
		} );
	}

	private static object Move( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "move" );

		var eye = ResolveEye( scene );
		if ( eye == null )
			return HandlerBase.Error( "No mcp_eye GameObject in scene. Call eye.attach first.", "move" );

		var posStr = HandlerBase.GetString( args, "position" );
		if ( string.IsNullOrEmpty( posStr ) )
			return HandlerBase.Error( "Missing required 'position' parameter (format: 'x,y,z').", "move" );

		var pos = HandlerBase.ParseVector3( posStr );
		eye.WorldPosition = pos;

		var lookStr = HandlerBase.GetString( args, "look_at" );
		if ( !string.IsNullOrEmpty( lookStr ) )
		{
			var target = HandlerBase.ParseVector3( lookStr );
			var dir = target - pos;
			if ( dir.LengthSquared > 0.0001f )
				eye.WorldRotation = Rotation.LookAt( dir.Normal );
		}

		var fovOpt = HandlerBase.GetFloat( args, "fov", -1f );
		if ( fovOpt > 0f ) _eyeFov = Math.Clamp( fovOpt, 5f, 170f );

		return HandlerBase.Success( new
		{
			ok = true,
			action = "move",
			id = eye.Id.ToString(),
			pos = HandlerBase.V3( eye.WorldPosition ),
			angles = HandlerBase.Rot( eye.WorldRotation ),
			fov = _eyeFov
		} );
	}

	private static object Orbit( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "orbit" );

		var eye = ResolveEye( scene );
		if ( eye == null )
			return HandlerBase.Error( "No mcp_eye GameObject in scene. Call eye.attach first.", "orbit" );

		var targetStr = HandlerBase.GetString( args, "target" );
		if ( string.IsNullOrEmpty( targetStr ) )
			return HandlerBase.Error( "Missing required 'target' parameter. Pass a world position 'x,y,z' or a GameObject id.", "orbit" );

		Vector3 target;
		if ( Guid.TryParse( targetStr, out var gid ) )
		{
			var go = scene.Directory.FindByGuid( gid );
			if ( go == null )
				return HandlerBase.Error( $"GameObject with id '{targetStr}' not found.", "orbit" );
			target = go.WorldPosition;
		}
		else
		{
			target = HandlerBase.ParseVector3( targetStr );
		}

		var distance = MathF.Max( HandlerBase.GetFloat( args, "distance", 200f ), MinDist );
		var yaw = HandlerBase.GetFloat( args, "yaw", 0f );
		var pitch = HandlerBase.GetFloat( args, "pitch", -15f );

		var rot = Rotation.From( pitch, yaw, 0f );
		var pos = target - rot.Forward * distance;
		eye.WorldPosition = pos;
		eye.WorldRotation = rot;

		var fovOpt = HandlerBase.GetFloat( args, "fov", -1f );
		if ( fovOpt > 0f ) _eyeFov = Math.Clamp( fovOpt, 5f, 170f );

		return HandlerBase.Success( new
		{
			ok = true,
			action = "orbit",
			id = eye.Id.ToString(),
			pos = HandlerBase.V3( pos ),
			angles = HandlerBase.Rot( rot ),
			target = HandlerBase.V3( target ),
			distance,
			yaw,
			pitch,
			fov = _eyeFov
		} );
	}

	private static object Frame( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "frame" );

		var eye = ResolveEye( scene );
		if ( eye == null )
			return HandlerBase.Error( "No mcp_eye GameObject in scene. Call eye.attach first.", "frame" );

		var targetStr = HandlerBase.GetString( args, "target" );
		var bboxStr = HandlerBase.GetString( args, "bbox" );
		var margin = MathF.Max( HandlerBase.GetFloat( args, "margin", 1.2f ), 1f );

		BBox bbox;
		if ( !string.IsNullOrEmpty( bboxStr ) )
		{
			var parts = bboxStr.Split( ',' ).Select( s => s.Trim() ).ToArray();
			if ( parts.Length != 6 )
				return HandlerBase.Error( "bbox format: 'minX,minY,minZ,maxX,maxY,maxZ'.", "frame" );
			bbox = new BBox(
				new Vector3( float.Parse( parts[0] ), float.Parse( parts[1] ), float.Parse( parts[2] ) ),
				new Vector3( float.Parse( parts[3] ), float.Parse( parts[4] ), float.Parse( parts[5] ) ) );
		}
		else if ( !string.IsNullOrEmpty( targetStr ) && Guid.TryParse( targetStr, out var gid ) )
		{
			var go = scene.Directory.FindByGuid( gid );
			if ( go == null )
				return HandlerBase.Error( $"GameObject with id '{targetStr}' not found.", "frame" );
			var renderer = go.Components.Get<ModelRenderer>();
			if ( renderer != null && renderer.SceneObject != null )
				bbox = renderer.Bounds;
			else
				bbox = BBox.FromPositionAndSize( go.WorldPosition, 64f );
		}
		else
		{
			return HandlerBase.Error( "Missing 'target' (GameObject id) or 'bbox' (minX,minY,minZ,maxX,maxY,maxZ).", "frame" );
		}

		var center = bbox.Center;
		var radius = bbox.Size.Length * 0.5f;
		var distance = MathF.Max( radius * 2f * margin, MinDist );

		// Use current eye yaw if eye already exists, else default-front pose.
		var currentYaw = eye.WorldRotation.Yaw();
		var rot = Rotation.From( -15f, currentYaw, 0f );
		var pos = center - rot.Forward * distance;
		eye.WorldPosition = pos;
		eye.WorldRotation = rot;

		return HandlerBase.Success( new
		{
			ok = true,
			action = "frame",
			id = eye.Id.ToString(),
			pos = HandlerBase.V3( pos ),
			angles = HandlerBase.Rot( rot ),
			bbox = new
			{
				center = HandlerBase.V3( center ),
				size = HandlerBase.V3( bbox.Size )
			},
			distance,
			margin,
			fov = _eyeFov
		} );
	}

	private static object Snapshot( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "snapshot" );

		var eye = ResolveEye( scene );
		if ( eye == null )
			return HandlerBase.Error( "No mcp_eye GameObject in scene. Call eye.attach first.", "snapshot" );

		var cam = ResolveMainCamera( scene );
		if ( cam == null )
			return HandlerBase.Error( "No CameraComponent in scene — snapshot needs a main camera as the render source.", "snapshot" );

		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 1280 ), 320, 3840 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 720 ), 240, 2160 );
		var quality = Math.Clamp( HandlerBase.GetInt( args, "quality", 75 ), 10, 100 );
		var withManifest = HandlerBase.GetBool( args, "manifest", false );

		var mode = withManifest
			? CaptureAnnotatedHelper.ManifestMode.Terse
			: CaptureAnnotatedHelper.ManifestMode.Terse;

		var result = CaptureAnnotatedHelper.Build(
			scene, cam, eye.WorldPosition, eye.WorldRotation, _eyeFov, width, height, quality, mode );

		var content = new List<object>();
		if ( result.ImageJpeg is { Length: > 0 } )
		{
			content.Add( new
			{
				type = "image",
				data = Convert.ToBase64String( result.ImageJpeg ),
				mimeType = "image/jpeg"
			} );
		}

		var summary = new
		{
			ok = result.ImageJpeg is { Length: > 0 },
			action = "snapshot",
			eye_id = eye.Id.ToString(),
			pose = new
			{
				pos = HandlerBase.V3( eye.WorldPosition ),
				angles = HandlerBase.Rot( eye.WorldRotation ),
				fov = _eyeFov
			},
			width = result.Width,
			height = result.Height,
			image_ok = result.ImageJpeg is { Length: > 0 },
			image_error = result.ImageError,
			image_bytes = result.ImageJpeg?.Length ?? 0,
			manifest_count = withManifest ? result.Manifest.Count : 0
		};

		content.Add( new
		{
			type = "text",
			text = JsonSerializer.Serialize( summary, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			} )
		} );

		return new { content };
	}

	private static object DepthPick( JsonElement args )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "depth_pick" );

		var eye = ResolveEye( scene );
		if ( eye == null )
			return HandlerBase.Error( "No mcp_eye GameObject in scene. Call eye.attach first.", "depth_pick" );

		var u = Math.Clamp( HandlerBase.GetFloat( args, "u", 0.5f ), 0f, 1f );
		var v = Math.Clamp( HandlerBase.GetFloat( args, "v", 0.5f ), 0f, 1f );
		var maxDistance = MathF.Max( HandlerBase.GetFloat( args, "max_distance", 16000f ), 1f );

		// Compute ray manually from eye pose + fov + (u,v).
		// FOV is interpreted as the HORIZONTAL field-of-view in degrees
		// (CameraComponent default FovAxis = Horizontal). Aspect ratio derives
		// from the snapshot dims so the picked ray matches what eye.snapshot
		// would render at that pixel.
		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 1280 ), 320, 3840 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 720 ), 240, 2160 );
		var aspect = (float)width / height;

		var halfFovHRad = MathX.DegreeToRadian( _eyeFov * 0.5f );
		var halfFovHTan = MathF.Tan( halfFovHRad );
		var halfFovVTan = halfFovHTan / aspect;

		// Center of image is (0.5, 0.5). u increases right, v increases down (screen convention).
		var screenX = ( u - 0.5f ) * 2f * halfFovHTan;
		var screenY = -( v - 0.5f ) * 2f * halfFovVTan;

		var rot = eye.WorldRotation;
		var dir = ( rot.Forward + rot.Right * screenX + rot.Up * screenY ).Normal;
		var origin = eye.WorldPosition;

		var trace = scene.Trace.Ray( origin, origin + dir * maxDistance ).Run();

		return HandlerBase.Success( new
		{
			ok = true,
			action = "depth_pick",
			eye_id = eye.Id.ToString(),
			pose = new
			{
				pos = HandlerBase.V3( origin ),
				angles = HandlerBase.Rot( rot ),
				fov = _eyeFov
			},
			pick_uv = new { u, v },
			ray_dir = HandlerBase.V3( dir ),
			hit = trace.Hit,
			hit_pos = trace.Hit ? (object)HandlerBase.V3( trace.HitPosition ) : null,
			hit_normal = trace.Hit ? (object)HandlerBase.V3( trace.Normal ) : null,
			hit_distance = trace.Hit ? (object)MathF.Round( trace.Distance, 2 ) : null,
			hit_fraction = trace.Hit ? (object)MathF.Round( trace.Fraction, 4 ) : null,
			hit_gameobject_id = trace.Hit && trace.GameObject != null
				? (object)trace.GameObject.Id.ToString() : null,
			hit_gameobject_name = trace.Hit && trace.GameObject != null
				? (object)trace.GameObject.Name : null
		} );
	}
}
