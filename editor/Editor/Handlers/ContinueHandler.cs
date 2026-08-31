using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// continue tool: retrieve next pages of large paginated responses.
/// When any tool response includes a '_cursor' block with 'has_more: true',
/// use this tool with the cursor_id to get the next page.
///
/// Migrated to the [McpTool] attribute scan in mcp-dynamic-discovery Phase 3
/// Wave 3.1 — single-action tool, MainThread dispatch.
/// </summary>
[McpTool( "continue",
	"Retrieve the next page of a large result that was automatically paginated. When any tool response includes a '_cursor' block with 'has_more: true', use this tool with the cursor_id to get the next page. Cursors expire after 60 seconds — if expired, re-run the original query. Do not call this tool unless you see a _cursor block in a previous response.",
	ReadOnlyHint = true )]
internal static class ContinueHandler
{
	/// <summary>
	/// 'next' action handler. The try/catch preserves the legacy
	/// <c>HandlerBase.Error</c> response shape — the pre-migration
	/// <c>Handle()</c> switch wrapped <see cref="Next"/> the same way.
	/// </summary>
	[McpAction( "next", ReadOnly = true )]
	internal static object HandleNext( JsonElement args )
	{
		try
		{
			return Next( args );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, "next" );
		}
	}

	private static object Next( JsonElement args )
	{
		var cursorId = HandlerBase.GetString( args, "cursor_id" );
		if ( string.IsNullOrEmpty( cursorId ) )
			return HandlerBase.Error( "Missing required 'cursor_id' parameter.", "next" );

		var result = CursorStore.GetNextPage( cursorId );
		if ( result == null )
			return HandlerBase.Error(
				$"Cursor '{cursorId}' not found or expired. Cursors expire after 60 seconds — re-run the original query.",
				"next" );

		return result;
	}

	/// <summary>
	/// tools/list inputSchema fragment — verbatim copy of the legacy
	/// <c>ToolRegistry.Continue.inputSchema</c> literal (mcp-dynamic-discovery
	/// Phase 3). The scanner invokes this once at plugin load and caches it.
	/// </summary>
	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>
		{
			["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "next" } },
			["cursor_id"] = new { type = "string", description = "Cursor ID from a previous response's _cursor block. Required." }
		},
		required = new[] { "action" },
		additionalProperties = false
	};
}
