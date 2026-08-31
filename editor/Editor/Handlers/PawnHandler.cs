// Editor/Handlers/PawnHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 2.1 of mcp-agent-capability-system — sibling to Wave 1 SHIPPED 2026-05-15 (capture.annotated + gizmo.* + attention.peek). NOT a continuation of any thruster-vfx wave2 handoff (haze-shake, shock-diamonds, smoke-embergen); those touch Code/SBEP/Vfx/** + Assets/shaders/thruster_*.shader, zero file overlap with this handler.]
//
// [HANDOFF-TRUST: synthetic_input + drive/release/observe added for the agent-as-QA-player system (docs/ai/initiatives/synthetic-input-and-bot-system/agent-qa-player-architecture.md) — greenfield feature, NOT a bug-fix; no handoff hypothesis inherited. drive/release reflection-bridge to the game-side SBEP.Bots.McpAgentBridge, which drives the live player via PlayerController.WishVelocity (proven by sbep.test_agent_drive — 539.8u displacement). synthetic_input writes the Input seam (verified) but cannot drive a controller — see the architecture doc.]
//
// MCP handler for the 'pawn' tool namespace. Actions against the engine
// PlayerController + the agent-as-QA-player driver:
//   pawn.teleport        — move the player to a world position, optional eye angles
//   pawn.set_velocity    — overwrite Velocity (engine PlayerController.Velocity)
//   pawn.get_state       — structured pos / eye_angles / velocity / mode / is_on_ground...
//   pawn.drive           — drive the live player as an autonomous QA pilot: analog
//                          move + look + held actions, persistent until changed.
//                          Bridges to SBEP.Bots.McpAgentBridge (PlayerBot →
//                          PlayerController.WishVelocity — the proven actuation path).
//   pawn.release         — detach the agent driver; return the player to normal control.
//   pawn.observe         — agent-centric telemetry (pos / velocity / driving flag).
//   pawn.maneuver        — run a named QA maneuver (walk_test / jump_test /
//                          pilot_hover) and return a structured PASS/FAIL verdict.
//                          Bridges to SBEP.Bots.McpAgentBridge.Maneuver.
// [HANDOFF-TRUST: pawn.maneuver doc added for greenfield agent-as-QA-player Layer 4 — NEW feature, no inherited hypothesis (handoffs 211/54/212/223 are keyword-collision false matches).]
//   pawn.synthetic_input — low-level engine Input-seam writer (Input.SetAction/
//                          AnalogMove/AnalogLook). Verified at the seam; NOT an
//                          actuation path — the seam cannot drive a PlayerController
//                          (InputContext.Flip wipes synthetic writes). Use pawn.drive.
//
// DEFERRED to v1.1 (separate session):
//   - pawn.set_mode       — Sandbox.Movement.MoveMode is an abstract Component
//                            (polymorphic, score-based swap), NOT an enum.
//   - pawn.possess / eject — needs GameSession.Player access verification.
//   - pawn.add_impulse    — surface decision Velocity-add vs Rigidbody-impulse pending.
//
// Player resolution mirrors the project's existing pattern in
// Code/SBEP/Debug/SbepDevCommandDispatcher.cs:372,464 — the failure-memory
// `feedback_scene_all_resolver_for_post_play_cycle` confirms the Scene.All +
// Game.ActiveScene union is the resilient pattern for post-stop/start-play state.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Sandbox;

namespace Arenula;

internal static class PawnHandler
{
	internal static object Handle( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				"teleport"        => Teleport( args ),
				"set_velocity"    => SetVelocity( args ),
				"get_state"       => GetState( args ),
				"drive"           => Drive( args ),
				"release"         => Release( args ),
				"observe"         => Observe( args ),
				"synthetic_input" => SyntheticInput( args ),
				// [HANDOFF-TRUST: maneuver switch arm added — greenfield agent-as-QA-player Layer 4, no inherited hypothesis (handoffs 211/54/212/223 keyword-collision false match).]
				"maneuver"        => Maneuver( args ),
				// [HANDOFF-TRUST: spawn/despawn switch arms — greenfield agent-qa-player v3 dedicated QA pawn, no inherited hypothesis (handoffs 211/54/212/223/196 keyword-collision false matches).]
				"spawn"           => Spawn( args ),
				"despawn"         => Despawn( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Valid actions: teleport, set_velocity, get_state, drive, release, observe, synthetic_input, maneuver, spawn, despawn." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ── Resolution ───────────────────────────────────────────────────

	internal readonly struct ControllerCandidateFacts
	{
		internal bool Valid { get; init; }
		internal bool Enabled { get; init; }
		internal bool InActiveScene { get; init; }
		internal bool Networked { get; init; }
		internal bool IsOwner { get; init; }
		internal bool IsProxy { get; init; }
	}

	internal static int SelectControllerCandidate(
		IReadOnlyList<ControllerCandidateFacts> candidates,
		bool isPlaying,
		out string selectionReason )
	{
		selectionReason = null;
		int bestIndex = -1;
		int bestRank = 0;

		for ( int i = 0; i < candidates.Count; i++ )
		{
			var c = candidates[i];
			if ( !c.Valid || !c.Enabled ) continue;
			if ( isPlaying && !c.InActiveScene ) continue;

			var localRank = c.IsOwner && !c.IsProxy ? 3
				: c.Networked && !c.IsProxy ? 2
				: !c.Networked && !c.IsProxy ? 1
				: 0;
			if ( isPlaying && localRank == 0 ) continue;

			var rank = (c.InActiveScene ? 10 : 0) + localRank;
			if ( rank <= bestRank ) continue;
			bestRank = rank;
			bestIndex = i;
		}

		if ( bestIndex >= 0 )
		{
			var selected = candidates[bestIndex];
			selectionReason = selected.IsOwner ? "active_scene_network_owner"
				: selected.Networked ? "active_scene_non_proxy"
				: selected.InActiveScene ? "active_scene_local_unnetworked"
				: "editor_fallback";
		}

		return bestIndex;
	}

	private static PlayerController ResolveController(
		out string failReason,
		out string errorCode,
		out string selectionReason,
		out int candidateCount )
	{
		failReason = null;
		errorCode = null;
		selectionReason = null;

		var activeScene = Game.ActiveScene;
		IEnumerable<Scene> scenes = Scene.All ?? Enumerable.Empty<Scene>();
		if ( activeScene != null )
			scenes = new[] { activeScene }.Concat( scenes );

		var controllers = scenes
			.Where( s => s != null )
			.Distinct()
			.SelectMany( s => s.GetAllComponents<PlayerController>() )
			.Where( c => c != null )
			.Distinct()
			.ToList();
		candidateCount = controllers.Count;

		var facts = controllers.Select( c => new ControllerCandidateFacts
		{
			Valid = c.IsValid(),
			Enabled = c.Enabled,
			InActiveScene = activeScene != null && ReferenceEquals( c.Scene, activeScene ),
			Networked = c.GameObject?.Network.Active == true,
			IsOwner = c.GameObject?.Network.IsOwner == true,
			IsProxy = c.IsProxy
		} ).ToList();

		var selectedIndex = SelectControllerCandidate( facts, Game.IsPlaying, out selectionReason );
		if ( selectedIndex >= 0 ) return controllers[selectedIndex];

		if ( Game.IsPlaying && controllers.Any( c => ReferenceEquals( c.Scene, activeScene ) ) )
		{
			errorCode = McpErrorCode.PawnOwnerNotLocal;
			failReason = "The play scene has PlayerController candidates, but none is locally controlled. "
				+ "Wait for the local network owner to become active; if this persists, inspect owner/proxy state.";
			return null;
		}

		if ( Game.IsPlaying )
		{
			errorCode = McpErrorCode.PawnNotReady;
			failReason = "The play scene has no local PlayerController yet. It may still be initializing, "
				+ "or a team/deployment game mode may be intentionally waiting for player selection.";
			return null;
		}

		failReason = "No enabled PlayerController found. Start play mode (or check that the player spawned).";
		errorCode = McpErrorCode.NotFound;
		return null;
	}

	internal static object GetReadinessSnapshot()
	{
		var controller = ResolveController(
			out var reason, out var errorCode, out var selectionReason, out var candidateCount );
		if ( controller != null )
		{
			return new
			{
				status = Game.IsPlaying ? "pawn_ready" : "editor_fallback",
				ready = Game.IsPlaying,
				controller_id = controller.Id.ToString(),
				controller_name = controller.GameObject?.Name,
				selection_reason = selectionReason,
				candidate_count = candidateCount,
				selected_scene = controller.Scene?.Name
			};
		}

		return new
		{
			status = errorCode == McpErrorCode.PawnOwnerNotLocal
				? "owner_not_local"
				: Game.IsPlaying ? "initializing_or_team_lobby" : "not_playing",
			ready = false,
			error_code = errorCode,
			retryable = McpErrorCode.IsRetryable( errorCode ),
			reason,
			candidate_count = candidateCount,
			selected_scene = Game.ActiveScene?.Name
		};
	}

	private static object ControllerUnavailable(
		string action, string reason, string errorCode, int candidateCount )
	{
		var suggestion = errorCode == McpErrorCode.PawnOwnerNotLocal
			? "Inspect the play-scene pawn owner/proxy state; do not control a proxy or editor template."
			: "Retry after play initialization, or complete the active team/deployment selection if the mode intentionally defers spawning.";
		return HandlerBase.Error( reason, action, suggestion, new
		{
			candidate_count = candidateCount,
			active_scene = Game.ActiveScene?.Name,
			is_playing = Game.IsPlaying
		}, errorCode );
	}

	// ── Actions ──────────────────────────────────────────────────────

	private static object Teleport( JsonElement args )
	{
		var posStr = HandlerBase.GetString( args, "position" );
		if ( string.IsNullOrEmpty( posStr ) )
			return HandlerBase.Error( "Missing required 'position' parameter (format: 'x,y,z').", "teleport" );

		Vector3 pos;
		try { pos = HandlerBase.ParseVector3( posStr ); }
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Failed to parse 'position': {ex.Message}", "teleport",
				"Format: 'x,y,z' (floats, comma-separated)." );
		}

		var anglesStr = HandlerBase.GetString( args, "eye_angles" );
		var preserveVelocity = HandlerBase.GetBool( args, "preserve_velocity", false );

		var controller = ResolveController( out var reason, out var errorCode, out var selectionReason, out var candidateCount );
		if ( controller == null )
			return ControllerUnavailable( "teleport", reason, errorCode, candidateCount );

		var prevPos = controller.WorldPosition;
		var prevVel = controller.Velocity;
		bool eyeAnglesApplied = false;

		controller.WorldPosition = pos;

		if ( !string.IsNullOrEmpty( anglesStr ) )
		{
			try
			{
				var a = HandlerBase.ParseVector3( anglesStr );
				controller.EyeAngles = new Angles( a.x, a.y, a.z );
				eyeAnglesApplied = true;
			}
			catch ( Exception ex )
			{
				return HandlerBase.Error( $"Failed to parse 'eye_angles': {ex.Message}", "teleport",
					"Format: 'pitch,yaw,roll' (floats, comma-separated)." );
			}
		}

		// [HANDOFF-TRUST: PlayerController.Velocity is read-only (computed as Body.Velocity - GroundVelocity); the settable equivalent is the underlying Rigidbody.Velocity. CS0200 fix for the brand-new mcp-agent-capability-system Wave 2.1 surface — no inherited hypothesis applies (falsified sentinel on file).]
		bool velocityResetApplied = false;
		if ( !preserveVelocity && controller.Body.IsValid() )
		{
			controller.Body.Velocity = Vector3.Zero;
			controller.Body.AngularVelocity = Vector3.Zero;
			velocityResetApplied = true;
		}

		return HandlerBase.Success( new
		{
			ok = true,
			action = "teleport",
			from = HandlerBase.V3( prevPos ),
			to = HandlerBase.V3( controller.WorldPosition ),
			eye_angles_applied = eyeAnglesApplied,
			preserve_velocity = preserveVelocity,
			velocity_reset_applied = velocityResetApplied,
			previous_velocity = HandlerBase.V3( prevVel ),
			controller_id = controller.Id.ToString(),
			selection_reason = selectionReason,
			candidate_count = candidateCount,
			selected_scene = controller.Scene?.Name
		} );
	}

	private static object SetVelocity( JsonElement args )
	{
		var velStr = HandlerBase.GetString( args, "velocity" );
		if ( string.IsNullOrEmpty( velStr ) )
			return HandlerBase.Error( "Missing required 'velocity' parameter (format: 'x,y,z').", "set_velocity" );

		Vector3 vel;
		try { vel = HandlerBase.ParseVector3( velStr ); }
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Failed to parse 'velocity': {ex.Message}", "set_velocity",
				"Format: 'x,y,z' (floats, comma-separated)." );
		}

		var controller = ResolveController( out var reason, out var errorCode, out var selectionReason, out var candidateCount );
		if ( controller == null )
			return ControllerUnavailable( "set_velocity", reason, errorCode, candidateCount );

		// [HANDOFF-TRUST: PlayerController.Velocity is read-only; route through controller.Body (Rigidbody) for the settable side. Same CS0200 fix as Teleport above.]
		if ( !controller.Body.IsValid() )
			return HandlerBase.Error( "PlayerController has no valid Rigidbody Body — cannot set velocity directly.", "set_velocity" );

		var prev = controller.Velocity;
		controller.Body.Velocity = vel;

		return HandlerBase.Success( new
		{
			ok = true,
			action = "set_velocity",
			from = HandlerBase.V3( prev ),
			to = HandlerBase.V3( controller.Body.Velocity ),
			previous_computed_velocity = HandlerBase.V3( prev ),
			note = "Sets Rigidbody.Velocity directly. PlayerController.Velocity is computed (Body.Velocity - GroundVelocity), so the post-state controller.Velocity may differ if the player is standing on moving geometry.",
			controller_id = controller.Id.ToString(),
			selection_reason = selectionReason,
			candidate_count = candidateCount,
			selected_scene = controller.Scene?.Name
		} );
	}

	private static object GetState( JsonElement args )
	{
		var controller = ResolveController( out var reason, out var errorCode, out var selectionReason, out var candidateCount );
		if ( controller == null )
			return ControllerUnavailable( "get_state", reason, errorCode, candidateCount );

		var modeName = controller.Mode?.GetType().Name ?? "<none>";
		var groundId = controller.GroundObject?.Id.ToString();
		var groundName = controller.GroundObject?.Name;

		return HandlerBase.Success( new
		{
			ok = true,
			action = "get_state",
			controller_id = controller.Id.ToString(),
			controller_name = controller.GameObject?.Name,
			selection_reason = selectionReason,
			candidate_count = candidateCount,
			selected_scene = controller.Scene?.Name,
			pos = HandlerBase.V3( controller.WorldPosition ),
			eye_position = HandlerBase.V3( controller.EyePosition ),
			eye_angles = new
			{
				pitch = MathF.Round( controller.EyeAngles.pitch, 2 ),
				yaw = MathF.Round( controller.EyeAngles.yaw, 2 ),
				roll = MathF.Round( controller.EyeAngles.roll, 2 )
			},
			velocity = HandlerBase.V3( controller.Velocity ),
			wish_velocity = HandlerBase.V3( controller.WishVelocity ),
			ground_velocity = HandlerBase.V3( controller.GroundVelocity ),
			is_on_ground = controller.IsOnGround,
			is_airborne = controller.IsAirborne,
			is_swimming = controller.IsSwimming,
			is_climbing = controller.IsClimbing,
			is_ducking = controller.IsDucking,
			ground_object_id = groundId,
			ground_object_name = groundName,
			mode = modeName,
			mode_note = "v1: mode name is read-only (Sandbox.Movement.MoveMode is a polymorphic Component, not an enum). pawn.set_mode ships in v1.1.",
			time_since_grounded = MathF.Round( (float)controller.TimeSinceGrounded, 3 ),
			time_since_ungrounded = MathF.Round( (float)controller.TimeSinceUngrounded, 3 )
		} );
	}

	// ── Agent driver (charter §3.4 / agent-as-QA-player Layer 1) ─────────

	// [HANDOFF-TRUST: drive/release/observe bridge to the game-side SBEP.Bots.McpAgentBridge via reflection — the editor library cannot AssemblyReference the game ident without type ambiguity (memory feedback_library_assemblyrefs_to_game_ident_creates_type_ambiguity). Greenfield agent-as-QA-player work, no inherited hypothesis.]
	private static object Drive( JsonElement args )
	{
		var actionsCsv = ParseActionsCsv( args );
		var moveStr = HandlerBase.GetString( args, "move" );
		var lookStr = HandlerBase.GetString( args, "look" );

		if ( actionsCsv == null && string.IsNullOrEmpty( moveStr ) && string.IsNullOrEmpty( lookStr ) )
			return HandlerBase.Error(
				"Missing parameters — provide at least one of 'move', 'look', or 'actions'.",
				"drive",
				"Examples: move:'1,0,0' walks forward; look:'0,90,0' faces +Y; actions:[\"Jump\"] holds jump." );

		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Drive",
			new[] { typeof( string ), typeof( string ), typeof( string ) },
			new object[] { moveStr, lookStr, actionsCsv }, "drive" );
	}

	private static object Release( JsonElement args )
	{
		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Release",
			Type.EmptyTypes, Array.Empty<object>(), "release" );
	}

	private static object Observe( JsonElement args )
	{
		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Observe",
			Type.EmptyTypes, Array.Empty<object>(), "observe" );
	}

	// ── Maneuver (agent-as-QA-player Layer 4) ────────────────────────────────

	// [HANDOFF-TRUST: pawn.maneuver bridges to the game-side SBEP.Bots.McpAgentBridge.Maneuver via reflection — greenfield agent-as-QA-player Layer 4, NO inherited hypothesis (handoffs 211/54/212/223 are keyword-collision false matches). The verdict JSON carries ok:true even for BLOCKED/ERROR, so InvokeGameBridge surfaces it as a result, not an MCP error.]
	private static object Maneuver( JsonElement args )
	{
		var name = HandlerBase.GetString( args, "name" );
		if ( string.IsNullOrEmpty( name ) )
			name = HandlerBase.GetString( args, "maneuver" );

		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Maneuver",
			new[] { typeof( string ) }, new object[] { name }, "maneuver" );
	}

	// ── Dedicated QA pawn (agent-as-QA-player v3) ────────────────────────────

	// [HANDOFF-TRUST: spawn/despawn bridge to SBEP.Bots.McpAgentBridge.Spawn/Despawn via reflection — greenfield agent-qa-player v3 dedicated QA pawn, NO inherited hypothesis (handoffs 211/54/212/223/196 are keyword-collision false matches). Clones the player prefab into a separate pawn so QA runs do not commandeer the human's player.]
	private static object Spawn( JsonElement args )
	{
		var posStr = HandlerBase.GetString( args, "position" );
		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Spawn",
			new[] { typeof( string ) }, new object[] { posStr }, "spawn" );
	}

	private static object Despawn( JsonElement args )
	{
		return InvokeGameBridge( "SBEP.Bots.McpAgentBridge", "Despawn",
			Type.EmptyTypes, Array.Empty<object>(), "despawn" );
	}

	// ── Synthetic input (low-level Input-seam writer) ────────────────────

	// [HANDOFF-TRUST: synthetic_input bridges to the game-side SBEP.Bots.McpSyntheticInputBridge via reflection — the editor library cannot AssemblyReference the game ident without type ambiguity. Greenfield work, no inherited hypothesis. NOTE: this writes the engine Input seam (verified) but does not drive a PlayerController — use pawn.drive for actuation.]
	private static object SyntheticInput( JsonElement args )
	{
		var actionsCsv = ParseActionsCsv( args );
		var moveStr = HandlerBase.GetString( args, "move" );
		var lookStr = HandlerBase.GetString( args, "look" );

		if ( actionsCsv == null && string.IsNullOrEmpty( moveStr ) && string.IsNullOrEmpty( lookStr ) )
			return HandlerBase.Error(
				"Missing parameters — provide at least one of 'actions', 'move', or 'look'.",
				"synthetic_input",
				"Examples: actions:[\"Forward\"] holds forward; actions:[] releases all; move:'1,0,0'; look:'0,90,0'." );

		return InvokeGameBridge( "SBEP.Bots.McpSyntheticInputBridge", "SyntheticInput",
			new[] { typeof( string ), typeof( string ), typeof( string ) },
			new object[] { actionsCsv, moveStr, lookStr }, "synthetic_input" );
	}

	// ── Reflection bridge to game-side facades ───────────────────────────

	/// <summary>
	/// Extract 'actions' from JSON args as a comma-separated string (or null if
	/// absent). Accepts a JSON array of strings OR a comma-separated string.
	/// </summary>
	private static string ParseActionsCsv( JsonElement args )
	{
		if ( args.ValueKind == JsonValueKind.Undefined
			|| !args.TryGetProperty( "actions", out var el )
			|| el.ValueKind == JsonValueKind.Null )
			return null;

		if ( el.ValueKind == JsonValueKind.Array )
		{
			var names = new List<string>();
			foreach ( var item in el.EnumerateArray() )
			{
				if ( item.ValueKind == JsonValueKind.String )
				{
					var s = item.GetString();
					if ( !string.IsNullOrWhiteSpace( s ) ) names.Add( s.Trim() );
				}
			}
			return string.Join( ",", names );
		}
		if ( el.ValueKind == JsonValueKind.String )
			return el.GetString() ?? "";

		return null;
	}

	/// <summary>
	/// Reflection-invoke a static string-returning method on a game-side facade
	/// and surface its JSON payload as an MCP response. The facade returns JSON
	/// with an "ok" boolean; ok:false becomes an MCP error.
	/// </summary>
	private static object InvokeGameBridge( string typeName, string methodName,
		Type[] paramTypes, object[] args, string action )
	{
		var type = ResolveGameType( typeName );
		if ( type == null )
			return HandlerBase.Error(
				$"Game-side bridge type '{typeName}' not found.", action,
				"Game code may be uncompiled or stale — compile the project and retry." );

		var method = type.GetMethod( methodName,
			BindingFlags.Public | BindingFlags.Static, null, paramTypes, null );
		if ( method == null )
			return HandlerBase.Error(
				$"Bridge method '{typeName}.{methodName}' not found.", action,
				"Editor plugin and game code are out of sync — recompile both." );

		string json;
		try
		{
			json = method.Invoke( null, args ) as string;
		}
		catch ( TargetInvocationException tex )
		{
			return HandlerBase.Error(
				$"Bridge threw: {tex.InnerException?.Message ?? tex.Message}", action );
		}

		if ( string.IsNullOrEmpty( json ) )
			return HandlerBase.Error( "Bridge returned an empty response.", action );

		// The facade returns JSON with an "ok" boolean; surface ok:false as an MCP
		// error. A malformed payload throws out of Parse into Handle()'s catch.
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
	/// objects — a stale AppDomain copy would resolve a dead generation. Falls
	/// back to an AppDomain scan. Re-resolved every call — never cached.
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
