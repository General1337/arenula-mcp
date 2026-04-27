using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Arenula;

internal sealed class McpOperationLease : IDisposable
{
    private readonly string _operationId;
    private bool _disposed;

    internal McpOperationLease( string operationId )
    {
        _operationId = operationId;
    }

    public void Dispose()
    {
        if ( _disposed ) return;
        _disposed = true;
        McpOperationCoordinator.Release( _operationId );
    }
}

/// <summary>
/// Coordinates editor-facing MCP operations so independent agents cannot interleave
/// scene writes, file writes, compile/play lifecycle work, and rollback.
/// </summary>
internal static class McpOperationCoordinator
{
    private sealed class ActiveOperation
    {
        public string OperationId { get; init; }
        public string SessionId { get; init; }
        public string Tool { get; init; }
        public string Action { get; init; }
        public ToolRegistry.ToolActionMetadata Metadata { get; init; }
        public DateTime StartedUtc { get; init; }
    }

    private sealed class OperationScope
    {
        public string OperationId { get; init; }
        public string SessionId { get; init; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveOperation> Active = new();
    private static readonly AsyncLocal<OperationScope> Current = new();

    internal static string CurrentOperationId => Current.Value?.OperationId;
    internal static string CurrentSessionId => Current.Value?.SessionId;
    internal static int EditorRevision { get; private set; }

    internal static bool TryBegin(
        string sessionId,
        string tool,
        string action,
        ToolRegistry.ToolActionMetadata metadata,
        out McpOperationLease lease,
        out object busyError )
    {
        metadata ??= ToolRegistry.GetOperationMetadata( tool, action );
        var operationId = "op_" + Guid.NewGuid().ToString( "N" )[..12];
        lease = null;
        busyError = null;

        lock ( Sync )
        {
            var conflict = FindConflict( metadata );
            if ( conflict != null )
            {
                busyError = BuildBusyError( tool, action, operationId, conflict );
                return false;
            }

            Active[operationId] = new ActiveOperation
            {
                OperationId = operationId,
                SessionId = sessionId,
                Tool = tool,
                Action = action,
                Metadata = metadata,
                StartedUtc = DateTime.UtcNow
            };
            Current.Value = new OperationScope { OperationId = operationId, SessionId = sessionId };
            lease = new McpOperationLease( operationId );
            return true;
        }
    }

    private static ActiveOperation FindConflict( ToolRegistry.ToolActionMetadata metadata )
    {
        if ( metadata.ReadOnly )
        {
            return Active.Values.FirstOrDefault( op => !op.Metadata.ReadOnly );
        }

        return Active.Values.OrderBy( op => op.StartedUtc ).FirstOrDefault();
    }

    private static object BuildBusyError( string tool, string action, string rejectedOperationId, ActiveOperation active )
    {
        var elapsed = DateTime.UtcNow - active.StartedUtc;
        return HandlerBase.Error(
            "Another editor operation is already running.",
            $"{tool}.{action}",
            "Retry after the active operation completes. Read-only tools can run concurrently unless an exclusive editor operation is active.",
            new
            {
                code = "mcp_busy",
                rejected_operation_id = rejectedOperationId,
                active_operation = new
                {
                    operation_id = active.OperationId,
                    session_id = active.SessionId,
                    tool = active.Tool,
                    action = active.Action,
                    read_only = active.Metadata.ReadOnly,
                    write_kind = active.Metadata.WriteKind,
                    lifecycle_kind = active.Metadata.LifecycleKind,
                    elapsed_ms = (int)elapsed.TotalMilliseconds
                }
            } );
    }

    internal static void Release( string operationId )
    {
        lock ( Sync )
        {
            if ( Active.TryGetValue( operationId, out var op ) )
            {
                Active.Remove( operationId );
                if ( !op.Metadata.ReadOnly )
                    EditorRevision++;
            }

            if ( Current.Value?.OperationId == operationId )
                Current.Value = null;
        }
    }

    internal static object Snapshot()
    {
        lock ( Sync )
        {
            return new
            {
                editor_revision = EditorRevision,
                active = Active.Values
                    .OrderBy( op => op.StartedUtc )
                    .Select( op => new
                    {
                        operation_id = op.OperationId,
                        session_id = op.SessionId,
                        tool = op.Tool,
                        action = op.Action,
                        read_only = op.Metadata.ReadOnly,
                        write_kind = op.Metadata.WriteKind,
                        lifecycle_kind = op.Metadata.LifecycleKind,
                        elapsed_ms = (int)(DateTime.UtcNow - op.StartedUtc).TotalMilliseconds
                    } )
                    .ToArray()
            };
        }
    }
}
