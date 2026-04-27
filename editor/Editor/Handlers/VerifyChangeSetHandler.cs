using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Arenula;

internal static class VerifyChangeSetHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "plan" => Plan( args ),
                "run" => await Run( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: plan, run" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object Plan( JsonElement args )
    {
        var paths = ParsePaths( HandlerBase.GetString( args, "paths" ) );
        var profiles = DetectProfiles( paths );
        return HandlerBase.Success( new { paths, profiles = profiles.Select( p => new { name = p, checks = SuggestedChecks( p ) } ).ToList() } );
    }

    private static async Task<object> Run( JsonElement args )
    {
        var paths = ParsePaths( HandlerBase.GetString( args, "paths" ) );
        var profiles = DetectProfiles( paths );
        var steps = new List<object>();

        var compileNeeded = profiles.Any( p => p is "sbep" or "gameplay" or "ui" or "networking" or "tools" );
        if ( compileNeeded )
        {
            var compile = await PipelineHandler.HandleAsync( "compile_and_verify", BuildArgs( new { action = "compile_and_verify", skip_verify = !profiles.Contains( "sbep" ) } ) );
            steps.Add( new { check = "compile_and_verify", result = compile } );
        }

        if ( profiles.Contains( "sbep" ) )
        {
            var verify = await SbepVerifyHandler.HandleAsync( "full_verify", BuildArgs( new { action = "full_verify" } ) );
            steps.Add( new { check = "sbep_verify.full_verify", result = verify } );
        }

        if ( profiles.Contains( "assets" ) )
        {
            var assetChecks = paths.Where( p => p.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
                .Select( path => AutofixHandler.Handle( "prefab_fixups", BuildArgs( new { action = "prefab_fixups", path } ) ) )
                .ToList();
            steps.Add( new { check = "asset_prefab_fixups", results = assetChecks } );
        }

        return HandlerBase.Success( new { paths, profiles, steps } );
    }

    private static List<string> ParsePaths( string csv ) => (csv ?? string.Empty)
        .Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
        .Select( p => p.Replace( '\\', '/' ) )
        .ToList();

    private static List<string> DetectProfiles( IReadOnlyList<string> paths )
    {
        var profiles = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var path in paths )
        {
            if ( path.Contains( "/SBEP/", StringComparison.OrdinalIgnoreCase ) || path.Contains( "/sbep/", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "sbep" );
            if ( path.Contains( "/UI/", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "ui" );
            if ( path.Contains( "/Weapons/", StringComparison.OrdinalIgnoreCase ) || path.Contains( "/Player/", StringComparison.OrdinalIgnoreCase ) || path.Contains( "/Game", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "gameplay" );
            if ( path.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) || path.EndsWith( ".sent", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "assets" );
            if ( path.Contains( "Networking", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "networking" );
            if ( path.Contains( "Tools/", StringComparison.OrdinalIgnoreCase ) || path.Contains( "Tool", StringComparison.OrdinalIgnoreCase ) ) profiles.Add( "tools" );
        }

        if ( profiles.Count == 0 ) profiles.Add( "general" );
        return profiles.OrderBy( p => p ).ToList();
    }

    private static IReadOnlyList<string> SuggestedChecks( string profile ) => profile switch
    {
        "sbep" => new[] { "pipeline.compile_and_verify", "sbep_verify.full_verify" },
        "ui" => new[] { "pipeline.compile_and_verify", "playtest.run_assertions(ui smoke)" },
        "assets" => new[] { "autofix.prefab_fixups", "transaction.preview" },
        "networking" => new[] { "pipeline.compile_and_verify", "workflow.verify_networked_component" },
        "tools" => new[] { "pipeline.compile_and_verify", "cross_index.build" },
        _ => new[] { "pipeline.compile_and_verify" }
    };

    private static JsonElement BuildArgs( object obj )
    {
        using var doc = JsonDocument.Parse( JsonSerializer.Serialize( obj ) );
        return doc.RootElement.Clone();
    }
}
