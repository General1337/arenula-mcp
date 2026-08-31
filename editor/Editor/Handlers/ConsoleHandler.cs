// Editor/Handlers/ConsoleHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
// Alias rather than `using Editor;` — Editor.ConsoleSystem would make every bare
// ConsoleSystem reference in this file ambiguous (CS0104).
using SceneEditorSession = Editor.SceneEditorSession;

namespace Arenula;

/// <summary>
/// Console actions for the editor tool: console_list, console_run.
/// console_run auto-detects ConVar (get/set) vs ConCmd (execute + capture output).
/// ConCmd execution runs through the engine console registry on the main thread
/// and captures log output for the response.
/// </summary>
internal static class ConsoleHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "console_list" => ConsoleList( args ),
                "console_run"  => await ConsoleRunAsync( args ),
                _ => HandlerBase.Error( $"Unknown console action '{action}'", action,
                    "Valid console actions: console_list, console_run" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── console_list ─────────────────────────────────────────────────────

    private static object ConsoleList( JsonElement args )
    {
        var filter = HandlerBase.GetString( args, "filter" );
        var entries = new List<object>();

        foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
        {
            try
            {
                foreach ( var type in asm.GetTypes() )
                {
                    // ConVars
                    foreach ( var prop in type.GetProperties(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static ) )
                    {
                        var attr = prop.GetCustomAttributes( typeof( ConVarAttribute ), false )
                            .FirstOrDefault() as ConVarAttribute;
                        if ( attr == null ) continue;

                        var cvarName = !string.IsNullOrEmpty( attr.Name )
                            ? attr.Name
                            : prop.Name.ToLowerInvariant();

                        if ( !string.IsNullOrEmpty( filter )
                            && cvarName.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                            continue;

                        string currentValue = null;
                        try { currentValue = ConsoleSystem.GetValue( cvarName ); } catch { }

                        entries.Add( new
                        {
                            name = cvarName,
                            kind = "convar",
                            help = attr.Help ?? "",
                            flags = attr.Flags.ToString(),
                            saved = attr.Flags.HasFlag( ConVarFlags.Saved ),
                            currentValue,
                            declaringType = type.Name
                        } );
                    }

                    // ConCmds
                    foreach ( var method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static ) )
                    {
                        var attr = method.GetCustomAttributes( typeof( ConCmdAttribute ), false )
                            .FirstOrDefault() as ConCmdAttribute;
                        if ( attr == null ) continue;

                        var cmdName = attr.Name ?? method.Name.ToLowerInvariant();

                        if ( !string.IsNullOrEmpty( filter )
                            && cmdName.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                            continue;

                        var paramNames = string.Join( ", ", method.GetParameters()
                            .Where( p => p.ParameterType != typeof( Connection ) )
                            .Select( p => $"{p.ParameterType.Name} {p.Name}" ) );

                        entries.Add( new
                        {
                            name = cmdName,
                            kind = "concmd",
                            help = attr.Help ?? "",
                            parameters = paramNames,
                            declaringType = type.Name
                        } );
                    }
                }
            }
            catch { }
        }

        // Deduplicate by name and sort
        var unique = entries
            .GroupBy( e =>
            {
                var nameField = e.GetType().GetProperty( "name" );
                return nameField?.GetValue( e )?.ToString() ?? "";
            } )
            .Select( g => g.First() )
            .OrderBy( e =>
            {
                var nameField = e.GetType().GetProperty( "name" );
                return nameField?.GetValue( e )?.ToString() ?? "";
            } )
            .ToList();

        return HandlerBase.Success( new
        {
            count = unique.Count,
            entries = unique
        } );
    }

    // ── console_run (async, supports ConVar + ConCmd) ────────────────────

    private static async Task<object> ConsoleRunAsync( JsonElement args )
    {
        var command = HandlerBase.GetString( args, "command" );
        if ( string.IsNullOrEmpty( command ) )
            return HandlerBase.Error( "Missing required 'command' parameter.", "console_run" );

        var waitMs = HandlerBase.GetInt( args, "wait_ms", 100 );
        var bindScene = HandlerBase.GetBool( args, "bind_scene", true );

        var parts = command.Trim().Split( ' ', StringSplitOptions.RemoveEmptyEntries );
        if ( parts.Length == 0 )
            return HandlerBase.Error( "Empty command.", "console_run" );

        var cmdName = parts[0];

        // Path 1: ConVar (get/set) — same as before
        string current = null;
        try { current = ConsoleSystem.GetValue( cmdName ); } catch { }

        if ( current != null )
        {
            // Read-only query
            if ( parts.Length == 1 )
                return HandlerBase.Text( $"{cmdName} = {current}" );

            // Write: set the convar value
            var newValue = string.Join( " ", parts, 1, parts.Length - 1 );
            ConsoleSystem.SetValue( cmdName, newValue );

            string readback = null;
            try { readback = ConsoleSystem.GetValue( cmdName ); } catch { }

            return HandlerBase.Text( $"Set {cmdName} = {readback ?? newValue}" );
        }

        // Path 2: ConCmd — execute through the engine registry.
        //
        // Do not prefer MCP reflection here. Hotloaded game assemblies can leave old
        // MethodInfo instances loaded in the AppDomain even after a clean compile and
        // successful TypeLibrary probe; invoking them directly makes already-named
        // commands such as sbep.dev keep running stale IL. ConsoleSystem.Run follows
        // the engine's current ConCmd/ConVar registry, including full-hotload fallback
        // re-registration, so it is the authoritative dispatch path.
        return await TryRunRaw( command, waitMs, bindScene );
    }

    private static async Task<object> TryRunRaw( string command, int waitMs, bool bindScene )
    {
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();

        string sceneBound = null;
        bool sceneIsPlaySession = false;

        try
        {
            await GameTask.MainThread();

            // Bind the live session scene so the ConCmd body sees a valid Game.ActiveScene
            // (findings.md #1 item 3 / AP-061: the tools-context ActiveScene is stale or
            // null for ~30s after a compile/hotload, so handlers calling
            // Game.ActiveScene.GetAllComponents<T>() silently no-op or throw).
            //
            // Why this works — verified against engine source (fork worktree
            // SBox-Pub-hotload-full-fallback):
            //   * Scene.Push() is PUBLIC (Scene.cs:156) — sets Game.ActiveScene + scene
            //     Time for the scope, restores on Dispose, main-thread asserted.
            //   * ConsoleSystem.Run → RunInternal → ConVarSystem.RunSingle →
            //     ManagedCommand method.Invoke (Command.cs:246) is fully SYNCHRONOUS on
            //     this thread, so the Push scope covers the entire ConCmd body.
            //   * During play, SceneEditorSession.Active.Scene IS the play-mode clone
            //     (same source PipelineHandler relies on for IsPlaying / save-skip).
            Scene targetScene = null;
            if ( bindScene )
            {
                var session = SceneEditorSession.Active;
                targetScene = session?.Scene;
                sceneIsPlaySession = session?.IsPlaying == true;
                if ( targetScene == null )
                {
                    targetScene = Game.ActiveScene;
                    sceneIsPlaySession = false;
                }
            }

            if ( targetScene != null )
            {
                using ( targetScene.Push() )
                {
                    ConsoleSystem.Run( command );
                }
                sceneBound = targetScene.Name;
            }
            else
            {
                ConsoleSystem.Run( command );
            }
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error(
                $"Unknown command: '{command}'. Error: {ex.Message}",
                "console_run",
                "Use editor.console_list to see available command names." );
        }

        if ( waitMs > 0 )
            await Task.Delay( Math.Clamp( waitMs, 0, 5000 ) );

        var after = EditorHandler.LogSnapshotText();
        var newLines = after.Skip( before.Length ).ToArray();

        return HandlerBase.Success( new
        {
            command,
            kind = "raw",
            scene_bound = sceneBound,
            scene_is_play_session = sceneIsPlaySession,
            output_lines = newLines.Length,
            output = newLines
        } );
    }

    /// <summary>Kept for compile pipeline compatibility; console_run no longer caches reflected ConCmd methods.</summary>
    internal static void InvalidateCache() { }
}
