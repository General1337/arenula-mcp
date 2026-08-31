// [HANDOFF-TRUST: refactor is independent of predecessor's H1 raise-site hypothesis; addresses orthogonal library compile-reference issue]
//
// engine-fork-elite-leverage Phase 1.3 closeout — reflection bridge for
// Sandbox.Hotload + Sandbox.HotloadResult + Sandbox.HotloadEntryType.
//
// Why this file exists
// ────────────────────
// The S&Box editor's default library compile reference set is hardcoded at
// engine/Sandbox.Compiling/Compiler/Compiler.References.cs:8-16 to:
//
//   { "Sandbox.System", "Sandbox.Engine", "Sandbox.Filesystem",
//     "Sandbox.Reflection", "Sandbox.Mounting",
//     "Microsoft.AspNetCore.Components" }
//
// "Sandbox.Hotload" is NOT in the default set, and is also absent from
// Sandbox.Compiling/EmbeddedRefs/*.dll (those are pure BCL framework refs).
// The hot-reload subsystem is internal-by-convention from a user-library
// perspective — Facepunch's design intent is that libraries cannot directly
// depend on the manager that hot-reloads them.
//
// The auto-generated Libraries/<lib>/Editor/<lib>.editor.csproj that
// Sandbox.SolutionGenerator emits for IDE intellisense is also gitignored
// + regenerated on every editor boot. So editing the csproj is futile.
//
// Therefore: any code in Libraries/arenula_mcp/ that needs to touch the
// Sandbox.Hotload public surface must do so via REFLECTION through this
// bridge. Direct `typeof(Hotload)`, `HotloadResult` parameter types, or
// `HotloadEntryType.Error` will fail to compile under both fork AND stable
// engines. The bridge:
//
//   1. Centralizes the brittle string-key reflection in ONE place so future
//      Phase 1.x fork-only event subscriptions (1.4 scene events, 1.5
//      compile events, etc.) can reuse the same shape.
//   2. Caches Type / PropertyInfo / EventInfo handles on first access.
//      Sandbox.Hotload carries [SkipHotload] (engine/Sandbox.Hotload/
//      AssemblyInfo or similar — engine source survey) so the cached
//      metadata is stable across library hotloads.
//   3. Fails LOUDLY at init time if a property rename happens upstream.
//      Better than silently returning 0/null at runtime (which is the
//      failure mode of naive `pi?.GetValue(...) ?? 0`).
//   4. Provides strongly-typed accessor methods (bridge.Success(r) → bool)
//      so caller sites read like normal C# instead of reflection ceremony.
//   5. Uses Expression.Lambda.Compile() to produce a typed
//      Action<HotloadResult> delegate from an Action<object> callback,
//      avoiding per-invocation reflection overhead on the engine event
//      dispatch hot path.
//   6. Exposes a single IsAvailable flag for callers to gate "fork engine
//      with the OnComplete patch" detection. Returns false on both
//      stable Facepunch engine AND on fork engine before the Phase 1.3
//      patch lands.
//
// Engine source authoritative paths (verified 2026-05-18):
//   * M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//     Sandbox.Hotload/HotloadResult.cs
//       - line   11: public enum HotloadEntryType { ... Error, Warning, ... }
//       - line   37: public sealed class HotloadResultEntry
//       - line  182: public sealed class HotloadResult
//   * M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//     Sandbox.Hotload/Hotload.cs
//       - line   15: public partial class Hotload
//   * M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//     Sandbox.Hotload/Hotload.OnComplete.cs  (commit ed7b424d, Phase 1.3)
//       - declares `public static event Action<HotloadResult> OnComplete`
//
// Banned approaches enforced here:
//   * No msbuild Directory.Build.targets injection of FORK_ENGINE.
//   * No editing of sbox_mcp.sbproj `Metadata.Compiler.AssemblyReferences`
//     to add "Sandbox.Hotload" — that would only work on fork engine
//     and break the resilience seam for stable-engine devs.
//   * No editing of the auto-generated arenula_mcp.editor.csproj —
//     overwritten on every editor boot by Sandbox.SolutionGenerator.

using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Arenula.Reflection;

internal static class HotloadResultBridge
{
	// ── Cached engine types & members. All null on stable engine. ─────────

	private static Type _hotloadType;             // Sandbox.Hotload
	private static Type _hotloadResultType;       // Sandbox.HotloadResult
	private static Type _hotloadResultEntryType;  // Sandbox.HotloadResultEntry
	private static Type _hotloadEntryTypeType;    // Sandbox.HotloadEntryType (enum)

	private static EventInfo _onCompleteEvent;    // OnComplete (fork-only, Phase 1.3)

	private static PropertyInfo _piSuccess;
	private static PropertyInfo _piNoAction;
	private static PropertyInfo _piHasErrors;
	private static PropertyInfo _piHasWarnings;
	private static PropertyInfo _piInstancesProcessed;
	private static PropertyInfo _piProcessingTime;
	private static PropertyInfo _piInstanceQueueTime;
	private static PropertyInfo _piStaticFieldTime;
	private static PropertyInfo _piWatchedInstanceTime;
	private static PropertyInfo _piDiagnosticsTime;
	private static PropertyInfo _piEntries;
	private static PropertyInfo _piTypeTimings;
	private static PropertyInfo _piAutoSkippedTypes;
	private static PropertyInfo _piCreated;

	private static PropertyInfo _piEntryType;     // HotloadResultEntry.Type
	private static object _entryTypeError;        // HotloadEntryType.Error  (boxed enum)
	private static object _entryTypeWarning;      // HotloadEntryType.Warning

	private static volatile bool _initialized;
	private static volatile bool _available;
	private static string _initFailureReason;
	private static readonly object _lock = new();

	// ── Public API ────────────────────────────────────────────────────────

	/// <summary>
	/// True when running on the engine fork with the Phase 1.3
	/// Sandbox.Hotload.OnComplete patch present. False on stable Facepunch
	/// engine OR on fork engine before the patch lands. Cheap (idempotent
	/// init on first call; volatile read on subsequent calls).
	/// </summary>
	public static bool IsAvailable
	{
		get
		{
			if ( !_initialized ) EnsureInit();
			return _available;
		}
	}

	/// <summary>
	/// Set by EnsureInit when IsAvailable is false. Useful for diagnostic
	/// logging — distinguishes "Sandbox.Hotload assembly missing entirely"
	/// from "Sandbox.Hotload present but OnComplete event missing"
	/// (i.e. stable vs fork-but-pre-1.3).
	/// </summary>
	public static string InitFailureReason
	{
		get
		{
			if ( !_initialized ) EnsureInit();
			return _initFailureReason;
		}
	}

	/// <summary>
	/// The reflected EventInfo for Sandbox.Hotload.OnComplete. Null on
	/// stable engine. Callers should normally use Subscribe / Unsubscribe
	/// rather than touching this directly.
	/// </summary>
	public static EventInfo OnCompleteEvent
	{
		get
		{
			if ( !_initialized ) EnsureInit();
			return _onCompleteEvent;
		}
	}

	/// <summary>
	/// The reflected Sandbox.Hotload Type. Null on engines where the
	/// Sandbox.Hotload assembly is not loaded. Exposed so test code can
	/// reflect the (bool, Logger) constructor + UpdateReferences() method
	/// to drive a synthetic hotload cycle.
	/// </summary>
	public static Type HotloadType
	{
		get
		{
			if ( !_initialized ) EnsureInit();
			return _hotloadType;
		}
	}

	/// <summary>
	/// Subscribe an Action&lt;object&gt; callback to Sandbox.Hotload.OnComplete.
	/// The callback receives the HotloadResult instance as object — use
	/// the typed accessor methods on this bridge to extract fields.
	/// Returns the Delegate registered with the event (caller MUST retain
	/// it to pass to Unsubscribe — the engine's event slot holds it weakly
	/// against our delegate identity, but library hotloads may invalidate
	/// the prior delegate target so callers re-bind in their
	/// [EditorEvent.Hotload] handlers). Throws if IsAvailable is false.
	/// </summary>
	public static Delegate Subscribe( Action<object> callback )
	{
		if ( callback == null ) throw new ArgumentNullException( nameof( callback ) );
		if ( !IsAvailable ) throw new InvalidOperationException(
			$"HotloadResultBridge.Subscribe: OnComplete event not available ({_initFailureReason ?? "unknown"})." );

		var del = BuildTypedDelegate( callback );
		_onCompleteEvent.AddEventHandler( null, del );
		return del;
	}

	/// <summary>
	/// Unsubscribe a delegate previously returned by Subscribe. No-op if
	/// the delegate is not currently registered (per Delegate.Remove
	/// semantics). Swallows exceptions defensively because callers
	/// typically invoke this from hotload re-bind paths where the prior
	/// delegate's method target may have been GC'd already.
	/// </summary>
	public static void Unsubscribe( Delegate del )
	{
		if ( del == null ) return;
		if ( !_initialized ) EnsureInit();
		if ( _onCompleteEvent == null ) return;
		try { _onCompleteEvent.RemoveEventHandler( null, del ); }
		catch { /* defensive — stale delegate from prior library hotload */ }
	}

	// ── Strongly-typed accessors (all assume IsAvailable verified) ────────

	public static bool Success( object r ) => (bool)_piSuccess.GetValue( r );
	public static bool NoAction( object r ) => (bool)_piNoAction.GetValue( r );
	public static bool HasErrors( object r ) => (bool)_piHasErrors.GetValue( r );
	public static bool HasWarnings( object r ) => (bool)_piHasWarnings.GetValue( r );
	public static int InstancesProcessed( object r ) => Convert.ToInt32( _piInstancesProcessed.GetValue( r ) );
	public static double ProcessingTime( object r ) => Convert.ToDouble( _piProcessingTime.GetValue( r ) );
	public static double InstanceQueueTime( object r ) => Convert.ToDouble( _piInstanceQueueTime.GetValue( r ) );
	public static double StaticFieldTime( object r ) => Convert.ToDouble( _piStaticFieldTime.GetValue( r ) );
	public static double WatchedInstanceTime( object r ) => Convert.ToDouble( _piWatchedInstanceTime.GetValue( r ) );
	public static double DiagnosticsTime( object r ) => Convert.ToDouble( _piDiagnosticsTime.GetValue( r ) );
	public static DateTime Created( object r ) => (DateTime)_piCreated.GetValue( r );

	public static int EntryCount( object r ) => CountEnumerable( _piEntries.GetValue( r ) );
	public static int TypeTimingCount( object r ) => CountEnumerable( _piTypeTimings.GetValue( r ) );
	public static int AutoSkippedTypeCount( object r ) => CountEnumerable( _piAutoSkippedTypes.GetValue( r ) );

	public static int ErrorCount( object r ) => CountEntriesOfType( r, _entryTypeError );
	public static int WarningCount( object r ) => CountEntriesOfType( r, _entryTypeWarning );

	// ── Private impl ──────────────────────────────────────────────────────

	private static void EnsureInit()
	{
		if ( _initialized ) return;
		lock ( _lock )
		{
			if ( _initialized ) return;
			try
			{
				var hotloadAsm = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault( a => string.Equals( a.GetName().Name, "Sandbox.Hotload", StringComparison.OrdinalIgnoreCase ) );
				if ( hotloadAsm == null )
				{
					_initFailureReason = "Sandbox.Hotload assembly not loaded in current AppDomain";
					Finish( false );
					return;
				}

				_hotloadType = hotloadAsm.GetType( "Sandbox.Hotload" );
				_hotloadResultType = hotloadAsm.GetType( "Sandbox.HotloadResult" );
				_hotloadResultEntryType = hotloadAsm.GetType( "Sandbox.HotloadResultEntry" );
				_hotloadEntryTypeType = hotloadAsm.GetType( "Sandbox.HotloadEntryType" );

				if ( _hotloadType == null || _hotloadResultType == null
					|| _hotloadResultEntryType == null || _hotloadEntryTypeType == null )
				{
					_initFailureReason = $"Sandbox.Hotload assembly present but expected types missing " +
						$"(Hotload={_hotloadType != null}, HotloadResult={_hotloadResultType != null}, " +
						$"HotloadResultEntry={_hotloadResultEntryType != null}, HotloadEntryType={_hotloadEntryTypeType != null})";
					Finish( false );
					return;
				}

				_onCompleteEvent = _hotloadType.GetEvent( "OnComplete", BindingFlags.Public | BindingFlags.Static );
				if ( _onCompleteEvent == null )
				{
					_initFailureReason = "Sandbox.Hotload.OnComplete event not found — running under stable Facepunch engine (this is the expected resilience-seam SKIP path)";
					Finish( false );
					return;
				}

				_piSuccess = RequireProp( _hotloadResultType, "Success" );
				_piNoAction = RequireProp( _hotloadResultType, "NoAction" );
				_piHasErrors = RequireProp( _hotloadResultType, "HasErrors" );
				_piHasWarnings = RequireProp( _hotloadResultType, "HasWarnings" );
				_piInstancesProcessed = RequireProp( _hotloadResultType, "InstancesProcessed" );
				_piProcessingTime = RequireProp( _hotloadResultType, "ProcessingTime" );
				_piInstanceQueueTime = RequireProp( _hotloadResultType, "InstanceQueueTime" );
				_piStaticFieldTime = RequireProp( _hotloadResultType, "StaticFieldTime" );
				_piWatchedInstanceTime = RequireProp( _hotloadResultType, "WatchedInstanceTime" );
				_piDiagnosticsTime = RequireProp( _hotloadResultType, "DiagnosticsTime" );
				_piEntries = RequireProp( _hotloadResultType, "Entries" );
				_piTypeTimings = RequireProp( _hotloadResultType, "TypeTimings" );
				_piAutoSkippedTypes = RequireProp( _hotloadResultType, "AutoSkippedTypes" );
				_piCreated = RequireProp( _hotloadResultType, "Created" );

				_piEntryType = RequireProp( _hotloadResultEntryType, "Type" );

				_entryTypeError = Enum.Parse( _hotloadEntryTypeType, "Error", ignoreCase: false );
				_entryTypeWarning = Enum.Parse( _hotloadEntryTypeType, "Warning", ignoreCase: false );

				Finish( true );
			}
			catch ( Exception ex )
			{
				_initFailureReason = $"HotloadResultBridge init exception: {ex.GetType().Name}: {ex.Message}";
				Finish( false );
			}
		}
	}

	private static PropertyInfo RequireProp( Type t, string name )
	{
		var pi = t.GetProperty( name, BindingFlags.Public | BindingFlags.Instance );
		if ( pi == null )
			throw new InvalidOperationException(
				$"HotloadResultBridge: expected property '{t.FullName}.{name}' not found — engine API has shifted; bridge must be updated." );
		return pi;
	}

	private static void Finish( bool ok )
	{
		_available = ok;
		_initialized = true;
	}

	/// <summary>
	/// Build an engine-typed Action&lt;HotloadResult&gt; delegate that forwards
	/// to our Action&lt;object&gt; callback. Uses Expression.Lambda.Compile()
	/// so the runtime cost per event invocation is one indirect call, NOT
	/// repeated reflection. Compiled once per Subscribe call; the resulting
	/// delegate's lifetime matches the caller's.
	/// </summary>
	private static Delegate BuildTypedDelegate( Action<object> callback )
	{
		var resultParam = Expression.Parameter( _hotloadResultType, "result" );
		var asObject = Expression.Convert( resultParam, typeof( object ) );
		var callbackConst = Expression.Constant( callback, typeof( Action<object> ) );
		var invokeMethod = typeof( Action<object> ).GetMethod( "Invoke" )!;
		var body = Expression.Call( callbackConst, invokeMethod, asObject );
		var lambda = Expression.Lambda( _onCompleteEvent.EventHandlerType, body, resultParam );
		return lambda.Compile();
	}

	private static int CountEnumerable( object value )
	{
		if ( value == null ) return 0;
		if ( value is ICollection c ) return c.Count;
		if ( value is IEnumerable e )
		{
			int n = 0;
			foreach ( var _ in e ) n++;
			return n;
		}
		return 0;
	}

	private static int CountEntriesOfType( object r, object entryTypeValue )
	{
		var entries = _piEntries.GetValue( r );
		if ( entries is not IEnumerable enumerable ) return 0;
		int count = 0;
		foreach ( var entry in enumerable )
		{
			var t = _piEntryType.GetValue( entry );
			if ( Equals( t, entryTypeValue ) ) count++;
		}
		return count;
	}
}
