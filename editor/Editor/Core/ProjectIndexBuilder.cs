using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sandbox;

namespace Arenula;

internal static class ProjectIndexBuilder
{
    private static readonly Regex TypeRegex = new( @"\b(class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled );
    private static readonly Regex ConCmdRegex = new( @"\[(ConCmd|ConVar)\s*\(\s*""([^""]+)""", RegexOptions.Compiled );
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static string DefaultArtifactPath => WorkspacePaths.Combine( "docs", "generated", "engine-cross-index.json" );

    internal static object BuildAndPersist( bool includeScene = true )
    {
        var index = BuildIndex( includeScene );
        var artifactPath = DefaultArtifactPath;
        if ( !string.IsNullOrEmpty( artifactPath ) )
        {
            var dir = Path.GetDirectoryName( artifactPath );
            if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
                Directory.CreateDirectory( dir );
            File.WriteAllText( artifactPath, JsonSerializer.Serialize( index, new JsonSerializerOptions { WriteIndented = true } ) );
        }

        return new
        {
            artifact_path = artifactPath,
            summary = index.summary,
            counts = index.counts
        };
    }

    internal static IndexResult BuildIndex( bool includeScene = true )
    {
        var root = WorkspacePaths.ProjectRoot;
        var codeSymbols = new List<object>();
        var assets = new List<object>();
        var sents = new List<object>();
        var consoleCommands = new List<object>();
        var docs = new List<object>();
        var examples = new List<object>();

        if ( string.IsNullOrEmpty( root ) )
        {
            return new IndexResult
            {
                generated_at = DateTime.UtcNow,
                repo_root = null,
                summary = "Project root could not be resolved from the running editor.",
                counts = new { code_symbols = 0, assets = 0, sent_files = 0, console_commands = 0, docs = 0, examples = 0, scene_objects = 0 },
                code_symbols = codeSymbols,
                assets = assets,
                sent_files = sents,
                console_commands = consoleCommands,
                docs = docs,
                examples = examples,
                scene_objects = Array.Empty<object>()
            };
        }

        var codeDir = Path.Combine( root, "Code" );
        if ( Directory.Exists( codeDir ) )
        {
            foreach ( var file in Directory.EnumerateFiles( codeDir, "*.cs", SearchOption.AllDirectories ) )
            {
                var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
                var text = SafeReadAllText( file );
                foreach ( Match match in TypeRegex.Matches( text ) )
                    codeSymbols.Add( new { name = match.Groups[2].Value, kind = match.Groups[1].Value, path = rel } );

                foreach ( Match match in ConCmdRegex.Matches( text ) )
                    consoleCommands.Add( new { name = match.Groups[2].Value, kind = match.Groups[1].Value.ToLowerInvariant(), path = rel } );
            }
        }

        var assetsDir = Path.Combine( root, "Assets" );
        if ( Directory.Exists( assetsDir ) )
        {
            foreach ( var file in Directory.EnumerateFiles( assetsDir, "*.*", SearchOption.AllDirectories ) )
            {
                var ext = Path.GetExtension( file ).ToLowerInvariant();
                if ( ext is not (".prefab" or ".scene" or ".sent" or ".vmdl" or ".vmat" or ".sound") )
                    continue;

                var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
                if ( ext == ".sent" )
                {
                    sents.Add( new { path = rel, sibling_prefab = Path.ChangeExtension( rel, ".prefab" ).Replace( '\\', '/' ) } );
                    continue;
                }

                assets.Add( new { path = rel, type = ext.TrimStart( '.' ) } );
            }
        }

        var docsDir = Path.Combine( root, "docs" );
        if ( Directory.Exists( docsDir ) )
        {
            var upstreamManifest = LoadUpstreamManifest( root );
            foreach ( var file in Directory.EnumerateFiles( docsDir, "*.md", SearchOption.AllDirectories ) )
            {
                var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
                var sourceKind = GetDocSourceKind( rel );
                upstreamManifest.TryGetValue( rel, out var upstreamEntry );
                docs.Add( new
                {
                    path = rel,
                    title = ExtractMarkdownTitle( file ),
                    source_kind = sourceKind,
                    source_url = upstreamEntry?.source_url,
                    topic_tags = upstreamEntry?.topic_tags ?? Array.Empty<string>(),
                    aliases = upstreamEntry?.aliases ?? GetDocAliases( rel ),
                    staleness_status = upstreamEntry?.staleness_status
                } );
            }
        }

        var examplesDir = Path.Combine( root, "docs", "agent-examples" );
        if ( Directory.Exists( examplesDir ) )
        {
            foreach ( var file in Directory.EnumerateFiles( examplesDir, "*.md", SearchOption.AllDirectories ) )
            {
                var rel = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
                var title = SafeReadLines( file ).FirstOrDefault( l => l.StartsWith( "#" ) )?.TrimStart( '#', ' ' ) ?? Path.GetFileNameWithoutExtension( file );
                examples.Add( new { path = rel, title } );
            }
        }

        var sceneObjects = includeScene
            ? SceneHelpers.ResolveScene() is Scene scene
                ? SceneHelpers.WalkAll( scene, true ).Take( 500 ).Select( go => (object)new
                {
                    id = go.Id.ToString(),
                    name = go.Name,
                    path = SceneHelpers.GetObjectPath( go ),
                    handle = SceneHelpers.BuildHandle( go ),
                    components = SceneHelpers.GetComponentNames( go )
                } ).ToList()
                : new List<object>()
            : new List<object>();

        return new IndexResult
        {
            generated_at = DateTime.UtcNow,
            repo_root = root.Replace( '\\', '/' ),
            summary = $"Indexed {codeSymbols.Count} code symbols, {assets.Count} assets, {sents.Count} sent files, {consoleCommands.Count} console commands, {docs.Count} docs, {examples.Count} examples, and {sceneObjects.Count} scene objects.",
            counts = new { code_symbols = codeSymbols.Count, assets = assets.Count, sent_files = sents.Count, console_commands = consoleCommands.Count, docs = docs.Count, examples = examples.Count, scene_objects = sceneObjects.Count },
            code_symbols = codeSymbols,
            assets = assets,
            sent_files = sents,
            console_commands = consoleCommands,
            docs = docs,
            examples = examples,
            scene_objects = sceneObjects
        };
    }

    internal sealed class IndexResult
    {
        public DateTime generated_at { get; set; }
        public string repo_root { get; set; }
        public string summary { get; set; }
        public object counts { get; set; }
        public IReadOnlyList<object> code_symbols { get; set; }
        public IReadOnlyList<object> assets { get; set; }
        public IReadOnlyList<object> sent_files { get; set; }
        public IReadOnlyList<object> console_commands { get; set; }
        public IReadOnlyList<object> docs { get; set; }
        public IReadOnlyList<object> examples { get; set; }
        public IReadOnlyList<object> scene_objects { get; set; }
    }

    private static string SafeReadAllText( string path )
    {
        try { return File.ReadAllText( path ); }
        catch { return string.Empty; }
    }

    private static IEnumerable<string> SafeReadLines( string path )
    {
        try { return File.ReadLines( path ); }
        catch { return Array.Empty<string>(); }
    }

    private static string ExtractMarkdownTitle( string path )
    {
        var lines = SafeReadLines( path ).ToList();
        foreach ( var line in lines )
        {
            if ( string.IsNullOrWhiteSpace( line ) || line == "---" )
                continue;

            if ( line.StartsWith( "title:", StringComparison.OrdinalIgnoreCase ) )
                return line[(line.IndexOf( ':' ) + 1)..].Trim().Trim( '"' );

            if ( line.StartsWith( "#" ) )
                return line.TrimStart( '#', ' ' );
        }

        return Path.GetFileNameWithoutExtension( path );
    }

    private static string GetDocSourceKind( string relativePath )
    {
        if ( relativePath.StartsWith( "docs/upstream/sbox-docs/", StringComparison.OrdinalIgnoreCase ) )
            return "upstream_snapshot";

        if ( relativePath.StartsWith( "docs/generated/", StringComparison.OrdinalIgnoreCase ) )
            return "generated_summary";

        return "project_doc";
    }

    private static string[] GetDocAliases( string relativePath )
    {
        var aliases = relativePath.Replace( '-', ' ' )
            .Replace( '_', ' ' )
            .Split( '/', StringSplitOptions.RemoveEmptyEntries)
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();

        if ( relativePath.EndsWith( "docs/sbox-reference/02-networking.md", StringComparison.OrdinalIgnoreCase ) )
            aliases.AddRange( ["network.refresh", "networkspawn", "[sync]", "rpc"] );

        if ( relativePath.EndsWith( "docs/sbox-reference/03-scene-system.md", StringComparison.OrdinalIgnoreCase ) )
            aliases.AddRange( ["gameobjectsystem", "prefab cloning"] );

        if ( relativePath.EndsWith( "docs/sbox-reference/upstream-sandbox-lessons.md", StringComparison.OrdinalIgnoreCase ) )
            aliases.AddRange( ["toolmode", "undo", "partial classes", "ispawner"] );

        return aliases.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
    }

    private static Dictionary<string, UpstreamManifestEntry> LoadUpstreamManifest( string root )
    {
        var manifestPath = Path.Combine( root, "docs", "upstream", "manifest.json" );
        if ( !File.Exists( manifestPath ) )
            return new Dictionary<string, UpstreamManifestEntry>( StringComparer.OrdinalIgnoreCase );

        try
        {
            var manifest = JsonSerializer.Deserialize<UpstreamManifest>( File.ReadAllText( manifestPath ), JsonOptions );
            return manifest?.pages?
                .Where( x => !string.IsNullOrWhiteSpace( x.local_path ) )
                .ToDictionary( x => x.local_path, x => x, StringComparer.OrdinalIgnoreCase )
                ?? new Dictionary<string, UpstreamManifestEntry>( StringComparer.OrdinalIgnoreCase );
        }
        catch
        {
            return new Dictionary<string, UpstreamManifestEntry>( StringComparer.OrdinalIgnoreCase );
        }
    }

    private sealed class UpstreamManifest
    {
        public List<UpstreamManifestEntry> pages { get; set; }
    }

    private sealed class UpstreamManifestEntry
    {
        public string local_path { get; set; }
        public string source_url { get; set; }
        public string[] topic_tags { get; set; }
        public string[] aliases { get; set; }
        public string staleness_status { get; set; }
    }
}
