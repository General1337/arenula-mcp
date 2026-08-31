// Editor/Handlers/McpCapabilitiesTool.cs
//
// mcp-dynamic-discovery Phase 3 Wave 3.2 — [McpTool] migration of 'mcp.capabilities'.
// Extracted from the former McpHandler (deleted this wave — its last two tools,
// mcp.capabilities + mcp.server_log, both migrate to the [McpTool] attribute scan)
// so the one-name-per-class [McpTool] contract holds. Siblings: McpHealthTool
// (Phase 2), McpServerLogTool (this wave).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// MCP tool <c>mcp.capabilities</c> — compact tool-inventory + server-capability
/// summary. Migrated from the former <c>McpHandler</c> to the
/// <see cref="McpToolAttribute"/> scan path (mcp-dynamic-discovery Phase 3 Wave 3.2).
/// Degenerate 0-action tool: the single <see cref="McpActionAttribute"/> handler is
/// keyed on the empty action string and dispatched whenever a <c>tools/call</c>
/// arrives for this tool (its inputSchema declares no <c>action</c> param).
/// </summary>
[McpTool( "mcp.capabilities",
	"Return the current Arenula tool inventory with advertised actions and basic server capabilities. Use this when a client needs a compact capability summary without downloading and inspecting the full tools/list schema. This is read-only and has no required action parameter.",
	ReadOnlyHint = true )]
internal static class McpCapabilitiesTool
{
	/// <summary>
	/// 0-action handler (empty action key). The discovered dispatcher routes a
	/// no-action <c>tools/call</c> for <c>mcp.capabilities</c> here.
	/// <paramref name="args"/> is unused — the tool's inputSchema declares no
	/// properties. Body is the verbatim former <c>McpHandler.Capabilities()</c>
	/// plus the same try/catch wrapper <c>McpHandler.Handle</c> applied.
	/// </summary>
	[McpAction( "", ReadOnly = true )]
	internal static object Handle( JsonElement args )
	{
		try
		{
			var scannedPayload = McpToolScanner.BuildToolsListPayload( McpToolScanner.Cached );
			var toolSchemas = scannedPayload
				.OrderBy( t => ToolRegistry.GetStringProperty( t, "name" ), StringComparer.OrdinalIgnoreCase )
				.ThenBy( t => ToolRegistry.GetStringProperty( t, "name" ), StringComparer.Ordinal )
				.ToArray();

			var tools = toolSchemas.Select( t => new
			{
				name = ToolRegistry.GetStringProperty( t, "name" ),
				actions = ToolRegistry.GetActionNames( t )
					.OrderBy( action => action, StringComparer.OrdinalIgnoreCase )
					.ThenBy( action => action, StringComparer.Ordinal )
					.ToArray(),
				read_only = HasReadOnlyHint( t )
			} ).ToArray();

			var actionMetadata = toolSchemas
				.SelectMany( ToolRegistry.DescribeActions )
				.ToArray();
			var parity = McpToolRegistryParity.Compute();
			var payloadMismatches = parity.MigratedTools
				.Where( row => !row.Match )
				.Select( row => row.Name )
				.OrderBy( name => name, StringComparer.OrdinalIgnoreCase )
				.ToArray();

			return HandlerBase.Success( new
			{
				server = new
				{
					name = "Arenula Editor",
					version = ToolRegistry.Version,
					transport = "streamable-http",
					endpoint = "/mcp"
				},
				tool_count = tools.Length,
				discovery_parity = new
				{
					status = parity.AllMatch ? "pass" : "fail",
					legacy_advertised = parity.LegacyAllCount,
					scanner_tools = parity.ScannerToolCount,
					frozen_contracts = parity.FrozenContractCount,
					native_scanner_tools = parity.NativeScannerCount,
					payload_mismatches = payloadMismatches,
					action_inventory_mismatches = parity.ActionInventoryMismatches,
					unknown_action_failures = parity.UnknownActionFailures,
					rollback = "GeneratedLegacyToolCatalog plus ToolRegistry.MigratedToolLiterals"
				},
				action_metadata_schema_version = 1,
				unknown_action_fallback = ToolRegistry.DescribeUnknownActionFallback(),
				action_metadata = actionMetadata,
				routing_hints = new[]
				{
					new { task = "code edits", route = "pipeline.compile_and_verify", reason = "compile, hotload, scene-integrity, and optional SBEP verification in one guarded lifecycle operation" },
					new { task = "mixed code/assets/scenes", route = "verify_change_set.run", reason = "derives the correct validation path from touched files" },
					new { task = "scene writes", route = "operation.preview/apply or transaction.preview/apply", reason = "preview first; apply under an exclusive editor write operation" },
					new { task = "runtime assertions", route = "playtest", reason = "boot/start/stop and assertion flow is lifecycle-locked" },
					new { task = "API or schema uncertainty", route = "reflection and schema", reason = "discover exact types, properties, enum values, and accepted value shapes before writing" }
				},
				coordination = McpOperationCoordinator.Snapshot(),
				tools
			} );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, "mcp.capabilities" );
		}
	}

	/// <summary>
	/// Reflection probe for a discovered tools/list payload's
	/// <c>annotations.readOnlyHint</c> flag.
	/// </summary>
	private static bool HasReadOnlyHint( object tool )
	{
		var annotations = tool?.GetType().GetProperty( "annotations" )?.GetValue( tool );
		var hint = annotations?.GetType().GetProperty( "readOnlyHint" )?.GetValue( annotations );
		return hint is bool b && b;
	}

	/// <summary>
	/// tools/list inputSchema fragment — verbatim copy of the legacy
	/// <c>ToolRegistry.McpCapabilities.inputSchema</c> literal. <c>mcp.capabilities</c>
	/// takes no parameters, so this is the empty-object schema.
	/// </summary>
	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>(),
		additionalProperties = false
	};
}
