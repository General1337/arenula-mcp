// Editor/Handlers/McpServerLogTool.cs
//
// mcp-dynamic-discovery Phase 3 Wave 3.2 — [McpTool] migration of 'mcp.server_log'.
// Extracted from the former McpHandler (deleted this wave) so the one-name-per-class
// [McpTool] contract holds. Siblings: McpHealthTool (Phase 2), McpCapabilitiesTool
// (this wave).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// MCP tool <c>mcp.server_log</c> — paginated read of the MCP/editor structured
/// log ring buffer. Migrated from the former <c>McpHandler</c> to the
/// <see cref="McpToolAttribute"/> scan path (mcp-dynamic-discovery Phase 3 Wave 3.2).
/// Degenerate 0-action tool: the single <see cref="McpActionAttribute"/> handler is
/// keyed on the empty action string and dispatched whenever a <c>tools/call</c>
/// arrives for this tool (its inputSchema declares no <c>action</c> param).
/// </summary>
[McpTool( "mcp.server_log",
	"Read the MCP/editor structured log ring buffer. Use this to inspect recent server errors, tool calls, compile messages, and runtime log entries captured by the live editor. Supports count, filter, offset, and limit; this is read-only and has no required action parameter.",
	ReadOnlyHint = true )]
internal static class McpServerLogTool
{
	/// <summary>
	/// 0-action handler (empty action key). The hybrid dispatcher routes a
	/// no-action <c>tools/call</c> for <c>mcp.server_log</c> here. Body is the
	/// verbatim former <c>McpHandler.ServerLog()</c> plus the same try/catch
	/// wrapper <c>McpHandler.Handle</c> applied.
	/// </summary>
	[McpAction( "", ReadOnly = true )]
	internal static object Handle( JsonElement args )
	{
		try
		{
			EditorHandler.EnsureInitialized();

			var count = HandlerBase.GetInt( args, "count", 200 );
			var filter = HandlerBase.GetString( args, "filter" );
			var level = HandlerBase.GetString( args, "level" );
			var source = HandlerBase.GetString( args, "source" );
			var offset = HandlerBase.GetInt( args, "offset", 0 );
			var limit = HandlerBase.GetInt( args, "limit", 100 );
			// Findings #6 payload diet: inline stacks are ~2KB/item — at the default
			// count=200 that was 250KB+ per call. Opt-in only; stacks_omitted in the
			// envelope tells the agent a re-call with include_stacks=true has more.
			var includeStacks = HandlerBase.GetBool( args, "include_stacks", false );
			if ( limit < 1 ) limit = 1;
			if ( limit > 500 ) limit = 500;

			var entries = EditorHandler.LogSnapshot();
			var bufferSize = entries.Length;
			if ( count > 0 && count < entries.Length )
				entries = entries[^count..];

			if ( !string.IsNullOrEmpty( level ) )
			{
				var wanted = level.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
					.Select( s => s.ToLowerInvariant() )
					.ToHashSet();
				entries = entries.Where( e => wanted.Contains( (e.Level ?? "").ToLowerInvariant() ) ).ToArray();
			}

			if ( !string.IsNullOrEmpty( source ) )
			{
				var wanted = source.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
					.Select( s => s.ToLowerInvariant() )
					.ToHashSet();
				entries = entries.Where( e => wanted.Contains( (e.Source ?? "").ToLowerInvariant() ) ).ToArray();
			}

			if ( !string.IsNullOrEmpty( filter ) )
			{
				entries = entries.Where( e =>
					(e.Message ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0 ).ToArray();
			}

			var total = entries.Length;
			var pageEntries = entries.Skip( offset ).Take( limit ).ToArray();
			int stacksOmitted = includeStacks ? 0 : pageEntries.Count( e => !string.IsNullOrEmpty( e.Stack ) );
			var page = pageEntries.Select( e => new
			{
				time = e.Time.ToString( "yyyy-MM-ddTHH:mm:ss.fffK" ),
				level = e.Level,
				logger = e.Logger,
				message = e.Message,
				stack = includeStacks && !string.IsNullOrEmpty( e.Stack ) ? e.Stack : null,
				source = e.Source,
				repeats = e.Repeats,
				operation_id = e.OperationId
			} ).ToList();

			return HandlerBase.Success( new
			{
				items = page,
				count = page.Count,
				total,
				offset,
				limit,
				has_more = offset + limit < total,
				buffer_size = bufferSize,
				buffer_cap = EditorHandler.LogCap,
				stacks_omitted = stacksOmitted > 0 ? stacksOmitted : (int?)null
			} );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, "mcp.server_log", null, null, McpErrorCode.InternalException );
		}
	}

	/// <summary>
	/// tools/list inputSchema fragment — verbatim copy of the legacy
	/// <c>ToolRegistry.McpServerLog.inputSchema</c> literal.
	/// </summary>
	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>
		{
			["count"] = new { type = "integer", description = "Trim to the last N retained entries before filtering. Default: 200." },
			["filter"] = new { type = "string", description = "Case-insensitive substring filter over messages." },
			["level"] = new { type = "string", description = "Comma-separated severity filter such as 'error' or 'warn,error'." },
			["source"] = new { type = "string", description = "Comma-separated source filter such as 'engine' or 'mcp'." },
			["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
			["limit"] = new { type = "integer", description = "Max entries to return. Default: 100." },
			["include_stacks"] = new { type = "boolean", description = "Include full inline stack traces per entry (~2KB each). Default: false — the envelope's stacks_omitted reports how many entries have one available." }
		},
		additionalProperties = false
	};
}
