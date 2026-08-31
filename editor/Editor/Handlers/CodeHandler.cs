using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;

namespace Arenula;

/// <summary>
/// code tool: read_file, write_file, list_files, search_code.
/// Async handler — file I/O dispatched on background thread.
/// All paths are validated to stay within the project root.
/// </summary>
internal static class CodeHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "read_file"   => await ReadFile( args ),
                "write_file"  => await WriteFile( args ),
                "list_files"  => await ListFiles( args ),
                "search_code" => await SearchCode( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'code'.", action,
                    "Valid actions: read_file, write_file, list_files, search_code" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── Path safety ──────────────────────────────────────────────────

    /// <summary>
    /// Resolve the project root directory from the editor filesystem.
    /// </summary>
    private static string GetProjectRoot()
    {
        var libPath = Editor.FileSystem.Libraries.GetFullPath( "" );
        var projectDir = Path.GetDirectoryName( libPath );
        return projectDir;
    }

    /// <summary>
    /// Validate and resolve a relative path to an absolute path within the project root.
    /// Returns null if the path escapes the project directory or targets restricted areas.
    /// </summary>
    private static string ResolveSafePath( string projectRoot, string relativePath, out string error )
    {
        error = null;

        if ( string.IsNullOrEmpty( relativePath ) )
        {
            error = "Path cannot be empty.";
            return null;
        }

        // Normalize separators
        relativePath = relativePath.Replace( '\\', '/' );

        // Block absolute paths — only relative paths allowed
        if ( Path.IsPathRooted( relativePath ) )
        {
            error = "Only relative paths within the project are allowed.";
            return null;
        }

        var fullPath = Path.GetFullPath( Path.Combine( projectRoot, relativePath ) );
        var normalizedRoot = Path.GetFullPath( projectRoot );

        // Ensure the resolved path is within the project root
        if ( !fullPath.StartsWith( normalizedRoot, StringComparison.OrdinalIgnoreCase ) )
        {
            error = "Path escapes the project directory.";
            return null;
        }

        // Block restricted file types and directories
        var relFromRoot = fullPath.Substring( normalizedRoot.Length ).TrimStart( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
        if ( relFromRoot.StartsWith( "Libraries", StringComparison.OrdinalIgnoreCase ) && !relFromRoot.StartsWith( "Libraries" + Path.DirectorySeparatorChar + "arenula_mcp", StringComparison.OrdinalIgnoreCase ) )
        {
            error = "Cannot access Libraries/ directory (engine binaries). Only Libraries/arenula_mcp/ is allowed.";
            return null;
        }

        return fullPath;
    }

    // ── read_file ────────────────────────────────────────────────────

    private static async Task<object> ReadFile( JsonElement args )
    {
        var projectRoot = GetProjectRoot();
        if ( projectRoot == null )
            return HandlerBase.Error( "Could not locate project root.", "read_file" );

        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "read_file" );

        var fullPath = ResolveSafePath( projectRoot, path, out var error );
        if ( fullPath == null )
            return HandlerBase.Error( error, "read_file" );

        if ( !File.Exists( fullPath ) )
            return HandlerBase.Error( $"File not found: {path}", "read_file" );

        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 200 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 1000 ) limit = 1000;

        var allLines = await Task.Run( () => File.ReadAllLines( fullPath ) );
        var totalLines = allLines.Length;

        if ( offset < 0 ) offset = 0;
        if ( offset >= totalLines )
            return HandlerBase.Success( new { path, totalLines, lines = Array.Empty<object>(), message = $"Offset {offset} is beyond end of file ({totalLines} lines)." } );

        var end = Math.Min( offset + limit, totalLines );
        var lines = new List<object>();
        for ( int i = offset; i < end; i++ )
        {
            lines.Add( new { line = i + 1, text = allLines[i] } );
        }

        return HandlerBase.Success( new
        {
            path,
            totalLines,
            offset,
            returned = lines.Count,
            hasMore = end < totalLines,
            lines
        } );
    }

    // ── write_file ───────────────────────────────────────────────────

    private static async Task<object> WriteFile( JsonElement args )
    {
        var projectRoot = GetProjectRoot();
        if ( projectRoot == null )
            return HandlerBase.Error( "Could not locate project root.", "write_file" );

        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "write_file" );

        var content = HandlerBase.GetString( args, "content" );
        if ( content == null )
            return HandlerBase.Error( "Missing required 'content' parameter.", "write_file" );

        var fullPath = ResolveSafePath( projectRoot, path, out var error );
        if ( fullPath == null )
            return HandlerBase.Error( error, "write_file" );

        var createDirs = HandlerBase.GetBool( args, "create_directories", true );
        var existed = File.Exists( fullPath );

        await Task.Run( () =>
        {
            var dir = Path.GetDirectoryName( fullPath );
            if ( createDirs && !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
                Directory.CreateDirectory( dir );

            File.WriteAllText( fullPath, content );
        } );

        var lineCount = content.Split( '\n' ).Length;

        return HandlerBase.Success( new
        {
            path,
            bytes = content.Length,
            lines = lineCount,
            created = !existed,
            message = $"{(existed ? "Overwrote" : "Created")} {path} ({content.Length} bytes, {lineCount} lines)."
        } );
    }

    // ── list_files ───────────────────────────────────────────────────

    private static async Task<object> ListFiles( JsonElement args )
    {
        var projectRoot = GetProjectRoot();
        if ( projectRoot == null )
            return HandlerBase.Error( "Could not locate project root.", "list_files" );

        var directory = HandlerBase.GetString( args, "directory" ) ?? "Code/";
        var pattern = HandlerBase.GetString( args, "pattern" ) ?? "*.cs";
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 100 );
        if ( limit < 1 ) limit = 1;
        if ( limit > 500 ) limit = 500;

        var fullDir = ResolveSafePath( projectRoot, directory, out var error );
        if ( fullDir == null )
            return HandlerBase.Error( error, "list_files" );

        if ( !Directory.Exists( fullDir ) )
            return HandlerBase.Error( $"Directory not found: {directory}", "list_files" );

        var normalizedRoot = Path.GetFullPath( projectRoot );

        var files = await Task.Run( () =>
            Directory.EnumerateFiles( fullDir, pattern, SearchOption.AllDirectories )
                .OrderBy( f => f )
                .Skip( offset )
                .Take( limit + 1 ) // take one extra to check hasMore
                .ToList()
        );

        var hasMore = files.Count > limit;
        if ( hasMore ) files.RemoveAt( files.Count - 1 );

        var results = files.Select( f =>
        {
            var info = new FileInfo( f );
            var relative = f.Substring( normalizedRoot.Length ).TrimStart( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ).Replace( '\\', '/' );
            return new
            {
                path = relative,
                size = info.Length,
                modified = info.LastWriteTimeUtc.ToString( "yyyy-MM-dd HH:mm:ss" )
            };
        } ).ToList();

        return HandlerBase.Success( new
        {
            directory,
            pattern,
            total = results.Count,
            offset,
            hasMore,
            files = results
        } );
    }

    // ── search_code ──────────────────────────────────────────────────

    private static async Task<object> SearchCode( JsonElement args )
    {
        var projectRoot = GetProjectRoot();
        if ( projectRoot == null )
            return HandlerBase.Error( "Could not locate project root.", "search_code" );

        var query = HandlerBase.GetString( args, "query" );
        if ( string.IsNullOrEmpty( query ) )
            return HandlerBase.Error( "Missing required 'query' parameter.", "search_code" );

        var directory = HandlerBase.GetString( args, "directory" ) ?? "Code/";
        var caseSensitive = HandlerBase.GetBool( args, "case_sensitive", false );
        var maxResults = HandlerBase.GetInt( args, "max_results", 50 );
        if ( maxResults < 1 ) maxResults = 1;
        if ( maxResults > 200 ) maxResults = 200;

        var contextLines = HandlerBase.GetInt( args, "context_lines", 2 );
        if ( contextLines < 0 ) contextLines = 0;
        if ( contextLines > 5 ) contextLines = 5;

        var fullDir = ResolveSafePath( projectRoot, directory, out var error );
        if ( fullDir == null )
            return HandlerBase.Error( error, "search_code" );

        if ( !Directory.Exists( fullDir ) )
            return HandlerBase.Error( $"Directory not found: {directory}", "search_code" );

        var normalizedRoot = Path.GetFullPath( projectRoot );
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var matches = await Task.Run( () =>
        {
            var results = new List<object>();
            var csFiles = Directory.EnumerateFiles( fullDir, "*.cs", SearchOption.AllDirectories );

            foreach ( var file in csFiles )
            {
                if ( results.Count >= maxResults ) break;

                string[] lines;
                try { lines = File.ReadAllLines( file ); }
                catch { continue; }

                var relative = file.Substring( normalizedRoot.Length ).TrimStart( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ).Replace( '\\', '/' );

                for ( int i = 0; i < lines.Length; i++ )
                {
                    if ( results.Count >= maxResults ) break;

                    if ( lines[i].IndexOf( query, comparison ) < 0 ) continue;

                    // Gather context lines
                    var contextBefore = new List<string>();
                    var contextAfter = new List<string>();

                    for ( int j = Math.Max( 0, i - contextLines ); j < i; j++ )
                        contextBefore.Add( $"{j + 1}: {lines[j]}" );

                    for ( int j = i + 1; j <= Math.Min( lines.Length - 1, i + contextLines ); j++ )
                        contextAfter.Add( $"{j + 1}: {lines[j]}" );

                    results.Add( new
                    {
                        file = relative,
                        line = i + 1,
                        text = lines[i].Trim(),
                        before = contextBefore,
                        after = contextAfter
                    } );
                }
            }

            return results;
        } );

        return HandlerBase.Success( new
        {
            query,
            directory,
            caseSensitive,
            matchCount = matches.Count,
            capped = matches.Count >= maxResults,
            matches
        } );
    }
}
