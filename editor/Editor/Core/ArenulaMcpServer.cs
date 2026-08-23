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
    internal const long MaxRequestSizeBytes = 8L * 1024L * 1024L;

    [ConVar( "mcp_server_port", ConVarFlags.Saved )]
    public static int Port { get; set; } = 8098;

    [ConVar( "mcp_log_session_lifecycle", ConVarFlags.Saved )]
    public static bool LogSessionLifecycle { get; set; } = false;

    [ConVar( "mcp_log_disconnect_errors", ConVarFlags.Saved )]
    public static bool LogDisconnectErrors { get; set; } = false;

    // ── GUI events & state ─────────────────────────────────────────────────
    public static event Action OnServerStateChanged;
    public static event Action<string> OnLogMessage;
    public static bool IsRunning   => _listener != null && _listener.IsListening;
    public static int  SessionCount => _sessions.Count;

    /// <summary>
    /// Dispatches an action to the main thread, swallowing exceptions if the thread is gone (e.g.
    /// during cancel). Fire-and-forget: the action has usually NOT run by the time this returns.
    /// </summary>
    /// <remarks>
    /// Must hop via <c>GameTask.MainThread()</c>, never <c>GameTask.RunInThreadAsync()</c> — the
    /// latter posts to <c>SyncContext.WorkerThread</c>. Subscribers of <see cref="OnLogMessage"/> and
    /// <see cref="OnServerStateChanged"/> touch editor widgets, and every Qt widget constructor calls
    /// <c>InteropSystem.Alloc</c>, which asserts the main thread.
    /// </remarks>
    private static async void SafeRunOnMainThread( Action action )
    {
        try
        {
            await GameTask.MainThread();
            action();
        }
        catch { /* Main thread unavailable (cancelled / disposed) – safe to ignore */ }
    }

    /// <summary>
    /// Raises <see cref="OnLogMessage"/> on the main thread. Never invoke the event directly from a
    /// request handler: those run on HTTP worker threads and subscribers build Qt widgets.
    /// </summary>
    internal static void RaiseLogMessage( string msg )
    {
        SafeRunOnMainThread( () => OnLogMessage?.Invoke( msg ) );
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

    private static void LogSessionLifecycleInfo( string msg )
    {
        if ( LogSessionLifecycle )
            LogInfo( msg );
        else
            RaiseLogMessage( $"[TRACE] {msg}" );
    }

    private static void LogConnectionError( string prefix, Exception ex )
    {
        if ( IsExpectedClientDisconnect( ex ) && !LogDisconnectErrors )
        {
            RaiseLogMessage( $"[TRACE] {prefix}: {ex.Message}" );
            return;
        }

        // Full exception, not just Message: a bare .Message discards the stack, which is the only
        // thing that identifies which subscriber or handler actually threw.
        LogError( $"{prefix}: {ex}" );
    }

    private static bool IsExpectedClientDisconnect( Exception ex )
    {
        var msg = ex.Message ?? "";
        return ex is IOException
            || ex is ObjectDisposedException
            || msg.Contains( "nonexistent network connection", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "forcibly closed", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "broken pipe", StringComparison.OrdinalIgnoreCase )
            || msg.Contains( "An existing connection was forcibly closed", StringComparison.OrdinalIgnoreCase );
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
    //
    // Deliberately no `_loggerHooked` flag: it was a static, a hot-reload reset it to false, and
    // gating RemoveLogger on it leaked one OnGlobalLog registration per hotload. AddLogger/RemoveLogger
    // are idempotent enough to call unconditionally, which is what EditorHandler already does.

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
            // Drop reflection records from the outgoing editor assembly before
            // the restarted listener can accept a tools/list or tools/call.
            McpToolScanner.Invalidate();
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
            // Hook into the global editor log stream. The RemoveLogger is unconditional and
            // idempotent — same pattern as EditorHandler.EnsureInitialized, for the same reason.
            // Guarding it behind a static flag leaked one surplus OnGlobalLog registration per
            // hotload, so every engine log line fanned out N times into the MCP ring buffer.
            // Measured at 7 live registrations on 2026-07-09 (vs 1 for OnEngineLog, which already
            // removed unconditionally).
            try { EditorUtility.RemoveLogger( OnGlobalLog ); } catch { }
            EditorUtility.AddLogger( OnGlobalLog );

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
            // Unhook the global logger. Unconditional — see the note at OnGlobalLog.
            try { EditorUtility.RemoveLogger( OnGlobalLog ); } catch { }

            _cts?.Cancel();

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            // Don't wait for in-flight tasks — they may be blocked waiting for the
            // main thread (GameTask.MainThread) which would deadlock here.
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
        while ( !token.IsCancellationRequested )
        {
            var listener = _listener;
            if ( listener == null || !listener.IsListening )
                break;

            try
            {
                var context = await listener.GetContextAsync();

                // Observe the task's fault. Discarding it meant any exception escaping HandleContext
                // went unobserved and was rethrown by the finalizer thread.
                _ = Task.Run( () => HandleContext( context ), token )
                    .ContinueWith(
                        t => LogError( $"Unhandled MCP request failure: {t.Exception?.GetBaseException()}" ),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default );
            }
            catch ( ObjectDisposedException ) when ( token.IsCancellationRequested || _listener == null )
            {
                break;
            }
            catch ( HttpListenerException ) when ( token.IsCancellationRequested || _listener == null )
            {
                break;
            }
            catch ( InvalidOperationException ) when ( token.IsCancellationRequested || _listener == null )
            {
                break;
            }
            catch ( Exception ex )
            {
                LogError( $"Error in MCP listen loop: {ex.Message}" );
                try { await Task.Delay( 100, token ); } catch { break; }
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
            LogError( $"Error handling MCP request: {ex}" );

            // The handler that threw may already have closed the response or sent its headers.
            // Touching it here then throws OUT of this catch block — that is how a single handler
            // fault used to escape HandleContext entirely and surface as a disposed-object warning.
            try
            {
                res.StatusCode = 500;
                res.Close();
            }
            catch { /* response already closed / headers sent */ }
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

        var body = await ReadRequestBody( req );
        if ( body == null )
        {
            await WritePayloadTooLargeResponse( res );
            return;
        }

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

    /// <summary>
    /// Read one request body without allowing fixed-length or chunked clients to
    /// allocate beyond <see cref="MaxRequestSizeBytes"/>. Content-Length is an
    /// early rejection hint only; chunked requests report -1, so the stream itself
    /// remains the authority.
    /// </summary>
    private static async Task<string> ReadRequestBody( HttpListenerRequest req )
    {
        if ( req.ContentLength64 > MaxRequestSizeBytes )
            return null;

        var initialCapacity = req.ContentLength64 is > 0 and <= MaxRequestSizeBytes
            ? (int)req.ContentLength64
            : 0;

        using var buffer = initialCapacity > 0
            ? new MemoryStream( initialCapacity )
            : new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ( (read = await req.InputStream.ReadAsync( chunk, 0, chunk.Length )) > 0 )
        {
            buffer.Write( chunk, 0, read );
            if ( buffer.Length > MaxRequestSizeBytes )
                return null;
        }

        return req.ContentEncoding.GetString( buffer.GetBuffer(), 0, (int)buffer.Length );
    }

    private static Task WritePayloadTooLargeResponse( HttpListenerResponse res )
    {
        var json = JsonSerializer.Serialize( new
        {
            error = "request_too_large",
            message = $"MCP request body exceeds the {MaxRequestSizeBytes}-byte limit.",
            max_bytes = MaxRequestSizeBytes
        }, JsonOptions );
        return WriteJsonResponse( res, (int)HttpStatusCode.RequestEntityTooLarge, json );
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
            await session.WriteLock.WaitAsync();
            try
            {
                await res.OutputStream.WriteAsync( connected, 0, connected.Length );
                await res.OutputStream.FlushAsync();
            }
            finally { session.WriteLock.Release(); }

            LogSessionLifecycleInfo( $"Created MCP streamable HTTP session: {sessionId}" );
            NotifyStateChanged();

            while ( !_cts.IsCancellationRequested )
            {
                var done = await Task.WhenAny( session.Tcs.Task, Task.Delay( 30_000 ) );
                if ( done == session.Tcs.Task || _cts.IsCancellationRequested ) break;
                var ping = Encoding.UTF8.GetBytes( ": ping\n\n" );
                await session.WriteLock.WaitAsync();
                try
                {
                    await res.OutputStream.WriteAsync( ping, 0, ping.Length );
                    await res.OutputStream.FlushAsync();
                }
                finally { session.WriteLock.Release(); }
            }
        }
        catch ( Exception ex ) { LogConnectionError( "MCP streamable HTTP connection error", ex ); }
        finally
        {
            // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 session-close lock release; Path C committed-design plumbing, independent of any inherited ranked hypothesis — Phase 1 already falsified the RFC editor-mutex-layer claim.]
            _sessions.TryRemove( sessionId, out var closedSession );
            ReleaseSessionLocks( sessionId, closedSession );
            try { res.Close(); } catch { }
            LogSessionLifecycleInfo( $"Closed MCP streamable HTTP session: {sessionId}" );
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
            await session.WriteLock.WaitAsync();
            try
            {
                await res.OutputStream.WriteAsync( buffer, 0, buffer.Length );
                await res.OutputStream.FlushAsync();
            }
            finally { session.WriteLock.Release(); }

            LogSessionLifecycleInfo( $"Created new MCP SSE session: {sessionId}" );
            NotifyStateChanged();

            // Ping every 30 s so we detect when the client disconnects
            while ( !_cts.IsCancellationRequested )
            {
                var done = await Task.WhenAny( session.Tcs.Task, Task.Delay( 30_000 ) );
                if ( done == session.Tcs.Task || _cts.IsCancellationRequested ) break;
                var ping = Encoding.UTF8.GetBytes( ": ping\n\n" );
                await session.WriteLock.WaitAsync();
                try
                {
                    await res.OutputStream.WriteAsync( ping, 0, ping.Length );
                    await res.OutputStream.FlushAsync(); // throws if client is gone → caught below
                }
                finally { session.WriteLock.Release(); }
            }
        }
        catch ( Exception ex ) { LogConnectionError( "SSE connection error", ex ); }
        finally
        {
            // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 session-close lock release; Path C committed-design plumbing, independent of any inherited ranked hypothesis — Phase 1 already falsified the RFC editor-mutex-layer claim.]
            _sessions.TryRemove( sessionId, out var closedSession );
            ReleaseSessionLocks( sessionId, closedSession );
            try { res.Close(); } catch { }
            LogSessionLifecycleInfo( $"Closed MCP SSE session: {sessionId}" );
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

        var body = await ReadRequestBody( req );
        if ( body == null )
        {
            await WritePayloadTooLargeResponse( res );
            return;
        }

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

    // ── Session lock release ──────────────────────────────────────────────

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 — on SSE/streamable session close, evict the dropped agent's Coordinator operation leases and per-GameObject locks so a disconnected agent never strands cross-session contention state. Connection-drop is the PRIMARY lock-release signal per the Path C committed design. No inherited handoff hypothesis applies.]
    /// <summary>Releases all MCP lock state owned by a closing session.</summary>
    private static void ReleaseSessionLocks( string sessionId, McpSession session )
    {
        if ( string.IsNullOrEmpty( sessionId ) ) return;
        try
        {
            var evicted = McpOperationCoordinator.EvictBySession( sessionId );
            var released = 0;
            if ( session?.AgentId is { Length: > 0 } agentId )
            {
                released = GameObjectLockMap.ReleaseAll( agentId );
                // engine-fork-elite-leverage Phase 3.3 — flag the dropped agent's
                // scratch container for lazy reaping. In-memory ONLY: GameObject
                // .DestroyImmediate requires the MainThread and this SSE-close
                // path is not guaranteed to be on it. The actual destroy happens
                // on the next MainThread pool op (plan.md §3.2 / §4).
                ScratchScenePool.MarkForReap( agentId );
            }
            if ( evicted > 0 || released > 0 )
                LogInfo( $"Session {sessionId[..Math.Min( 8, sessionId.Length )]} close: evicted {evicted} operation lease(s), released {released} GameObject lock(s)." );
        }
        catch ( Exception ex )
        {
            LogError( $"ReleaseSessionLocks failed for session {sessionId}: {ex.Message}" );
        }
    }

    // ── SSE write ──────────────────────────────────────────────────────────

    /// <summary>Writes a single SSE event to the given session's output stream.</summary>
    internal static async Task SendSseEvent( McpSession session, string eventName, string data )
    {
        if ( session.SseResponse == null || !session.SseResponse.OutputStream.CanWrite ) return;
        await session.WriteLock.WaitAsync();
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
        finally
        {
            session.WriteLock.Release();
        }
    }
}
