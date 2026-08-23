using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

/// <summary>
/// JSON-RPC dispatch: extracts tool name + action, routes to handler.
/// Handles threading: async tools dispatch before MainThread, sync tools after.
/// </summary>
internal static class RpcDispatcher
{
    internal const string Instructions = """
        Arenula is embedded in the live s&box editor and operates on the project currently open in it. Begin coordinated work by calling session with action=identify and your unique agent_id plus session slug. Use mcp.capabilities for the live tool/action inventory and schema or reflection before guessing component names, properties, or argument shapes.

        Tool execution failures are returned as tool results with isError=true. When error_code/retryable marks contention as retryable, re-issue the exact same call with the exact same arguments; do not route around another agent's lease. Prefer feed.compile for new C# compile truth, and never call editor.restart_mcp from the MCP connection you are using.

        Arenula tools are grouped into stable families and select behavior with the action argument. Read-only discovery is safe; scene, file, compile, play, and lifecycle actions participate in the repository coordination rules.
        """;

    internal static async Task<string> ProcessRpcRequestAndSerialize(
        McpSession session,
        object id,
        string method,
        string rawBody,
        JsonSerializerOptions jsonOptions,
        Action<string> logInfo,
        Action<string> logError )
    {
        object result = null;
        object error = null;
        McpOperationTelemetry.Call telemetryCall = null;

        using var doc = JsonDocument.Parse( rawBody );
        var root = doc.RootElement;

        try
        {
            if ( method == "initialize" )
            {
                if ( root.TryGetProperty( "params", out var initParams )
                     && initParams.TryGetProperty( "protocolVersion", out var pv )
                     && pv.ValueKind == JsonValueKind.String
                     && ArenulaMcpServer.IsSupportedProtocolVersion( pv.GetString() ) )
                {
                    session.ProtocolVersion = pv.GetString();
                }

                result = new
                {
                    protocolVersion = session.ProtocolVersion ?? "2025-03-26",
                    capabilities = new { tools = new { listChanged = true }, resources = new { } },
                    serverInfo = new { name = "Arenula Editor", version = ToolRegistry.Version },
                    instructions = Instructions
                };
            }
            else if ( method == "tools/list" )
            {
                result = new
                {
                    tools = McpToolScanner.BuildToolsListPayload( McpToolScanner.Cached )
                };
            }
            else if ( method == "resources/list" )
            {
                result = new { resources = ResourceRegistry.All };
            }
            else if ( method == "resources/read" )
            {
                var uri = root.GetProperty( "params" ).GetProperty( "uri" ).GetString();
                await GameTask.MainThread();
                result = ResourceHandler.Read( uri );
            }
            else if ( method == "tools/call" )
            {
                var args = root.TryGetProperty( "params", out var p ) && p.TryGetProperty( "arguments", out var a ) ? a : default;
                var toolName = root.GetProperty( "params" ).GetProperty( "name" ).GetString();
                var action = HandlerBase.GetString( args, "action" );
                var metadata = ToolRegistry.GetOperationMetadata( toolName, action );
                telemetryCall = McpOperationTelemetry.Begin(
                    session, toolName, action, metadata, rawBody?.Length ?? 0 );

                if ( toolName is not "mcp.health" and not "mcp.capabilities" and not "mcp.server_log"
                     && string.IsNullOrEmpty( action ) )
                {
                    error = HandlerBase.ProtocolError( -32602, $"Missing required 'action' parameter for tool '{toolName}'." );
                }
                else
                {
                    result = await DispatchToolCall(
                        session, toolName, action, args, logInfo, telemetryCall );
                }
            }
            else
            {
                error = HandlerBase.ProtocolError( -32601, $"Method '{method}' not found" );
            }
        }
        catch ( ArgumentException ex )
        {
            error = HandlerBase.ProtocolError( -32602, ex.Message );
        }
        catch ( InvalidOperationException ex ) when ( ex.Message.Contains( "not found" ) )
        {
            error = HandlerBase.ProtocolError( -32601, ex.Message );
        }
        catch ( Exception ex )
        {
            logError?.Invoke( $"RpcDispatcher catch: method={method} ex={ex.Message}" );
            error = new { code = -32603, message = $"Internal error: {ex.Message}" };
        }

        var response = new { jsonrpc = "2.0", id, result, error };
        string responseJson = null;
        var outcome = error != null
            ? "protocol_error"
            : McpResult.IsErrorResult( result ) ? "tool_error" : "ok";
        try
        {
            responseJson = JsonSerializer.Serialize( response, jsonOptions );
            return responseJson;
        }
        finally
        {
            McpOperationTelemetry.Complete(
                telemetryCall,
                responseJson == null ? "serialization_error" : outcome,
                responseJson?.Length ?? 0 );
        }
    }

    private static async Task<object> DispatchToolCall(
        McpSession session,
        string toolName,
        string action,
        JsonElement args,
        Action<string> logInfo,
        McpOperationTelemetry.Call telemetryCall )
    {
        if ( !McpToolScanner.Cached.TryGetValue( toolName, out var scannedTool ) )
        {
            return HandlerBase.Error(
                $"Unknown tool '{toolName}'.", toolName,
                "Use tools/list or mcp.capabilities to discover the current inventory." );
        }

        // Control-plane fast path (mcp-operation-lifecycle-hardening): coordinator-
        // administrative actions must never be gated by the coordinator itself —
        // operation.cancel exists precisely to clear a wedged lease that would
        // otherwise block this very call (FindConflict rejects even ReadOnly ops
        // while a scene-wide write lease is active). These touch only MCP-internal
        // thread-safe static state — no scene access, no MainThread hop, no lease.
        if ( toolName == "operation" && action is "cancel" or "list" or "locks_held" )
        {
            logInfo?.Invoke( $"[control] operation.{action} (coordinator bypass)" );
            return OperationHandler.HandleControl( action, args, session );
        }

        var metadata = ToolRegistry.GetOperationMetadata( toolName, action );
        // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 4 — extract per-call lock keys and thread agent identity + keys into the lock-key-aware TryBegin overload (Path C committed design, phase-01.md item 7). No inherited handoff hypothesis applies; banned approaches Path A/B are NOT re-attempted.]
        var lockKeys = LockKeyExtractor.Extract( metadata.LockKey, args );
        if ( !McpOperationCoordinator.TryBegin(
                 session.SessionId, session.AgentId, session.AgentSlug,
                 toolName, action, lockKeys, metadata, out var lease, out var busyError ) )
            return busyError;

        var operationId = McpOperationCoordinator.CurrentOperationId;
        McpOperationTelemetry.BindOperation( telemetryCall, operationId );
        try
        {
            var opLabel = $"{toolName}{(string.IsNullOrEmpty( action ) ? "" : "." + action)}";
            logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Tool start: {opLabel} read_only={metadata.ReadOnly} write_kind={metadata.WriteKind} lifecycle_kind={metadata.LifecycleKind}" );

            object result = await InvokeScanned( scannedTool, action, args, session, logInfo );
            // Stamp completion BEFORE the finally releases the lease: if the
            // release path is ever lost (dead client connection tearing down the
            // continuation), the zombie sweep evicts this op after a short grace
            // instead of wedging the coordinator forever.
            McpOperationCoordinator.MarkCompleted( operationId );
            logInfo?.Invoke( $"[{operationId}] Tool finish: {opLabel}" );
            return result;
        }
        finally
        {
            // Finally-guaranteed release (was a using-block — made explicit by
            // mcp-operation-lifecycle-hardening): success, handler throw, or
            // task cancellation — the lease cannot outlive this dispatch frame.
            lease.Dispose();
        }
    }

    // [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — reflection-invoke for scanned tools; no handoff hypothesis inherited]
    /// <summary>
    /// Reflection-invoke a scanned [McpTool] handler, routing threading per the
    /// action's <see cref="McpThreading"/>. MainThread handlers are sync
    /// (return <c>object</c>) and invoked after awaiting the main thread; async
    /// handlers return <c>Task&lt;object&gt;</c> and are invoked without awaiting
    /// the main thread first.
    /// </summary>
    private static async Task<object> InvokeScanned(
        ScannedTool tool, string action, JsonElement args, McpSession session, Action<string> logInfo )
    {
        var scanned = ResolveAction( tool, action );
        if ( scanned is null )
        {
            return HandlerBase.Error(
                $"Unknown action '{action}' for tool '{tool.Name}'.", action,
                $"Valid actions: {string.Join( ", ", tool.Actions.Keys )}" );
        }

        // [McpAction] handlers are (JsonElement) or (JsonElement, McpSession) — the
        // scanner records which via ScannedAction.WantsSession (mcp-dynamic-discovery
        // Wave 3.1). The dispatcher threads the live McpSession in when wanted.
        var invokeArgs = scanned.WantsSession
            ? new object[] { args, session }
            : new object[] { args };

        try
        {
            if ( scanned.Threading == McpThreading.MainThread )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Waiting for MainThread to execute {tool.Name} (scanned)..." );
                await GameTask.MainThread();
                return scanned.Method.Invoke( null, invokeArgs );
            }

            logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async scanned tool {tool.Name}..." );
            return await (Task<object>)scanned.Method.Invoke( null, invokeArgs );
        }
        catch ( System.Reflection.TargetInvocationException tie )
        {
            // Unwrap so the outer RPC catch reports the real handler error, not
            // "Exception has been thrown by the target of an invocation."
            throw tie.InnerException ?? tie;
        }
    }

    /// <summary>
    /// Resolve the <see cref="ScannedAction"/> for an incoming tools/call. A
    /// 0-action tool (a single handler keyed on the empty string) ignores the
    /// <c>action</c> param entirely — matching the legacy mcp.health behavior.
    /// </summary>
    private static ScannedAction ResolveAction( ScannedTool tool, string action )
    {
        if ( tool.Actions.Count == 1 && tool.Actions.TryGetValue( "", out var soleAction ) )
            return soleAction;
        return tool.Actions.TryGetValue( action ?? "", out var found ) ? found : null;
    }

    internal static async Task ProcessRpcRequest(
        McpSession session,
        object id,
        string method,
        string rawBody,
        JsonSerializerOptions jsonOptions,
        Action<string> logInfo,
        Action<string> logError )
    {
        var json = await ProcessRpcRequestAndSerialize(
            session, id, method, rawBody, jsonOptions, logInfo, logError );
        await ArenulaMcpServer.SendSseEvent( session, "message", json );
    }
}
