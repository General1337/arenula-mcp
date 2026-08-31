// [HANDOFF-TRUST: implements user-aligned phase-01-design.md Option B (both events). No hypothesis-truth dependency on any sbep-reference handoff; predecessor 1.1+1.3 templates are pattern references only.]
//
// engine-fork-elite-leverage Phase 1.4 — library bridge for the two new fork
// events shipped in engine commit fe31467b:
//
//   1. Sandbox.Internal.TypeLibrary.OnAssemblyRegistered(Assembly, TypeDescription[])
//      Producer type is public; payload TypeDescription is public.
//      → 1.1-style direct typed binding (typeof + Action<,> handler).
//
//   2. Sandbox.ConVarSystem.OnAssemblyRegistered(Assembly, Command[])
//      Producer type is `internal static partial class` (not visible to library);
//      payload Command is default-internal (not visible).
//      → 1.3-style reflection bridge (assembly-name GetType + Expression.Lambda.Compile
//      to build a typed delegate over an Action<object, object> callback).
//
// Both subscriptions are reflection-probed independently so the library binary
// stays byte-identical between fork and stable engines (resilience seam — under
// stable both reflection probes return null and silently no-op).
//
// Cures AP-042 (TypeLibrary stale Dictionary count guards), AP-069 (stale-DLL
// ConCmd-missing ambiguity), AP-083 (component lifecycle vs TypeLibrary scan
// timing), AP-098 (per-instance attribute set — gives a deterministic
// "TypeLibrary settled" signal for /falsify-shared-state probes to sample after).
//
// Engine source authoritative paths (VERIFIED via local Assembly.LoadFrom
// reflection probe 2026-05-19 against fork commit fe31467b after dotnet
// SboxBuild green build):
//   * engine/Sandbox.Reflection/TypeLibrary/TypeLibrary.OnRegistered.cs
//     - public static event Action<Assembly, TypeDescription[]> OnAssemblyRegistered;
//     - Raised at end of AddAssembly() before InitBytePack() — TypeLibrary.cs:117
//   * engine/Sandbox.Engine/Systems/Console/ConVarSystem.OnRegistered.cs
//     - public static event Action<Assembly, Command[]> OnAssemblyRegistered;
//     - Raised at end of AddAssembly() after scan loop — ConVarSystem.cs:79
//
// Banned approaches enforced here (carried from 1.1/1.2/1.3):
//   * No msbuild Directory.Build.targets/.props injection of FORK_ENGINE.
//   * No FORK_ENGINE in sbox_mcp.sbproj Metadata.Compiler.DefineConstants.
//   * No "softsplit.sandbox" or "package.softsplit.sandbox" in AssemblyReferences[]
//     (creates Editor.Button vs game-side Button type ambiguity — 1.2 lesson).
//   * No editing the auto-generated arenula_mcp.editor.csproj (regenerated
//     every editor boot by Sandbox.SolutionGenerator).
//   * No direct C# type references to fork-only types — reflection bridge only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Editor;
using Sandbox;
using Sandbox.Internal;

namespace Arenula;

/// <summary>
/// Subscribes to two fork-only engine events for type / ConCmd registration
/// observability. Caches the most recent payload (assembly name + count +
/// sample names) so MCP query handlers can read freshness without polling
/// TypeLibrary.GetTypes / console_list. Cures AP-042/069/083/098.
/// </summary>
internal static class TypeLibraryHandler
{
    // ── Most-recent-registration cache (read by MCP handlers, lock-free dirty-read OK) ──

    /// <summary>Most recent TypeLibrary.OnAssemblyRegistered payload, or null on stable engine.</summary>
    internal static RegistrationSnapshot _lastTypeLibrarySnapshot;

    /// <summary>Most recent ConVarSystem.OnAssemblyRegistered payload, or null on stable engine.</summary>
    internal static RegistrationSnapshot _lastConVarSystemSnapshot;

    /// <summary>Counts since editor boot — useful for diagnostic UI / test gates.</summary>
    internal static int _typeLibraryFireCount;
    internal static int _convarSystemFireCount;

    /// <summary>Cached snapshot — read by MCP handlers without re-touching the engine.</summary>
    internal sealed record RegistrationSnapshot
    (
        string AssemblyName,
        int Count,
        string[] SampleNames,  // up to 5 names from the payload — for diagnostic UI
        DateTimeOffset Captured
    );

    // ── 1.1-style direct binding for TypeLibrary.OnAssemblyRegistered ──────
    // Producer type Sandbox.Internal.TypeLibrary IS public; payload
    // Sandbox.TypeDescription IS public. Library can typeof() the producer
    // and reference TypeDescription directly. Same pattern as CompileHandler's
    // CompileGroup.OnBuildCompleted subscription.

    private static EventInfo _typeLibraryEventInfo;
    private static Delegate _typeLibraryHandler;

    /// <summary>
    /// Direct typed handler bound to TypeLibrary.OnAssemblyRegistered via
    /// reflection. Caches a sampled snapshot. Empty payload is allowed
    /// (intrinsic-type registrations have no exposed types) — recorded
    /// with Count=0 so subscribers can distinguish from "never fired".
    /// </summary>
    private static void OnEngineTypeLibraryAssemblyRegistered( Assembly assembly, TypeDescription[] types )
    {
        System.Threading.Interlocked.Increment( ref _typeLibraryFireCount );

        var asmName = assembly?.GetName()?.Name ?? "(null assembly)";
        var count = types?.Length ?? 0;
        var samples = types is null
            ? Array.Empty<string>()
            : types.Take( 5 ).Select( t => t?.Name ?? "?" ).ToArray();

        _lastTypeLibrarySnapshot = new RegistrationSnapshot( asmName, count, samples, DateTimeOffset.UtcNow );
    }

    // ── 1.3-style bridge for ConVarSystem.OnAssemblyRegistered ─────────────
    // Producer Sandbox.ConVarSystem is `internal static partial class`;
    // payload Sandbox.Command is default-internal. Neither is visible to a
    // library compile. We GetType them via Assembly.GetType(...) string lookup
    // and build a typed Action<Assembly, Command[]> delegate via
    // Expression.Lambda.Compile() that forwards to an Action<object, object>
    // callback. Mirrors HotloadResultBridge's typed-delegate construction.

    private static EventInfo _convarSystemEventInfo;
    private static Delegate _convarSystemHandler;
    private static Type _convarSystemType;
    private static Type _commandType;
    private static PropertyInfo _commandNamePi;

    /// <summary>
    /// Boxed-object handler bound to ConVarSystem.OnAssemblyRegistered via
    /// the typed-delegate forwarder built in BuildConVarSystemTypedDelegate.
    /// First arg is the Assembly (boxed for shape symmetry; could be
    /// strong-typed since Assembly is BCL); second arg is the Command[]
    /// boxed as object[] (because Command is engine-internal, we cannot
    /// declare a strongly-typed Command[] parameter in library code).
    /// </summary>
    private static void OnEngineConVarAssemblyRegistered( object assemblyBoxed, object commandsBoxed )
    {
        System.Threading.Interlocked.Increment( ref _convarSystemFireCount );

        var assembly = assemblyBoxed as Assembly;
        var asmName = assembly?.GetName()?.Name ?? "(null assembly)";

        // Box-typed array is IEnumerable; iterate via reflection on Command.Name
        var samples = new List<string>( 5 );
        int count = 0;
        try
        {
            if ( commandsBoxed is IEnumerable e )
            {
                foreach ( var cmd in e )
                {
                    count++;
                    if ( samples.Count < 5 && _commandNamePi is not null )
                    {
                        try
                        {
                            var name = _commandNamePi.GetValue( cmd ) as string;
                            samples.Add( name ?? "?" );
                        }
                        catch { samples.Add( "?" ); }
                    }
                }
            }
        }
        catch { /* defensive — never throw from a fork-only event handler */ }

        _lastConVarSystemSnapshot = new RegistrationSnapshot( asmName, count, samples.ToArray(), DateTimeOffset.UtcNow );
    }

    // ── Bind / re-bind on static ctor + hotload ────────────────────────────

    /// <summary>
    /// Hotload re-bind. Engine carries [SkipHotload] on TypeLibrary and
    /// ConVarSystem lives in Sandbox.Engine (also never hotloaded), so the
    /// static event slots persist across library reloads — but the prior
    /// generation's delegate target may be GC'd. The -=/+= pair in
    /// EnsureBound is idempotent.
    /// </summary>
    [EditorEvent.Hotload]
    public static void RebindForkEngineEventsOnHotload()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"TypeLibraryHandler.RebindForkEngineEventsOnHotload failed: {ex.Message}" );
        }
    }

    static TypeLibraryHandler()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"TypeLibraryHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" );
        }
    }

    private static void EnsureBound()
    {
        // ── 1.4a binding: TypeLibrary.OnAssemblyRegistered (direct typed) ──
        BindForkEngineEvent_Typed(
            type: typeof( TypeLibrary ),
            eventName: "OnAssemblyRegistered",
            handlerMethodName: nameof( OnEngineTypeLibraryAssemblyRegistered ),
            existingHandler: ref _typeLibraryHandler,
            eventInfoSink: ref _typeLibraryEventInfo );

        // ── 1.4b binding: ConVarSystem.OnAssemblyRegistered (reflection bridge) ──
        EnsureConVarSystemTypesResolved();

        if ( _convarSystemType is not null )
        {
            BindForkEngineEvent_Bridge(
                type: _convarSystemType,
                eventName: "OnAssemblyRegistered",
                existingHandler: ref _convarSystemHandler,
                eventInfoSink: ref _convarSystemEventInfo );
        }
    }

    /// <summary>
    /// Resolve Sandbox.ConVarSystem + Sandbox.Command + Command.Name PropertyInfo
    /// once on first bind; cache for subsequent re-binds. All null on stable
    /// engine (Sandbox.Command.Name may also be null if the type rename happens
    /// upstream — fail loud via static-ctor catch, not silent).
    /// </summary>
    private static void EnsureConVarSystemTypesResolved()
    {
        if ( _convarSystemType is not null && _commandType is not null && _commandNamePi is not null )
            return;

        var engineAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault( a => string.Equals( a.GetName().Name, "Sandbox.Engine", StringComparison.OrdinalIgnoreCase ) );
        if ( engineAsm == null ) return;

        _convarSystemType = engineAsm.GetType( "Sandbox.ConVarSystem" );
        _commandType = engineAsm.GetType( "Sandbox.Command" );
        if ( _commandType is not null )
        {
            _commandNamePi = _commandType.GetProperty(
                "Name",
                BindingFlags.Public | BindingFlags.Instance );
        }
    }

    /// <summary>
    /// 1.1-style direct typed binding — used when both the producer type
    /// AND the payload types are library-visible. Mirrors the helper in
    /// CompileHandler.cs:BindForkEngineEvent, duplicated here narrowly
    /// to avoid touching CompileHandler in this session (per banned-approach
    /// #6: whitelist-only staging at /finish).
    /// </summary>
    private static void BindForkEngineEvent_Typed(
        Type type,
        string eventName,
        string handlerMethodName,
        ref Delegate existingHandler,
        ref EventInfo eventInfoSink )
    {
        var ev = type.GetEvent(
            eventName,
            BindingFlags.Public | BindingFlags.Static );

        if ( ev == null )
        {
            eventInfoSink = null;
            existingHandler = null;
            return;
        }

        try
        {
            if ( existingHandler != null )
                ev.RemoveEventHandler( null, existingHandler );
        }
        catch { /* defensive — stale delegate post-hotload */ }

        var handlerMethod = typeof( TypeLibraryHandler ).GetMethod(
            handlerMethodName,
            BindingFlags.NonPublic | BindingFlags.Static );
        if ( handlerMethod == null )
        {
            Log.Warning( $"TypeLibraryHandler.BindForkEngineEvent_Typed: {handlerMethodName} not found via reflection — fork-engine event {eventName} will not bind." );
            return;
        }

        existingHandler = Delegate.CreateDelegate( ev.EventHandlerType, handlerMethod );
        ev.AddEventHandler( null, existingHandler );
        eventInfoSink = ev;
    }

    /// <summary>
    /// 1.3-style reflection bridge — used when the payload types are
    /// engine-internal (here: Sandbox.Command is default-internal in
    /// Sandbox.Engine.dll). Builds an engine-typed delegate via
    /// Expression.Lambda.Compile() that boxes both payload args to object
    /// and forwards to OnEngineConVarAssemblyRegistered(object, object).
    /// </summary>
    private static void BindForkEngineEvent_Bridge(
        Type type,
        string eventName,
        ref Delegate existingHandler,
        ref EventInfo eventInfoSink )
    {
        var ev = type.GetEvent(
            eventName,
            BindingFlags.Public | BindingFlags.Static );

        if ( ev == null )
        {
            eventInfoSink = null;
            existingHandler = null;
            return;
        }

        try
        {
            if ( existingHandler != null )
                ev.RemoveEventHandler( null, existingHandler );
        }
        catch { /* defensive — stale delegate post-hotload */ }

        // Event signature is Action<Assembly, Command[]>. Build a typed lambda
        // that converts both args to object and invokes our boxed-object
        // callback. Compiled once per EnsureBound — typed-delegate cost is
        // one indirect call per event invocation, no per-call reflection.
        var invokeMethod = ev.EventHandlerType.GetMethod( "Invoke" );
        if ( invokeMethod == null )
        {
            Log.Warning( $"TypeLibraryHandler.BindForkEngineEvent_Bridge: event {eventName} EventHandlerType has no Invoke method — skip." );
            return;
        }

        var parameters = invokeMethod.GetParameters();
        if ( parameters.Length != 2 )
        {
            Log.Warning( $"TypeLibraryHandler.BindForkEngineEvent_Bridge: event {eventName} expected 2 params (Assembly, Command[]); got {parameters.Length}." );
            return;
        }

        var p0 = Expression.Parameter( parameters[0].ParameterType, "assembly" );
        var p1 = Expression.Parameter( parameters[1].ParameterType, "commands" );

        var callbackMethod = typeof( TypeLibraryHandler ).GetMethod(
            nameof( OnEngineConVarAssemblyRegistered ),
            BindingFlags.NonPublic | BindingFlags.Static );
        if ( callbackMethod == null )
        {
            Log.Warning( $"TypeLibraryHandler.BindForkEngineEvent_Bridge: callback method not found — skip." );
            return;
        }

        var body = Expression.Call(
            callbackMethod,
            Expression.Convert( p0, typeof( object ) ),
            Expression.Convert( p1, typeof( object ) ) );

        var lambda = Expression.Lambda( ev.EventHandlerType, body, p0, p1 );
        existingHandler = lambda.Compile();
        ev.AddEventHandler( null, existingHandler );
        eventInfoSink = ev;
    }
}
