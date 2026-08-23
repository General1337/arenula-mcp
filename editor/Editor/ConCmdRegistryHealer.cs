using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Self-heals the engine's console-command registry after a hotload (findings.md #1,
/// session 2026-06-11-mcp-agent-efficiency-audit — the AP-069 / stale-IL class).
///
/// Why this exists — the stale-IL mechanism, verified against engine source
/// (fork worktree M:/SBoxProjects/SBox-Pub-hotload-full-fallback, the running build):
///
///   * <c>ConVarSystem.Members</c> maps command names to <c>ManagedCommand</c> objects
///     that each wrap a <c>MethodInfo</c> from the assembly they were registered from
///     (Command.cs:246 — <c>method.Invoke( null, callargs )</c> is the dispatch).
///   * Registration happens ONLY via <c>AssemblyEnroller.OnAssemblyAdded/OnAssemblyRemoved</c>
///     (GameInstanceDll.cs:213/234) and the ToolsDll equivalents. The fork's fast-hotload
///     path (<c>OnAssemblyFastHotload</c>, GameInstanceDll.cs:~239) does NOT call
///     <c>ConVarSystem.AddAssembly</c>.
///   * <c>ConVarSystem.AddCommand</c> uses <c>Members.TryAdd</c> — it never overwrites
///     (ConVarSystem.cs:105). So once a name is bound to an old assembly's MethodInfo,
///     a re-scan alone cannot fix it; the old entry must be removed first.
///
/// Net effect without this healer: after a hotload that bypasses the enroller pair,
/// existing ConCmds keep executing OLD IL (the "live ConCmd still emitted the removed
/// string" class, 8+ sessions) and brand-new [ConCmd]s never register (AP-069, 12+
/// sessions). Both are cured by remove-then-re-add against the newest loaded assembly.
///
/// All ConVarSystem access is via reflection — the type is internal to Sandbox.Engine.
/// Value safety: ConVarSystem.RemoveAssembly calls RememberValue() per command and
/// AddConVar restores from remembered values (ConVarSystem.cs:90-140), so ConVar values
/// survive the remove/re-add cycle. ConCmds carry no value state.
/// </summary>
internal static class ConCmdRegistryHealer
{
    /// <summary>Outcome of one heal pass, embedded by compile.check / verify_hotreload.</summary>
    internal sealed record HealSummary(
        string Utc,
        string Trigger,
        double DurationMs,
        int StaleAssembliesHealed,
        int CommandsRemoved,
        int CommandsReRegistered,
        int NewAssembliesRegistered,
        IReadOnlyList<string> HealedAssemblies,
        IReadOnlyList<string> ReRegisteredCommands,
        string Error );

    /// <summary>Most recent heal outcome (null until the first hotload/check). Read by
    /// CompileHandler so verify_hotreload / compile.check can report what self-healing did.</summary>
    internal static HealSummary LastHealSummary { get; private set; }

    // Engine assemblies are registered with their own cookie contexts by Bootstrap /
    // ToolsDll and are never produced by the in-editor Roslyn pipeline — skip them.
    // Ordinal CASE-SENSITIVE on purpose: engine assemblies are "Sandbox.*" (capital S,
    // dotted); a game project assembly named "sandbox" must NOT match.
    private static readonly string[] SkipPrefixes =
    {
        "Sandbox.", "System.", "Microsoft.", "netstandard", "mscorlib", "WindowsBase", "Mono."
    };

    [EditorEvent.Hotload]
    private static void OnHotload()
    {
        // Engine source: SBox-Pub-hotload-full-fallback/engine/Sandbox.Tools — [EditorEvent.Hotload]
        // fires on the main thread after a library swap; same pattern as HotReloadProbe.OnHotload
        // and CompileHandler.RebindForkEngineEventsOnHotload in this plugin.
        try { HealNow( "hotload_event" ); }
        catch ( Exception ex )
        {
            // Never throw from a hotload event handler — same defense as HotReloadProbe.
            Log.Warning( $"ConCmdRegistryHealer.OnHotload failed: {ex.Message}" );
        }
    }

    /// <summary>
    /// Run one heal pass. Main-thread only (ConVarSystem.Members is mutated without locks
    /// by engine code that asserts main thread). Idempotent — a healthy registry yields a
    /// zero-count summary. Returns the summary it also stores in <see cref="LastHealSummary"/>.
    /// </summary>
    internal static HealSummary HealNow( string trigger )
    {
        var sw = Stopwatch.StartNew();
        var healedAssemblies = new List<string>();
        var reRegisteredCommands = new List<string>();
        int commandsRemoved = 0, commandsReAdded = 0, newAssemblies = 0;
        string error = null;

        try
        {
            var refl = EngineReflection.Resolve();
            if ( refl == null )
            {
                error = "Could not resolve Sandbox.ConVarSystem internals via reflection (engine layout changed?).";
            }
            else
            {
                var newestByName = SurveyNewestAssemblies( out var instanceCounts );

                // ── Pass 1: stale-pair healing ─────────────────────────────────
                // Group registered commands by their backing assembly; any backing
                // assembly that is no longer the newest loaded instance of its simple
                // name is stale → RemoveAssembly(old) + AddAssembly(newest).
                var staleByOldAssembly = new Dictionary<Assembly, Assembly>();
                foreach ( var (name, backing) in EnumerateRegisteredBackingAssemblies( refl ) )
                {
                    if ( backing == null ) continue;
                    var simple = backing.GetName().Name ?? "";
                    if ( IsSkipped( simple ) ) continue;
                    if ( !newestByName.TryGetValue( simple, out var newest ) ) continue;
                    if ( ReferenceEquals( newest, backing ) ) continue;
                    staleByOldAssembly[backing] = newest;
                }

                foreach ( var (oldAsm, newAsm) in staleByOldAssembly )
                {
                    int beforeOld = CountCommandsFrom( refl, oldAsm );
                    refl.RemoveAssembly( oldAsm );
                    commandsRemoved += beforeOld;

                    refl.AddAssembly( newAsm, CookieKeyFor( newAsm ), null );
                    int afterNew = CountCommandsFrom( refl, newAsm, collectInto: reRegisteredCommands );
                    commandsReAdded += afterNew;

                    healedAssemblies.Add( newAsm.GetName().Name );
                    Log.Info( $"[ConCmdRegistryHealer] re-registered {afterNew} command(s) from " +
                              $"{newAsm.GetName().Name} (removed {beforeOld} stale; trigger={trigger})" );
                }

                // ── Pass 2: never-registered sweep ─────────────────────────────
                // Catches a brand-new [ConCmd] in an assembly that previously had NO
                // commands (so pass 1 had nothing to remove). Candidates: in-memory
                // (engine-Roslyn-compiled) or multiply-loaded assemblies that declare
                // [ConVar]/[ConCmd] static members whose names are absent from Members.
                foreach ( var (simple, asm) in newestByName )
                {
                    if ( staleByOldAssembly.ContainsValue( asm ) ) continue; // already re-added above

                    bool hotloadShaped = AssemblyLocationIsEmpty( asm ) || instanceCounts[simple] > 1;
                    if ( !hotloadShaped ) continue;

                    var declared = DeclaredCommandNames( asm );
                    if ( declared.Count == 0 ) continue;

                    bool anyMissing = declared.Any( n => refl.Find( n ) == null );
                    if ( !anyMissing ) continue;

                    refl.AddAssembly( asm, CookieKeyFor( asm ), null );
                    int added = CountCommandsFrom( refl, asm, collectInto: reRegisteredCommands );
                    if ( added > 0 )
                    {
                        commandsReAdded += added;
                        newAssemblies++;
                        healedAssemblies.Add( asm.GetName().Name );
                        Log.Info( $"[ConCmdRegistryHealer] registered {added} command(s) from previously-" +
                                  $"unregistered assembly {asm.GetName().Name} (trigger={trigger})" );
                    }
                }
            }
        }
        catch ( Exception ex )
        {
            error = ex.Message;
        }

        sw.Stop();
        var summary = new HealSummary(
            Utc: DateTime.UtcNow.ToString( "o" ),
            Trigger: trigger,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            StaleAssembliesHealed: healedAssemblies.Count - newAssemblies,
            CommandsRemoved: commandsRemoved,
            CommandsReRegistered: commandsReAdded,
            NewAssembliesRegistered: newAssemblies,
            HealedAssemblies: healedAssemblies,
            ReRegisteredCommands: reRegisteredCommands.Take( 32 ).ToList(),
            Error: error );
        LastHealSummary = summary;
        return summary;
    }

    // ── Assembly survey (shared with CompileHandler's loaded-code hash) ───────

    /// <summary>
    /// Newest loaded instance per assembly simple name, skipping engine/BCL assemblies.
    /// AppDomain.GetAssemblies() returns assemblies in load order, so for a hotloaded
    /// name the LAST instance is the live one (old instances stay resident — .NET
    /// cannot unload them outside collectible contexts).
    /// </summary>
    internal static Dictionary<string, Assembly> SurveyNewestAssemblies( out Dictionary<string, int> instanceCounts )
    {
        var newest = new Dictionary<string, Assembly>( StringComparer.Ordinal );
        instanceCounts = new Dictionary<string, int>( StringComparer.Ordinal );

        foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
        {
            if ( asm.IsDynamic ) continue;
            var simple = asm.GetName().Name;
            if ( string.IsNullOrEmpty( simple ) || IsSkipped( simple ) ) continue;

            newest[simple] = asm; // later load order wins
            instanceCounts[simple] = instanceCounts.TryGetValue( simple, out var c ) ? c + 1 : 1;
        }
        return newest;
    }

    private static bool IsSkipped( string simpleName )
    {
        foreach ( var p in SkipPrefixes )
            if ( simpleName.StartsWith( p, StringComparison.Ordinal ) ) return true;
        // exact engine/BCL names without dots
        return simpleName is "System" or "mscorlib" or "netstandard" or "Sandbox";
    }

    private static bool AssemblyLocationIsEmpty( Assembly asm )
    {
        try { return string.IsNullOrEmpty( asm.Location ); }
        catch { return true; } // dynamic-ish assemblies throw — treat as in-memory
    }

    /// <summary>
    /// Cookie-container key for re-registration, mirroring the engine's own choices:
    /// game assemblies use "game" (GameInstanceDll.cs:213), editor libraries use
    /// "editor.&lt;name&gt;" (ToolsDll.cs:136). Cookies only affect where SAVED ConVar
    /// values persist on disk; ConCmds ignore them entirely, and in-session values are
    /// preserved by the engine's remembered-value path regardless of cookie key.
    /// </summary>
    private static string CookieKeyFor( Assembly asm )
    {
        var name = asm.GetName().Name ?? "";
        if ( name.EndsWith( ".editor", StringComparison.OrdinalIgnoreCase ) )
            return "editor." + name.Substring( 0, name.Length - ".editor".Length );
        return "game";
    }

    /// <summary>
    /// Scan an assembly for [ConVar]/[ConCmd] static members the way the engine's
    /// AddAssembly does (ConVarSystem.cs:30-85: GetMembers Static|Public|NonPublic,
    /// GetCustomAttribute&lt;ConVarAttribute&gt;, Context==null filter). ConCmdAttribute
    /// derives from ConVarAttribute (Sandbox.System/ConVar/ConCmdAttributes.cs:4) so a
    /// single attribute check covers both. Name default mirrors the engine/ConsoleHandler:
    /// attr.Name, else member name lowercased.
    /// </summary>
    // ConVarAttribute.Context exists in engine source (ConVarSystem.cs:46 filters on it)
    // but is not exposed on the published ref assembly — read it via reflection.
    private static readonly PropertyInfo _attrContextProp = typeof( ConVarAttribute )
        .GetProperty( "Context", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );

    private static List<string> DeclaredCommandNames( Assembly asm )
    {
        var names = new List<string>();
        Type[] types;
        try { types = asm.GetTypes(); }
        catch ( ReflectionTypeLoadException rtle ) { types = rtle.Types.Where( t => t != null ).ToArray(); }
        catch { return names; }

        foreach ( var t in types )
        {
            MemberInfo[] members;
            try { members = t.GetMembers( BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic ); }
            catch { continue; }

            foreach ( var member in members )
            {
                ConVarAttribute attr;
                try { attr = member.GetCustomAttribute<ConVarAttribute>(); }
                catch { continue; }
                if ( attr == null ) continue;

                // Game/editor context only (Context == null) — menu commands register
                // through MenuDll with context "menu" and are not our concern.
                try
                {
                    if ( _attrContextProp?.GetValue( attr ) is string ctx && ctx != null ) continue;
                }
                catch { }

                var name = !string.IsNullOrEmpty( attr.Name ) ? attr.Name : member.Name.ToLowerInvariant();
                names.Add( name );
            }
        }
        return names;
    }

    private static int CountCommandsFrom( EngineReflection refl, Assembly asm, List<string> collectInto = null )
    {
        int count = 0;
        foreach ( var (name, backing) in EnumerateRegisteredBackingAssemblies( refl ) )
        {
            if ( !ReferenceEquals( backing, asm ) ) continue;
            count++;
            collectInto?.Add( name );
        }
        return count;
    }

    private static IEnumerable<(string name, Assembly backing)> EnumerateRegisteredBackingAssemblies( EngineReflection refl )
    {
        // Snapshot first — callers mutate Members (Remove/AddAssembly) between passes.
        var snapshot = new List<(string, object)>();
        foreach ( System.Collections.DictionaryEntry entry in refl.Members )
            snapshot.Add( ((string)entry.Key, entry.Value) );

        foreach ( var (name, cmd) in snapshot )
            yield return (name, refl.BackingAssemblyOf( cmd ));
    }

    // ── Reflection plumbing for Sandbox.ConVarSystem (internal, Sandbox.Engine) ──

    private sealed class EngineReflection
    {
        private readonly MethodInfo _addAssembly;    // AddAssembly(Assembly, string, string)
        private readonly MethodInfo _removeAssembly; // RemoveAssembly(Assembly)
        private readonly MethodInfo _find;           // Find(string) → Command
        private readonly FieldInfo _membersField;    // Dictionary<string, Command>
        private readonly FieldInfo _managedCommandAssemblyField; // ManagedCommand.assembly (may be null → fallback)
        private readonly MethodInfo _isFromAssembly; // Command.IsFromAssembly(Assembly) — fallback probe

        private static EngineReflection _cached;
        private static bool _resolveAttempted;

        private EngineReflection( MethodInfo add, MethodInfo remove, MethodInfo find,
            FieldInfo members, FieldInfo mcAsmField, MethodInfo isFrom )
        {
            _addAssembly = add; _removeAssembly = remove; _find = find;
            _membersField = members; _managedCommandAssemblyField = mcAsmField; _isFromAssembly = isFrom;
        }

        internal System.Collections.IDictionary Members =>
            (System.Collections.IDictionary)_membersField.GetValue( null );

        internal void AddAssembly( Assembly asm, string cookies, string context ) =>
            _addAssembly.Invoke( null, new object[] { asm, cookies, context } );

        internal void RemoveAssembly( Assembly asm ) =>
            _removeAssembly.Invoke( null, new object[] { asm } );

        internal object Find( string name ) =>
            _find.Invoke( null, new object[] { name } );

        internal Assembly BackingAssemblyOf( object command )
        {
            if ( command == null ) return null;
            try
            {
                if ( _managedCommandAssemblyField != null
                     && _managedCommandAssemblyField.DeclaringType.IsInstanceOfType( command ) )
                    return _managedCommandAssemblyField.GetValue( command ) as Assembly;

                // Fallback: probe IsFromAssembly against every loaded assembly instance.
                if ( _isFromAssembly != null )
                {
                    foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
                    {
                        if ( asm.IsDynamic ) continue;
                        if ( (bool)_isFromAssembly.Invoke( command, new object[] { asm } ) ) return asm;
                    }
                }
            }
            catch { }
            return null;
        }

        internal static EngineReflection Resolve()
        {
            if ( _resolveAttempted ) return _cached;
            _resolveAttempted = true;

            try
            {
                // Engine source: SBox-Pub-hotload-full-fallback/engine/Sandbox.Engine/Systems/Console/
                // ConVarSystem.cs — internal static partial class ConVarSystem in the same assembly
                // as the public Sandbox.ConsoleSystem.
                var engineAsm = typeof( Sandbox.ConsoleSystem ).Assembly;
                var convarSystem = engineAsm.GetType( "Sandbox.ConVarSystem" );
                if ( convarSystem == null ) return null;

                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var add = convarSystem.GetMethod( "AddAssembly", S, null,
                    new[] { typeof( Assembly ), typeof( string ), typeof( string ) }, null );
                var remove = convarSystem.GetMethod( "RemoveAssembly", S, null,
                    new[] { typeof( Assembly ) }, null );
                var find = convarSystem.GetMethod( "Find", S, null, new[] { typeof( string ) }, null );
                var members = convarSystem.GetField( "Members", S );
                if ( add == null || remove == null || find == null || members == null ) return null;

                var managedCommand = engineAsm.GetType( "Sandbox.ManagedCommand" );
                // Engine source: Command.cs:316 — IsFromAssembly compares against the private
                // instance field `assembly` on ManagedCommand.
                var mcAsmField = managedCommand?.GetField( "assembly",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );
                var commandType = engineAsm.GetType( "Sandbox.Command" );
                var isFrom = commandType?.GetMethod( "IsFromAssembly",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );

                _cached = new EngineReflection( add, remove, find, members, mcAsmField, isFrom );
            }
            catch
            {
                _cached = null;
            }
            return _cached;
        }
    }
}
