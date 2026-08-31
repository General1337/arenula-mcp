using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Arenula;

/// <summary>operation tool: validate, preview, apply, and rollback compatibility wrapper.</summary>
internal static class OperationHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "validate" => Validate( args ),
                "preview"  => Preview( args ),
                "apply"    => Apply( args ),
                "rollback" => Rollback( args ),
                // Control-plane actions normally enter via RpcDispatcher's
                // coordinator-bypass fast path; this arm covers indirect routes
                // (e.g. batch dispatch) where no McpSession is threaded.
                "cancel" or "list" or "locks_held" => HandleControl( action, args, null ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'operation'.", action,
                    "Valid actions: validate, preview, apply, rollback, cancel, list, locks_held" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    /// <summary>
    /// Coordinator control plane (mcp-operation-lifecycle-hardening, audit
    /// finding #3). Dispatched by RpcDispatcher BEFORE McpOperationCoordinator.
    /// TryBegin — a wedged scene-wide lease blocks even ReadOnly ops in
    /// FindConflict, so the cancel that exists to clear the wedge must never be
    /// gated by it. These actions touch only MCP-internal thread-safe static
    /// state: no scene access, no MainThread hop, no lease.
    /// </summary>
    internal static object HandleControl( string action, JsonElement args, McpSession session )
    {
        try
        {
            return action switch
            {
                "cancel"     => Cancel( args, session ),
                "list"       => HandlerBase.Success( McpOperationCoordinator.Snapshot() ),
                "locks_held" => HandlerBase.Success( GameObjectLockMap.LocksHeldSnapshot() ),
                _ => HandlerBase.Error( $"Unknown control action '{action}' for tool 'operation'.", action,
                    "Control actions: cancel, list, locks_held" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object Cancel( JsonElement args, McpSession session )
    {
        var operationId = HandlerBase.GetString( args, "operation_id" );
        if ( string.IsNullOrWhiteSpace( operationId ) )
            return HandlerBase.Error( "Missing required 'operation_id'.", "cancel",
                "Get in-flight operation ids from operation.list, or from the rejected_operation_id / holder.operation_id fields of a gameobject_locked / mcp_busy error." );

        var canceled = McpOperationCoordinator.CancelOperation( operationId, session?.AgentId );
        if ( canceled is null )
            return HandlerBase.Error( $"No active operation '{operationId}'.", "cancel",
                "It may have completed, been canceled, or been zombie-swept already. operation.list shows current in-flight operations.",
                McpOperationCoordinator.Snapshot() );

        return HandlerBase.Success( new
        {
            canceled = true,
            operation = canceled,
            note = "Lease released — other agents are unblocked. The underlying handler task, if still "
                 + "running, is detached and may still mutate editor state until it finishes; its eventual "
                 + "lease release is a no-op. Cancel coordination bookkeeping, not handler execution."
        } );
    }

    private static object Validate( JsonElement args )
    {
        if ( TryGetOperationsJson( args, out var operationsJson ) )
            return ValidateOperations( operationsJson );

        var spec = ParseSingleOperation( args );
        var errors = ValidateSingleOperation( spec );
        return HandlerBase.Success( new
        {
            valid = errors.Count == 0,
            mode = "single",
            tool = spec.Tool,
            operation_action = spec.Action,
            errors
        } );
    }

    private static object Preview( JsonElement args )
    {
        if ( TryGetOperationsJson( args, out _ ) )
            return TransactionHandler.Handle( "preview", args );

        var spec = ParseSingleOperation( args );
        var errors = ValidateSingleOperation( spec );
        return HandlerBase.Success( new
        {
            valid = errors.Count == 0,
            mode = "single",
            tool = spec.Tool,
            operation_action = spec.Action,
            will_apply = errors.Count == 0,
            errors
        } );
    }

    private static object Apply( JsonElement args )
    {
        var transactionId = HandlerBase.GetString( args, "transaction_id" );
        if ( !string.IsNullOrWhiteSpace( transactionId ) )
            return TransactionHandler.Handle( "apply", args );

        var spec = ParseSingleOperation( args );
        var errors = ValidateSingleOperation( spec );
        if ( errors.Count > 0 )
            return HandlerBase.Success( new { applied = false, errors } );

        var merged = BatchHandler.MergeActionAndParams( spec.Action, spec.Arguments );
        // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — operation.apply does not currently receive McpSession; passing null is safe because session.identify is not a valid target for operation.apply (which dispatches scene edits only). Phase 4 may thread McpSession through OperationHandler for full per-key Coordinator integration. No inherited handoff hypothesis applies.]
        var result = BatchHandler.DispatchToHandler( spec.Tool, spec.Action, merged, null );

        return HandlerBase.Success( new
        {
            applied = true,
            mode = "single",
            tool = spec.Tool,
            operation_action = spec.Action,
            rollback = "Use editor.undo or operation.rollback to undo the most recent editor write.",
            result
        } );
    }

    private static object Rollback( JsonElement args )
    {
        var transactionId = HandlerBase.GetString( args, "transaction_id" );
        if ( !string.IsNullOrWhiteSpace( transactionId ) )
            return TransactionHandler.Handle( "rollback", args );

        return EditorHandler.Handle( "undo", default );
    }

    private static object ValidateOperations( string operationsJson )
    {
        var errors = new List<object>();
        var operations = new List<object>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse( operationsJson );
            root = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Success( new
            {
                valid = false,
                mode = "batch",
                errors = new[] { $"Invalid operations JSON: {ex.Message}" }
            } );
        }

        if ( root.ValueKind != JsonValueKind.Array )
        {
            return HandlerBase.Success( new
            {
                valid = false,
                mode = "batch",
                errors = new[] { "'operations' must be a JSON array." }
            } );
        }

        var index = 0;
        foreach ( var op in root.EnumerateArray() )
        {
            var tool = op.TryGetProperty( "tool", out var toolEl ) ? toolEl.GetString() : null;
            var opAction = op.TryGetProperty( "action", out var actionEl ) ? actionEl.GetString() : null;
            var opErrors = ValidateToolAction( tool, opAction );
            operations.Add( new { index, tool, action = opAction, valid = opErrors.Count == 0 } );
            foreach ( var error in opErrors )
                errors.Add( new { index, error } );
            index++;
        }

        return HandlerBase.Success( new
        {
            valid = errors.Count == 0,
            mode = "batch",
            operation_count = operations.Count,
            operations,
            errors
        } );
    }

    private static bool TryGetOperationsJson( JsonElement args, out string operationsJson )
    {
        operationsJson = HandlerBase.GetString( args, "operations" );
        return !string.IsNullOrWhiteSpace( operationsJson );
    }

    private static OperationSpec ParseSingleOperation( JsonElement args )
    {
        var tool = HandlerBase.GetString( args, "tool" );
        var opAction = HandlerBase.GetString( args, "operation_action" );

        JsonElement arguments = default;
        if ( args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty( "arguments", out var argEl ) && argEl.ValueKind == JsonValueKind.Object )
        {
            arguments = argEl.Clone();
        }
        else
        {
            arguments = CopyTopLevelArguments( args );
        }

        return new OperationSpec( tool, opAction, arguments );
    }

    private static JsonElement CopyTopLevelArguments( JsonElement args )
    {
        var dict = new Dictionary<string, JsonElement>();
        if ( args.ValueKind == JsonValueKind.Object )
        {
            foreach ( var prop in args.EnumerateObject() )
            {
                if ( prop.Name is "action" or "tool" or "operation_action" or "transaction_id" or "operations" )
                    continue;
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        var json = JsonSerializer.Serialize( dict );
        using var doc = JsonDocument.Parse( json );
        return doc.RootElement.Clone();
    }

    private static List<string> ValidateSingleOperation( OperationSpec spec )
    {
        return ValidateToolAction( spec.Tool, spec.Action );
    }

    private static List<string> ValidateToolAction( string tool, string action )
    {
        var errors = new List<string>();

        if ( string.IsNullOrWhiteSpace( tool ) )
            errors.Add( "Missing tool." );
        if ( string.IsNullOrWhiteSpace( action ) )
            errors.Add( "Missing operation_action." );

        if ( errors.Count > 0 )
            return errors;

        var supported = tool switch
        {
            "gameobject" => new[] { "create", "destroy", "destroy_by_name", "destroy_children", "duplicate", "reparent", "rename", "enable", "set_tags", "set_transform", "batch_transform" },
            "component" => new[] { "add", "remove", "set_property", "set_enabled", "copy", "bulk_set", "apply_properties" },
            "prefab" => new[] { "instantiate", "break", "update", "create", "save_overrides", "revert" },
            "asset_manage" => new[] { "create", "delete", "rename", "move", "save", "reload" },
            _ => Array.Empty<string>()
        };

        if ( supported.Length == 0 )
        {
            errors.Add( $"Unsupported operation tool '{tool}'. Supported tools: gameobject, component, prefab, asset_manage." );
        }
        else if ( !supported.Contains( action, StringComparer.OrdinalIgnoreCase ) )
        {
            errors.Add( $"Unsupported action '{action}' for operation tool '{tool}'." );
        }

        return errors;
    }

    private sealed record OperationSpec( string Tool, string Action, JsonElement Arguments );
}
