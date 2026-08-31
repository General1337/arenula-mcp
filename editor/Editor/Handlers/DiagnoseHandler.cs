// Editor/Handlers/DiagnoseHandler.cs
//
// [HANDOFF-TRUST: greenfield Layer-2 / P2.4 of the autonomous-qa-verifier
// initiative — NEW MCP tool, no bug-fix hypothesis inherited from any handoff.
// This handler is the editor-side surface of the `diagnose` tool; it reflection-
// bridges to the game-side SBEP.Tests.GravityHull.Diagnosis.GhDiagnosisBridge
// using the proven InvokeBridge / ResolveGameType pattern copied verbatim from
// QaHandler (the editor library cannot AssemblyReference the game ident without
// type ambiguity — memory feedback_library_assemblyrefs_to_game_ident_creates_
// type_ambiguity). It introduces no engine-internal symbols.]
//
// MCP handler for the 'diagnose' tool namespace — the Layer 2 diagnosis engine.
// Actions:
//   diagnose.test   — run the closed diagnosis loop for one failed test
//                     (isolate -> variance -> instrumented repro -> classify);
//                     returns a structured diagnosis bundle.
//   diagnose.suite  — run a named suite (or CSV) and diagnose every failing test.
//   diagnose.bundle — read the stored diagnosis bundles (pure in-memory read).
//
// test / suite dispatch play-mode test ConCmds and are async (they await the
// game-side closed loop). bundle is a pure read. The game-side bridge returns a
// Task<string> for the first two and a plain string for bundle — InvokeBridge
// awaits a Task<string> when one is returned and a plain string otherwise.

using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

internal static class DiagnoseHandler
{
	private const string BridgeType = "SBEP.Tests.GravityHull.Diagnosis.GhDiagnosisBridge";

	internal static async Task<object> HandleAsync( string action, JsonElement args )
	{
		try
		{
			// Get onto the main thread before touching scene / game state. The
			// game-side bridge starts an async chain that polls the verdict
			// registry via GameTask continuations (main-thread).
			await GameTask.MainThread();

			return action switch
			{
				"test"   => await DiagnoseTest( args ),
				"suite"  => await DiagnoseSuite( args ),
				"bundle" => await GetBundles( args ),
				_ => HandlerBase.Error( $"Unknown diagnose action '{action}'", action,
					"Valid actions: test, suite, bundle." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"diagnose.{action} failed: {ex.Message}", action );
		}
	}

	// ── Actions ──────────────────────────────────────────────────────────────

	private static async Task<object> DiagnoseTest( JsonElement args )
	{
		var test = HandlerBase.GetString( args, "test" );
		var n = HandlerBase.GetInt( args, "n", 0 );
		return await InvokeBridge( "DiagnoseTest",
			new[] { typeof( string ), typeof( int ) },
			new object[] { test, n }, "test" );
	}

	private static async Task<object> DiagnoseSuite( JsonElement args )
	{
		var suite = HandlerBase.GetString( args, "suite" );
		return await InvokeBridge( "DiagnoseSuite",
			new[] { typeof( string ) }, new object[] { suite }, "suite" );
	}

	private static async Task<object> GetBundles( JsonElement args )
	{
		var filter = HandlerBase.GetString( args, "filter" );
		return await InvokeBridge( "GetBundles",
			new[] { typeof( string ) }, new object[] { filter }, "bundle" );
	}

	// ── Reflection bridge to the game-side GhDiagnosisBridge ──────────────────

	/// <summary>
	/// Reflection-invoke a static method on
	/// <see cref="!:SBEP.Tests.GravityHull.Diagnosis.GhDiagnosisBridge"/> and
	/// surface its JSON payload as an MCP response. The bridge method returns
	/// either a <c>Task&lt;string&gt;</c> (test / suite) or a plain <c>string</c>
	/// (bundle); both carry an "ok" boolean — ok:false becomes an MCP error.
	/// </summary>
	private static async Task<object> InvokeBridge( string methodName,
		Type[] paramTypes, object[] args, string action )
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

		return HandlerBase.Text( json );
	}

	/// <summary>
	/// Resolve a game-side type, preferring the CURRENT hot-reload generation. A
	/// live game-code Component in the active scene belongs to the current
	/// generation, so its assembly resolves types that match the scene's live
	/// objects — a stale AppDomain copy would resolve a dead generation. Falls back
	/// to an AppDomain scan. Re-resolved every call — never cached. (Copied verbatim
	/// from QaHandler.ResolveGameType — the proven reflection-bridge resolver.)
	/// </summary>
	private static Type ResolveGameType( string typeName )
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
