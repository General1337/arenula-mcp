using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>
/// Passive, local operation telemetry for MCP <c>tools/call</c> requests.
///
/// This is deliberately a tee from <see cref="RpcDispatcher"/>, not a second
/// operation coordinator. It never receives tool arguments, response content,
/// console output, images, or source text. Events are serialized and written on
/// a background worker to a bounded, gitignored JSONL artifact.
/// </summary>
internal static class McpOperationTelemetry
{
    internal const long MaxLogBytes = 5L * 1024L * 1024L;
    private const int MaxFieldChars = 160;
    private const int MaxDrainBatch = 256;

    private static readonly ConcurrentQueue<TelemetryEvent> Pending = new();
    private static readonly object WriterSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int _drainScheduled;

    internal sealed class Call
    {
        internal string CallId { get; init; }
        internal string SessionId { get; init; }
        internal string AgentId { get; init; }
        internal string AgentSlug { get; init; }
        internal string Tool { get; init; }
        internal string Action { get; init; }
        internal bool ReadOnly { get; init; }
        internal string WriteKind { get; init; }
        internal string LifecycleKind { get; init; }
        internal int RequestChars { get; init; }
        internal long StartedTimestamp { get; init; }
        internal string OperationId { get; set; }
        internal int Completed;
    }

    private sealed class TelemetryEvent
    {
        [JsonPropertyName( "schema_version" )]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName( "phase" )]
        public string Phase { get; init; }

        [JsonPropertyName( "call_id" )]
        public string CallId { get; init; }

        [JsonPropertyName( "operation_id" )]
        public string OperationId { get; init; }

        [JsonPropertyName( "timestamp_utc" )]
        public DateTime TimestampUtc { get; init; }

        [JsonPropertyName( "session_id" )]
        public string SessionId { get; init; }

        [JsonPropertyName( "agent_id" )]
        public string AgentId { get; init; }

        [JsonPropertyName( "agent_slug" )]
        public string AgentSlug { get; init; }

        [JsonPropertyName( "tool" )]
        public string Tool { get; init; }

        [JsonPropertyName( "action" )]
        public string Action { get; init; }

        [JsonPropertyName( "read_only" )]
        public bool ReadOnly { get; init; }

        [JsonPropertyName( "write_kind" )]
        public string WriteKind { get; init; }

        [JsonPropertyName( "lifecycle_kind" )]
        public string LifecycleKind { get; init; }

        // Character counts are O(1) to obtain. Exact UTF-8 byte counting would
        // scan large serialized captures a second time on the request hot path.
        [JsonPropertyName( "request_chars" )]
        public int RequestChars { get; init; }

        [JsonPropertyName( "duration_ms" )]
        public double? DurationMs { get; init; }

        [JsonPropertyName( "response_chars" )]
        public int? ResponseChars { get; init; }

        [JsonPropertyName( "outcome" )]
        public string Outcome { get; init; }
    }

    internal static string ArtifactPath
    {
        get
        {
            var root = HandlerBase.GetProjectRoot();
            return string.IsNullOrWhiteSpace( root )
                ? null
                : Path.Combine( root, ".mcp-telemetry", "operations.jsonl" );
        }
    }

    internal static Call Begin(
        McpSession session,
        string tool,
        string action,
        ToolRegistry.ToolActionMetadata metadata,
        int requestChars )
    {
        try
        {
            var sessionId = Limit( session?.SessionId );
            var (agentId, agentSlug) = AgentIdentityRegistry.ResolveFresh(
                session?.SessionId, session?.AgentId, session?.AgentSlug );

            var call = new Call
            {
                CallId = "call_" + Guid.NewGuid().ToString( "N" )[..12],
                SessionId = sessionId,
                AgentId = Limit( agentId ),
                AgentSlug = Limit( agentSlug ),
                Tool = Limit( tool ),
                Action = Limit( action ),
                ReadOnly = metadata?.ReadOnly ?? false,
                WriteKind = Limit( metadata?.WriteKind ?? "unknown" ),
                LifecycleKind = Limit( metadata?.LifecycleKind ?? "unknown" ),
                RequestChars = Math.Max( 0, requestChars ),
                StartedTimestamp = Stopwatch.GetTimestamp()
            };

            Enqueue( ToEvent( call, "start", DateTime.UtcNow ) );
            return call;
        }
        catch
        {
            // Telemetry is evidence, never a dependency of the operation.
            return null;
        }
    }

    internal static void BindOperation( Call call, string operationId )
    {
        if ( call == null ) return;
        call.OperationId = Limit( operationId );
    }

    internal static void Complete( Call call, string outcome, int responseChars )
    {
        if ( call == null || Interlocked.Exchange( ref call.Completed, 1 ) != 0 )
            return;

        try
        {
            var elapsed = Stopwatch.GetTimestamp() - call.StartedTimestamp;
            var durationMs = Math.Max( 0d, elapsed * 1000d / Stopwatch.Frequency );
            Enqueue( ToEvent(
                call,
                "finish",
                DateTime.UtcNow,
                Limit( outcome ?? "unknown" ),
                Math.Max( 0, responseChars ),
                durationMs ) );
        }
        catch
        {
            // A failed completion event must not alter the MCP response.
        }
    }

    private static TelemetryEvent ToEvent(
        Call call,
        string phase,
        DateTime timestampUtc,
        string outcome = null,
        int? responseChars = null,
        double? durationMs = null )
        => new()
        {
            Phase = phase,
            CallId = call.CallId,
            OperationId = call.OperationId,
            TimestampUtc = timestampUtc,
            SessionId = call.SessionId,
            AgentId = call.AgentId,
            AgentSlug = call.AgentSlug,
            Tool = call.Tool,
            Action = call.Action,
            ReadOnly = call.ReadOnly,
            WriteKind = call.WriteKind,
            LifecycleKind = call.LifecycleKind,
            RequestChars = call.RequestChars,
            DurationMs = durationMs,
            ResponseChars = responseChars,
            Outcome = outcome
        };

    private static string Limit( string value )
    {
        if ( string.IsNullOrEmpty( value ) ) return "";
        var clean = value.Replace( '\r', ' ' ).Replace( '\n', ' ' );
        return clean.Length <= MaxFieldChars ? clean : clean[..MaxFieldChars];
    }

    private static void Enqueue( TelemetryEvent telemetryEvent )
    {
        if ( telemetryEvent == null || string.IsNullOrWhiteSpace( ArtifactPath ) )
            return;

        Pending.Enqueue( telemetryEvent );
        ScheduleDrain();
    }

    private static void ScheduleDrain()
    {
        if ( Interlocked.CompareExchange( ref _drainScheduled, 1, 0 ) != 0 )
            return;

        try
        {
            _ = Task.Run( DrainWorker );
        }
        catch
        {
            Interlocked.Exchange( ref _drainScheduled, 0 );
        }
    }

    private static void DrainWorker()
    {
        try
        {
            lock ( WriterSync )
                DrainLocked();
        }
        catch
        {
            // The caller never observes telemetry failures.
        }
        finally
        {
            Interlocked.Exchange( ref _drainScheduled, 0 );
            if ( !Pending.IsEmpty )
                ScheduleDrain();
        }
    }

    private static void DrainLocked()
    {
        while ( true )
        {
            var events = new List<TelemetryEvent>( MaxDrainBatch );
            while ( events.Count < MaxDrainBatch && Pending.TryDequeue( out var telemetryEvent ) )
                events.Add( telemetryEvent );

            if ( events.Count == 0 )
                return;

            var lines = new List<string>( events.Count );
            foreach ( var telemetryEvent in events )
            {
                try
                {
                    lines.Add( JsonSerializer.Serialize( telemetryEvent, JsonOptions ) );
                }
                catch
                {
                    // Skip only the malformed evidence row.
                }
            }

            if ( lines.Count > 0 )
                TryAppendLines( ArtifactPath, lines, MaxLogBytes );
        }
    }

    /// <summary>Synchronously drains queued rows for durable in-editor tests.</summary>
    internal static void FlushForTests()
    {
        lock ( WriterSync )
            DrainLocked();
    }

    /// <summary>
    /// Test seam for rotation/failure-isolation checks. Production dispatch
    /// supplies fixed-schema rows only; arbitrary strings never enter this API.
    /// </summary>
    internal static bool TryAppendLinesForTest(
        string path,
        IReadOnlyList<string> lines,
        long maxBytes )
    {
        lock ( WriterSync )
            return TryAppendLines( path, lines, maxBytes );
    }

    private static bool TryAppendLines(
        string path,
        IReadOnlyList<string> lines,
        long maxBytes )
    {
        try
        {
            if ( string.IsNullOrWhiteSpace( path ) || lines == null || lines.Count == 0 )
                return false;

            maxBytes = Math.Max( 128, maxBytes );
            var directory = Path.GetDirectoryName( path );
            if ( string.IsNullOrWhiteSpace( directory ) )
                return false;
            Directory.CreateDirectory( directory );

            long currentBytes = File.Exists( path ) ? new FileInfo( path ).Length : 0;
            long bufferedBytes = 0;
            var buffer = new StringBuilder();
            var utf8 = new UTF8Encoding( false );

            void FlushBuffer()
            {
                if ( buffer.Length == 0 ) return;
                File.AppendAllText( path, buffer.ToString(), utf8 );
                currentBytes += bufferedBytes;
                buffer.Clear();
                bufferedBytes = 0;
            }

            foreach ( var line in lines )
            {
                var safeLine = line ?? "";
                var encodedBytes = Encoding.UTF8.GetByteCount( safeLine ) + 1L;
                if ( encodedBytes > maxBytes )
                    continue;

                if ( currentBytes + bufferedBytes + encodedBytes > maxBytes )
                {
                    FlushBuffer();
                    Rotate( path );
                    currentBytes = 0;
                }

                buffer.Append( safeLine ).Append( '\n' );
                bufferedBytes += encodedBytes;
            }

            FlushBuffer();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Rotate( string path )
    {
        if ( !File.Exists( path ) ) return;
        var backup = path + ".1";
        if ( File.Exists( backup ) )
            File.Delete( backup );
        File.Move( path, backup );
    }
}
