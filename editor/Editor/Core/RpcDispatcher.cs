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
                    serverInfo = new { name = "Arenula Editor", version = ToolRegistry.Version }
                };
            }
            else if ( method == "tools/list" )
            {
                result = new { tools = ToolRegistry.All };
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

                if ( toolName is not "mcp.health" and not "mcp.capabilities" and not "mcp.server_log"
                     && string.IsNullOrEmpty( action ) )
                {
                    error = HandlerBase.ProtocolError( -32602, $"Missing required 'action' parameter for tool '{toolName}'." );
                }
                else
                {
                    result = await DispatchToolCall( session, toolName, action, args, logInfo );
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
        return JsonSerializer.Serialize( response, jsonOptions );
    }

    private static async Task<object> DispatchToolCall(
        McpSession session,
        string toolName,
        string action,
        JsonElement args,
        Action<string> logInfo )
    {
        var metadata = ToolRegistry.GetOperationMetadata( toolName, action );
        if ( !McpOperationCoordinator.TryBegin( session.SessionId, toolName, action, metadata, out var lease, out var busyError ) )
            return busyError;

        using ( lease )
        {
            var opLabel = $"{toolName}{(string.IsNullOrEmpty( action ) ? "" : "." + action)}";
            logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Tool start: {opLabel} read_only={metadata.ReadOnly} write_kind={metadata.WriteKind} lifecycle_kind={metadata.LifecycleKind}" );

            object result;

            if ( toolName is "mcp.health" or "mcp.capabilities" or "mcp.server_log" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Waiting for MainThread to execute {toolName}..." );
                await GameTask.MainThread();
                result = McpHandler.Handle( toolName, args );
            }
            // ── Async tools (dispatch before MainThread) ─────────
            else if ( toolName == "compile" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await CompileHandler.HandleAsync( action, args );
            }
            else if ( toolName == "cloud" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await CloudHandler.HandleAsync( action, args );
            }
            else if ( toolName == "code" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await CodeHandler.HandleAsync( action, args );
            }
            // ── Console (async dispatch for ConCmd execution support) ──
            else if ( toolName == "editor" && action is "console_run" or "console_list" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async console {action}..." );
                result = await ConsoleHandler.HandleAsync( action, args );
            }
            // ── SBEP verify (async — scene access + potential smoke tests) ──
            else if ( toolName == "sbep_verify" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await SbepVerifyHandler.HandleAsync( action, args );
            }
            else if ( toolName == "pipeline" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await PipelineHandler.HandleAsync( action, args );
            }
            else if ( toolName == "workflow" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await WorkflowHandler.HandleAsync( action, args );
            }
            else if ( toolName == "verify_change_set" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await VerifyChangeSetHandler.HandleAsync( action, args );
            }
            else if ( toolName == "playtest" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await PlaytestHandler.HandleAsync( action, args );
            }
            else if ( toolName == "cockpit_verify" )
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Running async tool {toolName}.{action}..." );
                result = await CockpitVerifyHandler.HandleAsync( action, args );
            }
            // ── All other tools (main thread) ────────────────────
            else
            {
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Waiting for MainThread to execute {toolName}.{action}..." );
                await GameTask.MainThread();
                logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Resumed on MainThread for {toolName}.{action}." );

                result = toolName switch
                {
                    "scene"        => SceneHandler.Handle( action, args ),
                    "gameobject"   => GameObjectHandler.Handle( action, args ),
                    "component"    => ComponentHandler.Handle( action, args ),
                    "prefab"       => PrefabHandler.Handle( action, args ),
                    "asset_query"  => AssetQueryHandler.Handle( action, args ),
                    "asset_manage" => AssetManageHandler.Handle( action, args ),
                    "editor"       => EditorHandler.Handle( action, args ),
                    "mesh"         => MeshHandler.Handle( action, args ),
                    "lighting"     => LightingHandler.Handle( action, args ),
                    "physics"      => PhysicsHandler.Handle( action, args ),
                    "audio"        => AudioHandler.Handle( action, args ),
                    "effects"      => EffectsHandler.Handle( action, args ),
                    "camera"       => CameraHandler.Handle( action, args ),
                    "navmesh"      => NavmeshHandler.Handle( action, args ),
                    "session"      => SessionHandler.Handle( action, args ),
                    "project"      => ProjectHandler.Handle( action, args ),
                    "terrain"      => TerrainHandler.Handle( action, args ),
                    "trace"        => TraceHandler.Handle( action, args ),
                    "batch"        => BatchHandler.Handle( action, args ),
                    "reflection"   => ReflectionHandler.Handle( action, args ),
                    "schema"       => SchemaHandler.Handle( action, args ),
                    "operation"    => OperationHandler.Handle( action, args ),
                    "cross_index"  => CrossIndexHandler.Handle( action, args ),
                    "autofix"      => AutofixHandler.Handle( action, args ),
                    "transaction"  => TransactionHandler.Handle( action, args ),
                    "search"       => SearchHandler.Handle( action, args ),
                    "snapshot"     => SnapshotHandler.Handle( action, args ),
                    "validate"     => ValidateHandler.Handle( action, args ),
                    "continue"     => ContinueHandler.Handle( action, args ),
                    "blueprint"    => BlueprintHandler.Handle( action, args ),
                    "animation"    => AnimationHandler.Handle( action, args ),
                    _              => throw new InvalidOperationException( $"Tool '{toolName}' not found" )
                };
            }

            logInfo?.Invoke( $"[{McpOperationCoordinator.CurrentOperationId}] Tool finish: {opLabel}" );
            return result;
        }
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
