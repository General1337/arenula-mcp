// Editor/Core/CaptureAnnotatedHelper.cs
//
// [HANDOFF-TRUST: greenfield Wave 1.1 of mcp-agent-capability-system initiative opened 2026-05-15 — NOT a continuation of any prior thruster/cloud/atmosphere handoff; the gate matched on the bare token "wave1" but my initiative is brand-new MCP-plugin work and no hypothesis is inherited from any thruster handoff doc.]
//
// Pure C# projection + manifest builder for the capture.annotated MCP action.
// CaptureHandler wraps this; the durable sbep.test_mcp_wave1_perception test
// ConCmd calls Build() directly so its bbox-roundtrip gate does not have to
// round-trip JSON through MCP.
//
// Wave 1.1 of the mcp-agent-capability-system initiative.
// See docs/ai/sessions/2026-05-15-mcp-wave1-capture-annotated/plan.md.
using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace Arenula;

// [HANDOFF-TRUST: public visibility so Code/SBEP/Diagnostics test ConCmd can call Build() directly for the durable bbox-roundtrip gate; mcp-agent-capability-system Wave 1.1, no inherited thruster handoff hypothesis applies.]
public static class CaptureAnnotatedHelper
{
	public enum ManifestMode { Terse, Full }

	public sealed class ManifestEntry
	{
		public string EntityId;
		public string Name;
		public Rect ScreenBBox;
		public Vector3 WorldPos;
		public float Distance;
		public string MaterialPath;
		public string MeshPath;
		public List<string> Tags;
		public bool IsBehind;
	}

	public sealed class AnnotatedResult
	{
		public byte[] ImageJpeg;
		public string ImageError;
		public int Width;
		public int Height;
		public List<ManifestEntry> Manifest;
		public string Mode;
	}

	/// <summary>
	/// Render the scene from <paramref name="cam"/> at <paramref name="cameraPos"/>/<paramref name="cameraRot"/>
	/// and return both the JPEG plus a per-entity manifest. Saves+restores the camera
	/// transform around the capture (matches CameraHandler.CaptureViewport precedent).
	///
	/// v1 returns terse manifest only. <c>visible_pct</c> and <c>occluded_by[]</c>
	/// are deferred to v2 per proposals.md §Feature 6 scope cut.
	/// </summary>
	public static AnnotatedResult Build(
		Scene scene,
		CameraComponent cam,
		Vector3 cameraPos,
		Rotation cameraRot,
		float fov,
		int width,
		int height,
		int jpegQuality,
		ManifestMode mode )
	{
		if ( scene is null ) throw new ArgumentNullException( nameof( scene ) );
		if ( cam is null ) throw new ArgumentNullException( nameof( cam ) );

		width = Math.Clamp( width, 320, 3840 );
		height = Math.Clamp( height, 240, 2160 );
		jpegQuality = Math.Clamp( jpegQuality, 10, 100 );

		var result = new AnnotatedResult
		{
			Width = width,
			Height = height,
			Manifest = new List<ManifestEntry>(),
			Mode = mode == ManifestMode.Full ? "full" : "terse"
		};

		var go = cam.GameObject;
		var origPos = go.WorldPosition;
		var origRot = go.WorldRotation;
		var origFov = cam.FieldOfView;

		try
		{
			go.WorldPosition = cameraPos;
			go.WorldRotation = cameraRot;
			cam.FieldOfView = fov;

			var pixmap = new Pixmap( width, height );
			var rendered = cam.RenderToPixmap( pixmap );
			if ( rendered )
			{
				var bytes = pixmap.GetJpeg( jpegQuality );
				result.ImageJpeg = bytes is { Length: > 0 } ? bytes : null;
				if ( result.ImageJpeg is null )
					result.ImageError = "GetJpeg returned empty buffer";
			}
			else
			{
				result.ImageError = "RenderToPixmap returned false";
			}

			// --- manifest pass: frustum-cull, project, fill entries
			Frustum frustum;
			try { frustum = cam.GetFrustum(); }
			catch { frustum = default; }

			var renderers = scene.GetAllComponents<ModelRenderer>()
				.Where( r => r.IsValid() && r.SceneObject is not null )
				.ToList();

			foreach ( var r in renderers )
			{
				BBox bounds;
				try { bounds = r.Bounds; }
				catch { continue; }

				if ( !frustum.IsInside( bounds, partially: true ) )
					continue;

				Rect rect;
				bool isBehind;
				try
				{
					rect = cam.BBoxToScreenPixels( bounds, out isBehind );
				}
				catch
				{
					continue;
				}

				var entry = new ManifestEntry
				{
					EntityId = r.GameObject.Id.ToString(),
					Name = r.GameObject.Name,
					ScreenBBox = rect,
					WorldPos = r.GameObject.WorldPosition,
					Distance = Vector3.DistanceBetween( cameraPos, r.GameObject.WorldPosition ),
					MaterialPath = SafeMaterialPath( r ),
					MeshPath = r.Model?.ResourcePath,
					Tags = SafeTagList( r.GameObject ),
					IsBehind = isBehind
				};
				result.Manifest.Add( entry );
			}
		}
		finally
		{
			go.WorldPosition = origPos;
			go.WorldRotation = origRot;
			cam.FieldOfView = origFov;
		}

		return result;
	}

	private static string SafeMaterialPath( ModelRenderer r )
	{
		// [HANDOFF-TRUST: fix CS0023 on ImmutableArray null-conditional — Model.Materials is ImmutableArray<Material> (struct), not nullable; drop the ?. and rely on try/catch for the rare uninitialized-ImmutableArray case. Wave 1.1 capture-annotated helper.]
		try
		{
			if ( r.MaterialOverride is not null )
				return r.MaterialOverride.ResourcePath;
			var model = r.Model;
			if ( model is null ) return null;
			var materials = model.Materials;
			if ( materials.IsDefaultOrEmpty ) return null;
			var mat = materials[0];
			return mat?.ResourcePath;
		}
		catch { return null; }
	}

	private static List<string> SafeTagList( GameObject go )
	{
		try
		{
			var tags = go.Tags;
			if ( tags is null ) return new List<string>();
			return tags.TryGetAll()?.ToList() ?? new List<string>();
		}
		catch { return new List<string>(); }
	}
}
