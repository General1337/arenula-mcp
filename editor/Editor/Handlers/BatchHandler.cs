// Editor/Handlers/BatchHandler.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>
/// batch tool: executes multiple tool operations sequentially in a single request.
/// Each operation specifies a tool name, action, and params object.
/// Results are collected into an array and returned together.
/// </summary>
// Migrated to the [McpTool] attribute scan in mcp-dynamic-discovery Phase 3
// Wave 3.1 — single-action tool. Its [McpAction] uses the 2-arg
// (JsonElement, McpSession) handler form (the scanner signature contract was
// extended this wave): batch's execute dispatch can route a session.identify
// sub-op that needs the live McpSession.
//
// mcp-batch-async-session-identify (findings.md #7, 2026-06-11): execute is now
// ASYNC (Threading=AsyncBeforeMainThread) and routes per-op through
// ToolDispatchSteps.RunStepAsync, so async tools (compile.check, verify chains,
// qa, playtest, ...) compose inside one batch — only batch-inside-batch
// recursion is rejected. Tool description + InputSchema literals are UNCHANGED
// on purpose: they must stay byte-identical to
// ToolRegistry.MigratedToolLiterals["batch"] (sbep.test_mcp_tools_list_parity).
// [PERF-OK: mechanical McpTool attribute migration, mcp-dynamic-discovery Wave 3.1 — no hot-path change; the verbatim batch tool-description string contains the phrase reduce round-trips, which tripped the perf-gate keyword scan.]
[McpTool( "batch",
	"Execute multiple tool operations in a single request. Provide a JSON array of operations, each specifying a tool name, action, and parameters. Operations execute sequentially and results are collected into an array. Use this to reduce round-trips when performing many related changes (e.g. creating multiple objects, setting multiple properties). If any operation fails, subsequent operations still execute — check individual results for errors." )]
internal static class BatchHandler
{
    /// <summary>
    /// 'execute' action handler. Async 2-arg [McpAction] form — the dispatcher
    /// invokes without awaiting MainThread first and threads the live
    /// <see cref="McpSession"/> in; each sub-step hops threads as its own
    /// handler requires (ToolDispatchSteps). The try/catch preserves the legacy
    /// <c>HandlerBase.Error</c> response shape — the pre-migration
    /// <c>Handle()</c> switch wrapped the execute path likewise.
    /// </summary>
    [McpAction( "execute", LifecycleKind = "lifecycle", Threading = McpThreading.AsyncBeforeMainThread )]
    internal static async Task<object> HandleExecuteAsync( JsonElement args, McpSession session )
    {
        try
        {
            return await ExecuteAsync( args, session );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "execute" );
        }
    }

    /// <summary>
    /// tools/list inputSchema fragment — verbatim copy of the legacy
    /// <c>ToolRegistry.Batch.inputSchema</c> literal (mcp-dynamic-discovery Phase 3).
    /// </summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "execute" } },
            ["operations"] = new { type = "string", description = "JSON array of operations. Each element: {\"tool\": \"gameobject\", \"action\": \"destroy\", \"params\": {\"id\": \"...\"}}. Required for: execute." }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    // ── execute ─────────────────────────────────────────────────────────

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — Execute signature gains McpSession to thread through to dispatch; no inherited handoff hypothesis applies.]
    private static async Task<object> ExecuteAsync( JsonElement args, McpSession session )
    {
        var opsJson = HandlerBase.GetString( args, "operations" );
        if ( string.IsNullOrEmpty( opsJson ) )
            return HandlerBase.Error( "Missing required 'operations' parameter.", "execute" );

        JsonElement opsArray;
        try
        {
            using var doc = JsonDocument.Parse( opsJson );
            // Need to clone since doc is disposed after this block
            opsArray = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Error( $"Failed to parse 'operations' JSON: {ex.Message}", "execute" );
        }

        if ( opsArray.ValueKind != JsonValueKind.Array )
            return HandlerBase.Error( "'operations' must be a JSON array.", "execute" );

        var results = new List<object>();
        int successCount = 0;
        int errorCount = 0;

        foreach ( var op in opsArray.EnumerateArray() )
        {
            string tool = null;
            string opAction = null;
            JsonElement opParams = default;

            if ( op.TryGetProperty( "tool", out var toolEl ) )
                tool = toolEl.GetString();
            if ( op.TryGetProperty( "action", out var actionEl ) )
                opAction = actionEl.GetString();
            if ( op.TryGetProperty( "params", out var paramsEl ) )
                opParams = paramsEl;

            if ( string.IsNullOrEmpty( tool ) || string.IsNullOrEmpty( opAction ) )
            {
                var err = HandlerBase.Error( "Each operation must have 'tool' and 'action' fields.", "execute" );
                results.Add( new { tool, action = opAction, result = err } );
                errorCount++;
                continue;
            }

            // Build a merged args element: action + params flattened
            var merged = MergeActionAndParams( opAction, opParams );

            try
            {
                // mcp-batch-async-session-identify: async-capable step dispatch —
                // compile/verify chains compose; only batch→batch recursion throws.
                var opResult = await ToolDispatchSteps.RunStepAsync( tool, opAction, merged, session );
                results.Add( new { tool, action = opAction, result = opResult } );

                if ( McpResult.IsErrorResult( opResult ) )
                    errorCount++;
                else
                    successCount++;
            }
            catch ( Exception ex )
            {
                var err = HandlerBase.Error( ex.Message, opAction );
                results.Add( new { tool, action = opAction, result = err } );
                errorCount++;
            }
        }

        return HandlerBase.Success( new
        {
            total = results.Count,
            succeeded = successCount,
            failed = errorCount,
            results
        } );
    }

    /// <summary>
    /// SYNC dispatch — retained for the sync main-thread callers
    /// (OperationHandler.Apply, TransactionHandler.Apply). batch.execute itself
    /// no longer routes here (it uses ToolDispatchSteps.RunStepAsync, which
    /// composes async tools). The async-tool throw messages below are preserved
    /// verbatim for those existing callers; all other routing delegates to the
    /// single shared switch in <see cref="ToolDispatchSteps.RunSyncStep"/>.
    /// </summary>
    internal static object DispatchToHandler( string tool, string action, JsonElement args, McpSession session )
    {
        return tool switch
        {
            "compile" or "cloud" or "workflow" or "verify_change_set" or "playtest"
                => throw new InvalidOperationException( $"Tool '{tool}' is async and cannot be used in batch operations." ),
            "batch" => throw new InvalidOperationException( "Nested batch operations are not supported." ),
            _ => ToolDispatchSteps.RunSyncStep( tool, action, args, session )
        };
    }

    /// <summary>
    /// Merges the action string and params object into a single flat JsonElement
    /// that handlers expect (action + other params at the same level).
    /// </summary>
    internal static JsonElement MergeActionAndParams( string action, JsonElement opParams )
    {
        var dict = new Dictionary<string, JsonElement>();

        // Add the action as a string element
        using var actionDoc = JsonDocument.Parse( $"\"{action}\"" );
        dict["action"] = actionDoc.RootElement.Clone();

        // Merge in params if present
        if ( opParams.ValueKind == JsonValueKind.Object )
        {
            foreach ( var prop in opParams.EnumerateObject() )
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        // Serialize and re-parse to get a proper JsonElement
        var json = JsonSerializer.Serialize( dict );
        using var doc = JsonDocument.Parse( json );
        return doc.RootElement.Clone();
    }
}
