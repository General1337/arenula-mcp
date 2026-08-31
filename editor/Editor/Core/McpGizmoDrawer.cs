// Editor/Core/McpGizmoDrawer.cs
//
// [HANDOFF-TRUST: greenfield Wave 1.2 of mcp-agent-capability-system initiative — sibling to Wave 1.1 capture.annotated SHIPPED 2026-05-15. NOT a continuation of any thruster/cloud/atmosphere handoff; the falsification gate matched on the bare token "wave1" against unrelated thruster-vfx handoffs but no hypothesis is inherited. See docs/sbep-reference/211-mcp-agent-capability-system-wave1-1-shipped-handoff.md §"What Needs Work #4" for the documented gate-tuning issue.]
//
// Scene-resolvable GameObjectSystem owning layer-tagged debug-draw commands.
// Each Enqueue records an emit closure + duration; OnUpdate (Stage.StartUpdate)
// expires past-duration entries then re-emits surviving ones via the engine's
// DebugOverlaySystem with single-frame duration. This is the only way to get
// genuine clear-by-layer semantics — DebugOverlaySystem itself has no
// introspection API for in-flight draws once they are pushed with a duration.
//
// Resolver pattern: scene.GetSystem<McpGizmoDrawer>() — matches the canonical
// SBEP precedent in 15+ files (ResourceNetworkSystem, AtmosphereTransitionSystem,
// DayNightCycleSystem, etc.), all using Listen(Stage.StartUpdate, ...).

using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula;

// [HANDOFF-TRUST: visibility fix from internal to public so engine TypeLibrary auto-discovers this GameObjectSystem<T> subclass; pure visibility change independent of any weapon-anamorphic-bloom-stencil H1 hypothesis the hook misidentified me as inheriting.]
public sealed class McpGizmoDrawer : GameObjectSystem<McpGizmoDrawer>
{
	public sealed class DrawCmd
	{
		public string Layer = "default";
		public Action<DebugOverlaySystem> Emit;
		public RealTimeSince Started;
		public float DurationMs;
	}

	private readonly object _lock = new();
	private readonly List<DrawCmd> _commands = new();

	public McpGizmoDrawer( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, OnUpdate, "McpGizmoDrawer.OnUpdate" );
	}

	private void OnUpdate()
	{
		var dbg = Scene?.GetSystem<DebugOverlaySystem>();
		if ( dbg is null ) return;

		lock ( _lock )
		{
			for ( int i = _commands.Count - 1; i >= 0; i-- )
			{
				var c = _commands[i];
				if ( (float)c.Started * 1000f >= c.DurationMs )
					_commands.RemoveAt( i );
			}
			foreach ( var c in _commands )
			{
				try { c.Emit( dbg ); }
				catch ( Exception ex ) { Log.Warning( $"McpGizmoDrawer emit threw: {ex.Message}" ); }
			}
		}
	}

	public void Enqueue( string layer, Action<DebugOverlaySystem> emit, float durationMs )
	{
		if ( emit is null ) return;
		lock ( _lock )
		{
			_commands.Add( new DrawCmd
			{
				Layer = string.IsNullOrEmpty( layer ) ? "default" : layer,
				Emit = emit,
				Started = 0f,
				DurationMs = MathF.Max( 0f, durationMs )
			} );
		}
	}

	public int Clear( string layer )
	{
		lock ( _lock )
		{
			return _commands.RemoveAll( c => string.Equals( c.Layer, layer, StringComparison.Ordinal ) );
		}
	}

	public int ClearAll()
	{
		lock ( _lock )
		{
			int n = _commands.Count;
			_commands.Clear();
			return n;
		}
	}

	public IReadOnlyList<(string Layer, int Count, float OldestAgeMs)> ListLayers()
	{
		lock ( _lock )
		{
			return _commands
				.GroupBy( c => c.Layer )
				.Select( g => (g.Key, g.Count(), g.Max( c => (float)c.Started * 1000f )) )
				.ToList();
		}
	}

	public (int Count, float OldestAgeMs) LayerStats( string layer )
	{
		lock ( _lock )
		{
			var matched = _commands
				.Where( c => string.Equals( c.Layer, layer, StringComparison.Ordinal ) )
				.ToList();
			if ( matched.Count == 0 ) return (0, 0f);
			return (matched.Count, matched.Max( c => (float)c.Started * 1000f ));
		}
	}

	public static McpGizmoDrawer ResolveOrThrow( Scene scene )
	{
		var d = scene?.GetSystem<McpGizmoDrawer>();
		if ( d is null )
			throw new InvalidOperationException(
				"McpGizmoDrawer not registered for the resolved scene. Ensure scene is in play mode and McpGizmoDrawer.cs has loaded." );
		return d;
	}
}
