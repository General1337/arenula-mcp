// Editor/Handlers/TimeHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 4.1 of mcp-agent-capability-system — sibling to Waves 1+2+3 SHIPPED 2026-05-15. NOT a continuation of any prior thruster/cloud/atmosphere wave4 handoff; those touch Code/SBEP/Vfx/** + Assets/shaders/**. Bare 'wave4' substring collision does NOT inherit any hypothesis.]
//
// MCP handler for the 'time' tool namespace. v1 actions:
//   time.pause      — Scene.TimeScale = 0 AND PhysicsWorld.TimeScale = 0
//                     (double-halt against plan.md Wave 4 risk #3:
//                      "physics may still tick at TimeScale=0").
//   time.resume     — Scene.TimeScale = saved or 1.0; PhysicsWorld too.
//   time.is_paused  — return Scene.TimeScale == 0.
//   time.step       — call Scene.GameTick(scene.FixedDelta) N times.
//                     Engine primitive verified via sbox-api: Scene.GameTick
//                     (timeDelta: System.Double = 0.1) exists on Sandbox.Scene.
//   time.scale      — set Scene.TimeScale = factor; return prev_scale.
//
// Scene resolution mirrors PawnHandler.ResolveController — the project's
// Scene.All.Concat([Game.ActiveScene]) pattern that survives play-cycle
// state divergence per memory feedback_scene_resolver_path_b_getallobjects_for_play_clones.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

internal static class TimeHandler
{
	// State stored when pause() is called so resume() can restore the prior scale.
	// Process-scoped (cleared on plugin hot-reload, which is acceptable — paused
	// state across hot-reload is meaningless).
	private static float _scaleBeforePause = 1f;
	private static bool _wePaused = false;

	internal static object Handle( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				"pause"     => Pause(),
				"resume"    => Resume(),
				"is_paused" => IsPaused(),
				"step"      => Step( args ),
				"scale"     => SetScale( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Valid actions: pause, resume, is_paused, step, scale." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ── Resolution ───────────────────────────────────────────────────

	private static Scene ResolveScene( out string failReason )
	{
		failReason = null;

		var activeScene = Game.ActiveScene;
		IEnumerable<Scene> scenes = Scene.All ?? Enumerable.Empty<Scene>();
		if ( activeScene != null )
			scenes = scenes.Concat( new[] { activeScene } );
		scenes = scenes.Where( s => s != null ).Distinct();

		foreach ( var sc in scenes )
		{
			return sc;
		}

		failReason = "No active Scene reachable via Scene.All + Game.ActiveScene. Start play mode or load a scene.";
		return null;
	}

	// ── pause ────────────────────────────────────────────────────────

	private static object Pause()
	{
		var scene = ResolveScene( out var fail );
		if ( scene == null )
			return HandlerBase.Error( fail, "pause" );

		// Save the prior scale ONLY when we're actually changing it; consecutive
		// pause() calls without intervening resume() shouldn't lose the original.
		if ( !_wePaused )
		{
			_scaleBeforePause = scene.TimeScale;
			_wePaused = true;
		}

		scene.TimeScale = 0f;

		// Double-halt physics per plan.md Wave 4 risk #3.
		var physicsScale = 1f;
		try
		{
			if ( scene.PhysicsWorld != null )
			{
				physicsScale = scene.PhysicsWorld.TimeScale;
				scene.PhysicsWorld.TimeScale = 0f;
			}
		}
		catch ( Exception ex )
		{
			// Non-fatal — Scene.TimeScale alone is the primary halt.
			Log.Warning( $"[time.pause] PhysicsWorld.TimeScale write threw {ex.GetType().Name}: {ex.Message}" );
		}

		return HandlerBase.Success( new
		{
			ok = true,
			action = "pause",
			scene_time_scale = scene.TimeScale,
			physics_time_scale = scene.PhysicsWorld?.TimeScale ?? 0f,
			restored_to_on_resume = _scaleBeforePause,
			prior_physics_scale = physicsScale
		} );
	}

	// ── resume ───────────────────────────────────────────────────────

	private static object Resume()
	{
		var scene = ResolveScene( out var fail );
		if ( scene == null )
			return HandlerBase.Error( fail, "resume" );

		var target = _wePaused ? _scaleBeforePause : 1f;
		scene.TimeScale = target;

		try
		{
			if ( scene.PhysicsWorld != null )
				scene.PhysicsWorld.TimeScale = target;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[time.resume] PhysicsWorld.TimeScale write threw {ex.GetType().Name}: {ex.Message}" );
		}

		_wePaused = false;

		return HandlerBase.Success( new
		{
			ok = true,
			action = "resume",
			scene_time_scale = scene.TimeScale,
			physics_time_scale = scene.PhysicsWorld?.TimeScale ?? 1f
		} );
	}

	// ── is_paused ────────────────────────────────────────────────────

	private static object IsPaused()
	{
		var scene = ResolveScene( out var fail );
		if ( scene == null )
			return HandlerBase.Error( fail, "is_paused" );

		var paused = scene.TimeScale == 0f;
		return HandlerBase.Success( new
		{
			paused,
			scene_time_scale = scene.TimeScale,
			physics_time_scale = scene.PhysicsWorld?.TimeScale ?? float.NaN,
			we_initiated_pause = _wePaused
		} );
	}

	// ── step ─────────────────────────────────────────────────────────

	private static object Step( JsonElement args )
	{
		var scene = ResolveScene( out var fail );
		if ( scene == null )
			return HandlerBase.Error( fail, "step" );

		var frames = Math.Clamp( HandlerBase.GetInt( args, "frames", 1 ), 1, 240 );
		var fixedDelta = scene.FixedDelta;
		if ( fixedDelta <= 0f || float.IsNaN( fixedDelta ) || float.IsInfinity( fixedDelta ) )
			fixedDelta = 1f / 60f;

		var stepped = 0;
		var firstError = (string)null;
		for ( var i = 0; i < frames; i++ )
		{
			try
			{
				scene.GameTick( fixedDelta );
				stepped++;
			}
			catch ( Exception ex )
			{
				firstError = $"{ex.GetType().Name}: {ex.Message}";
				break;
			}
		}

		return HandlerBase.Success( new
		{
			ok = firstError == null,
			action = "step",
			requested_frames = frames,
			stepped_frames = stepped,
			fixed_delta = fixedDelta,
			first_error = firstError,
			scene_time_scale = scene.TimeScale,
			note = "Each step advances Scene.GameTick(FixedDelta); the engine's per-tick subsystems run. Pair with time.pause for deterministic frame-step."
		} );
	}

	// ── scale ────────────────────────────────────────────────────────

	private static object SetScale( JsonElement args )
	{
		var scene = ResolveScene( out var fail );
		if ( scene == null )
			return HandlerBase.Error( fail, "scale" );

		var factor = HandlerBase.GetFloat( args, "factor", 1f );
		if ( float.IsNaN( factor ) || float.IsInfinity( factor ) || factor < 0f )
			return HandlerBase.Error( $"Invalid 'factor' {factor} — must be a finite non-negative float.", "scale",
				"Use factor>=0. factor=0 is equivalent to time.pause; factor=1.0 is realtime." );

		var prevScene = scene.TimeScale;
		var prevPhys = scene.PhysicsWorld?.TimeScale ?? float.NaN;

		scene.TimeScale = factor;
		try
		{
			if ( scene.PhysicsWorld != null )
				scene.PhysicsWorld.TimeScale = factor;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[time.scale] PhysicsWorld.TimeScale write threw {ex.GetType().Name}: {ex.Message}" );
		}

		// If we were paused and the user explicitly sets a scale, clear the
		// internal pause flag so resume() falls back to factor=1 rather than
		// the pre-pause memory (the user is now driving scale directly).
		if ( _wePaused && factor != 0f ) _wePaused = false;
		if ( factor == 0f )
		{
			// User used scale=0 as a manual pause. Track it like pause() did.
			if ( !_wePaused ) { _scaleBeforePause = prevScene == 0f ? 1f : prevScene; _wePaused = true; }
		}

		return HandlerBase.Success( new
		{
			ok = true,
			action = "scale",
			factor,
			prev_scene_scale = prevScene,
			prev_physics_scale = prevPhys,
			scene_time_scale = scene.TimeScale,
			physics_time_scale = scene.PhysicsWorld?.TimeScale ?? float.NaN
		} );
	}
}
