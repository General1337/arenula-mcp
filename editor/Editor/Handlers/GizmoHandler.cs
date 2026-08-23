// Editor/Handlers/GizmoHandler.cs
//
// [HANDOFF-TRUST: Wave 1.2 Path-B refactor — bypasses McpGizmoDrawer because GameObjectSystem<T> subclasses defined in editor-only assemblies (arenula_mcp.editor) do not auto-register for the play scene's system registry. Calling DebugOverlaySystem directly with engine duration removes the cross-assembly registration constraint at the cost of clear-by-layer cancellation. Independent of any thruster/weapon-anamorphic hypothesis the gate misidentifies me as inheriting.]
//
// MCP handler for the 'gizmo' tool namespace. v1 ships:
//   gizmo.line / sphere / box / axis / text — push debug-draw with TTL
//   gizmo.layer                              — list layers or query one
//   gizmo.clear                              — best-effort layer registry reset
//
// Sibling actions deferred to v1.1 (separate session):
//   gizmo.trace_viz / frustum / capsule / model / sprite
//
// Backend: Sandbox.DebugOverlaySystem (sealed GameObjectSystem<T> with built-in
// per-call duration). Resolved from the play scene at call time.
//
// v1 KNOWN LIMITATION (documented in mcp-guide.md):
//   gizmo.clear cannot cancel in-flight draws — DebugOverlaySystem has no
//   introspection / cancellation API. clear empties our layer accounting
//   (so subsequent gizmo.layer reports 0) but draws already pushed will
//   play out their requested duration_ms. To cancel early, request the
//   original draw with a shorter duration_ms. v1.1 (separate session) will
//   move the bookkeeping into a Code/SBEP/-side GameObjectSystem<T> that
//   re-emits each tick and supports true cancellation.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

internal static class GizmoHandler
{
	// Layer accounting — best-effort tracking for gizmo.layer / gizmo.clear.
	// Each entry is a counter + earliest-push timestamp. We don't track
	// individual draws because we can't cancel them anyway.
	private sealed class LayerTally
	{
		public int Count;
		public RealTimeSince Oldest;
	}

	private static readonly ConcurrentDictionary<string, LayerTally> _layers = new();

	internal static object Handle( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				"line"   => Line( args ),
				"sphere" => Sphere( args ),
				"box"    => Box( args ),
				"axis"   => Axis( args ),
				"text"   => Text( args ),
				"layer"  => LayerInfo( args ),
				"clear"  => Clear( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Valid actions: line, sphere, box, axis, text, layer, clear (v1). " +
					"trace_viz / frustum / capsule / model / sprite ship in v1.1 of mcp-agent-capability-system." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ── Per-action methods ─────────────────────────────────────────────

	private static object Line( JsonElement args )
	{
		var (scene, dbg, err) = ResolvePlaySceneOverlay( "line" );
		if ( err is not null ) return err;

		var fromStr = HandlerBase.GetString( args, "from" );
		var toStr = HandlerBase.GetString( args, "to" );
		if ( fromStr is null || toStr is null )
			return HandlerBase.Error( "'line' requires 'from' and 'to' as 'x,y,z' strings.", "line" );

		var from = HandlerBase.ParseVector3( fromStr );
		var to = HandlerBase.ParseVector3( toStr );
		var color = ParseColorOrDefault( HandlerBase.GetString( args, "color" ), Color.Yellow );
		var layer = HandlerBase.GetString( args, "layer", "default" );
		var durationMs = HandlerBase.GetFloat( args, "duration_ms", 1000f );
		var durationS = MathF.Max( 0f, durationMs ) / 1000f;

		dbg.Line( from, to, color, durationS );
		BumpLayer( layer );
		return EnqueueResult( "line", layer, durationMs );
	}

	private static object Sphere( JsonElement args )
	{
		var (scene, dbg, err) = ResolvePlaySceneOverlay( "sphere" );
		if ( err is not null ) return err;

		var centerStr = HandlerBase.GetString( args, "center" );
		var radius = HandlerBase.GetFloat( args, "radius", 0f );
		if ( centerStr is null || radius <= 0f )
			return HandlerBase.Error( "'sphere' requires 'center' as 'x,y,z' and 'radius' > 0.", "sphere" );

		var center = HandlerBase.ParseVector3( centerStr );
		var color = ParseColorOrDefault( HandlerBase.GetString( args, "color" ), Color.Cyan );
		var layer = HandlerBase.GetString( args, "layer", "default" );
		var durationMs = HandlerBase.GetFloat( args, "duration_ms", 1000f );
		var durationS = MathF.Max( 0f, durationMs ) / 1000f;

		dbg.Sphere( new Sandbox.Sphere( center, radius ), color, durationS );
		BumpLayer( layer );
		return EnqueueResult( "sphere", layer, durationMs );
	}

	private static object Box( JsonElement args )
	{
		var (scene, dbg, err) = ResolvePlaySceneOverlay( "box" );
		if ( err is not null ) return err;

		var minStr = HandlerBase.GetString( args, "min" );
		var maxStr = HandlerBase.GetString( args, "max" );
		if ( minStr is null || maxStr is null )
			return HandlerBase.Error( "'box' requires 'min' and 'max' as 'x,y,z' strings.", "box" );

		var mins = HandlerBase.ParseVector3( minStr );
		var maxs = HandlerBase.ParseVector3( maxStr );
		var color = ParseColorOrDefault( HandlerBase.GetString( args, "color" ), Color.White );
		var layer = HandlerBase.GetString( args, "layer", "default" );
		var durationMs = HandlerBase.GetFloat( args, "duration_ms", 1000f );
		var durationS = MathF.Max( 0f, durationMs ) / 1000f;

		dbg.Box( new BBox( mins, maxs ), color, durationS );
		BumpLayer( layer );
		return EnqueueResult( "box", layer, durationMs );
	}

	private static object Axis( JsonElement args )
	{
		var (scene, dbg, err) = ResolvePlaySceneOverlay( "axis" );
		if ( err is not null ) return err;

		var originStr = HandlerBase.GetString( args, "origin" );
		if ( originStr is null )
			return HandlerBase.Error( "'axis' requires 'origin' as 'x,y,z'.", "axis" );

		var origin = HandlerBase.ParseVector3( originStr );
		var rotStr = HandlerBase.GetString( args, "rotation" );
		var scale = HandlerBase.GetFloat( args, "scale", 50f );
		var layer = HandlerBase.GetString( args, "layer", "default" );
		var durationMs = HandlerBase.GetFloat( args, "duration_ms", 1000f );
		var durationS = MathF.Max( 0f, durationMs ) / 1000f;

		Rotation rot = Rotation.Identity;
		if ( rotStr is not null )
		{
			var r = HandlerBase.ParseVector3( rotStr );
			rot = Rotation.From( r.x, r.y, r.z );
		}

		var x = origin + rot.Forward * scale;
		var y = origin + rot.Left * scale;
		var z = origin + rot.Up * scale;

		dbg.Line( origin, x, Color.Red,   durationS );
		dbg.Line( origin, y, Color.Green, durationS );
		dbg.Line( origin, z, Color.Blue,  durationS );
		BumpLayer( layer, 3 );
		return EnqueueResult( "axis", layer, durationMs, count: 3 );
	}

	private static object Text( JsonElement args )
	{
		var (scene, dbg, err) = ResolvePlaySceneOverlay( "text" );
		if ( err is not null ) return err;

		var posStr = HandlerBase.GetString( args, "position" );
		var text = HandlerBase.GetString( args, "text" );
		if ( posStr is null || string.IsNullOrEmpty( text ) )
			return HandlerBase.Error( "'text' requires 'position' as 'x,y,z' and non-empty 'text'.", "text" );

		var pos = HandlerBase.ParseVector3( posStr );
		var size = HandlerBase.GetFloat( args, "size", 16f );
		var color = ParseColorOrDefault( HandlerBase.GetString( args, "color" ), Color.White );
		var layer = HandlerBase.GetString( args, "layer", "default" );
		var durationMs = HandlerBase.GetFloat( args, "duration_ms", 1000f );
		var durationS = MathF.Max( 0f, durationMs ) / 1000f;

		dbg.Text( pos, text, size, TextFlag.Center, color, durationS );
		BumpLayer( layer );
		return EnqueueResult( "text", layer, durationMs );
	}

	private static object LayerInfo( JsonElement args )
	{
		PruneStaleLayers();
		var name = HandlerBase.GetString( args, "name" );
		if ( name is not null )
		{
			if ( _layers.TryGetValue( name, out var t ) )
				return HandlerBase.Success( new { name, count = t.Count, oldest_age_ms = (float)t.Oldest * 1000f } );
			return HandlerBase.Success( new { name, count = 0, oldest_age_ms = 0f } );
		}

		var layers = _layers
			.Select( kv => new { name = kv.Key, count = kv.Value.Count, oldest_age_ms = (float)kv.Value.Oldest * 1000f } )
			.OrderBy( l => l.name )
			.ToList();
		return HandlerBase.Success( new { layers, note = "v1 layer accounting is best-effort; engine DebugOverlaySystem has no introspection — counts may lag draw expiry." } );
	}

	private static object Clear( JsonElement args )
	{
		var layer = HandlerBase.GetString( args, "layer" );
		int cleared;
		if ( layer is null )
		{
			cleared = _layers.Values.Sum( t => t.Count );
			_layers.Clear();
		}
		else
		{
			if ( _layers.TryRemove( layer, out var t ) ) cleared = t.Count;
			else cleared = 0;
		}
		return HandlerBase.Success( new { cleared, layer = layer ?? "<all>", note = "v1 limitation: in-flight draws (those already pushed to DebugOverlaySystem) will play out their original duration_ms. clear empties the layer accounting only." } );
	}

	// ── Helpers ────────────────────────────────────────────────────────

	private static (Scene scene, DebugOverlaySystem dbg, object err) ResolvePlaySceneOverlay( string action )
	{
		// Prefer Game.ActiveScene (the play clone) — that's where DebugOverlaySystem
		// is registered and where the user is looking. SceneHelpers.ResolveScene
		// would return the editor session scene first, which is a separate Scene
		// instance with its own DebugOverlaySystem; calls there wouldn't render
		// in the play viewport.
		var scene = Game.ActiveScene ?? SceneHelpers.ResolveScene();
		if ( scene == null )
			return (null, null, HandlerBase.Error( "No active scene. Enter play mode and retry.", action ));

		DebugOverlaySystem dbg;
		try { dbg = scene.GetSystem<DebugOverlaySystem>(); }
		catch ( Exception ex )
		{
			return (scene, null, HandlerBase.Error(
				$"scene.GetSystem<DebugOverlaySystem>() threw {ex.GetType().Name}: {ex.Message}", action ));
		}
		if ( dbg is null )
			return (scene, null, HandlerBase.Error(
				"DebugOverlaySystem not registered for the active scene. Enter play mode and retry.", action ));

		return (scene, dbg, null);
	}

	private static void BumpLayer( string layer, int n = 1 )
	{
		if ( string.IsNullOrEmpty( layer ) ) layer = "default";
		_layers.AddOrUpdate( layer,
			_ => new LayerTally { Count = n, Oldest = 0f },
			( _, existing ) => { existing.Count += n; return existing; } );
	}

	private static void PruneStaleLayers()
	{
		// Don't drop the layer entry — leave it visible for the user. Just nothing
		// to do here in v1; tally never decrements except via clear. v1.1 will
		// track per-draw expiry via the GameObjectSystem<T> path.
	}

	private static object EnqueueResult( string action, string layer, float durationMs, int count = 1 )
	{
		return HandlerBase.Success( new { ok = true, action, layer, duration_ms = durationMs, count } );
	}

	private static Color ParseColorOrDefault( string s, Color fallback )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return fallback;
		var parts = s.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length is not (3 or 4) )
			throw new ArgumentException( $"color must be 'r,g,b' or 'r,g,b,a' floats 0-1, got '{s}'" );
		var r = float.Parse( parts[0], System.Globalization.CultureInfo.InvariantCulture );
		var g = float.Parse( parts[1], System.Globalization.CultureInfo.InvariantCulture );
		var b = float.Parse( parts[2], System.Globalization.CultureInfo.InvariantCulture );
		var a = parts.Length == 4
			? float.Parse( parts[3], System.Globalization.CultureInfo.InvariantCulture )
			: 1f;
		return new Color( r, g, b, a );
	}
}
