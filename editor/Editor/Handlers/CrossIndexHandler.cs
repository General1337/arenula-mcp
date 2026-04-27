using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Arenula;

internal static class CrossIndexHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "build" => Build( args ),
                "stats" => Stats(),
                "query" => Query( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: build, stats, query" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object Build( JsonElement args )
    {
        var includeScene = HandlerBase.GetBool( args, "include_scene", true );
        return HandlerBase.Success( ProjectIndexBuilder.BuildAndPersist( includeScene ) );
    }

    private static object Stats()
    {
        var artifact = LoadOrBuild();
        return HandlerBase.Success( new
        {
            artifact_path = ProjectIndexBuilder.DefaultArtifactPath,
            artifact.generated_at,
            artifact.repo_root,
            artifact.summary,
            artifact.counts
        } );
    }

    private static object Query( JsonElement args )
    {
        var query = HandlerBase.GetString( args, "query" );
        if ( string.IsNullOrWhiteSpace( query ) )
            return HandlerBase.Error( "Missing required 'query' parameter.", "query" );

        var limit = HandlerBase.GetInt( args, "limit", 25 );
        var type = HandlerBase.GetString( args, "type" );
        var artifact = LoadOrBuild();
        var rows = Flatten( artifact, type )
            .Where( row => row.text.IndexOf( query, StringComparison.OrdinalIgnoreCase ) >= 0 )
            .Take( Math.Max( 1, limit ) )
            .Select( row => row.value )
            .ToList();

        return HandlerBase.Success( new { query, type = type ?? "all", count = rows.Count, results = rows } );
    }

    private static ProjectIndexBuilder.IndexResult LoadOrBuild()
    {
        var artifactPath = ProjectIndexBuilder.DefaultArtifactPath;
        if ( !string.IsNullOrEmpty( artifactPath ) && File.Exists( artifactPath ) )
        {
            var json = File.ReadAllText( artifactPath );
            var artifact = JsonSerializer.Deserialize<ProjectIndexBuilder.IndexResult>( json );
            if ( artifact != null ) return artifact;
        }

        return ProjectIndexBuilder.BuildIndex();
    }

    private static IEnumerable<(string text, object value)> Flatten( ProjectIndexBuilder.IndexResult artifact, string type )
    {
        bool Want( string key ) => string.IsNullOrEmpty( type ) || string.Equals( type, key, StringComparison.OrdinalIgnoreCase );

        if ( Want( "code" ) ) foreach ( var row in artifact.code_symbols ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "asset" ) ) foreach ( var row in artifact.assets ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "sent" ) ) foreach ( var row in artifact.sent_files ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "console" ) ) foreach ( var row in artifact.console_commands ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "doc" ) ) foreach ( var row in artifact.docs ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "example" ) ) foreach ( var row in artifact.examples ) yield return (JsonSerializer.Serialize( row ), row);
        if ( Want( "scene" ) ) foreach ( var row in artifact.scene_objects ) yield return (JsonSerializer.Serialize( row ), row);
    }
}
