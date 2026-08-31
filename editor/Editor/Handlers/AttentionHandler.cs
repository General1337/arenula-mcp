// Editor/Handlers/AttentionHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 1.3 of mcp-agent-capability-system initiative — sibling to Wave 1.1 capture.annotated SHIPPED 2026-05-15 and Wave 1.2 gizmo.* SHIPPED 2026-05-15. NOT a continuation of any thruster/cloud/atmosphere handoff; the bare 'wave1' substring collision against unrelated thruster handoffs does not inherit any hypothesis.]
//
// MCP handler for the 'attention' tool namespace. v1 ships:
//   attention.peek — viewport pose + selection + recent-selection history
//
// v1 SCOPE CUTS (deferred to v1.1, separate session):
//   - cursor_world_ray   — requires hook into the editor scene-viewport widget's
//                           cursor delivery; not exposed via a static engine API.
//                           Plan v1.1: subscribe to a widget mouse-move event,
//                           keep last cursor screen pos in a static, compute the
//                           ray on demand via SceneCamera.GetRay(pos, viewport_size).
//   - hit_entity / hit_pos — depends on cursor_world_ray.
//   - last_console_command — requires intercepting ConsoleSystem invocations.
//   - last_log_error       — requires hook into EditorHandler's ring buffer.
//
// What v1 DOES return:
//   {
//     focused: bool,                       // true if SceneEditorSession.Active != null
//     viewport_pose: { pos, angles, fov },  // editor scene camera or null
//     scene_name: string,
//     is_playing: bool,
//     selection: [{ id, name, type }, ...],
//     selection_count: int,
//     recent_selection: [{ id, name, ts_iso }, ...]   // ring buffer, last 10 adds
//   }

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

// [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — [McpTool] decoration; no handoff hypothesis inherited]
[McpTool( "attention",
	"Read what the human is looking at in the editor. Returns the active scene-viewport pose, current selection, and recent-selection history — so the agent can disambiguate 'this is wrong' without asking for a screenshot. v1 ships viewport_pose + selection + recent_selection; cursor_world_ray / last_console_command / last_log_error ship in v1.1." )]
internal static class AttentionHandler
{
	private sealed record SelectionRecord( string Id, string Name, DateTime Ts );

	private const int RecentCap = 10;
	private static readonly object _historyLock = new();
	private static readonly LinkedList<SelectionRecord> _recent = new();
	private static bool _historyHooked;
	private static SelectionSystem _hookedSel;

	// [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — Handle() action-switch dissolved into [McpAction]; no handoff hypothesis inherited]
	[McpAction( "peek", ReadOnly = true )]
	internal static object Peek( JsonElement args )
	{
		// SceneEditorSession.Active is the scene currently visible/edited in the editor
		// viewport. When it is null we have no scene context — return focused=false
		// cleanly per exit criterion (do NOT return null fields silently).
		SceneEditorSession session;
		try { session = SceneEditorSession.Active; }
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"SceneEditorSession.Active threw {ex.GetType().Name}: {ex.Message}", "peek" );
		}

		if ( session is null )
			return HandlerBase.Success( new { focused = false, reason = "no active SceneEditorSession (editor not focused on a scene)" } );

		EnsureHistoryHooked( session.Selection );

		// viewport_pose: read from the editor scene's main CameraComponent, falling
		// back to the first enabled camera. This matches the resolver shape used by
		// CaptureHandler and CameraHandler.
		object viewportPose = null;
		string sceneName = null;
		bool isPlaying = false;
		try
		{
			isPlaying = session.IsPlaying;
			var scene = session.Scene;
			if ( scene != null )
			{
				sceneName = scene.Name;
				CameraComponent cam = null;
				foreach ( var c in scene.GetAllComponents<CameraComponent>() )
				{
					if ( c.IsMainCamera && c.Enabled ) { cam = c; break; }
					if ( cam is null && c.Enabled ) cam = c;
				}
				if ( cam is not null && cam.GameObject.IsValid() )
				{
					var pos = cam.GameObject.WorldPosition;
					var ang = cam.GameObject.WorldRotation.Angles();
					viewportPose = new
					{
						pos = HandlerBase.V3( pos ),
						angles = HandlerBase.Rot( cam.GameObject.WorldRotation ),
						fov = MathF.Round( cam.FieldOfView, 2 )
					};
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"AttentionHandler viewport_pose probe threw: {ex.Message}" );
		}

		// selection: enumerate the SelectionSystem (objects are GameObject refs in
		// the typical case; Component is also possible for some editor flows).
		var selection = new List<object>();
		try
		{
			foreach ( var obj in session.Selection )
			{
				selection.Add( DescribeSelectionItem( obj ) );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"AttentionHandler selection probe threw: {ex.Message}" );
		}

		// recent_selection: ring buffer of the last N selection-adds, oldest first.
		List<object> recent;
		lock ( _historyLock )
		{
			recent = _recent.Select( r => (object)new
			{
				id = r.Id,
				name = r.Name,
				ts_iso = r.Ts.ToString( "yyyy-MM-ddTHH:mm:ssZ" )
			} ).ToList();
		}

		return HandlerBase.Success( new
		{
			focused = true,
			scene_name = sceneName,
			is_playing = isPlaying,
			viewport_pose = viewportPose,
			selection,
			selection_count = selection.Count,
			recent_selection = recent,
			v1_scope_cut_note = "cursor_world_ray / hit_entity / last_console_command ship in v1.1 — see docs/mcp-guide.md §attention.peek for the v1 shape."
		} );
	}

	/// <summary>
	/// tools/list inputSchema fragment — verbatim copy of the legacy
	/// <c>ToolRegistry.Attention.inputSchema</c> literal (mcp-dynamic-discovery
	/// Phase 2). The scanner invokes this once at plugin load and caches it.
	/// </summary>
	// [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — InputSchema() composer; no handoff hypothesis inherited]
	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>
		{
			["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "peek" } }
		},
		required = new[] { "action" },
		additionalProperties = false
	};

	private static object DescribeSelectionItem( object obj )
	{
		if ( obj is GameObject go )
			return new { id = go.Id.ToString(), name = go.Name ?? "", type = "GameObject" };
		if ( obj is Component c )
			return new { id = c.Id.ToString(), name = c.GameObject?.Name ?? "", type = c.GetType().Name };
		return new { id = "<unknown>", name = obj?.ToString() ?? "<null>", type = obj?.GetType().Name ?? "<null>" };
	}

	private static void EnsureHistoryHooked( SelectionSystem sel )
	{
		if ( sel is null ) return;
		if ( _historyHooked && ReferenceEquals( _hookedSel, sel ) ) return;

		// Active session changed — rebind. Detach previous if still alive.
		if ( _hookedSel is not null )
		{
			try { _hookedSel.OnItemAdded -= OnSelectionAdded; }
			catch { /* best-effort */ }
		}
		try
		{
			sel.OnItemAdded += OnSelectionAdded;
			_hookedSel = sel;
			_historyHooked = true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"AttentionHandler failed to hook SelectionSystem.OnItemAdded: {ex.Message}" );
			_historyHooked = false;
			_hookedSel = null;
		}
	}

	private static void OnSelectionAdded( object obj )
	{
		string id = "<unknown>";
		string name = "";
		if ( obj is GameObject go ) { id = go.Id.ToString(); name = go.Name ?? ""; }
		else if ( obj is Component c ) { id = c.Id.ToString(); name = c.GameObject?.Name ?? ""; }

		lock ( _historyLock )
		{
			_recent.AddLast( new SelectionRecord( id, name, DateTime.UtcNow ) );
			while ( _recent.Count > RecentCap ) _recent.RemoveFirst();
		}
	}
}
