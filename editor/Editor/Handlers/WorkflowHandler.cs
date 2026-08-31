using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Arenula;

internal static class WorkflowHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "list" => List(),
                "create_prefab_pair" => CreatePrefabPair( args ),
                "fix_compile_errors" => await FixCompileErrors(),
                "verify_networked_component" => await VerifyNetworkedComponent( args ),
                "implement_spawnable_entity" => ImplementSpawnableEntity( args ),
                "add_tool_mode" => AddToolMode( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: list, create_prefab_pair, fix_compile_errors, verify_networked_component, implement_spawnable_entity, add_tool_mode" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object List() => HandlerBase.Success( new
    {
        workflows = new[]
        {
            new { name = "create_prefab_pair", purpose = "Create or repair prefab + .sent pairing." },
            new { name = "fix_compile_errors", purpose = "Summarize compiler failures with likely fixes from project memory." },
            new { name = "verify_networked_component", purpose = "Compile, inspect [Sync]/RPC metadata, and return verification guidance." },
            new { name = "implement_spawnable_entity", purpose = "Return the canonical file/layout checklist for a new spawnable entity." },
            new { name = "add_tool_mode", purpose = "Return the canonical file/layout checklist for a new tool mode." }
        }
    } );

    private static object CreatePrefabPair( JsonElement args )
    {
        var prefabPath = HandlerBase.GetString( args, "prefab_path" );
        var title = HandlerBase.GetString( args, "title" );
        var description = HandlerBase.GetString( args, "description" );
        return AutofixHandler.Handle( "create_sent_pair", BuildArgs( new { action = "create_sent_pair", prefab_path = prefabPath, title, description } ) );
    }

    private static async Task<object> FixCompileErrors()
    {
        var diagnostics = await CompileHandler.HandleAsync( "errors", BuildArgs( new { action = "errors", severity = "error" } ) );
        return HandlerBase.Success( new { compile_errors = diagnostics, likely_fixes = LoadFailureMemory() } );
    }

    private static async Task<object> VerifyNetworkedComponent( JsonElement args )
    {
        var typeName = HandlerBase.GetString( args, "type_name" );
        if ( string.IsNullOrWhiteSpace( typeName ) )
            return HandlerBase.Error( "Missing required 'type_name' parameter.", "verify_networked_component" );

        // reflection migrated to the [McpTool] scan (mcp-dynamic-discovery Phase 3
        // Wave 3.2) — call the [McpAction("get_type")] handler directly.
        var reflection = ReflectionHandler.HandleGetType( BuildArgs( new { action = "get_type", name = typeName, section = "properties", include_inherited = true } ) );
        var compile = await PipelineHandler.HandleAsync( "compile_and_verify", BuildArgs( new { action = "compile_and_verify", skip_verify = true } ) );
        return HandlerBase.Success( new
        {
            type_name = typeName,
            compile,
            reflection,
            guidance = new[]
            {
                "Check that [Sync] is only applied to read/write properties.",
                "Use NetworkSpawn before enabling networked GameObjects.",
                "Use runtime playtest assertions when ownership or replication behavior matters."
            }
        } );
    }

    private static object ImplementSpawnableEntity( JsonElement args )
    {
        var prefabPath = HandlerBase.GetString( args, "prefab_path" );
        return HandlerBase.Success( new
        {
            prefab_path = prefabPath,
            checklist = new[]
            {
                "Create the component class under Code/ for the new entity.",
                "Create the prefab under Assets/ with Rigidbody, Collider, and the entity component.",
                "Create the sibling .sent file so the entity appears in the spawn menu.",
                "Run workflow.create_prefab_pair or autofix.create_sent_pair if the sibling file is missing.",
                "Run verify_change_set.run on the touched files after the entity is wired."
            }
        } );
    }

    private static object AddToolMode( JsonElement args )
    {
        var name = HandlerBase.GetString( args, "name" ) ?? "NewToolMode";
        return HandlerBase.Success( new
        {
            name,
            checklist = new[]
            {
                "Add the ToolMode class under Code/Weapons or the matching tools subsystem.",
                "Decorate it with the expected Group metadata so it appears in tool listings.",
                "Index supporting docs/examples through cross_index.build after adding the files.",
                "Run verify_change_set.run on the modified tool files."
            }
        } );
    }

    private static object LoadFailureMemory()
    {
        var path = WorkspacePaths.Combine( "docs", "ai", "failure-memory.json" );
        if ( string.IsNullOrEmpty( path ) || !File.Exists( path ) )
            return Array.Empty<object>();

        try { return JsonSerializer.Deserialize<object>( File.ReadAllText( path ) ); }
        catch { return Array.Empty<object>(); }
    }

    private static JsonElement BuildArgs( object obj )
    {
        using var doc = JsonDocument.Parse( JsonSerializer.Serialize( obj ) );
        return doc.RootElement.Clone();
    }
}
