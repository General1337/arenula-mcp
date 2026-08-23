using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// <c>place</c> — the one place to ask "where do I put this", and the one place to
/// verify a position you chose yourself.
///
/// <para>
/// Every other tool in this plugin ACCEPTS a position; none produces or checks one.
/// That gap is why agents spawned ships inside Earth (whose centre is the world origin,
/// and therefore the fallback every unset position lands on) and inside each other.
/// </para>
///
/// <para>
/// <b>This tool verifies; it does not decide.</b> <c>where</c> returns a ranked menu,
/// never a single answer, and reasoning out your own position and running it through
/// <c>check</c> is the intended path, not a fallback. Refusals carry the obstruction,
/// the distance and a direction with room, so the next attempt is reasoned rather than
/// guessed.
/// </para>
///
/// <para>
/// Bridged by reflection to <c>SBEP.World.PlacementBridge</c>: the editor library cannot
/// assembly-reference the game ident without type ambiguity (the same constraint
/// <c>pawn.drive</c> works around).
/// </para>
///
/// <para>Contract: <c>docs/ai/contracts/placement.md</c> (BINDING).
/// Plan: <c>docs/ai/plans/one-placement-command.md</c>.</para>
/// </summary>
[McpTool( "place",
	"Decide and verify WHERE to put something in the world. 'check' verifies a position you chose and is the action to reach for by default — its refusals name the obstruction, the overlap distance and a direction with room, so you can reason out a better position rather than guess again. 'where' returns a RANKED MENU of known-good spots for a given size (it never picks for you; ignoring the menu and choosing your own position is the intended path whenever nothing fits). 'explain' describes what is at a position — region, gravity, air, ground height, nearest objects — and when it genuinely cannot tell, it names the one trace that would settle it. 'regions' dumps the live map. 'inside' places the player in a ship interior through the hull-local seam, which is the only write the walk-in-hull movement mode does not discard — a raw world teleport there is what makes the player appear to be thrown out of the ship. IMPORTANT: world origin (0,0,0) is the centre of Earth, not empty space.",
	ReadOnlyHint = false )]
internal static class PlacementHandler
{
	private const string BridgeType = "SBEP.World.PlacementBridge";

	internal static object InputSchema() => new
	{
		type = "object",
		properties = new Dictionary<string, object>
		{
			["action"] = new
			{
				type = "string",
				description = "The operation to perform.",
				@enum = new[] { "check", "where", "explain", "regions", "inside" },
			},
			["position"] = new
			{
				type = "string",
				description = "World position as 'x,y,z'. Required for: check, explain.",
			},
			["size"] = new
			{
				type = "string",
				description = "Bounding size of the thing being placed, as 'x,y,z' or a single number for a cube. Used by: check, where. Default '512,512,512' (roughly a small ship).",
			},
			["region"] = new
			{
				type = "string",
				description = "Restrict the menu to one region — a planet name from 'regions', a free-space range name, 'space', or 'orbital'. Used by: where. Omit for everything.",
			},
			["ship"] = new
			{
				type = "string",
				description = "Name (or part of one) of the ship whose interior to enter. Used by: inside. Omit for the hull nearest the player.",
			},
		},
		required = new[] { "action" },
		additionalProperties = false,
	};

	// Every action passes the scene the MCP layer resolved, rather than letting the
	// game side reach for Game.ActiveScene. Those are DIFFERENT SCENES in edit mode —
	// SceneHelpers prefers the editor session and only falls back to Game.ActiveScene —
	// and the first cut of this handler shipped that mismatch: `check` at the world
	// origin came back "placeable" because the scene it inspected held no terrain.
	private static Scene Bound() => SceneHelpers.ResolveScene();

	[McpAction( "check", ReadOnly = true )]
	internal static object Check( JsonElement args ) => Bridge( "Check", "check",
		new[] { typeof( Scene ), typeof( string ), typeof( string ) },
		new object[]
		{
			Bound(),
			Require( args, "position", "check" ),
			HandlerBase.GetString( args, "size", "512,512,512" ),
		} );

	[McpAction( "where", ReadOnly = true )]
	internal static object Where( JsonElement args ) => Bridge( "Where", "where",
		new[] { typeof( Scene ), typeof( string ), typeof( string ) },
		new object[]
		{
			Bound(),
			HandlerBase.GetString( args, "size", "512,512,512" ),
			HandlerBase.GetString( args, "region" ),
		} );

	[McpAction( "explain", ReadOnly = true )]
	internal static object Explain( JsonElement args ) => Bridge( "Explain", "explain",
		new[] { typeof( Scene ), typeof( string ) },
		new object[] { Bound(), Require( args, "position", "explain" ) } );

	[McpAction( "regions", ReadOnly = true )]
	internal static object Regions( JsonElement args ) => Bridge( "Regions", "regions",
		new[] { typeof( Scene ) }, new object[] { Bound() } );

	// The only action here that mutates anything: it moves the player. Every scanner
	// action must declare exactly one operation class, and omitting it is a hard
	// registration error that takes the WHOLE tool list down, not just this action.
	[McpAction( "inside", WriteKind = "scene_write" )]
	internal static object Inside( JsonElement args ) => Bridge( "Inside", "inside",
		new[] { typeof( Scene ), typeof( string ) },
		new object[] { Bound(), HandlerBase.GetString( args, "ship" ) } );

	// ── plumbing ─────────────────────────────────────────────────────────

	private static string Require( JsonElement args, string name, string action )
	{
		var v = HandlerBase.GetString( args, name );
		if ( string.IsNullOrWhiteSpace( v ) )
			throw new ArgumentException(
				$"'{name}' is required for place.{action} (format: 'x,y,z')." );
		return v;
	}

	/// <summary>
	/// Reflection-invoke the game-side facade. A REFUSAL is not an error: the bridge
	/// answers ok:true with a verdict, because "no, and here is why" is a successful
	/// answer to "can I put this here". Only a broken request sets ok:false.
	/// </summary>
	private static object Bridge( string method, string action, Type[] paramTypes, object[] args )
	{
		var type = ResolveGameType( BridgeType );
		if ( type == null )
			return HandlerBase.Error(
				$"Game-side bridge '{BridgeType}' not found.", action,
				"Game code is uncompiled or stale — run compile action=check and retry." );

		var mi = type.GetMethod( method, BindingFlags.Public | BindingFlags.Static, null, paramTypes, null );
		if ( mi == null )
			return HandlerBase.Error(
				$"Bridge method '{BridgeType}.{method}' not found.", action,
				"Editor plugin and game code are out of sync — recompile both." );

		string json;
		try
		{
			json = mi.Invoke( null, args ) as string;
		}
		catch ( TargetInvocationException tex )
		{
			return HandlerBase.Error(
				$"Placement bridge threw: {tex.InnerException?.Message ?? tex.Message}", action );
		}

		if ( string.IsNullOrEmpty( json ) )
			return HandlerBase.Error( "Placement bridge returned nothing.", action );

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		if ( root.ValueKind == JsonValueKind.Object
			&& root.TryGetProperty( "ok", out var ok )
			&& ok.ValueKind == JsonValueKind.False )
		{
			var msg = root.TryGetProperty( "error", out var e ) ? e.GetString() : $"place.{action} failed.";
			var sug = root.TryGetProperty( "suggestion", out var s ) ? s.GetString() : null;
			return HandlerBase.Error( msg, action, sug );
		}

		return HandlerBase.Text( json );
	}

	/// <summary>
	/// Resolve a game type from the CURRENT hot-reload generation, preferring the
	/// assembly a live scene component belongs to. A stale AppDomain copy resolves a
	/// dead generation whose statics do not match the scene. Never cached.
	/// Mirrors <c>PawnHandler.ResolveGameType</c>.
	/// </summary>
	private static Type ResolveGameType( string typeName )
	{
		var scene = Game.ActiveScene;
		if ( scene != null )
		{
			foreach ( var comp in scene.GetAllComponents<Component>() )
			{
				if ( comp == null ) continue;
				var asm = comp.GetType().Assembly;
				if ( asm.FullName?.Contains( "package.", StringComparison.OrdinalIgnoreCase ) != true
					&& asm.GetType( typeName, throwOnError: false ) == null )
					continue;
				var hit = asm.GetType( typeName, throwOnError: false );
				if ( hit != null ) return hit;
			}
		}

		foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
		{
			var hit = asm.GetType( typeName, throwOnError: false );
			if ( hit != null ) return hit;
		}
		return null;
	}
}
