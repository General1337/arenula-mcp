using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Editor;

namespace Arenula;

/// <summary>Metadata and diagnostics tools for MCP clients.</summary>
internal static class McpHandler
{
    internal static object Handle( string toolName, JsonElement args )
    {
        try
        {
            return toolName switch
            {
                "mcp.health"       => Health(),
                "mcp.capabilities" => Capabilities(),
                "mcp.server_log"   => ServerLog( args ),
                _                  => HandlerBase.Error( $"Unknown MCP metadata tool '{toolName}'." )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, toolName );
        }
    }

    private static object Health()
    {
        EditorHandler.EnsureInitialized();

        var session = SceneEditorSession.Active;
        var scene = session?.Scene;
        var logs = EditorHandler.LogSnapshot();
        var recentErrors = logs.Count( e => string.Equals( e.Level, "error", StringComparison.OrdinalIgnoreCase ) );

        int rootObjects = 0;
        int totalObjects = 0;
        if ( scene != null )
        {
            try
            {
                rootObjects = scene.Children.Count;
                totalObjects = SceneHelpers.WalkAll( scene, includeDisabled: true ).Count();
            }
            catch { }
        }

        return HandlerBase.Success( new
        {
            ok = ArenulaMcpServer.IsRunning,
            server = new
            {
                name = "Arenula Editor",
                version = ToolRegistry.Version,
                port = ArenulaMcpServer.Port,
                running = ArenulaMcpServer.IsRunning,
                session_count = ArenulaMcpServer.SessionCount,
                transport = "streamable-http"
            },
            editor = new
            {
                has_active_session = session != null,
                is_playing = session?.IsPlaying == true,
                scene_name = scene?.Name,
                root_objects = rootObjects,
                total_objects = totalObjects
            },
            log = new
            {
                retained = logs.Length,
                cap = EditorHandler.LogCap,
                recent_error_count = recentErrors
            }
        } );
    }

    private static object Capabilities()
    {
        var tools = ToolRegistry.All.Select( t => new
        {
            name = ToolRegistry.GetStringProperty( t, "name" ),
            actions = ToolRegistry.GetActionNames( t ),
            read_only = HasReadOnlyHint( t )
        } ).ToList();

        return HandlerBase.Success( new
        {
            server = new
            {
                name = "Arenula Editor",
                version = ToolRegistry.Version,
                transport = "streamable-http",
                endpoint = "/mcp"
            },
            tool_count = tools.Count,
            routing_hints = new[]
            {
                new { task = "code edits", route = "pipeline.compile_and_verify", reason = "compile, hotload, scene-integrity, and optional SBEP verification in one guarded lifecycle operation" },
                new { task = "mixed code/assets/scenes", route = "verify_change_set.run", reason = "derives the correct validation path from touched files" },
                new { task = "scene writes", route = "operation.preview/apply or transaction.preview/apply", reason = "preview first; apply under an exclusive editor write operation" },
                new { task = "runtime assertions", route = "playtest", reason = "boot/start/stop and assertion flow is lifecycle-locked" },
                new { task = "API or schema uncertainty", route = "reflection and schema", reason = "discover exact types, properties, enum values, and accepted value shapes before writing" }
            },
            coordination = McpOperationCoordinator.Snapshot(),
            tools
        } );
    }

    private static object ServerLog( JsonElement args )
    {
        EditorHandler.EnsureInitialized();

        var count = HandlerBase.GetInt( args, "count", 200 );
        var filter = HandlerBase.GetString( args, "filter" );
        var level = HandlerBase.GetString( args, "level" );
        var source = HandlerBase.GetString( args, "source" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 100 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 500 ) limit = 500;

        var entries = EditorHandler.LogSnapshot();
        var bufferSize = entries.Length;
        if ( count > 0 && count < entries.Length )
            entries = entries[^count..];

        if ( !string.IsNullOrEmpty( level ) )
        {
            var wanted = level.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( s => s.ToLowerInvariant() )
                .ToHashSet();
            entries = entries.Where( e => wanted.Contains( (e.Level ?? "").ToLowerInvariant() ) ).ToArray();
        }

        if ( !string.IsNullOrEmpty( source ) )
        {
            var wanted = source.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( s => s.ToLowerInvariant() )
                .ToHashSet();
            entries = entries.Where( e => wanted.Contains( (e.Source ?? "").ToLowerInvariant() ) ).ToArray();
        }

        if ( !string.IsNullOrEmpty( filter ) )
        {
            entries = entries.Where( e =>
                (e.Message ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0 ).ToArray();
        }

        var total = entries.Length;
        var page = entries.Skip( offset ).Take( limit ).Select( e => new
        {
            time = e.Time.ToString( "yyyy-MM-ddTHH:mm:ss.fffK" ),
            level = e.Level,
            logger = e.Logger,
            message = e.Message,
            stack = string.IsNullOrEmpty( e.Stack ) ? null : e.Stack,
            source = e.Source,
            repeats = e.Repeats,
            operation_id = e.OperationId
        } ).ToList();

        return HandlerBase.Success( new
        {
            items = page,
            count = page.Count,
            total,
            offset,
            limit,
            has_more = offset + limit < total,
            buffer_size = bufferSize,
            buffer_cap = EditorHandler.LogCap
        } );
    }

    private static bool HasReadOnlyHint( object tool )
    {
        var annotations = tool?.GetType().GetProperty( "annotations" )?.GetValue( tool );
        var hint = annotations?.GetType().GetProperty( "readOnlyHint" )?.GetValue( annotations );
        return hint is bool b && b;
    }
}
