// Editor/Handlers/FuzzHandler.cs
//
// [HANDOFF-TRUST: greenfield engine-fork-elite-leverage Phase 6.4 — the `fuzz`
//  MCP tool, in-engine executor of the continuous TypeLibrary fuzzer. No prior-
//  session hypothesis inherited (packet.md §"Prior Failed Approaches" — none).
//  /proptest (tools/proptest-gen.py + Code/SBEP/Tests/Generated/ProptestGatling
//  Turret.cs) is a pattern reference for the runtime fuzz mechanics, NOT a
//  truth dependency; ScratchHandler.cs is the handler-shape template.]
//
// engine-fork-elite-leverage Phase 6.4 — TypeLibrary-driven engine fuzzer.
//
// FuzzHandler is the in-engine half of the continuous fuzzer: it does the only
// work that MUST run in-engine — the runtime TypeLibrary walk, component
// creation on a scratch fixture, [Property] boundary SetValue/GetValue, and
// exception classification. tools/fuzz-daemon.py is the host-side continuous
// loop that drives this tool target-by-target and auto-files crashes as
// docs/ai/failure-memory.json entries.
//
// v1 fuzzes [Property] / Component only. ConCmd-argument fuzzing is v1.1
// (firing arbitrary ConCmds is unsafe — save / restart / scene-mutate /
// recurse). `enumerate` reports the ConCmd count for v1.1 visibility but fires
// none. See docs/sbox-reference/typelibrary-fuzzer.md.
//
// Library-only — no engine fork patch, no fork-only symbol. Scanned via
// [McpTool] so RpcDispatcher routes it through InvokeScanned automatically (no
// switch case). Modern handler template: ScratchHandler.cs (same
// ScratchScenePool dependency the fuzzer needs).
//
// FUZZ-KIND-MATRIX: boolean integer number string vector3 vector2 color rotation angles component_reference gameobject_reference enum
//   ^ machine-readable parity marker. tools/fuzz-daemon.py --self-test asserts
//     this kind list matches tools/proptest-gen.py BOUNDARY_MATRIX + ENUM_KIND
//     (the boundary-matrix SSOT). Keep this line in sync with BoundaryValues()
//     and ClassifyKind() below if either changes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>One crash-class failure found while fuzzing a single [Property].</summary>
internal sealed class FuzzCrash
{
    public string Property { get; set; }
    public string Kind { get; set; }
    public string ValueLabel { get; set; }
    public string ExceptionType { get; set; }
    public string Message { get; set; }
    public string StackTop { get; set; }
}

/// <summary>One fuzzed [Property] — the natural gate granularity (one gate per property).</summary>
internal sealed class FuzzGate
{
    public string Property { get; set; }
    public string Kind { get; set; }
    public int ValueCount { get; set; }
    public string Outcome { get; set; }   // "pass" | "crash"
    public string Detail { get; set; }
}

/// <summary>A [Property] present on the component but with no boundary matrix — recorded, never silently dropped.</summary>
internal sealed class FuzzSkip
{
    public string Property { get; set; }
    public string Kind { get; set; }
    public string Reason { get; set; }
}

/// <summary>
/// Structured result of fuzzing ONE component. Returned by
/// <see cref="FuzzHandler.FuzzComponentCore"/>; the <c>component</c> MCP action
/// projects it to a snake_case wire object, and <c>FuzzHandlerTest</c> asserts
/// against it directly.
/// </summary>
internal sealed class FuzzResult
{
    public string Type { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; }                       // null on success
    public int PropertiesFuzzed { get; set; }
    public List<FuzzSkip> PropertiesSkipped { get; } = new();
    public List<FuzzGate> Gates { get; } = new();
    public List<FuzzCrash> Crashes { get; } = new();
    public int CrashCount => Crashes.Count;
}

/// <summary>
/// <c>fuzz</c> — continuous TypeLibrary-driven robustness fuzzer (executor half).
/// All actions are sync MainThread handlers; the dispatcher awaits MainThread
/// before invoking, so the scene mutation in <c>component</c> is thread-safe.
/// </summary>
[McpTool( "fuzz",
    "Continuous TypeLibrary-driven robustness fuzzer — the in-engine executor half of the Phase 6.4 background fuzzer. Walks a Component's writable [Property] schema via the live editor TypeLibrary, creates a disabled NotSaved fixture in the calling agent's scratch container, sets every property to a matrix of type-boundary values (zero, negative, int/float MaxValue and MinValue, NaN, positive and negative infinity, epsilon, empty and 4096-char and unicode strings, null references, enum extremes and an undefined ordinal), reads each value back, and classifies the outcome: an ArgumentException is a clean input rejection and passes, any other exception (NullReferenceException, IndexOutOfRangeException, InvalidCastException, ...) is a real crash-class robustness bug. Actions: 'enumerate' lists every fuzzable Component type name plus the registered ConCmd count, 'component' fuzzes one component and returns structured per-property gates plus a crashes array, 'status' returns process-lifetime counters. Drive it continuously with tools/fuzz-daemon.py; on-demand single-component fuzzing is the /proptest skill. v1 fuzzes [Property] only — ConCmd-argument fuzzing is deferred to v1.1 behind a safety allowlist because a ConCmd can save, restart, mutate the scene, or recurse.",
    ReadOnlyHint = false )]
internal static class FuzzHandler
{
    // ── Tuning ──────────────────────────────────────────────────────────
    private const int DefaultEnumerateLimit = 400;   // ~8.8 KB/page at 22 B/name — safely under HandlerBase.MaxResponseChars.
    private const int MaxEnumerateLimit = 2000;
    private const string SelfCheckPrefix = "FuzzSelfCheck";   // FuzzHandlerTest fixture components — excluded from `enumerate`.
    private const string DefaultAgentId = "fuzz-daemon";

    // ── Process-lifetime counters (surfaced by `status`) ────────────────
    private static int _componentsFuzzed;
    private static int _totalGates;
    private static int _totalCrashes;
    private static DateTime _lastRunUtc;

    // ── MCP actions ─────────────────────────────────────────────────────

    /// <summary>
    /// enumerate — every fuzzable Component type name plus the ConCmd count.
    /// Paginated (offset/limit) so the host daemon can page the full set without
    /// tripping CursorStore. Returns
    /// <c>{ ok, components, component_count, concmd_count, offset, limit, has_more }</c>.
    /// </summary>
    [McpAction( "enumerate", ReadOnly = true )]
    internal static object HandleEnumerate( JsonElement args )
    {
        try
        {
            int offset = Math.Max( 0, HandlerBase.GetInt( args, "offset", 0 ) );
            int limit = HandlerBase.GetInt( args, "limit", DefaultEnumerateLimit );
            if ( limit <= 0 || limit > MaxEnumerateLimit ) limit = DefaultEnumerateLimit;

            var all = EnumerateComponentNames();
            var page = all.Skip( offset ).Take( limit ).ToList();

            int concmdCount = 0;
            try { concmdCount = Game.TypeLibrary.GetMethodsWithAttribute<ConCmdAttribute>().Count(); }
            catch { /* enumeration is best-effort — never fail `enumerate` over a ConCmd count */ }

            return HandlerBase.Success( new
            {
                ok = true,
                components = page,
                component_count = all.Count,
                concmd_count = concmdCount,
                offset,
                limit,
                has_more = offset + page.Count < all.Count,
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "enumerate" );
        }
    }

    /// <summary>
    /// component — fuzz one component's writable [Property] surface. Args:
    /// <c>type</c> (required), <c>agent_id</c> (optional — the scratch container
    /// owner; defaults to the session agent, then <c>fuzz-daemon</c>). Returns the
    /// projected <see cref="FuzzResult"/>.
    /// </summary>
    [McpAction( "component", WriteKind = "scene_write" )]
    internal static object HandleComponent( JsonElement args, McpSession session )
    {
        try
        {
            var typeName = HandlerBase.GetString( args, "type" );
            if ( string.IsNullOrWhiteSpace( typeName ) )
                return HandlerBase.Error( "Missing required 'type' parameter.", "component",
                    "Use fuzz.enumerate or schema.components to find a component type name." );

            var scene = SceneHelpers.ResolveScene();
            if ( scene == null ) return HandlerBase.Error( "No active scene.", "component" );

            var agentId = ResolveAgentId( args, session );
            var result = FuzzComponentCore( scene, agentId, typeName );
            return HandlerBase.Success( ProjectResult( result ) );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "component" );
        }
    }

    /// <summary>
    /// status — process-lifetime counters. Returns
    /// <c>{ ok, components_fuzzed, total_gates, total_crashes, last_run_utc }</c>.
    /// </summary>
    [McpAction( "status", ReadOnly = true )]
    internal static object HandleStatus( JsonElement args )
    {
        return HandlerBase.Success( new
        {
            ok = true,
            components_fuzzed = _componentsFuzzed,
            total_gates = _totalGates,
            total_crashes = _totalCrashes,
            last_run_utc = _lastRunUtc == default ? null : _lastRunUtc.ToString( "o" ),
        } );
    }

    /// <summary>tools/list inputSchema fragment. The scanner invokes this once at plugin load and caches it.</summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new
            {
                type = "string",
                description = "The fuzz operation to perform.",
                @enum = new[] { "enumerate", "component", "status" },
            },
            ["type"] = new { type = "string", description = "Component type name. Required for: component." },
            ["agent_id"] = new
            {
                type = "string",
                description = "Scratch-container owner id. Optional — defaults to the calling session's identified agent, then 'fuzz-daemon'. Used by: component.",
            },
            ["offset"] = new { type = "integer", description = "Pagination offset for enumerate. Default 0." },
            ["limit"] = new { type = "integer", description = "Max component names per enumerate page. Default 400, max 2000." },
        },
        required = new[] { "action" },
        additionalProperties = false,
    };

    // ── Core — shared by the `component` action and FuzzHandlerTest ──────

    /// <summary>
    /// Sorted names of every fuzzable (non-abstract) Component type in the live
    /// TypeLibrary. The FuzzHandlerTest self-check fixtures are excluded so the
    /// continuous daemon never auto-files their deliberate crashes — they are
    /// reachable only by an explicit <c>type=FuzzSelfCheck*</c> call.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateComponentNames( bool includeSelfCheckFixtures = false )
    {
        return TypeLibrary.GetTypes<Component>()
            .Where( t => t.TargetType != null && !t.TargetType.IsAbstract )
            .Select( t => t.Name )
            .Where( n => !string.IsNullOrEmpty( n )
                && (includeSelfCheckFixtures || !IsSelfCheckFixture( n )) )
            .Distinct( StringComparer.Ordinal )
            .OrderBy( n => n, StringComparer.Ordinal )
            .ToList();
    }

    /// <summary>True for a FuzzHandlerTest self-check fixture component.</summary>
    internal static bool IsSelfCheckFixture( string typeName )
        => typeName != null && typeName.StartsWith( SelfCheckPrefix, StringComparison.Ordinal );

    /// <summary>
    /// Fuzz ONE component's writable [Property] surface. Never throws — an
    /// internal fault becomes <c>{ Ok=false, Error=... }</c>. The fixture
    /// GameObject is a disabled, NotSaved child of the agent's scratch container
    /// and is DestroyImmediate'd before return (idempotent — re-running leaves no
    /// residue).
    /// </summary>
    internal static FuzzResult FuzzComponentCore( Scene scene, string agentId, string typeName )
    {
        var result = new FuzzResult { Type = typeName };
        GameObject fixture = null;
        try
        {
            if ( scene == null ) { result.Error = "No active scene."; return result; }
            if ( string.IsNullOrWhiteSpace( typeName ) ) { result.Error = "Missing 'type'."; return result; }

            var td = TypeLibrary.GetType( typeName );
            if ( td == null || td.TargetType == null )
            {
                result.Error = $"type_unresolved: TypeLibrary.GetType(\"{typeName}\") returned null.";
                return result;
            }
            if ( !typeof( Component ).IsAssignableFrom( td.TargetType ) )
            {
                result.Error = $"not_a_component: '{typeName}' is {td.TargetType.FullName}, not a Component.";
                return result;
            }
            if ( td.TargetType.IsAbstract )
            {
                result.Error = $"abstract_component: '{typeName}' is abstract — cannot instantiate.";
                return result;
            }

            // Disabled, NotSaved fixture under the agent's scratch container —
            // startEnabled:false so no OnEnabled / OnStart / OnUpdate side
            // effects fire while the property surface is fuzzed.
            var container = ScratchScenePool.EnsureContainer(
                scene, string.IsNullOrWhiteSpace( agentId ) ? DefaultAgentId : agentId, "fuzz", out _ );
            fixture = scene.CreateObject();
            fixture.Name = "__fuzz_" + typeName;
            fixture.SetParent( container );
            fixture.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
            fixture.Enabled = false;

            var component = fixture.Components.Create( td, false );
            if ( component == null )
            {
                result.Error = $"instantiation_failed: Components.Create returned null for '{typeName}'.";
                return result;
            }

            // Walk the writable [Property] surface via reflection (the
            // SchemaHelpers pattern); set via td.SetValue/GetValue (the proven
            // ProptestGatlingTurret runtime path).
            foreach ( var prop in td.TargetType
                .GetProperties( BindingFlags.Public | BindingFlags.Instance )
                .Where( p => p.GetIndexParameters().Length == 0 )
                .OrderBy( p => p.Name, StringComparer.Ordinal ) )
            {
                if ( !prop.CanWrite || !prop.CanRead ) continue;
                if ( !HasPropertyAttribute( prop ) ) continue;   // not the [Property] designer surface

                var kind = ClassifyKind( prop.PropertyType );
                if ( kind == null )
                {
                    result.PropertiesSkipped.Add( new FuzzSkip
                    {
                        Property = prop.Name,
                        Kind = "<unfuzzable>",
                        Reason = $"type {SchemaHelpers.FormatTypeName( prop.PropertyType )} has no boundary matrix",
                    } );
                    continue;
                }

                FuzzOneProperty( td, component, prop, kind, result );
            }

            result.Ok = true;
            return result;
        }
        catch ( Exception ex )
        {
            result.Ok = false;
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }
        finally
        {
            try { fixture?.DestroyImmediate(); } catch { /* cleanup is best-effort */ }
            if ( result.Ok )
            {
                _componentsFuzzed++;
                _totalGates += result.Gates.Count;
                _totalCrashes += result.Crashes.Count;
                _lastRunUtc = DateTime.UtcNow;
            }
        }
    }

    // ── Internals ───────────────────────────────────────────────────────

    /// <summary>
    /// Fuzz one [Property] with every boundary value for its kind. An
    /// ArgumentException (or subclass) is a CLEAN rejection — the property
    /// validated its input; any other exception is a crash-class robustness bug.
    /// TargetInvocationException is unwrapped first so a reflection-marshalled
    /// setter throw classifies on its real inner type.
    /// </summary>
    private static void FuzzOneProperty( TypeDescription td, Component component,
        PropertyInfo prop, string kind, FuzzResult result )
    {
        var values = kind == "enum"
            ? EnumBoundaryValues( prop.PropertyType )
            : BoundaryValues( kind );

        result.PropertiesFuzzed++;
        var gate = new FuzzGate { Property = prop.Name, Kind = kind, ValueCount = values.Length };
        var detail = new StringBuilder();
        int crashesHere = 0;

        foreach ( var (value, label) in values )
        {
            try
            {
                bool applied = td.SetValue( component, prop.Name, value );
                _ = td.GetValue( component, prop.Name );
                detail.Append( label ).Append( applied ? "=ok " : "=nomarshal " );
            }
            catch ( Exception ex )
            {
                var actual = Unwrap( ex );
                if ( actual is ArgumentException )
                {
                    detail.Append( label ).Append( "=rejected(" ).Append( actual.GetType().Name ).Append( ") " );
                }
                else
                {
                    crashesHere++;
                    detail.Append( label ).Append( "=CRASH(" ).Append( actual.GetType().Name ).Append( ") " );
                    result.Crashes.Add( new FuzzCrash
                    {
                        Property = prop.Name,
                        Kind = kind,
                        ValueLabel = label,
                        ExceptionType = actual.GetType().Name,
                        Message = Truncate( actual.Message, 240 ),
                        StackTop = StackTop( actual ),
                    } );
                }
            }
        }

        gate.Outcome = crashesHere == 0 ? "pass" : "crash";
        gate.Detail = detail.ToString().Trim();
        result.Gates.Add( gate );
    }

    /// <summary>
    /// Map a property's declared CLR type to a boundary-matrix kind, or null when
    /// the kind has no matrix (object / list / array / Vector4 / asset-reference /
    /// unrecognized). Mirrors <see cref="SchemaHelpers.DescribeType"/> kind
    /// classification, restricted to the kinds tools/proptest-gen.py covers.
    /// </summary>
    private static string ClassifyKind( Type type )
    {
        if ( type == null ) return null;
        var actual = Nullable.GetUnderlyingType( type ) ?? type;

        if ( actual.IsEnum ) return "enum";
        if ( actual == typeof( bool ) ) return "boolean";
        if ( actual == typeof( int ) || actual == typeof( long ) || actual == typeof( short ) ) return "integer";
        if ( actual == typeof( float ) || actual == typeof( double ) || actual == typeof( decimal ) ) return "number";
        if ( actual == typeof( string ) ) return "string";
        if ( actual == typeof( Vector3 ) ) return "vector3";
        if ( actual == typeof( Vector2 ) ) return "vector2";
        if ( actual == typeof( Color ) || actual == typeof( Color32 ) ) return "color";
        if ( actual == typeof( Rotation ) ) return "rotation";
        if ( actual == typeof( Angles ) ) return "angles";
        if ( typeof( Component ).IsAssignableFrom( actual ) ) return "component_reference";
        if ( typeof( GameObject ).IsAssignableFrom( actual ) ) return "gameobject_reference";
        return null;
    }

    /// <summary>
    /// The runtime boundary-value matrix — parallel to tools/proptest-gen.py
    /// BOUNDARY_MATRIX (the SSOT). proptest-gen carries C# source literals for
    /// codegen; the executor needs boxed runtime values (float.NaN, NaN-bearing
    /// Vector3, ...) that cannot round-trip through JSON, so it carries this
    /// parallel copy. fuzz-daemon.py --self-test asserts kind-coverage parity
    /// against the FUZZ-KIND-MATRIX header marker.
    /// </summary>
    private static (object value, string label)[] BoundaryValues( string kind ) => kind switch
    {
        "boolean" => new (object, string)[] { (true, "true"), (false, "false") },
        "integer" => new (object, string)[]
        {
            (0, "0"), (1, "1"), (-1, "-1"),
            (int.MaxValue, "int.MaxValue"), (int.MinValue, "int.MinValue"),
        },
        "number" => new (object, string)[]
        {
            (0f, "0f"), (1f, "1f"), (-1f, "-1f"),
            (float.MaxValue, "float.MaxValue"), (float.MinValue, "float.MinValue"),
            (float.Epsilon, "float.Epsilon"), (float.NaN, "float.NaN"),
            (float.PositiveInfinity, "float.PositiveInfinity"),
            (float.NegativeInfinity, "float.NegativeInfinity"),
        },
        "string" => new (object, string)[]
        {
            ("", "empty"),
            (null, "null"),
            (new string( 'x', 4096 ), "len4096"),
            ("é中\t\r\n\"\\", "unicode+escape"),
        },
        "vector3" => new (object, string)[]
        {
            (Vector3.Zero, "Zero"), (Vector3.One, "One"),
            (new Vector3( 1e9f, -1e9f, 0f ), "huge"),
            (new Vector3( float.NaN, 0f, 0f ), "nan"),
        },
        "vector2" => new (object, string)[]
        {
            (Vector2.Zero, "Zero"), (Vector2.One, "One"),
            (new Vector2( 1e9f, -1e9f ), "huge"),
            (new Vector2( float.NaN, 0f ), "nan"),
        },
        "color" => new (object, string)[]
        {
            (Color.White, "White"), (Color.Black, "Black"),
            (new Color( 2f, 2f, 2f, 2f ), "over1"),
            (new Color( -1f, -1f, -1f, 1f ), "under0"),
        },
        "rotation" => new (object, string)[]
        {
            (Rotation.Identity, "Identity"),
            (Rotation.From( 89f, 179f, -179f ), "extreme"),
            (Rotation.From( 720f, 720f, 720f ), "wrap"),
        },
        "angles" => new (object, string)[]
        {
            (default( Angles ), "default"),
            (new Angles( 89f, 179f, -179f ), "extreme"),
            (new Angles( 720f, 720f, 720f ), "wrap"),
        },
        "component_reference" => new (object, string)[] { (null, "null") },
        "gameobject_reference" => new (object, string)[] { (null, "null") },
        _ => Array.Empty<(object, string)>(),
    };

    /// <summary>
    /// Enum boundary values resolved at runtime from the property type: every
    /// declared name, plus one undefined ordinal (the un-named-value path).
    /// </summary>
    private static (object value, string label)[] EnumBoundaryValues( Type type )
    {
        var enumType = Nullable.GetUnderlyingType( type ) ?? type;
        if ( !enumType.IsEnum ) return Array.Empty<(object, string)>();

        var list = new List<(object, string)>();
        foreach ( var name in Enum.GetNames( enumType ) )
        {
            try { list.Add( (Enum.Parse( enumType, name ), name) ); }
            catch { /* a name that won't parse on the runtime enum — drop it, not a failure */ }
        }
        try { list.Add( (Enum.ToObject( enumType, 99999 ), "undefined(99999)") ); }
        catch { /* enum has no backing for an out-of-range ordinal — skip */ }
        return list.ToArray();
    }

    /// <summary>True when the property carries the [Property] designer attribute.</summary>
    private static bool HasPropertyAttribute( PropertyInfo prop )
    {
        foreach ( var a in prop.GetCustomAttributes( false ) )
            if ( a.GetType().Name == "PropertyAttribute" ) return true;
        return false;
    }

    /// <summary>Unwrap a reflection-marshalled exception to its real inner type.</summary>
    private static Exception Unwrap( Exception ex )
    {
        while ( ex is TargetInvocationException tie && tie.InnerException != null )
            ex = tie.InnerException;
        return ex;
    }

    /// <summary>First non-empty frame of an exception's stack trace, truncated.</summary>
    private static string StackTop( Exception ex )
    {
        var st = ex?.StackTrace;
        if ( string.IsNullOrEmpty( st ) ) return "";
        foreach ( var line in st.Split( '\n' ) )
        {
            var t = line.Trim();
            if ( t.Length > 0 ) return Truncate( t, 200 );
        }
        return "";
    }

    private static string Truncate( string s, int max )
        => string.IsNullOrEmpty( s ) || s.Length <= max ? s : s.Substring( 0, max ) + "…";

    /// <summary>Resolve the scratch-container owner: explicit arg, then session, then the daemon default.</summary>
    private static string ResolveAgentId( JsonElement args, McpSession session )
    {
        var explicitId = HandlerBase.GetString( args, "agent_id" );
        if ( !string.IsNullOrWhiteSpace( explicitId ) ) return explicitId;
        if ( session != null && !string.IsNullOrWhiteSpace( session.AgentId ) ) return session.AgentId;
        return DefaultAgentId;
    }

    /// <summary>Project a <see cref="FuzzResult"/> to the snake_case MCP wire object.</summary>
    private static object ProjectResult( FuzzResult r ) => new
    {
        ok = r.Ok,
        type = r.Type,
        error = r.Error,
        properties_fuzzed = r.PropertiesFuzzed,
        properties_skipped = r.PropertiesSkipped
            .Select( s => new { property = s.Property, kind = s.Kind, reason = s.Reason } ).ToList(),
        gates = r.Gates
            .Select( g => new { property = g.Property, kind = g.Kind, value_count = g.ValueCount, outcome = g.Outcome, detail = g.Detail } ).ToList(),
        crashes = r.Crashes
            .Select( c => new
            {
                property = c.Property,
                kind = c.Kind,
                value_label = c.ValueLabel,
                exception_type = c.ExceptionType,
                message = c.Message,
                stack_top = c.StackTop,
            } ).ToList(),
        crash_count = r.CrashCount,
    };
}
