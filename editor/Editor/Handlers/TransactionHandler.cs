using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Arenula;

internal static class TransactionHandler
{
    private sealed class TransactionState
    {
        public string Id { get; init; }
        public string OperationsJson { get; init; }
        public string OwnerOperationId { get; init; }
        public string SessionId { get; init; }
        public int PreviewRevision { get; init; }
        public int ApplyRevision { get; set; } = -1;
        public int AppliedOps { get; set; }
        public List<object> LastResults { get; } = new();
    }

    private static readonly Dictionary<string, TransactionState> Transactions = new( StringComparer.OrdinalIgnoreCase );

    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "preview" => Preview( args ),
                "apply" => Apply( args ),
                "rollback" => Rollback( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: preview, apply, rollback" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object Preview( JsonElement args )
    {
        var opsJson = HandlerBase.GetString( args, "operations" );
        if ( string.IsNullOrWhiteSpace( opsJson ) )
            return HandlerBase.Error( "Missing required 'operations' parameter.", "preview" );

        using var doc = JsonDocument.Parse( opsJson );
        if ( doc.RootElement.ValueKind != JsonValueKind.Array )
            return HandlerBase.Error( "'operations' must be a JSON array.", "preview" );

        var txId = Guid.NewGuid().ToString();
        var ops = new List<object>();
        foreach ( var op in doc.RootElement.EnumerateArray() )
        {
            var hasParams = op.TryGetProperty( "params", out var paramsEl );
            ops.Add( new
            {
                tool = op.TryGetProperty( "tool", out var toolEl ) ? toolEl.GetString() : null,
                action = op.TryGetProperty( "action", out var actionEl ) ? actionEl.GetString() : null,
                params_present = hasParams,
                targets = ExtractTargets( hasParams ? paramsEl : default )
            } );
        }

        var ownerOperationId = McpOperationCoordinator.CurrentOperationId;
        var sessionId = McpOperationCoordinator.CurrentSessionId;
        var previewRevision = McpOperationCoordinator.EditorRevision;

        Transactions[txId] = new TransactionState
        {
            Id = txId,
            OperationsJson = opsJson,
            OwnerOperationId = ownerOperationId,
            SessionId = sessionId,
            PreviewRevision = previewRevision
        };

        return HandlerBase.Success( new
        {
            transaction_id = txId,
            operation_count = ops.Count,
            operations = ops,
            session_id = sessionId,
            owner_operation_id = ownerOperationId,
            pre_apply_editor_revision = previewRevision,
            rollback = "Rollback is allowed only if no unrelated write advances the editor revision after apply. editor.undo remains available but is global and unsafe across agents."
        } );
    }

    private static object Apply( JsonElement args )
    {
        var transactionId = HandlerBase.GetString( args, "transaction_id" );
        if ( string.IsNullOrWhiteSpace( transactionId ) || !Transactions.TryGetValue( transactionId, out var tx ) )
            return HandlerBase.Error( "Unknown 'transaction_id'. Call transaction.preview first.", "apply" );

        using var doc = JsonDocument.Parse( tx.OperationsJson );
        tx.LastResults.Clear();
        tx.AppliedOps = 0;
        tx.ApplyRevision = -1;
        var continueOnError = HandlerBase.GetBool( args, "continue_on_error", false );

        foreach ( var op in doc.RootElement.EnumerateArray() )
        {
            var tool = op.GetProperty( "tool" ).GetString();
            var opAction = op.GetProperty( "action" ).GetString();
            var merged = BatchHandler.MergeActionAndParams( opAction, op.TryGetProperty( "params", out var p ) ? p : default );
            // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — transaction.apply dispatches per-op handlers internally without McpSession; passing null is safe for Phase 3 (session.identify is not a valid transaction op). Phase 4 will thread McpSession through TransactionHandler to wire per-key Coordinator extraction across the transaction's ops. No inherited handoff hypothesis applies.]
            var result = BatchHandler.DispatchToHandler( tool, opAction, merged, null );
            tx.LastResults.Add( new { tool, action = opAction, result } );
            tx.AppliedOps++;

            if ( IsToolError( result ) && !continueOnError )
                break;
        }

        tx.ApplyRevision = McpOperationCoordinator.EditorRevision + 1;

        return HandlerBase.Success( new
        {
            transaction_id = transactionId,
            applied = tx.AppliedOps,
            continue_on_error = continueOnError,
            stopped_on_error = tx.LastResults.Count > 0 && IsToolError( tx.LastResults[^1] ) && !continueOnError,
            owner_operation_id = tx.OwnerOperationId,
            apply_operation_id = McpOperationCoordinator.CurrentOperationId,
            preview_revision = tx.PreviewRevision,
            apply_revision = tx.ApplyRevision,
            results = tx.LastResults
        } );
    }

    private static object Rollback( JsonElement args )
    {
        var transactionId = HandlerBase.GetString( args, "transaction_id" );
        if ( string.IsNullOrWhiteSpace( transactionId ) || !Transactions.TryGetValue( transactionId, out var tx ) )
            return HandlerBase.Error( "Unknown 'transaction_id'.", "rollback" );

        if ( tx.AppliedOps <= 0 || tx.ApplyRevision < 0 )
            return HandlerBase.Error( "Transaction has not been applied; nothing to rollback.", "rollback" );

        var currentRevision = McpOperationCoordinator.EditorRevision;
        if ( currentRevision != tx.ApplyRevision )
        {
            return HandlerBase.Error(
                "Rollback refused because another write advanced the editor revision after this transaction applied.",
                "rollback",
                "Manual recovery required: inspect transaction results and use targeted editor undo/revert only if you own the intervening changes.",
                new
                {
                    transaction_id = transactionId,
                    apply_revision = tx.ApplyRevision,
                    current_revision = currentRevision,
                    applied_ops = tx.AppliedOps,
                    owner_operation_id = tx.OwnerOperationId
                } );
        }

        for ( int i = 0; i < tx.AppliedOps; i++ )
            EditorHandler.Handle( "undo", default );

        return HandlerBase.Success( new
        {
            transaction_id = transactionId,
            rolled_back = tx.AppliedOps,
            rollback_operation_id = McpOperationCoordinator.CurrentOperationId,
            note = "Rollback used the global editor undo stack after confirming no unrelated MCP write advanced the editor revision."
        } );
    }

    private static object ExtractTargets( JsonElement paramsEl )
    {
        if ( paramsEl.ValueKind != JsonValueKind.Object )
            return Array.Empty<object>();

        var targets = new List<object>();
        foreach ( var name in new[] { "id", "component_id", "target_id", "path", "prefab_path", "save_path" } )
        {
            if ( paramsEl.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String )
                targets.Add( new { field = name, value = value.GetString() } );
        }
        return targets;
    }

    private static bool IsToolError( object result )
        => McpResult.IsErrorResult( result );
}
