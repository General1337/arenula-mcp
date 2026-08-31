// Editor/Handlers/StepDebuggerHandler.cs
//
// [HANDOFF-TRUST: R3 greenfield deliverable of gravity-hull-instrumentation-suite per docs/ai/plans/gravity-hull-instrumentation-suite.md § R3. Plugin-side handler for the step_debugger MCP tool. It reflects on the game-side SBEP.Debug.StepDebuggerWatcher Component (shipped this session in Code/SBEP/Debug/) and delegates pause/resume/step to the existing TimeHandler (Wave 4.1 SHIPPED). Sibling pattern: PawnHandler's ResolveGameType + InvokeGameBridge — same reason (no AssemblyReference to game ident per feedback_library_assemblyrefs_to_game_ident_creates_type_ambiguity). No behavioral coupling to any inherited gravity-hull H1 — this tool adds diagnostic capability for future bug-fix sessions, it does not fix a hypothesized motion bug today.]
//
// MCP tool 'step_debugger' actions:
//   pause          — delegate to time.pause (Scene.TimeScale=0 AND PhysicsWorld.TimeScale=0).
//   resume         — delegate to time.resume.
//   step           — delegate to time.step (frames=N, scene.GameTick(FixedDelta) loop).
//   snapshot       — one-shot read of watched-body state. Installs a transient
//                    watcher with predicate="always" + HaltOnFire=false, reads the
//                    first-tick Snapshot, destroys the watcher.
//   watch          — set the watched body GameObject path. Stored per-agent.
//   step_until     — install the persistent watcher, poll Done with Task.Delay,
//                    return result envelope. Halts scene on match (predicate fires).
//   watcher_status — list any per-agent watchers still alive (rare — step_until
//                    polls to completion before returning).
//
// PUBLISH-COMPAT: plugin code may use reflection for game-side type lookup per
// engine-fork-guide.md § 6. The watcher class lives in publish-safe Code/SBEP/Debug/
// where it ships in Workshop publish on stock S&Box; this handler is publish-
// EXCLUDED (Libraries/arenula_mcp/**). All time tool delegation is via TimeHandler
// which also lives in Libraries/ — both plugin-only.

// [HANDOFF-TRUST: greenfield R3 — adding using System.Linq to enable .FirstOrDefault() in CreateComponentByType reflection helper; pure compile-fix import-add, independent of any inherited gravity-hull H1.]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

[McpTool( "step_debugger",
	"Deterministic gravity-hull step-debugger. Pause / resume the scene+physics double-halt, step N fixed-ticks, install a predicate-watcher that pauses on match, capture a snapshot of the watched body (player by default). pause/resume/step delegate to the existing time tool — this tool adds the gravity-hull-specific watch/step_until/snapshot surface on top. Predicates v1: 'gravity_axis_velocity < -100', 'mode is WalkInGravityHullMoveMode', 'is_on_ground == 0', 'world_velocity_magnitude > 200', 'world_position_z < -2000', 'always' (debug); plus typed not-yet-available errors for 'ownership_violation' (R2 dependency) and 'blackbox_field' (R1 dependency). The watcher Component lives game-side in Code/SBEP/Debug/StepDebuggerWatcher.cs and is reflectively installed by this plugin. Build R3 of docs/ai/plans/gravity-hull-instrumentation-suite.md (suite charter: gravity-hull-instrumentation-suite)." )]
internal static class StepDebuggerHandler
{
	// ── Per-agent state (mirrors SnapshotHandler pattern) ────────────

	private sealed class StepSession
	{
		public string WatchedGoPath;     // last value set via watch
		public string LastInstalledHostId;
	}

	private static readonly Dictionary<string, StepSession> _byAgent = new( StringComparer.OrdinalIgnoreCase );
	private const string AnonymousBucket = "_anonymous";

	private static string ResolveAgentId( JsonElement args, McpSession session )
	{
		var explicitId = HandlerBase.GetString( args, "agent_id" );
		if ( !string.IsNullOrWhiteSpace( explicitId ) ) return explicitId;
		if ( session != null && !string.IsNullOrWhiteSpace( session.AgentId ) ) return session.AgentId;
		return AnonymousBucket;
	}

	private static StepSession GetOrCreateSession( string agentId )
	{
		if ( !_byAgent.TryGetValue( agentId, out var s ) )
		{
			s = new StepSession();
			_byAgent[agentId] = s;
		}
		return s;
	}

	// ── pause / resume / step — delegate to TimeHandler ──────────────

	[McpAction( "pause", WriteKind = "scene_write" )]
	internal static object HandlePause( JsonElement args )
		=> TimeHandler.Handle( "pause", args );

	[McpAction( "resume", WriteKind = "scene_write" )]
	internal static object HandleResume( JsonElement args )
		=> TimeHandler.Handle( "resume", args );

	[McpAction( "step", WriteKind = "scene_write" )]
	internal static object HandleStep( JsonElement args )
		=> TimeHandler.Handle( "step", args );

	// ── watch — set per-agent watched body path ──────────────────────

	[McpAction( "watch", WriteKind = "scene_write" )]
	internal static object HandleWatch( JsonElement args, McpSession session )
	{
		var agentId = ResolveAgentId( args, session );
		var bodyPath = HandlerBase.GetString( args, "body_path" );
		GetOrCreateSession( agentId ).WatchedGoPath = bodyPath;
		return HandlerBase.Success( new
		{
			agentId,
			watched_body_path = bodyPath ?? "<local-player-default>",
			note = "Stored per-agent. Used as default body resolution for subsequent snapshot/step_until calls (override per-call with body_path arg)."
		} );
	}

	// ── snapshot — transient watcher with 'always' predicate ─────────

	[McpAction( "snapshot", WriteKind = "scene_write" )]
	internal static object HandleSnapshot( JsonElement args, McpSession session )
	{
		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene. Start play mode first.", "snapshot" );

		var watcherType = ResolveGameType( "SBEP.Debug.StepDebuggerWatcher" );
		if ( watcherType == null )
			return HandlerBase.Error( "SBEP.Debug.StepDebuggerWatcher type not found in loaded assemblies. Ensure Code/SBEP/Debug/StepDebuggerWatcher.cs compiled and the editor has hot-reloaded the game code.", "snapshot" );

		var agentId = ResolveAgentId( args, session );
		var bodyPath = HandlerBase.GetString( args, "body_path" ) ?? GetOrCreateSession( agentId ).WatchedGoPath;
		var watchedBody = ResolveWatchedBody( scene, bodyPath );

		// Install transient watcher: predicate "always" (fires first tick), HaltOnFire=false.
		var host = scene.CreateObject();
		host.Name = $"__step_debugger_snapshot_{DateTime.UtcNow:HHmmssfff}";
		host.Tags.Add( "editor_internal" );

		object watcher;
		try
		{
			watcher = CreateComponentByType( host, watcherType );
			if ( watcher == null )
				return CleanupAndFail( host, "Components.Create<StepDebuggerWatcher>() returned null via reflection.", "snapshot" );

			SetWatcherProperty( watcher, watcherType, "ExpressionText", "always" );
			SetWatcherProperty( watcher, watcherType, "MaxTicks", 1 );
			SetWatcherProperty( watcher, watcherType, "HaltOnFire", false );
			SetWatcherProperty( watcher, watcherType, "WatchedBody", watchedBody );
			SetWatcherProperty( watcher, watcherType, "InstallerTag", $"mcp-snapshot:{agentId}" );
		}
		catch ( Exception ex )
		{
			return CleanupAndFail( host, $"Watcher install threw {ex.GetType().Name}: {ex.Message}", "snapshot" );
		}

		// Snapshot doesn't busy-wait — caller invokes again if the first poll
		// hasn't yet seen a tick (the watcher runs on OnFixedUpdate and may
		// take 1-2 frames to fire). We do a single short wait here for
		// convenience; full pump semantics live in step_until.
		var snapshot = PollWatcherOnce( watcher, watcherType, host, 250 ).GetAwaiter().GetResult();
		var result = BuildSnapshotResult( watcher, watcherType, snapshot, agentId );
		try { if ( host.IsValid() ) host.Destroy(); } catch { }
		return HandlerBase.Success( result );
	}

	// ── step_until — install persistent watcher, poll Done ───────────

	[McpAction( "step_until", WriteKind = "scene_write", Threading = McpThreading.AsyncBeforeMainThread )]
	internal static async Task<object> HandleStepUntilAsync( JsonElement args, McpSession session )
	{
		var expr = HandlerBase.GetString( args, "expr" );
		var maxTicks = Math.Clamp( HandlerBase.GetInt( args, "max", 500 ), 1, 100_000 );
		var wallClockCapMs = Math.Clamp( HandlerBase.GetInt( args, "wall_clock_cap_ms", 15_000 ), 100, 600_000 );
		var pollIntervalMs = Math.Clamp( HandlerBase.GetInt( args, "poll_interval_ms", 100 ), 25, 5_000 );
		var haltOnFire = HandlerBase.GetBool( args, "halt_on_fire", true );

		if ( string.IsNullOrWhiteSpace( expr ) )
			return HandlerBase.Error( "Missing required 'expr' parameter (the predicate text).", "step_until" );

		await GameTask.MainThread();

		var scene = ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene. Start play mode first.", "step_until" );

		var watcherType = ResolveGameType( "SBEP.Debug.StepDebuggerWatcher" );
		if ( watcherType == null )
			return HandlerBase.Error( "SBEP.Debug.StepDebuggerWatcher type not found in loaded assemblies. Ensure Code/SBEP/Debug/StepDebuggerWatcher.cs compiled and the editor has hot-reloaded the game code.", "step_until" );

		var agentId = ResolveAgentId( args, session );
		var bodyPath = HandlerBase.GetString( args, "body_path" ) ?? GetOrCreateSession( agentId ).WatchedGoPath;
		var watchedBody = ResolveWatchedBody( scene, bodyPath );

		var host = scene.CreateObject();
		host.Name = $"__step_debugger_step_until_{DateTime.UtcNow:HHmmssfff}";
		host.Tags.Add( "editor_internal" );

		object watcher;
		try
		{
			watcher = CreateComponentByType( host, watcherType );
			if ( watcher == null )
				return CleanupAndFail( host, "Components.Create<StepDebuggerWatcher>() returned null via reflection.", "step_until" );

			SetWatcherProperty( watcher, watcherType, "ExpressionText", expr );
			SetWatcherProperty( watcher, watcherType, "MaxTicks", maxTicks );
			SetWatcherProperty( watcher, watcherType, "HaltOnFire", haltOnFire );
			SetWatcherProperty( watcher, watcherType, "WatchedBody", watchedBody );
			SetWatcherProperty( watcher, watcherType, "InstallerTag", $"mcp:{agentId}" );
		}
		catch ( Exception ex )
		{
			return CleanupAndFail( host, $"Watcher install threw {ex.GetType().Name}: {ex.Message}", "step_until" );
		}

		GetOrCreateSession( agentId ).LastInstalledHostId = host.Id.ToString();

		var deadline = DateTime.UtcNow.AddMilliseconds( wallClockCapMs );
		while ( DateTime.UtcNow < deadline )
		{
			await Task.Delay( pollIntervalMs );
			await GameTask.MainThread();

			if ( !ComponentIsValid( watcher ) ) break;
			if ( GetWatcherBool( watcher, watcherType, "Done" ) ) break;
		}

		var resultEnvelope = BuildStepUntilResult( watcher, watcherType, host, agentId, expr, maxTicks, wallClockCapMs );

		try { if ( host.IsValid() ) host.Destroy(); } catch { }

		return HandlerBase.Success( resultEnvelope );
	}

	// ── watcher_status — list per-agent watchers still alive ─────────

	[McpAction( "watcher_status", ReadOnly = true )]
	internal static object HandleWatcherStatus( JsonElement args, McpSession session )
	{
		var agentId = ResolveAgentId( args, session );
		var sess = GetOrCreateSession( agentId );
		// step_until polls to completion before returning, so a non-empty
		// LastInstalledHostId is informational only — the host may already be
		// destroyed.
		return HandlerBase.Success( new
		{
			agentId,
			watched_body_path = sess.WatchedGoPath ?? "<local-player-default>",
			last_installed_host_id = sess.LastInstalledHostId ?? "<none>",
			note = "step_until is synchronous-on-completion (it polls Done before returning). watcher_status is for diagnostics only — a non-empty last_installed_host_id does NOT imply a live host."
		} );
	}

	// ── InputSchema (verbatim mirror in ToolRegistry.MigratedToolLiterals) ─

	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>
		{
			["action"] = new
			{
				type = "string",
				description = "The operation to perform.",
				@enum = new[] { "pause", "resume", "step", "snapshot", "watch", "step_until", "watcher_status" }
			},
			["expr"] = new
			{
				type = "string",
				description = "Predicate expression for step_until. Forms: '<field> <op> <number>' (e.g. 'gravity_axis_velocity < -100'); 'mode is <TypeName>' / 'mode != <TypeName>'; 'always' (debug, fires first tick); 'ownership_violation' (R2 not-yet-available); 'blackbox_field <field> <op> <number>' (R1 not-yet-available). Fields: gravity_axis_velocity, world_velocity_z, world_velocity_magnitude, world_position_z, is_on_ground, active_hull_id_int. Ops: < <= > >= == != = (= treated as ==)."
			},
			["max"] = new
			{
				type = "integer",
				minimum = 1,
				maximum = 100000,
				@default = 500,
				description = "step_until: max ticks before giving up with fired=false. Default 500."
			},
			["wall_clock_cap_ms"] = new
			{
				type = "integer",
				minimum = 100,
				maximum = 600000,
				@default = 15000,
				description = "step_until: max wall-clock time to wait before returning (covers max-ticks * fixed-delta + scheduling jitter)."
			},
			["poll_interval_ms"] = new
			{
				type = "integer",
				minimum = 25,
				maximum = 5000,
				@default = 100,
				description = "step_until: how often to check watcher.Done while waiting."
			},
			["halt_on_fire"] = new
			{
				type = "boolean",
				@default = true,
				description = "step_until: when true (default), set Scene.TimeScale and PhysicsWorld.TimeScale to 0 on predicate match. Set false to capture snapshot without freezing."
			},
			["body_path"] = new
			{
				type = "string",
				description = "GameObject name to use as the watched body. When omitted, falls back to the per-agent watch() value or the first enabled PlayerController in scene."
			},
			["frames"] = new
			{
				type = "integer",
				minimum = 1,
				maximum = 240,
				@default = 1,
				description = "step action only: number of Scene.GameTick(FixedDelta) calls. Delegated to the existing time tool."
			},
			["agent_id"] = new
			{
				type = "string",
				description = "Override the session agent id (rarely used — for supervisor flows that need to target another agent's watch state)."
			}
		},
		required = new[] { "action" },
		additionalProperties = false
	};

	// ── Helpers: scene / type / component reflection ─────────────────

	private static Scene ResolveScene()
	{
		var activeScene = Game.ActiveScene;
		if ( activeScene != null ) return activeScene;
		var all = Scene.All;
		if ( all != null )
		{
			foreach ( var sc in all )
				if ( sc != null ) return sc;
		}
		return null;
	}

	private static Type ResolveGameType( string typeName )
	{
		var scene = Game.ActiveScene;
		if ( scene != null )
		{
			foreach ( var comp in scene.GetAllComponents<Component>() )
			{
				if ( !comp.IsValid() ) continue;
				Type t;
				try { t = comp.GetType().Assembly.GetType( typeName, throwOnError: false ); }
				catch ( Exception ) { continue; }
				if ( t != null ) return t;
			}
		}

		Type fallback = null;
		foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
		{
			try
			{
				var t = asm.GetType( typeName, throwOnError: false );
				if ( t != null ) fallback = t;
			}
			catch ( Exception ) { continue; }
		}
		return fallback;
	}

	private static GameObject ResolveWatchedBody( Scene scene, string bodyPath )
	{
		if ( string.IsNullOrWhiteSpace( bodyPath ) ) return null;
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( string.Equals( go.Name, bodyPath, StringComparison.Ordinal ) ) return go;
		}
		return null;
	}

	// [HANDOFF-TRUST: R3 reflection fix — Phase-4 first run hit InvalidOperationException because GameObject.Components.Create<T>() requires a static-known type parameter; the runtime-only path goes through Sandbox.TypeLibrary (TypeDescription -> go.Components.Create(td)). Sibling pattern: ComponentHandler.FindComponentTypeDescription + Add. Independent of inherited gravity-hull H1.]
	private static object CreateComponentByType( GameObject host, Type componentType )
	{
		// Game-side Components.Create accepts a TypeDescription, not a runtime Type.
		// Walk TypeLibrary to find the TypeDescription whose TargetType matches.
		// [HANDOFF-TRUST: drop Sandbox.Internal qualifier — TypeDescription is at namespace Sandbox via using directive; compile fix unrelated to gravity-hull H1.]
		TypeDescription td = null;
		foreach ( var candidate in TypeLibrary.GetTypes<Component>() )
		{
			if ( candidate.TargetType == componentType ) { td = candidate; break; }
		}
		if ( td == null )
			throw new InvalidOperationException( $"No TypeDescription found for component type '{componentType.FullName}'." );
		return host.Components.Create( td );
	}

	private static void SetWatcherProperty( object watcher, Type watcherType, string propName, object value )
	{
		var prop = watcherType.GetProperty( propName, BindingFlags.Public | BindingFlags.Instance );
		if ( prop == null )
			throw new InvalidOperationException( $"StepDebuggerWatcher has no public property '{propName}'." );
		if ( !prop.CanWrite )
			throw new InvalidOperationException( $"StepDebuggerWatcher property '{propName}' is read-only." );
		prop.SetValue( watcher, value );
	}

	private static object GetWatcherProperty( object watcher, Type watcherType, string propName )
	{
		var prop = watcherType.GetProperty( propName, BindingFlags.Public | BindingFlags.Instance );
		return prop?.GetValue( watcher );
	}

	private static bool GetWatcherBool( object watcher, Type watcherType, string propName )
	{
		var v = GetWatcherProperty( watcher, watcherType, propName );
		return v is bool b && b;
	}

	private static int GetWatcherInt( object watcher, Type watcherType, string propName )
	{
		var v = GetWatcherProperty( watcher, watcherType, propName );
		return v is int i ? i : 0;
	}

	private static string GetWatcherString( object watcher, Type watcherType, string propName )
	{
		var v = GetWatcherProperty( watcher, watcherType, propName );
		return v as string;
	}

	private static bool ComponentIsValid( object component )
	{
		if ( component is Component c ) return c.IsValid();
		return component != null;
	}

	private static async Task<object> PollWatcherOnce( object watcher, Type watcherType, GameObject host, int maxWaitMs )
	{
		var deadline = DateTime.UtcNow.AddMilliseconds( maxWaitMs );
		while ( DateTime.UtcNow < deadline )
		{
			await Task.Delay( 50 );
			await GameTask.MainThread();
			if ( !ComponentIsValid( watcher ) ) return null;
			if ( GetWatcherBool( watcher, watcherType, "Done" ) )
				return GetWatcherProperty( watcher, watcherType, "Snapshot" );
		}
		return GetWatcherProperty( watcher, watcherType, "Snapshot" );
	}

	private static object BuildSnapshotResult( object watcher, Type watcherType, object snapshotObj, string agentId )
	{
		return new
		{
			agentId,
			done = GetWatcherBool( watcher, watcherType, "Done" ),
			fired = GetWatcherBool( watcher, watcherType, "Fired" ),
			ticks_run = GetWatcherInt( watcher, watcherType, "TicksRun" ),
			parse_error = GetWatcherString( watcher, watcherType, "ParseError" ),
			last_eval_error = GetWatcherString( watcher, watcherType, "LastEvalError" ),
			snapshot = SnapshotToWire( snapshotObj ),
		};
	}

	private static object BuildStepUntilResult( object watcher, Type watcherType, GameObject host, string agentId,
		string expr, int maxTicks, int wallClockCapMs )
	{
		var snapshotObj = ComponentIsValid( watcher ) ? GetWatcherProperty( watcher, watcherType, "Snapshot" ) : null;
		return new
		{
			agentId,
			expression_text = expr,
			max_ticks = maxTicks,
			wall_clock_cap_ms = wallClockCapMs,
			done = ComponentIsValid( watcher ) && GetWatcherBool( watcher, watcherType, "Done" ),
			fired = ComponentIsValid( watcher ) && GetWatcherBool( watcher, watcherType, "Fired" ),
			ticks_run = ComponentIsValid( watcher ) ? GetWatcherInt( watcher, watcherType, "TicksRun" ) : 0,
			parse_error = ComponentIsValid( watcher ) ? GetWatcherString( watcher, watcherType, "ParseError" ) : null,
			last_eval_error = ComponentIsValid( watcher ) ? GetWatcherString( watcher, watcherType, "LastEvalError" ) : null,
			host_id = host?.Id.ToString(),
			snapshot = SnapshotToWire( snapshotObj ),
		};
	}

	/// <summary>
	/// Convert the game-side StepDebuggerSnapshot into a wire-shape anonymous
	/// record. Uses reflection to invoke the snapshot's own ToWire() method —
	/// that keeps the wire schema as game-side SSOT.
	/// </summary>
	private static object SnapshotToWire( object snapshotObj )
	{
		if ( snapshotObj == null ) return null;
		try
		{
			var toWire = snapshotObj.GetType().GetMethod( "ToWire", BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null );
			if ( toWire != null && toWire.ReturnType == typeof( object ) )
				return toWire.Invoke( snapshotObj, null );
		}
		catch ( Exception ex )
		{
			return new { wire_error = $"ToWire reflection threw {ex.GetType().Name}: {ex.Message}" };
		}
		// Fallback: stringified ToString.
		return new { wire_error = "snapshot.ToWire() not found", to_string = snapshotObj.ToString() };
	}

	private static object CleanupAndFail( GameObject host, string message, string action )
	{
		try { if ( host.IsValid() ) host.Destroy(); } catch { }
		return HandlerBase.Error( message, action );
	}
}
