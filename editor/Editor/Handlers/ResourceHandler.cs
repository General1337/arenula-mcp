using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Handles MCP resources/read requests.
/// Returns project context data and conventions to bootstrap AI conversations.
/// </summary>
internal static class ResourceHandler
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static object Read( string uri )
    {
        return uri switch
        {
            "arenula://project/context"     => ReadProjectContext(),
            "arenula://project/conventions"  => ReadProjectConventions(),
            _ => new { error = new { code = -32602, message = $"Unknown resource URI: {uri}" } }
        };
    }

    private static object ReadProjectContext()
    {
        var projectRoot = HandlerBase.GetProjectRoot();

        // 1. Project name from .sbproj
        string projectName = "unknown";
        string projectIdent = null;
        if ( projectRoot != null )
        {
            var sbprojFiles = Directory.GetFiles( projectRoot, "*.sbproj" );
            if ( sbprojFiles.Length > 0 )
            {
                try
                {
                    var content = File.ReadAllText( sbprojFiles[0] );
                    using var doc = JsonDocument.Parse( content );
                    if ( doc.RootElement.TryGetProperty( "Title", out var title ) )
                        projectName = title.GetString();
                    if ( doc.RootElement.TryGetProperty( "Org", out var org ) && doc.RootElement.TryGetProperty( "Ident", out var ident ) )
                        projectIdent = $"{org.GetString()}.{ident.GetString()}";
                }
                catch { }
            }
        }

        // 2. Custom components (non-Sandbox namespace)
        var customComponents = new List<object>();
        try
        {
            foreach ( var td in TypeLibrary.GetTypes<Component>() )
            {
                if ( td.TargetType == null || td.TargetType.IsAbstract ) continue;
                var ns = td.TargetType.Namespace ?? "";
                if ( ns.StartsWith( "Sandbox", StringComparison.OrdinalIgnoreCase ) ) continue;
                if ( ns.StartsWith( "Arenula", StringComparison.OrdinalIgnoreCase ) ) continue;

                var propCount = td.TargetType.GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly ).Length;

                customComponents.Add( new
                {
                    name = td.Name,
                    @namespace = ns,
                    propertyCount = propCount
                } );
            }
        }
        catch { }

        // 3. Available prefabs
        var prefabs = new List<object>();
        try
        {
            foreach ( var asset in AssetSystem.All )
            {
                if ( asset.AssetType?.FileExtension != "prefab" ) continue;
                prefabs.Add( new { path = asset.Path, name = asset.Name } );
            }
        }
        catch { }

        // 4. Compilation state
        object compileState = null;
        try
        {
            if ( CompileHandler._lastOutputs != null )
            {
                var anyFailed = CompileHandler._lastOutputs.Any( o => o.Compiler != null && !o.Compiler.BuildSuccess );
                var errorCount = CompileHandler._lastOutputs
                    .Where( o => o.Compiler?.Diagnostics != null )
                    .Sum( o => o.Compiler.Diagnostics.Count( d => d.Severity.ToString() == "Error" ) );

                compileState = new { success = !anyFailed, errorCount };
            }
        }
        catch { }

        // 5. Active scene summary
        object sceneSummary = null;
        try
        {
            var scene = SceneHelpers.ResolveScene();
            if ( scene != null )
            {
                var rootNames = new List<string>();
                foreach ( var child in scene.Children )
                    rootNames.Add( child.Name );

                sceneSummary = new
                {
                    name = scene.Name,
                    rootObjectCount = rootNames.Count,
                    rootObjects = rootNames.Take( 20 ).ToList()
                };
            }
        }
        catch { }

        var contextData = JsonSerializer.Serialize( new
        {
            projectName,
            projectIdent,
            customComponentCount = customComponents.Count,
            customComponents = customComponents.Take( 50 ).ToList(),
            prefabCount = prefabs.Count,
            prefabs = prefabs.Take( 30 ).ToList(),
            compileState,
            activeScene = sceneSummary
        }, Json );

        return new
        {
            contents = new object[]
            {
                new
                {
                    uri = "arenula://project/context",
                    mimeType = "application/json",
                    text = contextData
                }
            }
        };
    }

    private static object ReadProjectConventions()
    {
        var projectRoot = HandlerBase.GetProjectRoot();
        var content = "(No CLAUDE.md found in project root)";

        if ( projectRoot != null )
        {
            var claudeMdPath = Path.Combine( projectRoot, "CLAUDE.md" );
            if ( File.Exists( claudeMdPath ) )
            {
                try
                {
                    content = File.ReadAllText( claudeMdPath );
                    if ( content.Length > 12000 )
                        content = content.Substring( 0, 12000 ) + "\n... [truncated at 12000 chars]";
                }
                catch ( Exception ex )
                {
                    content = $"(Error reading CLAUDE.md: {ex.Message})";
                }
            }
        }

        return new
        {
            contents = new object[]
            {
                new
                {
                    uri = "arenula://project/conventions",
                    mimeType = "text/markdown",
                    text = content
                }
            }
        };
    }

}
