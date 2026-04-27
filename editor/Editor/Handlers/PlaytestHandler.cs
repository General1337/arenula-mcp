using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

internal static class PlaytestHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "boot_scene" => await BootScene( args ),
                "run_assertions" => await RunAssertions( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: boot_scene, run_assertions" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static async Task<object> BootScene( JsonElement args )
    {
        // await GameTask.MainThread() — NOT GetAwaiter().GetResult(). The .GetResult()
        // pattern blocks the background thread until the awaitable completes but does
        // not migrate the caller to the main thread. Subsequent load_scene + start_play
        // would then run on a background thread, where main-thread-only operations
        // (LoadingScreen, scene serialize/load, ISceneStartup events, GameSession setup)
        // silently misbehave — Game.IsPlaying flips but Active.GameSession never gets
        // populated, so get_play_state reads "stopped" after a successful-looking call.
        await GameTask.MainThread();
        var scenePath = HandlerBase.GetString( args, "scene_path" );
        if ( !string.IsNullOrWhiteSpace( scenePath ) )
            SessionHandler.Handle( "load_scene", BuildArgs( new { action = "load_scene", path = scenePath } ) );

        // Guard: if the active scene is empty (0 GameObjects) we have likely
        // hit the post-asset-recompile blanking bug. Auto-reload from disk
        // before attempting play mode so we don't burn a play cycle on a
        // blank scene (which compounds the corruption).
        bool autoReloaded = false;
        try
        {
            var session = Editor.SceneEditorSession.Active;
            var scene = session?.Scene;
            if ( scene != null && scene.Children.Count == 0 )
            {
                var reloadArgs = string.IsNullOrWhiteSpace( scenePath )
                    ? BuildArgs( new { action = "reload_scene" } )
                    : BuildArgs( new { action = "reload_scene", path = scenePath } );
                EditorHandler.ReloadScene( reloadArgs );
                autoReloaded = true;
            }
        }
        catch { /* tolerate — fall through to play */ }

        if ( HandlerBase.GetBool( args, "start_play", true ) )
            EditorHandler.Handle( "start_play", BuildArgs( new { action = "start_play" } ) );

        return HandlerBase.Success( new { scene_path = scenePath, play_state = "requested", auto_reloaded = autoReloaded } );
    }

    private static async Task<object> RunAssertions( JsonElement args )
    {
        await GameTask.MainThread();
        if ( HandlerBase.GetBool( args, "ensure_playing", true ) )
            EditorHandler.Handle( "start_play", BuildArgs( new { action = "start_play" } ) );

        var waitMs = HandlerBase.GetInt( args, "wait_ms", 250 );
        if ( waitMs > 0 ) await Task.Delay( waitMs );

        var assertionsJson = HandlerBase.GetString( args, "assertions" );
        if ( string.IsNullOrWhiteSpace( assertionsJson ) )
            return HandlerBase.Error( "Missing required 'assertions' parameter.", "run_assertions" );

        using var doc = JsonDocument.Parse( assertionsJson );
        if ( doc.RootElement.ValueKind != JsonValueKind.Array )
            return HandlerBase.Error( "'assertions' must be a JSON array.", "run_assertions" );

        var results = new List<object>();
        foreach ( var assertion in doc.RootElement.EnumerateArray() )
            results.Add( EvaluateAssertion( assertion ) );

        var failed = results.Count( r => JsonSerializer.Serialize( r ).Contains( "\"pass\":false", StringComparison.OrdinalIgnoreCase ) );
        return HandlerBase.Success( new { overall = failed == 0 ? "pass" : "fail", assertion_count = results.Count, failed, results } );
    }

    private static object EvaluateAssertion( JsonElement assertion )
    {
        var type = assertion.GetProperty( "type" ).GetString();
        return type switch
        {
            "play_state" => EvaluatePlayStateAssertion( assertion ),
            "log_contains" => EvaluateLogAssertion( assertion, true ),
            "log_not_contains" => EvaluateLogAssertion( assertion, false ),
            "scene_find" => EvaluateSceneFindAssertion( assertion ),
            "console_command" => EvaluateConsoleAssertion( assertion ),
            _ => new { type, pass = false, message = "Unsupported assertion type." }
        };
    }

    private static object EvaluatePlayStateAssertion( JsonElement assertion )
    {
        var expected = assertion.GetProperty( "expected" ).GetString();
        var raw = JsonSerializer.Serialize( EditorHandler.Handle( "get_play_state", BuildArgs( new { action = "get_play_state" } ) ) );
        return new { type = "play_state", expected, pass = raw.Contains( expected, StringComparison.OrdinalIgnoreCase ) };
    }

    private static object EvaluateLogAssertion( JsonElement assertion, bool shouldContain )
    {
        var text = assertion.GetProperty( "text" ).GetString();
        var output = JsonSerializer.Serialize( EditorHandler.Handle( "get_log", BuildArgs( new { action = "get_log", count = 200 } ) ) );
        var contains = output.Contains( text, StringComparison.OrdinalIgnoreCase );
        return new { type = shouldContain ? "log_contains" : "log_not_contains", text, pass = shouldContain ? contains : !contains };
    }

    private static object EvaluateSceneFindAssertion( JsonElement assertion )
    {
        var query = assertion.GetProperty( "query" ).GetString();
        var json = JsonSerializer.Serialize( SceneHandler.Handle( "find", BuildArgs( new { action = "find", query, limit = 5 } ) ) );
        var pass = json.Contains( "\"results\":[", StringComparison.OrdinalIgnoreCase ) && !json.Contains( "\"results\":[]", StringComparison.OrdinalIgnoreCase );
        return new { type = "scene_find", query, pass };
    }

    private static object EvaluateConsoleAssertion( JsonElement assertion )
    {
        var command = assertion.GetProperty( "command" ).GetString();
        var result = ConsoleHandler.HandleAsync( "console_run", BuildArgs( new { action = "console_run", command, wait_ms = 250 } ) ).GetAwaiter().GetResult();
        var mustContain = assertion.TryGetProperty( "contains", out var containsEl ) ? containsEl.GetString() : null;
        var json = JsonSerializer.Serialize( result );
        var pass = string.IsNullOrEmpty( mustContain ) || json.Contains( mustContain, StringComparison.OrdinalIgnoreCase );
        return new { type = "console_command", command, pass, contains = mustContain };
    }

    private static JsonElement BuildArgs( object obj )
    {
        using var doc = JsonDocument.Parse( JsonSerializer.Serialize( obj ) );
        return doc.RootElement.Clone();
    }
}
