// Editor/Handlers/BatchHandler.cs
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Arenula;

/// <summary>
/// batch tool: executes multiple tool operations sequentially in a single request.
/// Each operation specifies a tool name, action, and params object.
/// Results are collected into an array and returned together.
/// </summary>
internal static class BatchHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "execute" => Execute( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: execute" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── execute ─────────────────────────────────────────────────────────

    private static object Execute( JsonElement args )
    {
        var opsJson = HandlerBase.GetString( args, "operations" );
        if ( string.IsNullOrEmpty( opsJson ) )
            return HandlerBase.Error( "Missing required 'operations' parameter.", "execute" );

        JsonElement opsArray;
        try
        {
            using var doc = JsonDocument.Parse( opsJson );
            // Need to clone since doc is disposed after this block
            opsArray = doc.RootElement.Clone();
        }
        catch ( JsonException ex )
        {
            return HandlerBase.Error( $"Failed to parse 'operations' JSON: {ex.Message}", "execute" );
        }

        if ( opsArray.ValueKind != JsonValueKind.Array )
            return HandlerBase.Error( "'operations' must be a JSON array.", "execute" );

        var results = new List<object>();
        int successCount = 0;
        int errorCount = 0;

        foreach ( var op in opsArray.EnumerateArray() )
        {
            string tool = null;
            string opAction = null;
            JsonElement opParams = default;

            if ( op.TryGetProperty( "tool", out var toolEl ) )
                tool = toolEl.GetString();
            if ( op.TryGetProperty( "action", out var actionEl ) )
                opAction = actionEl.GetString();
            if ( op.TryGetProperty( "params", out var paramsEl ) )
                opParams = paramsEl;

            if ( string.IsNullOrEmpty( tool ) || string.IsNullOrEmpty( opAction ) )
            {
                var err = HandlerBase.Error( "Each operation must have 'tool' and 'action' fields.", "execute" );
                results.Add( new { tool, action = opAction, result = err } );
                errorCount++;
                continue;
            }

            // Build a merged args element: action + params flattened
            var merged = MergeActionAndParams( opAction, opParams );

            try
            {
                var opResult = DispatchToHandler( tool, opAction, merged );
                results.Add( new { tool, action = opAction, result = opResult } );

                // Check if the result is an error by inspecting for isError field
                var resultJson = JsonSerializer.Serialize( opResult );
                if ( resultJson.Contains( "\"isError\":true" ) )
                    errorCount++;
                else
                    successCount++;
            }
            catch ( Exception ex )
            {
                var err = HandlerBase.Error( ex.Message, opAction );
                results.Add( new { tool, action = opAction, result = err } );
                errorCount++;
            }
        }

        return HandlerBase.Success( new
        {
            total = results.Count,
            succeeded = successCount,
            failed = errorCount,
            results
        } );
    }

    /// <summary>
    /// Dispatches a single operation to the appropriate handler.
    /// Mirrors the routing logic in RpcDispatcher for main-thread tools.
    /// </summary>
    internal static object DispatchToHandler( string tool, string action, JsonElement args )
    {
        return tool switch
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
            "cross_index"  => CrossIndexHandler.Handle( action, args ),
            "autofix"      => AutofixHandler.Handle( action, args ),
            "transaction"  => TransactionHandler.Handle( action, args ),
            "compile"      => throw new InvalidOperationException( "Tool 'compile' is async and cannot be used in batch operations." ),
            "cloud"        => throw new InvalidOperationException( "Tool 'cloud' is async and cannot be used in batch operations." ),
            "workflow"     => throw new InvalidOperationException( "Tool 'workflow' is async and cannot be used in batch operations." ),
            "verify_change_set" => throw new InvalidOperationException( "Tool 'verify_change_set' is async and cannot be used in batch operations." ),
            "playtest"     => throw new InvalidOperationException( "Tool 'playtest' is async and cannot be used in batch operations." ),
            "batch"        => throw new InvalidOperationException( "Nested batch operations are not supported." ),
            _ => throw new InvalidOperationException( $"Tool '{tool}' not found" )
        };
    }

    /// <summary>
    /// Merges the action string and params object into a single flat JsonElement
    /// that handlers expect (action + other params at the same level).
    /// </summary>
    internal static JsonElement MergeActionAndParams( string action, JsonElement opParams )
    {
        var dict = new Dictionary<string, JsonElement>();

        // Add the action as a string element
        using var actionDoc = JsonDocument.Parse( $"\"{action}\"" );
        dict["action"] = actionDoc.RootElement.Clone();

        // Merge in params if present
        if ( opParams.ValueKind == JsonValueKind.Object )
        {
            foreach ( var prop in opParams.EnumerateObject() )
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        // Serialize and re-parse to get a proper JsonElement
        var json = JsonSerializer.Serialize( dict );
        using var doc = JsonDocument.Parse( json );
        return doc.RootElement.Clone();
    }
}
