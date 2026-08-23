// Editor/Handlers/McpHealthTool.cs
//
// mcp-dynamic-discovery Phase 2 — first [McpTool] migration. 'mcp.health' is
// extracted from the then-3-tool McpHandler into its own attribute-decorated class
// so the [McpTool] one-name-per-class contract holds. (Wave 3.2 migrated the last
// two — mcp.capabilities + mcp.server_log — and deleted McpHandler entirely.)
//
// [HANDOFF-TRUST: charter-spec feature build per docs/ai/initiatives/mcp-dynamic-discovery/charter.md §Phase 2 — no handoff hypothesis inherited; the falsification-gate matched unrelated 'phase2' substring-collision handoffs.]

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;

namespace Arenula;

/// <summary>
/// MCP tool <c>mcp.health</c> — server health + live editor context. Migrated
/// from the former <c>McpHandler</c> to the <see cref="McpToolAttribute"/> scan
/// path (mcp-dynamic-discovery Phase 2). Degenerate 0-action tool: the single
/// <see cref="McpActionAttribute"/> handler is keyed on the empty action string
/// and is dispatched whenever a <c>tools/call</c> arrives for this tool (which,
/// per its inputSchema, never carries an <c>action</c> param).
/// </summary>
[McpTool( "mcp.health",
	"Return MCP server health and live editor context. Use this before a tool sequence to confirm transport, port, session count, play mode, active scene, and retained log health. This is read-only and has no required action parameter.",
	ReadOnlyHint = true )]
internal static class McpHealthTool
{
	/// <summary>
	/// 0-action handler (empty action key). The hybrid dispatcher routes a
	/// no-action <c>tools/call</c> for <c>mcp.health</c> here.
	/// <paramref name="args"/> is unused — the tool's inputSchema declares no
	/// properties. Body is the verbatim former <c>McpHandler.Health()</c> plus
	/// the same try/catch wrapper <c>McpHandler.Handle</c> applied.
	/// </summary>
	[McpAction( "", ReadOnly = true )]
	internal static object Handle( JsonElement args )
	{
		try
		{
			EditorHandler.EnsureInitialized();

			var session = SceneEditorSession.Active;
			var scene = session?.Scene;
			var logs = EditorHandler.LogSnapshot();
			var recentErrors = logs.Count( e => string.Equals( e.Level, "error", StringComparison.OrdinalIgnoreCase ) );

			int rootObjects = 0;
			int totalObjects = 0;
			if ( scene != null )
			{
				try
				{
					rootObjects = scene.Children.Count;
					totalObjects = SceneHelpers.WalkAll( scene, includeDisabled: true ).Count();
				}
				catch { }
			}

			return HandlerBase.Success( new
			{
				ok = ArenulaMcpServer.IsRunning,
				server = new
				{
					name = "Arenula Editor",
					version = ToolRegistry.Version,
					port = ArenulaMcpServer.Port,
					running = ArenulaMcpServer.IsRunning,
					session_count = ArenulaMcpServer.SessionCount,
					transport = "streamable-http"
				},
				editor = new
				{
					has_active_session = session != null,
					is_playing = session?.IsPlaying == true,
					scene_name = scene?.Name,
					root_objects = rootObjects,
					total_objects = totalObjects
				},
				log = new
				{
					retained = logs.Length,
					cap = EditorHandler.LogCap,
					recent_error_count = recentErrors
				},
				// [HANDOFF-TRUST: additive liveness field for editor stall self-report, independent of inherited handoff hypotheses]
				// Stall self-report: read from EditorHeartbeat's volatile tick (any-thread safe).
				// If ms_since_main_thread_tick grows large while this call still returns, the
				// editor main thread is frozen (zombie) — see EditorHeartbeat + tools/editor-health.py.
				liveness = new
				{
					last_main_thread_tick_ms = EditorHeartbeat.LastMainThreadTickUnixMs,
					ms_since_main_thread_tick = EditorHeartbeat.LastMainThreadTickUnixMs > 0
						? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - EditorHeartbeat.LastMainThreadTickUnixMs
						: -1L
				}
			} );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, "mcp.health" );
		}
	}

	/// <summary>
	/// tools/list inputSchema fragment — verbatim copy of the legacy
	/// <c>ToolRegistry.McpHealth.inputSchema</c> literal. <c>mcp.health</c> takes
	/// no parameters, so this is the empty-object schema.
	/// </summary>
	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>(),
		additionalProperties = false
	};
}
