// Editor/Handlers/QaHandler.cs
//
// [HANDOFF-TRUST: greenfield Layer-1 / P1.3 of the autonomous-qa-verifier
// initiative — NEW MCP tool, no bug-fix hypothesis inherited from any handoff.
// This handler is the editor-side surface of the `qa` tool; it reflection-bridges
// to the game-side SBEP.Tests.QaSuiteBridge using the proven InvokeGameBridge /
// ResolveGameType pattern copied from PawnHandler (the editor library cannot
// AssemblyReference the game ident without type ambiguity — memory
// feedback_library_assemblyrefs_to_game_ident_creates_type_ambiguity). It
// introduces no engine-internal symbols.]
//
// MCP handler for the 'qa' tool namespace — the autonomous QA verifier. Actions:
//   qa.run_suite        — run a named suite (or CSV of ConCmd names) result-driven;
//                         returns one structured per-gate verdict per test.
//   qa.get_verdicts     — read recorded verdicts from the in-memory registry.
//   qa.rerun_test       — re-run one test standalone; returns its verdict.
//   qa.measure_variance — run a test N times; classify stable-pass / stable-fail /
//                         flaky (the flaky-vs-real-failure proof).
//   qa.bisect_pollution — binary-search a suite for the test that pollutes a victim.
//   qa.list_suites      — enumerate the named suites and their test lists.
//
// run_suite / rerun_test / measure_variance / bisect_pollution dispatch play-mode
// test ConCmds and are async (they await QaSuiteRunner's result-driven completion).
// get_verdicts / list_suites are pure reads. The game-side bridge is async for the
// first four — InvokeBridge awaits a Task<string> when one is returned and a plain
// string otherwise.

using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

internal static class QaHandler
{
	private const string BridgeType = "SBEP.Tests.QaSuiteBridge";

	internal static async Task<object> HandleAsync( string action, JsonElement args, McpSession session = null )
	{
		try
		{
			// Get onto the main thread before touching scene / game state. The
			// game-side bridge starts an async chain that polls the verdict
			// registry via GameTask continuations (main-thread).
			await GameTask.MainThread();

			return action switch
			{
				// Test-dispatching actions are serialized through QaRunGate
				// (mcp-multiagent-verdict-integrity, findings.md #4): a concurrent
				// submission gets a structured queued/running response instead of
				// colliding in play mode and producing a false Timeout verdict.
				"run_suite"        => await RunGated( action, args, session ),
				"rerun_test"       => await RunGated( action, args, session ),
				"measure_variance" => await RunGated( action, args, session ),
				"bisect_pollution" => await RunGated( action, args, session ),
				// Pure reads bypass the gate.
				"get_verdicts"     => await GetVerdicts( args ),
				"list_suites"      => await ListSuites( args ),
				_ => HandlerBase.Error( $"Unknown qa action '{action}'", action,
					"Valid actions: run_suite, get_verdicts, rerun_test, measure_variance, bisect_pollution, list_suites." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"qa.{action} failed: {ex.Message}", action );
		}
	}

	// ── Server-side run serialization (findings.md #4) ───────────────────────

	/// <summary>
	/// Admit exactly one test-dispatching qa action at a time. When the gate is
	/// held, return the structured busy response immediately (or after an
	/// optional bounded server-side wait via <c>wait_ms</c>) — never dispatch a
	/// second suite into play mode where it collides with the holder's run and
	/// times out as a false negative.
	/// </summary>
	private static async Task<object> RunGated( string action, JsonElement args, McpSession session )
	{
		var (callerAgentId, callerSlug) = AgentIdentityRegistry.ResolveCaller( args, session );
		var target = HandlerBase.GetString( args, "suite" ) ?? HandlerBase.GetString( args, "test" );

		var waitMs = HandlerBase.GetInt( args, "wait_ms", 0 );
		if ( waitMs < 0 ) waitMs = 0;
		if ( waitMs > 120000 ) waitMs = 120000;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		bool acquired = QaRunGate.TryAcquire( callerAgentId, callerSlug, action, target, out var leaseId, out var busy );

		if ( !acquired && waitMs > 0 )
		{
			QaRunGate.WaiterEnter();
			try
			{
				while ( sw.ElapsedMilliseconds < waitMs )
				{
					await Task.Delay( 250 );
					acquired = QaRunGate.TryAcquire( callerAgentId, callerSlug, action, target, out leaseId, out busy );
					if ( acquired ) break;
				}
			}
			finally { QaRunGate.WaiterExit(); }
		}

		if ( !acquired )
		{
			// Structured busy contract: status "running" = a foreign run holds the
			// gate and you did not opt into waiting; status "queued" = you waited
			// wait_ms in the queue and the gate never freed. NEVER a Timeout verdict.
			return HandlerBase.Success( new
			{
				status = waitMs > 0 ? "queued" : "running",
				holder = new
				{
					agent_id = busy.HolderAgentId ?? "unidentified",
					agent_slug = busy.HolderSlug,
					action = busy.HolderAction,
					suite = busy.HolderSuite,
					started_utc = busy.StartedUtc.ToString( "o" )
				},
				elapsed_ms = busy.ElapsedMs,
				waited_ms = waitMs > 0 ? (long?)sw.ElapsedMilliseconds : null,
				queue_depth = busy.QueueDepth,
				hint = "qa run gate is held — this is serialization, not a failure, and NOT a suite verdict. "
					 + "Retry after the holder finishes (watch elapsed_ms), or pass wait_ms=<n> (max 120000) "
					 + "to wait server-side. Stale leases (>30 min) are reclaimed automatically."
			} );
		}

		try
		{
			// Re-hop: the Task.Delay continuations above may have left main thread.
			await GameTask.MainThread();
			return action switch
			{
				"run_suite"        => await RunSuite( args ),
				"rerun_test"       => await RerunTest( args ),
				"measure_variance" => await MeasureVariance( args ),
				"bisect_pollution" => await BisectPollution( args ),
				_ => HandlerBase.Error( $"Unknown gated qa action '{action}'", action )
			};
		}
		finally
		{
			QaRunGate.Release( leaseId );
		}
	}

	// ── Actions ──────────────────────────────────────────────────────────────

	private static async Task<object> RunSuite( JsonElement args )
	{
		var suite = HandlerBase.GetString( args, "suite" );
		var resetBetween = HandlerBase.GetBool( args, "reset_between", true );
		return await InvokeBridge( "RunSuite",
			new[] { typeof( string ), typeof( bool ) },
			new object[] { suite, resetBetween }, "run_suite", stampEnv: true );
	}

	private static async Task<object> GetVerdicts( JsonElement args )
	{
		var sinceRunId = HandlerBase.GetInt( args, "since_run_id", 0 );
		var filter = HandlerBase.GetString( args, "filter" );
		return await InvokeBridge( "GetVerdicts",
			new[] { typeof( int ), typeof( string ) },
			new object[] { sinceRunId, filter }, "get_verdicts" );
	}

	private static async Task<object> RerunTest( JsonElement args )
	{
		var test = HandlerBase.GetString( args, "test" );
		return await InvokeBridge( "RerunTest",
			new[] { typeof( string ) }, new object[] { test }, "rerun_test", stampEnv: true );
	}

	private static async Task<object> MeasureVariance( JsonElement args )
	{
		var test = HandlerBase.GetString( args, "test" );
		var n = HandlerBase.GetInt( args, "n", 0 );
		return await InvokeBridge( "MeasureVariance",
			new[] { typeof( string ), typeof( int ) },
			new object[] { test, n }, "measure_variance", stampEnv: true );
	}

	private static async Task<object> BisectPollution( JsonElement args )
	{
		var test = HandlerBase.GetString( args, "test" );
		var suite = HandlerBase.GetString( args, "suite" );
		return await InvokeBridge( "BisectPollution",
			new[] { typeof( string ), typeof( string ) },
			new object[] { test, suite }, "bisect_pollution", stampEnv: true );
	}

	private static async Task<object> ListSuites( JsonElement args )
	{
		return await InvokeBridge( "ListSuites",
			Type.EmptyTypes, Array.Empty<object>(), "list_suites" );
	}

	// ── Reflection bridge to the game-side QaSuiteBridge ─────────────────────

	/// <summary>
	/// Reflection-invoke a static method on <see cref="!:SBEP.Tests.QaSuiteBridge"/>
	/// and surface its JSON payload as an MCP response. The bridge method returns
	/// either a <c>Task&lt;string&gt;</c> (the test-dispatching actions) or a plain
	/// <c>string</c> (the pure reads); both carry an "ok" boolean — ok:false becomes
	/// an MCP error.
	/// </summary>
	private static async Task<object> InvokeBridge( string methodName,
		Type[] paramTypes, object[] args, string action, bool stampEnv = false )
	{
		var type = ResolveGameType( BridgeType );
		if ( type == null )
			return HandlerBase.Error(
				$"Game-side bridge type '{BridgeType}' not found.", action,
				"Game code may be uncompiled or stale — compile the project and retry." );

		var method = type.GetMethod( methodName,
			BindingFlags.Public | BindingFlags.Static, null, paramTypes, null );
		if ( method == null )
			return HandlerBase.Error(
				$"Bridge method '{BridgeType}.{methodName}' not found.", action,
				"Editor plugin and game code are out of sync — recompile both." );

		object raw;
		try
		{
			raw = method.Invoke( null, args );
		}
		catch ( TargetInvocationException tex )
		{
			return HandlerBase.Error(
				$"Bridge threw: {tex.InnerException?.Message ?? tex.Message}", action );
		}

		string json;
		if ( raw is Task<string> task )
		{
			try { json = await task; }
			catch ( Exception ex )
			{
				return HandlerBase.Error( $"Bridge async run threw: {ex.Message}", action );
			}
		}
		else
		{
			json = raw as string;
		}

		if ( string.IsNullOrEmpty( json ) )
			return HandlerBase.Error( "Bridge returned an empty response.", action );

		// The facade returns JSON with an "ok" boolean; surface ok:false as an MCP
		// error. A malformed payload throws out of Parse into HandleAsync's catch.
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		if ( root.ValueKind == JsonValueKind.Object
			&& root.TryGetProperty( "ok", out var okEl )
			&& okEl.ValueKind == JsonValueKind.False )
		{
			var msg = root.TryGetProperty( "error", out var eEl ) ? eEl.GetString() : $"{action} failed.";
			var sug = root.TryGetProperty( "suggestion", out var sEl ) ? sEl.GetString() : null;
			return HandlerBase.Error( msg, action, sug );
		}

		if ( stampEnv )
			json = StampEnvContext( json );

		return HandlerBase.Text( json );
	}

	// ── Environment-context stamping (test-reliability session C, 2026-06-10) ──
	//
	// The vertical_live wall-grind failure was ENVIRONMENTAL — a sibling agent's
	// uncommitted GravityHullPassengerShell change live in the shared editor — and
	// the verdicts carried no trace of it. Every test-dispatching qa response is
	// stamped with the editor-side environment (uncommitted source files + sibling
	// agent presence) so the consuming agent's FIRST hypothesis on failure can be
	// "polluted environment", not "my geometry is wrong". Best-effort: stamping
	// failures never break the response.
	private static string StampEnvContext( string json )
	{
		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse( json )?.AsObject();
			if ( node is null ) return json;
			node["env_context"] = BuildEnvContext();
			return node.ToJsonString();
		}
		catch ( Exception e )
		{
			Log.Warning( $"[qa] env-context stamping failed (response returned unstamped): {e.Message}" );
			return json;
		}
	}

	private static System.Text.Json.Nodes.JsonObject BuildEnvContext()
	{
		var ctx = new System.Text.Json.Nodes.JsonObject();
		ctx["captured_utc"] = DateTime.UtcNow.ToString( "o" );
		var root = HandlerBase.GetProjectRoot();

		// Uncommitted source files in the shared working copy — the stale-DLL /
		// sibling-in-flight pollution class.
		var uncommittedCs = new System.Text.Json.Nodes.JsonArray();
		int uncommittedTotal = 0;
		try
		{
			if ( !string.IsNullOrEmpty( root ) )
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "git",
					Arguments = "status --porcelain",
					WorkingDirectory = root,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				using var p = System.Diagnostics.Process.Start( psi );
				string stdout = p.StandardOutput.ReadToEnd();
				if ( !p.WaitForExit( 3000 ) ) p.Kill();
				foreach ( var line in stdout.Split( '\n' ) )
				{
					var trimmed = line.TrimEnd();
					if ( trimmed.Length < 4 ) continue;
					uncommittedTotal++;
					if ( trimmed.EndsWith( ".cs", StringComparison.OrdinalIgnoreCase ) && uncommittedCs.Count < 20 )
						uncommittedCs.Add( trimmed );
				}
			}
		}
		catch ( Exception e ) { ctx["git_error"] = e.Message; }
		ctx["uncommitted_total"] = uncommittedTotal;
		ctx["uncommitted_cs"] = uncommittedCs;

		// Sibling-agent presence at run time.
		var agents = new System.Text.Json.Nodes.JsonArray();
		try
		{
			if ( !string.IsNullOrEmpty( root ) )
			{
				var presencePath = System.IO.Path.Combine( root, ".claude", "session-state", "presence.json" );
				if ( System.IO.File.Exists( presencePath ) )
				{
					using var doc = JsonDocument.Parse( System.IO.File.ReadAllText( presencePath ) );
					JsonElement rows = default;
					if ( doc.RootElement.ValueKind == JsonValueKind.Array )
						rows = doc.RootElement;
					else if ( doc.RootElement.ValueKind == JsonValueKind.Object
						&& doc.RootElement.TryGetProperty( "agents", out var a ) )
						rows = a;
					if ( rows.ValueKind == JsonValueKind.Array )
					{
						foreach ( var row in rows.EnumerateArray() )
						{
							if ( agents.Count >= 10 ) break;
							var o = new System.Text.Json.Nodes.JsonObject();
							if ( row.TryGetProperty( "agent_id", out var id ) )
							{
								var s = id.GetString() ?? "";
								o["agent"] = s.Length > 8 ? s[..8] : s;
							}
							if ( row.TryGetProperty( "slug", out var slug ) ) o["slug"] = slug.GetString();
							if ( row.TryGetProperty( "status", out var st ) ) o["status"] = st.GetString();
							agents.Add( o );
						}
					}
				}
			}
		}
		catch ( Exception e ) { ctx["presence_error"] = e.Message; }
		ctx["sibling_agents"] = agents;

		if ( uncommittedCs.Count > 0 || agents.Count > 1 )
			ctx["pollution_hint"] = "Run executed under uncommitted .cs changes and/or sibling agents sharing this editor. "
				+ "On FAILURE, suspect environment pollution FIRST — qa rerun_test on a quiet editor and check the "
				+ "uncommitted files' owning sessions — before treating it as a regression.";
		return ctx;
	}

	/// <summary>
	/// Resolve a game-side type, preferring the CURRENT hot-reload generation. A
	/// live game-code Component in the active scene belongs to the current
	/// generation, so its assembly resolves types that match the scene's live
	/// objects — a stale AppDomain copy would resolve a dead generation. Falls back
	/// to an AppDomain scan. Re-resolved every call — never cached. (Copied verbatim
	/// from PawnHandler.ResolveGameType — the proven reflection-bridge resolver.)
	/// </summary>
	internal static Type ResolveGameType( string typeName )
	{
		// Preferred: the current game-assembly generation, via a live scene Component.
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

		// Fallback: AppDomain scan, keeping the last match (best-effort newest generation).
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
}

// ── QaRunGate (mcp-multiagent-verdict-integrity, findings.md #4) ─────────────
//
// Server-side serialization of the test-dispatching qa actions. Play mode is a
// single shared resource: two suites dispatched concurrently steal each other's
// pawns/fixtures and the loser's gates time out — recorded as a false "Timeout"
// verdict that cost 30–120 min of misattribution in 8+ archived sessions.
//
// Lease-token design: TryAcquire hands out a monotonically-increasing lease id
// and Release is a no-op unless the token matches the CURRENT lease. This makes
// the stale-takeover path safe — when a wedged holder (>30 min, e.g. the
// game-side bridge died mid-await) is reclaimed by a new acquirer, the old
// holder's eventual finally-Release cannot drop the new holder's lease.
//
// Editor-plugin statics do not hot-reload (the arenula_mcp.editor DLL swaps only
// on editor restart), so the gate state survives game-assembly hotloads — the
// same lifetime as the verdict run it is protecting.

/// <summary>Snapshot of the gate holder returned to rejected acquirers.</summary>
internal readonly record struct QaRunBusySnapshot(
	string HolderAgentId,
	string HolderSlug,
	string HolderAction,
	string HolderSuite,
	DateTime StartedUtc,
	long ElapsedMs,
	int QueueDepth );

internal static class QaRunGate
{
	private static readonly object Sync = new();
	private static bool _held;
	private static long _leaseId;
	private static string _agentId;
	private static string _slug;
	private static string _action;
	private static string _suite;
	private static DateTime _startedUtc;
	private static int _waiters;

	/// <summary>Holders older than this are treated as abandoned (wedged bridge await).</summary>
	internal const int StaleHolderMs = 30 * 60 * 1000;

	/// <summary>
	/// Try to take the gate. On success returns true with a lease token the
	/// caller MUST pass back to <see cref="Release"/> (try/finally). On
	/// contention returns false with a holder snapshot for the structured
	/// busy response.
	/// </summary>
	internal static bool TryAcquire( string agentId, string slug, string action, string suite,
		out long leaseId, out QaRunBusySnapshot busy )
	{
		lock ( Sync )
		{
			if ( _held )
			{
				var elapsed = (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds;
				if ( elapsed < StaleHolderMs )
				{
					leaseId = 0;
					busy = new QaRunBusySnapshot( _agentId, _slug, _action, _suite, _startedUtc, elapsed, _waiters );
					return false;
				}
				Log.Warning( $"[qa] run gate held by '{_agentId ?? "unidentified"}' ({_action} {_suite}) "
					+ $"for {elapsed}ms (> {StaleHolderMs}ms stale threshold) — reclaiming the abandoned lease "
					+ $"for '{agentId ?? "unidentified"}'." );
			}

			_held = true;
			_leaseId++;
			_agentId = agentId;
			_slug = slug;
			_action = action;
			_suite = suite;
			_startedUtc = DateTime.UtcNow;
			leaseId = _leaseId;
			busy = default;
			return true;
		}
	}

	/// <summary>Release the gate. No-op unless the token matches the current lease.</summary>
	internal static void Release( long leaseId )
	{
		lock ( Sync )
		{
			if ( !_held || leaseId != _leaseId ) return;
			_held = false;
			_agentId = _slug = _action = _suite = null;
		}
	}

	internal static void WaiterEnter() { lock ( Sync ) { _waiters++; } }
	internal static void WaiterExit() { lock ( Sync ) { _waiters = Math.Max( 0, _waiters - 1 ); } }

	internal static bool IsHeld { get { lock ( Sync ) { return _held; } } }
}
