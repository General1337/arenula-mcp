using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>
/// Reflection-driven discovery of <see cref="McpToolAttribute"/>-decorated classes
/// and their <see cref="McpActionAttribute"/>-decorated handler methods. Replaces
/// the legacy triple-touch pattern (ToolRegistry.All + ActionMetadata + dispatcher
/// switch) per the <c>mcp-dynamic-discovery</c> initiative charter §"Phase 1".
///
/// Phase 1 scope (current): scanner type + records + validation + tools/list
/// payload builder. No tools are migrated yet — <see cref="Scan"/> against the
/// current assembly returns an empty dictionary, and the parity test ConCmd
/// (<c>sbep.test_mcp_tools_list_parity</c>) is expected to FAIL because the
/// legacy <c>ToolRegistry.All</c> hash differs from the empty scanner hash.
///
/// Phase 2+ (NOT in this file's scope) wires <see cref="BuildToolsListPayload"/>
/// into the dispatcher's <c>tools/list</c> response and reflection-invokes
/// <see cref="ScannedAction.Method"/> from <c>DispatchToolCall</c>.
///
/// Charter validation contract (§1.2):
/// <list type="bullet">
///   <item>Every <c>[McpAction]</c> method lives on a class tagged <c>[McpTool]</c>.</item>
///   <item>Actions per tool are unique (case-insensitive on <c>Action</c>).</item>
///   <item>Sync handlers (<c>Threading == MainThread</c>) must be
///     <c>static object Handle*(JsonElement args)</c>.</item>
///   <item>Async handlers (<c>Threading == AsyncBeforeMainThread</c> or
///     <c>AnyThread</c>) must be
///     <c>static Task&lt;object&gt; Handle*Async(JsonElement args)</c>.</item>
/// </list>
/// All four validations throw <see cref="InvalidOperationException"/> on first
/// failure — typos in attribute strings fail loud at plugin load, not at first
/// user call.
/// </summary>
internal static class McpToolScanner
{
	/// <summary>
	/// Scan the given assembly for <see cref="McpToolAttribute"/> classes and produce
	/// a name-keyed dictionary of <see cref="ScannedTool"/> records.
	///
	/// Idempotent and pure — callers should cache the result at plugin load.
	/// Throws <see cref="InvalidOperationException"/> on contract violations.
	/// </summary>
	internal static IReadOnlyDictionary<string, ScannedTool> Scan( Assembly assembly )
	{
		if ( assembly == null ) throw new ArgumentNullException( nameof( assembly ) );

		var tools = new Dictionary<string, ScannedTool>( StringComparer.OrdinalIgnoreCase );

		foreach ( var type in assembly.GetTypes() )
		{
			var toolAttr = type.GetCustomAttribute<McpToolAttribute>( inherit: false );
			if ( toolAttr == null )
			{
				// Charter §1.2 validation #1: an [McpAction] method on a class NOT
				// tagged [McpTool] is a decoration mistake — fail loud at scan time.
				ValidateNoOrphanActions( type );
				continue;
			}

			if ( tools.ContainsKey( toolAttr.Name ) )
			{
				throw new InvalidOperationException(
					$"[McpToolScanner] Duplicate [McpTool(Name=\"{toolAttr.Name}\")] on type " +
					$"'{type.FullName}' (already declared on '{tools[toolAttr.Name].DeclaringType.FullName}')." );
			}

			var actions = ScanActions( type, toolAttr.Name );
			var inputSchema = InvokeInputSchema( type, toolAttr.Name );

			tools[toolAttr.Name] = new ScannedTool(
				Name: toolAttr.Name,
				Description: toolAttr.Description,
				ReadOnlyHint: toolAttr.ReadOnlyHint,
				IncludeReadOnlyHint: toolAttr.IncludeReadOnlyHint,
				InputSchema: inputSchema,
				Actions: actions,
				DeclaringType: type );
		}

		return tools;
	}

	// [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — dispatcher-facing scan cache; no handoff hypothesis inherited]
	private static Lazy<IReadOnlyDictionary<string, ScannedTool>> _cached = CreateCache();

	private static Lazy<IReadOnlyDictionary<string, ScannedTool>> CreateCache()
		=> new( () => Scan( typeof( McpToolScanner ).Assembly ), isThreadSafe: true );

	/// <summary>
	/// Hotload-aware cached scan of the editor assembly, keyed by tool name. The
	/// hybrid dispatcher (<c>RpcDispatcher</c>) reads this on every tools/list
	/// and tools/call. Thread-safe lazy init — a malformed [McpTool]/[McpAction]
	/// surfaces as a thrown <see cref="InvalidOperationException"/> on first
	/// access (fail-loud per the charter §1.2 validation contract). Successful
	/// editor-assembly hotloads atomically replace the lazy before the MCP listener
	/// accepts new requests, so cached attributes and MethodInfo never span swaps.
	/// </summary>
	internal static IReadOnlyDictionary<string, ScannedTool> Cached => Volatile.Read( ref _cached ).Value;

	/// <summary>
	/// Discard the current reflection snapshot without scanning. The next caller
	/// rebuilds it lazily and observes scanner validation failures normally.
	/// </summary>
	internal static void Invalidate()
	{
		// VERIFIED via /check-engine 2026-07-31: Sandbox.Tools/ToolsDll.cs:147-158
		// registers the incoming editor assembly before OnAfterHotload raises the
		// event at :130-145. AssetSystem.Compile.cs:49-60 uses the same hotload
		// cache-clear pattern for reflection-derived compiler metadata.
		Interlocked.Exchange( ref _cached, CreateCache() );
	}

	// Static + instance so a mistakenly non-static [McpAction] is still discovered
	// and rejected loudly rather than silently skipped.
	private const BindingFlags MethodScanFlags =
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

	private static IReadOnlyDictionary<string, ScannedAction> ScanActions( Type toolType, string toolName )
	{
		var actions = new Dictionary<string, ScannedAction>( StringComparer.OrdinalIgnoreCase );

		foreach ( var method in toolType.GetMethods( MethodScanFlags ) )
		{
			var actionAttr = method.GetCustomAttribute<McpActionAttribute>( inherit: false );
			if ( actionAttr == null ) continue;

			ValidateMetadata( actionAttr, toolType, toolName, method );
			bool wantsSession = ValidateSignature( method, actionAttr, toolType, toolName );

			if ( actions.ContainsKey( actionAttr.Action ) )
			{
				throw new InvalidOperationException(
					$"[McpToolScanner] Duplicate [McpAction(\"{actionAttr.Action}\")] on tool '{toolName}' " +
					$"(method '{method.Name}' conflicts with '{actions[actionAttr.Action].Method.Name}')." );
			}

			actions[actionAttr.Action] = new ScannedAction(
				Action: actionAttr.Action,
				ReadOnly: actionAttr.ReadOnly,
				WriteKind: actionAttr.WriteKind,
				LifecycleKind: actionAttr.LifecycleKind,
				LockKey: actionAttr.LockKey,
				Threading: actionAttr.Threading,
				WantsSession: wantsSession,
				Method: method );
		}

		return actions;
	}

	/// <summary>
	/// Validate that one scanned action declares exactly one safety class. The
	/// default attribute values are deliberately not a class: an omitted
	/// ReadOnly/WriteKind/LifecycleKind is a registration error rather than an
	/// implicit permission decision.
	/// </summary>
	private static void ValidateMetadata(
		McpActionAttribute attr, Type type, string toolName, MethodInfo method )
	{
		var label = $"[McpToolScanner] Tool '{toolName}' action '{attr.Action}' method '{type.FullName}.{method.Name}'";

		bool validWriteKind = attr.WriteKind is "none" or "scene_write" or "file_write" or "scene_write_per_go";
		if ( !validWriteKind )
		{
			throw new InvalidOperationException(
				$"{label} declares unsupported WriteKind='{attr.WriteKind ?? "<null>"}'. " +
				"Valid values: none, scene_write, file_write, scene_write_per_go." );
		}

		bool validLifecycleKind = attr.LifecycleKind is "none" or "lifecycle";
		if ( !validLifecycleKind )
		{
			throw new InvalidOperationException(
				$"{label} declares unsupported LifecycleKind='{attr.LifecycleKind ?? "<null>"}'. " +
				"Valid values: none, lifecycle." );
		}

		if ( !Enum.IsDefined( typeof( LockKeyStrategy ), attr.LockKey ) )
			throw new InvalidOperationException( $"{label} declares unsupported LockKey='{attr.LockKey}'." );

		int classificationCount = (attr.ReadOnly ? 1 : 0)
			+ (attr.WriteKind != "none" ? 1 : 0)
			+ (attr.LifecycleKind != "none" ? 1 : 0);
		if ( classificationCount != 1 )
		{
			throw new InvalidOperationException(
				$"{label} must declare exactly one operation class: ReadOnly=true, a non-none WriteKind, " +
				$"or LifecycleKind=lifecycle (found {classificationCount})." );
		}

		bool keyedWrite = attr.WriteKind == "scene_write_per_go";
		if ( keyedWrite != (attr.LockKey != LockKeyStrategy.None) )
		{
			throw new InvalidOperationException(
				$"{label} must pair WriteKind=scene_write_per_go with a non-None LockKey, and only that " +
				"write kind may declare a LockKey." );
		}
	}

	/// <summary>
	/// Charter §1.2 validation #1 — reject an <see cref="McpActionAttribute"/> on a
	/// method whose declaring type is NOT tagged <see cref="McpToolAttribute"/>.
	/// Called for every non-tool type so the mistake fails loud at scan time.
	/// </summary>
	private static void ValidateNoOrphanActions( Type type )
	{
		foreach ( var method in type.GetMethods( MethodScanFlags ) )
		{
			var actionAttr = method.GetCustomAttribute<McpActionAttribute>( inherit: false );
			if ( actionAttr != null )
			{
				throw new InvalidOperationException(
					$"[McpToolScanner] Method '{type.FullName}.{method.Name}' is tagged " +
					$"[McpAction(\"{actionAttr.Action}\")] but its declaring type is not tagged [McpTool]. " +
					$"Every [McpAction] handler must live on an [McpTool]-decorated class (charter §1.2)." );
			}
		}
	}

	/// <summary>
	/// Validate an [McpAction] handler signature and return whether it wants the
	/// live <see cref="McpSession"/> threaded in as a second argument.
	///
	/// Accepted signatures (charter §1.2, arity extended Wave 3.1 of mcp-dynamic-discovery):
	/// <list type="bullet">
	///   <item>sync : <c>static object Handle*(JsonElement args[, McpSession session])</c></item>
	///   <item>async: <c>static Task&lt;object&gt; Handle*Async(JsonElement args[, McpSession session])</c></item>
	/// </list>
	/// The optional second <see cref="McpSession"/> parameter is for actions that
	/// dispatch sub-calls needing session identity (e.g. <c>batch</c>).
	/// </summary>
	private static bool ValidateSignature( MethodInfo method, McpActionAttribute attr, Type toolType, string toolName )
	{
		var parameters = method.GetParameters();
		var label = $"[McpToolScanner] Tool '{toolName}' action '{attr.Action}' method '{toolType.FullName}.{method.Name}'";

		if ( !method.IsStatic )
		{
			throw new InvalidOperationException(
				$"{label} is an instance method; [McpAction] handlers MUST be static " +
				$"('static object Handle*(JsonElement[, McpSession])' or 'static Task<object> Handle*Async(JsonElement[, McpSession])')." );
		}

		bool firstIsJsonElement = parameters.Length >= 1 && parameters[0].ParameterType == typeof( JsonElement );
		bool arityOk = parameters.Length == 1
			|| ( parameters.Length == 2 && parameters[1].ParameterType == typeof( McpSession ) );

		if ( !firstIsJsonElement || !arityOk )
		{
			throw new InvalidOperationException(
				$"{label} must have signature '(JsonElement args)' or '(JsonElement args, McpSession session)' " +
				$"but found '{FormatParameters( parameters )}'." );
		}

		bool wantsSession = parameters.Length == 2;

		bool isAsync = attr.Threading != McpThreading.MainThread;
		bool returnsTaskOfObject = method.ReturnType == typeof( Task<object> );
		bool returnsObject = method.ReturnType == typeof( object );

		if ( isAsync && !returnsTaskOfObject )
		{
			throw new InvalidOperationException(
				$"{label} declares Threading={attr.Threading} (async) but returns '{method.ReturnType.FullName}' " +
				$"instead of 'System.Threading.Tasks.Task<object>'. Async handlers MUST be 'static Task<object> Handle*Async(JsonElement args)'." );
		}
		if ( !isAsync && !returnsObject )
		{
			throw new InvalidOperationException(
				$"{label} declares Threading=MainThread (sync) but returns '{method.ReturnType.FullName}' " +
				$"instead of 'object'. Sync handlers MUST be 'static object Handle*(JsonElement args)'." );
		}

		return wantsSession;
	}

	private static string FormatParameters( ParameterInfo[] parameters )
	{
		if ( parameters.Length == 0 ) return "()";
		var sb = new StringBuilder( "(" );
		for ( int i = 0; i < parameters.Length; i++ )
		{
			if ( i > 0 ) sb.Append( ", " );
			sb.Append( parameters[i].ParameterType.FullName ).Append( ' ' ).Append( parameters[i].Name );
		}
		return sb.Append( ')' ).ToString();
	}

	private static object InvokeInputSchema( Type toolType, string toolName )
	{
		var schemaMethod = toolType.GetMethod( "InputSchema",
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
			binder: null, types: Type.EmptyTypes, modifiers: null );

		if ( schemaMethod == null )
		{
			throw new InvalidOperationException(
				$"[McpToolScanner] Tool '{toolName}' ({toolType.FullName}) is missing required " +
				$"'internal static object InputSchema()' method. Per charter §1.3, every [McpTool] class " +
				$"must expose a static InputSchema() composing its tools/list inputSchema fragment." );
		}

		if ( schemaMethod.ReturnType != typeof( object ) )
		{
			throw new InvalidOperationException(
				$"[McpToolScanner] Tool '{toolName}' InputSchema() returns '{schemaMethod.ReturnType.FullName}'; " +
				$"required return type is 'object'." );
		}

		try
		{
			return schemaMethod.Invoke( null, null );
		}
		catch ( TargetInvocationException ex )
		{
			throw new InvalidOperationException(
				$"[McpToolScanner] Tool '{toolName}' InputSchema() threw during scan: {ex.InnerException?.Message ?? ex.Message}",
				ex.InnerException ?? ex );
		}
	}

	/// <summary>
	/// Build the <c>tools/list</c> payload shape (array of anonymous objects with
	/// <c>name</c>, <c>description</c>, <c>annotations</c>, <c>inputSchema</c>)
	/// from a scanner result. Output is shape-equivalent to <c>ToolRegistry.All</c>
	/// so the parity test can compare the two byte-for-byte after normalization.
	///
	/// Tool order in the returned array follows insertion order of <paramref name="scanned"/>
	/// (the parity test normalizes by name, so order does not affect hash equality).
	/// </summary>
	internal static object[] BuildToolsListPayload( IReadOnlyDictionary<string, ScannedTool> scanned )
	{
		if ( scanned == null ) throw new ArgumentNullException( nameof( scanned ) );

		var result = new List<object>( scanned.Count );
		foreach ( var tool in scanned.Values )
		{
			if ( tool.ReadOnlyHint || tool.IncludeReadOnlyHint )
			{
				result.Add( new
				{
					name = tool.Name,
					description = tool.Description,
					annotations = new { readOnlyHint = tool.ReadOnlyHint },
					inputSchema = tool.InputSchema,
				} );
			}
			else
			{
				result.Add( new
				{
					name = tool.Name,
					description = tool.Description,
					inputSchema = tool.InputSchema,
				} );
			}
		}
		return result.ToArray();
	}

	/// <summary>
	/// Produce a deterministic JSON string from a <c>tools/list</c>-shape payload
	/// suitable for hashing. Sorts top-level entries by <c>name</c> (case-insensitive)
	/// and writes property names in a stable order via <see cref="JsonSerializer"/>
	/// with default reflection ordering.
	///
	/// Accepts either the legacy <c>ToolRegistry.All</c> array OR
	/// <see cref="BuildToolsListPayload"/> output — both are <c>object[]</c> of
	/// anonymous objects, so reflection-based serialization handles them uniformly.
	/// </summary>
	internal static string NormalizeToolsListPayload( object[] payload )
	{
		if ( payload == null ) throw new ArgumentNullException( nameof( payload ) );

		var options = new JsonSerializerOptions
		{
			WriteIndented = false,
			PropertyNamingPolicy = null,
		};

		var entries = new List<(string name, string json)>( payload.Length );
		foreach ( var entry in payload )
		{
			var name = ExtractName( entry ) ?? "";
			// Serialize via the runtime type — `entry` is statically `object` (an
			// anonymous type at runtime). The (object, Type, options) overload
			// guarantees the anonymous type's properties are emitted instead of an
			// empty `{}`.
			var json = entry == null
				? "null"
				: JsonSerializer.Serialize( entry, entry.GetType(), options );
			entries.Add( (name, json) );
		}
		entries.Sort( ( a, b ) => StringComparer.OrdinalIgnoreCase.Compare( a.name, b.name ) );

		var sb = new StringBuilder();
		sb.Append( '[' );
		for ( int i = 0; i < entries.Count; i++ )
		{
			if ( i > 0 ) sb.Append( ',' );
			sb.Append( entries[i].json );
		}
		sb.Append( ']' );
		return sb.ToString();
	}

	private static string ExtractName( object entry )
	{
		if ( entry == null ) return null;
		var prop = entry.GetType().GetProperty( "name", BindingFlags.Public | BindingFlags.Instance );
		return prop?.GetValue( entry ) as string;
	}
}

/// <summary>
/// One scanned MCP tool — produced by <see cref="McpToolScanner.Scan"/>.
/// <see cref="DeclaringType"/> is the <see cref="McpToolAttribute"/>-decorated
/// class; <see cref="InputSchema"/> is the result of invoking its static
/// <c>InputSchema()</c> at scan time.
/// </summary>
internal sealed record ScannedTool(
	string Name,
	string Description,
	bool ReadOnlyHint,
	bool IncludeReadOnlyHint,
	object InputSchema,
	IReadOnlyDictionary<string, ScannedAction> Actions,
	Type DeclaringType );

/// <summary>
/// One scanned action handler within a tool. <see cref="Method"/> is the
/// <see cref="McpActionAttribute"/>-decorated static method the dispatcher
/// reflection-invokes. <see cref="WantsSession"/> is true when the handler
/// takes a second <see cref="McpSession"/> parameter — the dispatcher threads
/// the live session in (Wave 3.1 of mcp-dynamic-discovery).
/// </summary>
internal sealed record ScannedAction(
	string Action,
	bool ReadOnly,
	string WriteKind,
	string LifecycleKind,
	LockKeyStrategy LockKey,
	McpThreading Threading,
	bool WantsSession,
	MethodInfo Method );

// [HANDOFF-TRUST: mcp-dynamic-discovery Phase 2 — parity machinery reworked from whole-registry hash to per-tool comparison; no handoff hypothesis inherited]

/// <summary>
/// Per-tool parity result for one migrated tool — produced by
/// <see cref="McpToolRegistryParity.Compute"/>. Compares a single scanned
/// tool's normalized tools/list entry against its retained legacy literal
/// (<see cref="ToolRegistry.MigratedToolLiterals"/>).
/// </summary>
internal sealed record ToolParity(
	string Name,
	bool LegacyLiteralPresent,
	bool ScannerFound,
	bool RemovedFromLegacyAll,
	string LegacyHash,
	string ScannerHash )
{
	/// <summary>
	/// True when this tool migrated cleanly: the legacy literal exists, the
	/// scanner found the tool, it is no longer double-listed in
	/// <c>ToolRegistry.All</c>, and the scanned payload hashes identically to
	/// the legacy literal.
	/// </summary>
	public bool Match => LegacyLiteralPresent && ScannerFound
		&& RemovedFromLegacyAll && LegacyHash == ScannerHash;
}

/// <summary>
/// Result of one <see cref="McpToolRegistryParity.Compute"/> run — per-tool
/// parity for every entry in <see cref="ToolRegistry.MigratedToolLiterals"/>,
/// plus the legacy <c>All</c> and scanner tool counts for diagnostics.
/// </summary>
internal sealed record ParityReport(
	int LegacyAllCount,
	int ScannerToolCount,
	int FrozenContractCount,
	int NativeScannerCount,
	IReadOnlyList<ToolParity> MigratedTools,
	IReadOnlyList<string> ActionInventoryMismatches,
	int UnknownActionFailures )
{
	/// <summary>True when final scanner ownership, payload, action, and fail-closed gates all hold.</summary>
	public bool AllMatch
	{
		get
		{
			if ( LegacyAllCount != 0 || FrozenContractCount == 0 ) return false;
			if ( ScannerToolCount < FrozenContractCount ) return false;
			if ( MigratedTools.Count != FrozenContractCount ) return false;
			if ( ActionInventoryMismatches.Count != 0 || UnknownActionFailures != 0 ) return false;
			foreach ( var t in MigratedTools )
				if ( !t.Match ) return false;
			return true;
		}
	}
}

/// <summary>
/// Parity computation for the <c>mcp-dynamic-discovery</c> migration. For each
/// tool already migrated to the [McpTool] scan
/// (<see cref="ToolRegistry.MigratedToolLiterals"/>), hashes the scanner's
/// normalized tools/list entry against the retained legacy literal so the test
/// ConCmd (<c>sbep.test_mcp_tools_list_parity</c>) can assert byte-equivalence
/// at every migration wave.
///
/// Lives in <c>arenula_mcp.editor.dll</c> alongside the scanner and the legacy
/// registry — the test ConCmd is editor-side (<c>Editor/Tests/</c>) because the
/// S&amp;Box game-code whitelist (<c>SB1000</c>) bans <c>System.Reflection</c> /
/// <c>AppDomain</c>, so a game-side ConCmd cannot reach these editor-only types
/// at all. Everything here is same-assembly — no reflection, no whitelist.
/// </summary>
internal static class McpToolRegistryParity
{
	/// <summary>
	/// Compute the per-tool parity report.
	///
	/// Phase 2+ expected result: <see cref="ParityReport.AllMatch"/> is
	/// <c>true</c> — every migrated tool's scanner payload byte-matches its
	/// retained legacy literal. (Phase 1 had no migrated tools, so
	/// <c>MigratedTools</c> was empty and <c>AllMatch</c> was false.)
	/// </summary>
	internal static ParityReport Compute()
	{
		var scanned = McpToolScanner.Scan( typeof( McpToolScanner ).Assembly );
		var scannerPayload = McpToolScanner.BuildToolsListPayload( scanned );

		var scannerByName = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in scannerPayload )
		{
			var n = ToolRegistry.GetStringProperty( entry, "name" );
			if ( n != null ) scannerByName[n] = entry;
		}

		var legacyAll = ToolRegistry.All ?? Array.Empty<object>();
		var legacyAllNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in legacyAll )
		{
			var n = ToolRegistry.GetStringProperty( entry, "name" );
			if ( n != null ) legacyAllNames.Add( n );
		}

		var frozen = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		foreach ( var kvp in GeneratedLegacyToolCatalog.Literals ) frozen.Add( kvp.Key, kvp.Value );
		foreach ( var kvp in ToolRegistry.MigratedToolLiterals ) frozen.Add( kvp.Key, kvp.Value );

		var results = new List<ToolParity>();
		foreach ( var kvp in frozen )
		{
			var name = kvp.Key;
			var literal = kvp.Value;
			bool scannerFound = scannerByName.TryGetValue( name, out var scannerEntry );
			string legacyHash = literal != null
				? Sha256Hex( McpToolScanner.NormalizeToolsListPayload( new[] { literal } ) )
				: "(no-literal)";
			string scannerHash = scannerFound
				? Sha256Hex( McpToolScanner.NormalizeToolsListPayload( new[] { scannerEntry } ) )
				: "(scanner-miss)";
			results.Add( new ToolParity(
				Name: name,
				LegacyLiteralPresent: literal != null,
				ScannerFound: scannerFound,
				RemovedFromLegacyAll: !legacyAllNames.Contains( name ),
				LegacyHash: legacyHash,
				ScannerHash: scannerHash ) );
		}

		var actionMismatches = new List<string>();
		int unknownActionFailures = 0;
		foreach ( var kvp in scanned )
		{
			var tool = kvp.Value;
			var scannerEntry = scannerByName[kvp.Key];
			var schemaActions = new HashSet<string>( ToolRegistry.GetActionNames( scannerEntry ), StringComparer.OrdinalIgnoreCase );
			var attributedActions = new HashSet<string>( tool.Actions.Keys, StringComparer.OrdinalIgnoreCase );
			bool bareTool = attributedActions.Count == 1 && attributedActions.Contains( "" );
			if ( bareTool ) attributedActions.Clear();
			if ( !schemaActions.SetEquals( attributedActions ) )
			{
				actionMismatches.Add( $"{tool.Name}: schema=[{string.Join( ",", schemaActions )}] attrs=[{string.Join( ",", attributedActions )}]" );
			}

			var unknown = ToolRegistry.ResolveOperationMetadata( tool.Name, "__mcp_parity_unknown_action__" );
			if ( !unknown.IsFallback || unknown.Metadata.ReadOnly
				 || unknown.Metadata.WriteKind != "scene_write" || unknown.Metadata.LifecycleKind != "none" )
			{
				unknownActionFailures++;
			}
		}

		int nativeScannerCount = scanned.Keys.Count( name => !frozen.ContainsKey( name ) );
		return new ParityReport(
			legacyAll.Length,
			scanned.Count,
			frozen.Count,
			nativeScannerCount,
			results,
			actionMismatches,
			unknownActionFailures );
	}

	private static string Sha256Hex( string input )
	{
		var hash = SHA256.HashData( Encoding.UTF8.GetBytes( input ?? "" ) );
		var sb = new StringBuilder( hash.Length * 2 );
		for ( int i = 0; i < hash.Length; i++ ) sb.Append( hash[i].ToString( "x2" ) );
		return sb.ToString();
	}
}
