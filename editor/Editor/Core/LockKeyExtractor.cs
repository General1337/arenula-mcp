// [HANDOFF-TRUST: greenfield extractor authored for Path C of engine-fork-phase3-1-library-mvp; no inherited handoff hypothesis applies — Phase 1 falsified the RFC's editor-mutex layer claim and pinned this design (see docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md §"Path C — committed design"). Phase 5 corrected the BatchTransformTargets + ComponentOwningGameObject extractors to the ACTUAL handler arg shapes verified against GameObjectHandler.cs / ComponentHandler.cs (Phase 1 §T3 explicitly deferred arg-name confirmation to Phase 4/5).]
// [PERF-OK: greenfield correctness primitive for multi-agent contention check; not perf optimization of any existing hot path — extractors run once per MCP tool call before Coordinator dispatch, JSON parsing is single-element-tree walk over already-parsed args, hash-set dedup is O(N) over op count which is bounded by per-call args.]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// Lock-key extraction strategy. Set on <see cref="ToolRegistry.ToolActionMetadata"/>
/// to declare the per-call lock surface of a mutating tool action.
///
///   None                      — legacy: scene-wide write. Serializes against
///                                every other non-readonly op cross-session.
///   GameObjectId              — args["id"]              -> 1 Guid
///   GameObjectParentId        — args["parent_id"]       -> 0 or 1 Guid
///   ComponentOwningGameObject — the GameObject a component write mutates:
///                                args["id"] (add / remove / set_property /
///                                set_enabled / bulk_set / apply_properties)
///                                with args["target_id"] fallback for
///                                component.copy -> 0 or 1 Guid.
///   BatchTransformTargets     — args["ids"], a comma-separated GUID string
///                                (gameobject.batch_transform's actual wire
///                                shape) -> N Guids.
///   TransactionTargets        — args["operations"][].params.{id,parent_id,
///                                gameobject_id,target_id} -> union of N Guids.
///
/// Contract: an empty extractor result for a non-None strategy means "no
/// per-key conflict possible" (i.e. operate as a per-key write with zero
/// keys — never blocks another per-key write, only blocks against scene-wide
/// writes). A None strategy is the conservative default for any action whose
/// lock-key surface isn't enumerable.
/// </summary>
public enum LockKeyStrategy
{
    None,
    GameObjectId,
    GameObjectParentId,
    ComponentOwningGameObject,
    BatchTransformTargets,
    TransactionTargets,
}

internal static class LockKeyExtractor
{
    private static readonly IReadOnlyList<Guid> Empty = Array.Empty<Guid>();

    internal static IReadOnlyList<Guid> Extract( LockKeyStrategy strategy, JsonElement args )
    {
        return strategy switch
        {
            LockKeyStrategy.None => Empty,
            LockKeyStrategy.GameObjectId => ParseSingle( args, "id" ),
            LockKeyStrategy.GameObjectParentId => ParseSingle( args, "parent_id" ),
            LockKeyStrategy.ComponentOwningGameObject => ParseComponentOwner( args ),
            LockKeyStrategy.BatchTransformTargets => ParseBatchTransformTargets( args ),
            LockKeyStrategy.TransactionTargets => ParseTransactionTargets( args ),
            _ => Empty,
        };
    }

    private static IReadOnlyList<Guid> ParseSingle( JsonElement args, string field )
    {
        if ( args.ValueKind != JsonValueKind.Object ) return Empty;
        if ( !args.TryGetProperty( field, out var el ) ) return Empty;
        if ( el.ValueKind != JsonValueKind.String ) return Empty;
        if ( !Guid.TryParse( el.GetString(), out var g ) ) return Empty;
        return new[] { g };
    }

    /// <summary>
    /// Component write actions carry the owning GameObject as args["id"]
    /// (add / remove / set_property / set_enabled / bulk_set / apply_properties,
    /// per ComponentHandler.cs). component.copy is the exception — it mutates
    /// args["target_id"]. Try "id" first, fall back to "target_id".
    /// </summary>
    private static IReadOnlyList<Guid> ParseComponentOwner( JsonElement args )
    {
        var byId = ParseSingle( args, "id" );
        return byId.Count > 0 ? byId : ParseSingle( args, "target_id" );
    }

    /// <summary>
    /// gameobject.batch_transform passes its targets as args["ids"] — a
    /// comma-separated GUID string (GameObjectHandler.BatchTransform splits on
    /// ','). The design RFC assumed a transforms[].id array; the real wire
    /// shape is the comma string. Parse that.
    /// </summary>
    private static IReadOnlyList<Guid> ParseBatchTransformTargets( JsonElement args )
    {
        if ( args.ValueKind != JsonValueKind.Object ) return Empty;
        if ( !args.TryGetProperty( "ids", out var idsEl ) ) return Empty;
        if ( idsEl.ValueKind != JsonValueKind.String ) return Empty;
        var raw = idsEl.GetString();
        if ( string.IsNullOrWhiteSpace( raw ) ) return Empty;

        var seen = new HashSet<Guid>();
        foreach ( var part in raw.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
            if ( Guid.TryParse( part, out var g ) ) seen.Add( g );
        return seen.Count == 0 ? Empty : seen.ToArray();
    }

    /// <summary>
    /// transaction.preview passes operations as a stringified JSON array (per
    /// TransactionHandler.Preview which calls JsonDocument.Parse on
    /// args.operations). Walk each op's params and collect any Guid-shaped
    /// id / parent_id / gameobject_id / target_id values.
    ///
    /// Note: transaction.apply does NOT carry operations inline — it references
    /// a stored transaction_id (preview/apply split). Per-transaction keying of
    /// transaction.apply is therefore deferred; transaction.apply is registered
    /// as a scene-wide write in ToolRegistry. This strategy remains defined for
    /// transaction.preview-shaped callers and future activation.
    /// </summary>
    private static IReadOnlyList<Guid> ParseTransactionTargets( JsonElement args )
    {
        if ( args.ValueKind != JsonValueKind.Object ) return Empty;
        if ( !args.TryGetProperty( "operations", out var opsEl ) ) return Empty;

        JsonElement opsArray;
        JsonDocument inner = null;
        try
        {
            if ( opsEl.ValueKind == JsonValueKind.String )
            {
                inner = JsonDocument.Parse( opsEl.GetString() ?? "" );
                opsArray = inner.RootElement;
            }
            else
            {
                opsArray = opsEl;
            }
            if ( opsArray.ValueKind != JsonValueKind.Array ) return Empty;

            var seen = new HashSet<Guid>();
            foreach ( var op in opsArray.EnumerateArray() )
            {
                if ( !op.TryGetProperty( "params", out var p ) ) continue;
                if ( p.ValueKind != JsonValueKind.Object ) continue;
                foreach ( var field in new[] { "id", "parent_id", "gameobject_id", "target_id" } )
                {
                    if ( p.TryGetProperty( field, out var v )
                         && v.ValueKind == JsonValueKind.String
                         && Guid.TryParse( v.GetString(), out var g ) )
                        seen.Add( g );
                }
            }
            return seen.Count == 0 ? Empty : seen.ToArray();
        }
        catch ( JsonException ) { return Empty; }
        finally { inner?.Dispose(); }
    }
}
