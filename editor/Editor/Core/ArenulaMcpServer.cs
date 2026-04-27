using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// HTTP/SSE transport layer for the MCP server.
/// Responsible for accepting connections, managing SSE sessions, and routing
/// raw HTTP requests. Tool logic lives in handler classes,
/// dispatch in RpcDispatcher, and schemas in tool definition classes.
/// </summary>
public static class ArenulaMcpServer
{
    [ConVar( "mcp_server_port", ConVarFlags.Saved )]
    public static int Port { get; set; } = 8098;

    // ── GUI events & state ─────────────────────────────────────────────────
    public static event Action OnServerStateChanged;
    public static event Action<string> OnLogMessage;
    public static bool IsRunning   => _listener != null && _listener.IsListening;
    public static int  SessionCount => _sessions.Count;

    /// <summary>Safely dispatches an action to the main thread, swallowing exceptions if the thread is gone (e.g. during cancel).</summary>
    private static async void SafeRunOnMainThread( Action action )
    {
        try
        {
            await GameTask.RunInThreadAsync( action );
        }
        catch { /* Main thread unavailable (cancelled / disposed) – safe to ignore */ }
    }

    private static void LogInfo( string msg )
    {
        SafeRunOnMainThread( () =>
        {
            Log.Info( msg );
            OnLogMessage?.Invoke( msg );
        } );
    }

    private static void LogError( string msg )
    {
        SafeRunOnMainThread( () =>
        {
            Log.Error( msg );
            OnLogMessage?.Invoke( $"[ERROR] {msg}" );
        } );
    }

    private static void LogWarning( string msg )
    {
        SafeRunOnMainThread( () =>
        {
            Log.Warning( msg );
            OnLogMessage?.Invoke( $"[WARNING] {msg}" );
        } );
    }

    private static void NotifyStateChanged()
    {
        SafeRunOnMainThread( () => OnServerStateChanged?.Invoke() );
    }

    // ── Internal state ─────────────────────────────────────────────────────
    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static readonly ConcurrentDictionary<string, McpSession> _sessions = new();
    private static readonly HashSet<string> SupportedProtocolVersions = new( StringComparer.Ordinal )
    {
        "2025-11-25",
        "2025-06-18",
        "2025-03-26"
    };

    // Tracks in-flight RPC tasks so StopServer can wait for them to finish
    private static readonly ConcurrentDictionary<Guid, Task> _inflightTasks = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    // ── Global log capture ────────────────────────────────────────────────
    private static bool _loggerHooked;

    private static void OnGlobalLog( LogEvent e )
    {
        var prefix = e.Level switch
        {
            LogLevel.Error   => "[ERROR] ",
            LogLevel.Warn    => "[WARNING] ",
            LogLevel.Trace   => "[TRACE] ",
            _                => ""
        };
        var logger = string.IsNullOrEmpty( e.Logger ) ? "" : $"{e.Logger} ";
        EditorHandler.AppendLog( $"{e.Time:HH:mm:ss} {logger}{prefix}{e.Message}" );
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    /// <summary>
    /// On hotload, stop the existing server (which retains its listener reference via
    /// preserved static state) and restart it so new handler code is picked up.
    /// </summary>
    [EditorEvent.Hotload]
    public static void OnHotload()
    {
        // 2026-04-21 — S&Box EditorEvent.Hotload contract requires handlers
        // to swallow all exceptions. An unhandled throw here breaks the
        // event dispatcher for every other subscriber on subsequent hotloads
        // (silent editor-wide breakage — extremely hard to diagnose).
        try
        {
            if ( IsRunning ) StopServer();
            StartServer();
        }
        catch ( Exception ex )
        {
            LogError( $"OnHotload failed: {ex.Message}" );
        }
    }

    public static void StartServer()
    {
        if ( _listener != null && _listener.IsListening )
        {
            LogInfo( "MCP Server is already running" );
            return;
        }

        try
        {
            // Hook into the global editor log stream
            if ( !_loggerHooked )
            {
                EditorUtility.AddLogger( OnGlobalLog );
                _loggerHooked = true;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add( $"http://localhost:{Port}/" );
            _listener.Prefixes.Add( $"http://127.0.0.1:{Port}/" );
            _listener.Start();

            _cts = new CancellationTokenSource();
            Task.Run( () => ListenLoop( _cts.Token ) );

            LogInfo( $"Started Model Context Protocol Server on port {Port}" );
            NotifyStateChanged();
        }
        catch ( Exception ex )
        {
            LogError( $"Failed to start MCP Server: {ex.Message}" );
        }
    }

    public static void StopServer()
    {
        try
        {
            // Unhook the global logger
            if ( _loggerHooked )
            {
                EditorUtility.RemoveLogger( OnGlobalLog );
                _loggerHooked = false;
            }

            _cts?.Cancel();

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            // Don't wait for in-flight tasks — they may be blocked waiting for the
            // main thread (GameTask.RunInThreadAsync) which would deadlock here.
            // Just clear the tracking dictionary; the tasks will see the cancelled token
            // and wind down on their own.
            _inflightTasks.Clear();

            foreach ( var session in _sessions.Values )
            {
                try { session.Tcs.TrySetResult( true ); } catch { }
                try { session.SseResponse?.Close(); } catch { }
            }
            _sessions.Clear();

            LogInfo( "Stopped Model Context Protocol Server" );
            NotifyStateChanged();
        }
        catch ( Exception ex )
        {
            try { LogError( $"Error stopping MCP Server: {ex.Message}" ); } catch { }
        }
    }

    // ── HTTP listen loop ───────────────────────────────────────────────────

    private static async Task ListenLoop( CancellationToken token )
    {
        while ( !token.IsCancellationRequested && _listener != null && _listener.IsListening )
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run( () => HandleContext( context ), token );
            }
            catch ( Exception ex ) when ( ex is not ObjectDisposedException )
            {
                LogError( $"Error in MCP listen loop: {ex.Message}" );
            }
        }
    }

    private static async Task HandleContext( HttpListenerContext context )
    {
        var req = context.Request;
        var res = context.Response;

        if ( !ValidateOrigin( req, res ) ) return;

        var origin = req.Headers["Origin"];
        res.Headers.Add( "Access-Control-Allow-Origin",
            string.IsNullOrWhiteSpace( origin ) ? $"http://localhost:{Port}" : origin );
        res.Headers.Add( "Access-Control-Allow-Methods", "GET, POST, OPTIONS" );
        res.Headers.Add( "Access-Control-Allow-Headers", "*" );

        if ( req.HttpMethod == "OPTIONS" ) { res.StatusCode = 200; res.Close(); return; }

        try
        {
            if      ( req.Url.AbsolutePath == "/sse"     && req.HttpMethod == "GET"  ) await HandleSse( req, res );
            else if ( req.Url.AbsolutePath == "/message" && req.HttpMethod == "POST" ) await HandleMessage( req, res );
            else if ( req.Url.AbsolutePath == "/mcp"     && req.HttpMethod == "GET"  ) await HandleMcpGet( req, res );
            else if ( req.Url.AbsolutePath == "/mcp"     && req.HttpMethod == "POST" ) await HandleMcpPost( req, res );
            else if ( req.Url.AbsolutePath.StartsWith( "/.well-known/" ) )             HandleWellKnown( req, res );
            else    { res.StatusCode = 404; res.Close(); }
        }
        catch ( Exception ex )
        {
            LogError( $"Error handling MCP request: {ex.Message}" );
            res.StatusCode = 500;
            res.Close();
        }
    }

    internal static bool IsSupportedProtocolVersion( string version )
    {
        return string.IsNullOrWhiteSpace( version ) || SupportedProtocolVersions.Contains( version );
    }

    private static bool ValidateOrigin( HttpListenerRequest req, HttpListenerResponse res )
    {
        var origin = req.Headers["Origin"];
        if ( string.IsNullOrWhiteSpace( origin ) )
            return true;

        if ( Uri.TryCreate( origin, UriKind.Absolute, out var uri ) && IsLoopbackHost( uri.Host ) )
            return true;

        res.StatusCode = 403;
        res.ContentType = "application/json";
        var json = JsonSerializer.Serialize( new
        {
            error = "forbidden_origin",
            message = "Arenula MCP accepts HTTP requests only from missing Origin or loopback origins."
        }, JsonOptions );
        var buffer = Encoding.UTF8.GetBytes( json );
        res.ContentLength64 = buffer.Length;
        res.OutputStream.Write( buffer, 0, buffer.Length );
        res.Close();
        LogWarning( $"Rejected MCP request from non-loopback Origin '{origin}'." );
        return false;
    }

    private static bool IsLoopbackHost( string host )
    {
        if ( string.IsNullOrWhiteSpace( host ) ) return false;
        if ( string.Equals( host, "localhost", StringComparison.OrdinalIgnoreCase ) ) return true;
        if ( IPAddress.TryParse( host, out var address ) ) return IPAddress.IsLoopback( address );
        return false;
    }

    private static string ResolveProtocolVersion( HttpListenerRequest req, out string errorJson )
    {
        var version = req.Headers["MCP-Protocol-Version"];
        if ( string.IsNullOrWhiteSpace( version ) )
        {
            errorJson = null;
            return "2025-03-26";
        }

        if ( SupportedProtocolVersions.Contains( version ) )
        {
            errorJson = null;
            return version;
        }

        errorJson = JsonSerializer.Serialize( new
        {
            jsonrpc = "2.0",
            id = (object)null,
            error = new
            {
                code = -32602,
                message = $"Unsupported MCP-Protocol-Version '{version}'. Supported versions: {string.Join( ", ", SupportedProtocolVersions )}."
            }
        }, JsonOptions );
        return null;
    }

    // ── OAuth metadata (tells clients no auth is required) ──────────────

    private static void HandleWellKnown( HttpListenerRequest req, HttpListenerResponse res )
    {
        // MCP clients (e.g. Claude Code) probe /.well-known/oauth-protected-resource
        // and /.well-known/oauth-authorization-server during OAuth discovery.
        // Return a minimal valid JSON response so the client knows no auth is needed
        // instead of 404 with empty body (which causes JSON parse errors).
        string json;
        if ( req.Url.AbsolutePath == "/.well-known/oauth-protected-resource" )
        {
            json = JsonSerializer.Serialize( new { resource = $"http://localhost:{Port}/" }, JsonOptions );
        }
        else
        {
            // Any other .well-known path — return 404 with valid JSON body
            res.StatusCode = 404;
            json = JsonSerializer.Serialize( new { error = "not_found" }, JsonOptions );
        }

        res.ContentType = "application/json";
        var buffer = Encoding.UTF8.GetBytes( json );
        res.ContentLength64 = buffer.Length;
        res.OutputStream.Write( buffer, 0, buffer.Length );
        res.Close();
    }

    private static async Task HandleMcpGet( HttpListenerRequest req, HttpListenerResponse res )
    {
        var accept = req.Headers["Accept"] ?? string.Empty;
        if ( accept.Contains( "text/event-stream", StringComparison.OrdinalIgnoreCase ) )
        {
            await HandleMcpStream( req, res );
            return;
        }

        var json = JsonSerializer.Serialize( new
        {
            name = "Arenula Editor",
            version = ToolRegistry.Version,
            transport = "streamable-http",
            endpoint = "/mcp"
        }, JsonOptions );

        var buffer = Encoding.UTF8.GetBytes( json );
        res.ContentType = "application/json";
        res.ContentLength64 = buffer.Length;
        await res.OutputStream.WriteAsync( buffer, 0, buffer.Length );
        res.Close();
    }

    private static async Task HandleMcpPost( HttpListenerRequest req, HttpListenerResponse res )
    {
        var protocolVersion = ResolveProtocolVersion( req, out var protocolErrorJson );
        if ( protocolVersion == null )
        {
            await WriteJsonResponse( res, 400, protocolErrorJson );
            return;
        }

        using var reader = new StreamReader( req.InputStream, Encoding.UTF8 );
        var body = await reader.ReadToEndAsync();

        try
        {
            using var doc = JsonDocument.Parse( body );
            var root = doc.RootElement;
            var sessionId = req.Headers["Mcp-Session-Id"] ?? req.QueryString["sessionId"] ?? Guid.NewGuid().ToString();
            if ( root.ValueKind == JsonValueKind.Array )
            {
                var responses = new List<string>();
                foreach ( var item in root.EnumerateArray() )
                {
                    var response = await HandleMcpRequestItem( item, sessionId, protocolVersion );
                    if ( response != null )
                    {
                        responses.Add( response );
                    }
                }

                if ( responses.Count == 0 )
                {
                    WriteAcceptedResponse( res, sessionId );
                    return;
                }

                var batchJson = $"[{string.Join( ",", responses )}]";

                await WriteJsonResponse( res, 200, batchJson, sessionId );
                return;
            }

            var json = await HandleMcpRequestItem( root, sessionId, protocolVersion );
            if ( json == null )
            {
                WriteAcceptedResponse( res, sessionId );
                return;
            }

            await WriteJsonResponse( res, 200, json, sessionId );
        }
        catch ( Exception ex )
        {
            LogError( $"Error parsing streamable MCP JSON-RPC: {ex.Message}" );
            var errorJson = JsonSerializer.Serialize( new
            {
                jsonrpc = "2.0",
                id = (object)null,
                error = new { code = -32700, message = $"Parse error: {ex.Message}" }
            }, JsonOptions );
            await WriteJsonResponse( res, 400, errorJson );
        }
    }

    private static async Task<string> HandleMcpRequestItem( JsonElement item, string sessionId, string protocolVersion )
    {
        string method = item.TryGetProperty( "method", out var m ) ? m.GetString() : null;
        object id = null;

        if ( item.TryGetProperty( "id", out var idProp ) )
        {
            if      ( idProp.ValueKind == JsonValueKind.Number ) id = idProp.GetInt32();
            else if ( idProp.ValueKind == JsonValueKind.String ) id = idProp.GetString();
        }

        if ( id == null )
        {
            if ( method != null && method.StartsWith( "notifications/", StringComparison.Ordinal ) )
            {
                if ( method == "notifications/initialized" )
                {
                    if ( _sessions.TryGetValue( sessionId, out var existing ) )
                    {
                        existing.Initialized = true;
                    }
                    LogInfo( $"MCP streamable session {sessionId} initialized." );
                }

                return null;
            }

            return JsonSerializer.Serialize( new
            {
                jsonrpc = "2.0",
                id = (object)null,
                error = new { code = -32600, message = "Invalid Request: missing id" }
            }, JsonOptions );
        }

        if ( method == "initialize"
             && item.TryGetProperty( "params", out var initParams )
             && initParams.TryGetProperty( "protocolVersion", out var pv )
             && pv.ValueKind == JsonValueKind.String
             && !IsSupportedProtocolVersion( pv.GetString() ) )
        {
            return JsonSerializer.Serialize( new
            {
                jsonrpc = "2.0",
                id,
                error = new
                {
                    code = -32602,
                    message = $"Unsupported initialize protocolVersion '{pv.GetString()}'. Supported versions: {string.Join( ", ", SupportedProtocolVersions )}."
                }
            }, JsonOptions );
        }

        var effectiveProtocol = protocolVersion;
        if ( method == "initialize"
             && item.TryGetProperty( "params", out var initParamsForSession )
             && initParamsForSession.TryGetProperty( "protocolVersion", out var pvForSession )
             && pvForSession.ValueKind == JsonValueKind.String
             && IsSupportedProtocolVersion( pvForSession.GetString() ) )
        {
            effectiveProtocol = pvForSession.GetString();
        }

        var session = new McpSession
        {
            SessionId = sessionId,
            Initialized = true,
            ProtocolVersion = effectiveProtocol
        };

        return await RpcDispatcher.ProcessRpcRequestAndSerialize(
            session, id, method, item.GetRawText(), JsonOptions, LogInfo, LogError );
    }

    private static async Task WriteJsonResponse( HttpListenerResponse res, int statusCode, string json, string sessionId = null )
    {
        var buffer = Encoding.UTF8.GetBytes( json );
        res.StatusCode = statusCode;
        res.ContentType = "application/json";
        if ( !string.IsNullOrEmpty( sessionId ) )
        {
            res.Headers["MCP-Session-Id"] = sessionId;
        }
        res.ContentLength64 = buffer.Length;
        await res.OutputStream.WriteAsync( buffer, 0, buffer.Length );
        res.Close();
    }

    private static void WriteAcceptedResponse( HttpListenerResponse res, string sessionId = null )
    {
        res.StatusCode = 202;
        if ( !string.IsNullOrEmpty( sessionId ) )
        {
            res.Headers["MCP-Session-Id"] = sessionId;
        }
        res.ContentLength64 = 0;
        res.Close();
    }

    // ── Streamable HTTP event stream ──────────────────────────────────────

    private static async Task HandleMcpStream( HttpListenerRequest req, HttpListenerResponse res )
    {
        var protocolVersion = ResolveProtocolVersion( req, out var protocolErrorJson );
        if ( protocolVersion == null )
        {
            await WriteJsonResponse( res, 400, protocolErrorJson );
            return;
        }

        var sessionId = req.Headers["Mcp-Session-Id"]
                     ?? req.QueryString["sessionId"]
                     ?? Guid.NewGuid().ToString();
        var session = new McpSession
        {
            SessionId = sessionId,
            SseResponse = res,
            Initialized = true,
            ProtocolVersion = protocolVersion
        };
        _sessions[sessionId] = session;

        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers.Add( "Cache-Control", "no-cache" );
        res.Headers.Add( "Connection",    "keep-alive" );
        res.Headers["MCP-Session-Id"] = sessionId;

        try
        {
            // Streamable HTTP does not use the legacy SSE "endpoint" bootstrap event.
            // A comment opens the stream without delivering a protocol message.
            var connected = Encoding.UTF8.GetBytes( ": connected\n\n" );
            await res.OutputStream.WriteAsync( connected, 0, connected.Length );
            await res.OutputStream.FlushAsync();

            LogInfo( $"Created MCP streamable HTTP session: {sessionId}" );
            NotifyStateChanged();

            while ( !_cts.IsCancellationRequested )
            {
                var done = await Task.WhenAny( session.Tcs.Task, Task.Delay( 30_000 ) );
                if ( done == session.Tcs.Task || _cts.IsCancellationRequested ) break;
                var ping = Encoding.UTF8.GetBytes( ": ping\n\n" );
                await res.OutputStream.WriteAsync( ping, 0, ping.Length );
                await res.OutputStream.FlushAsync();
            }
        }
        catch ( Exception ex ) { LogError( $"MCP streamable HTTP connection error: {ex.Message}" ); }
        finally
        {
            _sessions.TryRemove( sessionId, out _ );
            try { res.Close(); } catch { }
            LogInfo( $"Closed MCP streamable HTTP session: {sessionId}" );
            NotifyStateChanged();
        }
    }

    // ── SSE connection ─────────────────────────────────────────────────────

    private static async Task HandleSse( HttpListenerRequest req, HttpListenerResponse res )
    {
        var sessionId = Guid.NewGuid().ToString();
        var session   = new McpSession { SessionId = sessionId, SseResponse = res };
        _sessions[sessionId] = session;

        res.ContentType = "text/event-stream";
        res.Headers.Add( "Cache-Control", "no-cache" );
        res.Headers.Add( "Connection",    "keep-alive" );

        try
        {
            var msg    = $"event: endpoint\ndata: /message?sessionId={sessionId}\n\n";
            var buffer = Encoding.UTF8.GetBytes( msg );
            await res.OutputStream.WriteAsync( buffer, 0, buffer.Length );
            await res.OutputStream.FlushAsync();

            LogInfo( $"Created new MCP SSE session: {sessionId}" );
            NotifyStateChanged();

            // Ping every 30 s so we detect when the client disconnects
            while ( !_cts.IsCancellationRequested )
            {
                var done = await Task.WhenAny( session.Tcs.Task, Task.Delay( 30_000 ) );
                if ( done == session.Tcs.Task || _cts.IsCancellationRequested ) break;
                var ping = Encoding.UTF8.GetBytes( ": ping\n\n" );
                await res.OutputStream.WriteAsync( ping, 0, ping.Length );
                await res.OutputStream.FlushAsync(); // throws if client is gone → caught below
            }
        }
        catch ( Exception ex ) { LogError( $"SSE connection error: {ex.Message}" ); }
        finally
        {
            _sessions.TryRemove( sessionId, out _ );
            try { res.Close(); } catch { }
            LogInfo( $"Closed MCP SSE session: {sessionId}" );
            NotifyStateChanged();
        }
    }

    // ── Message (JSON-RPC) ─────────────────────────────────────────────────

    private static async Task HandleMessage( HttpListenerRequest req, HttpListenerResponse res )
    {
        var sessionId = req.QueryString["sessionId"];
        if ( string.IsNullOrEmpty( sessionId ) || !_sessions.TryGetValue( sessionId, out var session ) )
        {
            res.StatusCode = 400; res.Close(); return;
        }

        using var reader = new StreamReader( req.InputStream, Encoding.UTF8 );
        var body = await reader.ReadToEndAsync();

        try
        {
            using var doc = JsonDocument.Parse( body );
            var root      = doc.RootElement;
            string method = root.TryGetProperty( "method", out var m ) ? m.GetString() : null;
            object id     = null;

            if ( root.TryGetProperty( "id", out var idProp ) )
            {
                if      ( idProp.ValueKind == JsonValueKind.Number ) id = idProp.GetInt32();
                else if ( idProp.ValueKind == JsonValueKind.String ) id = idProp.GetString();
            }

            res.StatusCode = 202;
            res.Close();

            if ( id != null )
            {
                var bodyCopy   = body;
                var idCopy     = id;
                var methodCopy = method;
                var taskId     = Guid.NewGuid();
                var task       = GameTask.RunInThreadAsync( async () =>
                {
                    try
                    {
                        await RpcDispatcher.ProcessRpcRequest(
                            session, idCopy, methodCopy, bodyCopy,
                            JsonOptions, LogInfo, LogError );
                    }
                    catch ( Exception ex )
                    {
                        LogError( $"ProcessRpcRequest unhandled fault: {ex.Message}" );
                        var errResponse = new
                        {
                            jsonrpc = "2.0",
                            id      = idCopy,
                            result  = (object)null,
                            error   = new { code = -32603, message = $"Internal error: {ex.Message}" }
                        };
                        var errJson = JsonSerializer.Serialize( errResponse, JsonOptions );
                        await SendSseEvent( session, "message", errJson );
                    }
                    finally
                    {
                        _inflightTasks.TryRemove( taskId, out _ );
                    }
                } );
                _inflightTasks[taskId] = task;
            }
            else if ( method == "notifications/initialized" )
            {
                session.Initialized = true;
                LogInfo( $"MCP Session {sessionId} initialized." );
                NotifyStateChanged();
            }
        }
        catch ( Exception ex )
        {
            LogError( $"Error parsing JSON-RPC: {ex.Message}" );
        }
    }

    // ── SSE write ──────────────────────────────────────────────────────────

    /// <summary>Writes a single SSE event to the given session's output stream.</summary>
    internal static async Task SendSseEvent( McpSession session, string eventName, string data )
    {
        if ( session.SseResponse == null || !session.SseResponse.OutputStream.CanWrite ) return;
        try
        {
            var msg    = $"event: {eventName}\ndata: {data}\n\n";
            var buffer = Encoding.UTF8.GetBytes( msg );
            await session.SseResponse.OutputStream.WriteAsync( buffer, 0, buffer.Length );
            await session.SseResponse.OutputStream.FlushAsync();
        }
        catch ( Exception ex )
        {
            LogWarning( $"Failed to send SSE event to session {session.SessionId}: {ex.Message}" );
        }
    }
}
