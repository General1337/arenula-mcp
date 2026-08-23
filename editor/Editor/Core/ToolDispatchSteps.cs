// Editor/Core/ToolDispatchSteps.cs
//
// mcp-batch-async-session-identify (findings.md #7, 2026-06-11): stateless
// step-dispatch helpers extracted so composite tools (batch.execute, and later
// operation.apply / transaction.apply / RpcDispatcher itself) can route ANY
// tool.action to its handler with correct threading — including the async
// compile/verify chains that the legacy BatchHandler.DispatchToHandler
// rejected outright, forcing 4+ HTTP round-trips per verification loop.
// [PERF-OK: mechanical step-dispatch extraction for batch async coverage (findings #7) — no hot-path change; the phrase reduce round-trips describes agent-facing HTTP friction, which trips the perf-gate keyword scan (same false positive documented at BatchHandler.cs:18).]
//
// "Stateless" is load-bearing: these helpers take NO McpOperationCoordinator
// lease and do NO operation bookkeeping. A composite tool's sub-steps run
// under the lease its own dispatch already holds (the coordinator allows
// same-session reentry), exactly as the legacy sync DispatchToHandler did.
//
// RpcDispatcher.DispatchToolCall is the routing source of truth this file
// mirrors (it cannot delegate here this cycle — RpcDispatcher edits are
// reserved to mission control; see the session handoff for the collapse plan).

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

internal static class ToolDispatchSteps
{
    /// <summary>
    /// Route one tool.action to its handler with correct threading and await the
    /// result. Callable from any thread. Discovered [McpTool] handlers dispatch
    /// solely from their declared <see cref="McpThreading"/> contract.
    ///
    /// The ONLY structurally rejected composition is batch-inside-batch
    /// (unbounded recursion through a tool whose payload is agent-authored JSON).
    /// </summary>
    internal static async Task<object> RunStepAsync( string tool, string action, JsonElement args, McpSession session )
    {
        if ( string.Equals( tool, "batch", StringComparison.OrdinalIgnoreCase ) )
            throw new InvalidOperationException( "Nested batch operations are not supported." );

        if ( !McpToolScanner.Cached.TryGetValue( tool, out var scannedTool ) )
            throw new InvalidOperationException( $"Tool '{tool}' not found" );

        var scanned = ResolveAction( scannedTool, action );
        if ( scanned is null )
        {
            return HandlerBase.Error(
                $"Unknown action '{action}' for tool '{scannedTool.Name}'.", action,
                $"Valid actions: {string.Join( ", ", scannedTool.Actions.Keys )}" );
        }

        var invokeArgs = scanned.WantsSession
            ? new object[] { args, session }
            : new object[] { args };

        try
        {
            if ( scanned.Threading == McpThreading.MainThread )
            {
                await GameTask.MainThread();
                return scanned.Method.Invoke( null, invokeArgs );
            }

            return await (Task<object>)scanned.Method.Invoke( null, invokeArgs );
        }
        catch ( System.Reflection.TargetInvocationException tie )
        {
            throw tie.InnerException ?? tie;
        }
    }
    /// <summary>
    /// Synchronous discovered dispatch for composite callers. MUST already be on
    /// the main thread. Async-routed actions throw; compose those through
    /// <see cref="RunStepAsync"/> instead.
    /// </summary>
    internal static object RunSyncStep( string tool, string action, JsonElement args, McpSession session )
    {
        if ( string.Equals( tool, "batch", StringComparison.OrdinalIgnoreCase ) )
            throw new InvalidOperationException( "Nested batch operations are not supported." );

        if ( !McpToolScanner.Cached.TryGetValue( tool, out var scannedTool ) )
            throw new InvalidOperationException( $"Tool '{tool}' not found" );

        var scanned = ResolveAction( scannedTool, action );
        if ( scanned is null )
        {
            return HandlerBase.Error(
                $"Unknown action '{action}' for tool '{scannedTool.Name}'.", action,
                $"Valid actions: {string.Join( ", ", scannedTool.Actions.Keys )}" );
        }
        if ( scanned.Threading != McpThreading.MainThread )
            throw new InvalidOperationException(
                $"Tool '{tool}' action '{action}' is async and cannot be dispatched synchronously. Compose it via an async step (RunStepAsync)." );

        var invokeArgs = scanned.WantsSession
            ? new object[] { args, session }
            : new object[] { args };
        try
        {
            return scanned.Method.Invoke( null, invokeArgs );
        }
        catch ( System.Reflection.TargetInvocationException tie )
        {
            throw tie.InnerException ?? tie;
        }
    }
    /// <summary>
    /// True when the tool (or tool+action pair) routes exclusively through an
    /// async handler — i.e. it has NO sync dispatch form.
    /// </summary>
    internal static bool IsAsyncOnly( string tool, string action )
    {
        if ( !McpToolScanner.Cached.TryGetValue( tool, out var scannedTool ) )
            return false;
        return ResolveAction( scannedTool, action )?.Threading != McpThreading.MainThread;
    }

    /// <summary>
    /// Resolve the <see cref="ScannedAction"/> for a step. A 0-action tool (a
    /// single handler keyed on the empty string) ignores the action param —
    /// same rule as RpcDispatcher.ResolveAction.
    /// </summary>
    private static ScannedAction ResolveAction( ScannedTool tool, string action )
    {
        if ( tool.Actions.Count == 1 && tool.Actions.TryGetValue( "", out var soleAction ) )
            return soleAction;
        return tool.Actions.TryGetValue( action ?? "", out var found ) ? found : null;
    }
}
