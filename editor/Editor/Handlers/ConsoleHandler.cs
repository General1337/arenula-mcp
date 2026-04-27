// Editor/Handlers/ConsoleHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

/// <summary>
/// Console actions for the editor tool: console_list, console_run.
/// console_run auto-detects ConVar (get/set) vs ConCmd (execute + capture output).
/// ConCmd execution runs on main thread and captures log output for the response.
/// </summary>
internal static class ConsoleHandler
{
    // Lazy cache of ConCmd methods, keyed by command name
    private static Dictionary<string, MethodInfo> _conCmdCache;

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

        // Path 2: ConCmd — execute and capture log output
        var method = ResolveConCmd( cmdName );
        if ( method != null )
        {
            return await ExecuteConCmd( cmdName, parts, method, waitMs );
        }

        // Path 3: Try ConsoleSystem.Run as last resort (catches engine-registered commands)
        return await TryRunRaw( command, waitMs );
    }

    private static async Task<object> ExecuteConCmd(
        string cmdName, string[] parts, MethodInfo method, int waitMs )
    {
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();

        try
        {
            // Switch to main thread for scene access
            await GameTask.MainThread();

            // Build arguments — skip Connection parameters (editor has no player connection)
            var parameters = method.GetParameters();
            var invokeArgs = new List<object>();
            int argIndex = 1; // parts[0] is the command name

            foreach ( var param in parameters )
            {
                if ( param.ParameterType == typeof( Connection ) )
                {
                    invokeArgs.Add( null );
                    continue;
                }

                if ( argIndex < parts.Length )
                {
                    invokeArgs.Add( ConvertArg( parts[argIndex], param.ParameterType ) );
                    argIndex++;
                }
                else if ( param.HasDefaultValue )
                {
                    invokeArgs.Add( param.DefaultValue );
                }
                else
                {
                    return HandlerBase.Error(
                        $"ConCmd '{cmdName}' requires parameter '{param.Name}' ({param.ParameterType.Name}).",
                        "console_run" );
                }
            }

            method.Invoke( null, invokeArgs.ToArray() );
        }
        catch ( TargetInvocationException ex )
        {
            return HandlerBase.Error( $"ConCmd '{cmdName}' threw: {ex.InnerException?.Message ?? ex.Message}", "console_run" );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"ConCmd '{cmdName}' error: {ex.Message}", "console_run" );
        }

        // Wait for async log output
        if ( waitMs > 0 )
            await Task.Delay( Math.Clamp( waitMs, 0, 5000 ) );

        var after = EditorHandler.LogSnapshotText();
        var newLines = after.Skip( before.Length ).ToArray();

        return HandlerBase.Success( new
        {
            command = cmdName,
            kind = "concmd",
            output_lines = newLines.Length,
            output = newLines
        } );
    }

    private static async Task<object> TryRunRaw( string command, int waitMs )
    {
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();

        try
        {
            await GameTask.MainThread();
            ConsoleSystem.Run( command );
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
            output_lines = newLines.Length,
            output = newLines
        } );
    }

    // ── ConCmd reflection cache ──────────────────────────────────────────

    private static MethodInfo ResolveConCmd( string cmdName )
    {
        if ( _conCmdCache == null )
            BuildConCmdCache();

        _conCmdCache.TryGetValue( cmdName.ToLowerInvariant(), out var method );
        return method;
    }

    /// <summary>Call after recompilation to rebuild the ConCmd cache.</summary>
    internal static void InvalidateCache() => _conCmdCache = null;

    private static void BuildConCmdCache()
    {
        _conCmdCache = new( StringComparer.OrdinalIgnoreCase );

        foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
        {
            try
            {
                foreach ( var type in asm.GetTypes() )
                {
                    foreach ( var method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static ) )
                    {
                        var attr = method.GetCustomAttributes( typeof( ConCmdAttribute ), false )
                            .FirstOrDefault() as ConCmdAttribute;
                        if ( attr == null ) continue;

                        var name = ( attr.Name ?? method.Name ).ToLowerInvariant();
                        _conCmdCache.TryAdd( name, method );
                    }
                }
            }
            catch { }
        }
    }

    private static object ConvertArg( string value, Type targetType )
    {
        if ( targetType == typeof( string ) ) return value;
        if ( targetType == typeof( int ) ) return int.TryParse( value, out var i ) ? i : 0;
        if ( targetType == typeof( float ) ) return float.TryParse( value, out var f ) ? f : 0f;
        if ( targetType == typeof( bool ) ) return value is "1" or "true" or "True";
        return value;
    }
}
