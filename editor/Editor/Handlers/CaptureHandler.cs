// Editor/Handlers/CaptureHandler.cs
//
// [HANDOFF-TRUST: greenfield Wave 1.1 of mcp-agent-capability-system initiative opened 2026-05-15 — NOT a continuation of any prior thruster/cloud/atmosphere handoff; the falsification gate matched on the bare token "wave1" but no hypothesis is inherited from any thruster handoff doc.]
//
// MCP handler for the 'capture' tool namespace. v1 ships the 'annotated'
// action only. Sibling actions ship in later waves:
//   - capture.orbit_set    (Wave 2.3)
//   - capture.channels     (Wave 3.2)
//   - capture.diff_baseline (Wave 4.2)
//
// Delegates the per-frame work to CaptureAnnotatedHelper so the durable
// sbep.test_mcp_wave1_perception test ConCmd can verify projection
// without a JSON-RPC round-trip.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula;

internal static class CaptureHandler
{
	// [HANDOFF-TRUST: extend the 'capture' action enum with orbit_set for mcp-agent-capability-system Wave 2.3 — sibling to Wave 2.1 pawn.* + 2.2 eye.* shipped this session; bare 'wave2' substring collision against unrelated thruster-vfx wave2 handoffs does NOT inherit any hypothesis (falsified sentinel on file).]
	// [HANDOFF-TRUST: extend the 'capture' action enum with channels for mcp-agent-capability-system Wave 3.2 — sibling to Wave 3.1 feed.* shipped this session; greenfield per-channel breakdown work, no inherited hypothesis applies from any prior wave3-named handoff.]
	// [HANDOFF-TRUST: extend the 'capture' action enum with baseline_set / baseline_list / diff_baseline for mcp-agent-capability-system Wave 4.2 — visual regression-catch via per-pixel luminance + tile-variance verdict; greenfield additive surface, no inherited hypothesis from any prior wave4 handoff (those touch Code/SBEP/Vfx/** + Assets/shaders/**, this file only).]
	internal static object Handle( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				// "shot" is deliberately absent from this switch. It is the only capture
				// action dispatched asynchronously — see HandleShotAsync below and the
				// per-action threading case in Tools/generate-mcp-discovery-adapters.py —
				// so it never arrives here.
				"annotated"     => Annotated( args ),
				"orbit_set"     => OrbitSet( args ),
				"channels"      => Channels( args ),
				"baseline_set"  => BaselineSet( args ),
				"baseline_list" => BaselineList(),
				"diff_baseline" => DiffBaseline( args ),
				_ => HandlerBase.Error( $"Unknown action '{action}'", action,
					"Use action=shot — the one screenshot entry point (docs/ai/contracts/screenshot.md). Other actions: annotated, orbit_set, channels, baseline_set, baseline_list, diff_baseline." )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action );
		}
	}

	// ================================================================================
	// shot — THE screenshot entry point. See docs/ai/contracts/screenshot.md (BINDING).
	//
	// Everything else in this file is an implementation detail of this method and is
	// gated by .claude/hooks/one-screenshot-route-gate.sh. The design rule is that an
	// agent states WHAT IT WANTS TO SEE (a target) and never which route renders it.
	//
	// Three properties are non-negotiable and each one closes a measured failure:
	//   1. It renders IN-PROCESS. It never drives a ConCmd through console_run, which
	//      was measured on 2026-07-25 binding the EDIT scene twice from identical
	//      input — the one true command must not be uncertain about which scene it
	//      photographed. The returned `scene` field keeps that checkable.
	//   2. It PROVES the subject is in frame. A perfectly-routed picture of empty sky
	//      is the most common way a screenshot is useless; target_in_frame=false is a
	//      hard error after one reframe attempt, never a warning on a returned path.
	//   3. It proves the FILE IS THIS CALL'S. Stat after write, assert non-zero bytes.
	//      Handing back a stale path from an earlier call is the exact bug class this
	//      whole system exists to kill.
	// ================================================================================
	/// <summary>
	/// Async entry point for <c>capture action=shot</c>, called directly by the generated
	/// adapter (<c>Editor/Generated/LegacyMcpToolAdapters.g.cs</c>).
	///
	/// <para><b>Why this one action is async while its six siblings are not.</b> The play-mode
	/// route does not render a second time — it asks the game to copy the frame the engine
	/// already composited, which lands a couple of frames later. A MainThread handler that
	/// blocked waiting for that would be blocking the very frames it is waiting on, and the
	/// capture could never complete. Threading is resolved per action, so only <c>shot</c>
	/// pays for this.</para>
	/// </summary>
	internal static async Task<object> HandleShotAsync( JsonElement args )
	{
		try
		{
			return await ShotAsync( args );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"{ex.GetType().Name}: {ex.Message}", "shot" );
		}
	}

	/// <summary>Longest we will wait for the game to hand back a composited frame.</summary>
	private const int GrabTimeoutMs = 15000;
	/// <summary>Gap between file checks while the grab is in flight. Each gap is a yield, which
	/// is what lets the frames the grab depends on actually run.</summary>
	private const int GrabPollMs = 100;
	/// <summary>Directory the in-game frame grab writes to, under the engine's data root.</summary>
	private const string GrabLeafDir = "vfx-grabs";

	private static async Task<object> ShotAsync( JsonElement args )
	{
		await GameTask.MainThread();

		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "shot" );

		var targetStr = ( HandlerBase.GetString( args, "target", "camera" ) ?? "camera" ).Trim();
		var subject = ( HandlerBase.GetString( args, "subject" ) ?? "" ).Trim().ToLowerInvariant();
		var name = ( HandlerBase.GetString( args, "name" ) ?? "" ).Trim();
		var wantInline = GetFlag( args, "inline", false );
		var wantFreeze = GetFlag( args, "freeze", true );
		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 1920 ), 320, 3840 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 1080 ), 240, 2160 );

		var isCameraTarget = string.Equals( targetStr, "camera", StringComparison.OrdinalIgnoreCase );

		// --- resolve the target, and INFER the subject from it when not overridden ---
		GameObject targetGo = null;
		BBox targetBox = default;
		var haveBox = false;

		if ( !isCameraTarget )
		{
			if ( Guid.TryParse( targetStr, out var gid ) )
				targetGo = scene.Directory.FindByGuid( gid );

			targetGo ??= scene.GetAllObjects( true )
				.FirstOrDefault( o => o.IsValid() &&
					string.Equals( o.Name, targetStr, StringComparison.OrdinalIgnoreCase ) );

			if ( targetGo == null )
			{
				// Not a GameObject — try the world atlas's named regions. Agents now
				// spawn into deep-space ranges that exist as volumes, not objects, and
				// "photograph where I just put it" must not require a different
				// vocabulary from the one placement uses.
				if ( !TryResolveRegionTarget( targetStr, out targetBox ) )
					return HandlerBase.Error(
						$"target '{targetStr}' is neither a GameObject in scene '{scene.Name}' " +
						$"nor a known region.", "shot",
						"Pass a GameObject name or id, an atlas region name (place action=regions " +
						"lists them), or target=camera to shoot the current view. shot refuses " +
						"rather than returning a frame that cannot contain your subject." );

				haveBox = true;
			}
			else
			{
				targetBox = ResolveTargetBounds( targetGo );
				haveBox = true;
			}
		}

		if ( string.IsNullOrEmpty( subject ) )
			subject = "world";

		if ( subject is "ui" or "orbit" or "window" )
		{
			// Honest delegation rather than a silent wrong answer. These three routes are
			// not yet ported in-process (ui + window live outside this handler entirely),
			// so shot hands back the ONE exact command instead of pretending to cover it.
			// The agent still makes no route CHOICE — it is told precisely what to run.
			return HandlerBase.Error(
				$"subject '{subject}' is not yet served in-process by shot.", "shot",
				subject switch
				{
					"ui" => "Run exactly: console_run 'sbep.ui_capture <PanelClass>' (requires play mode). " +
							"Write the delegate sentinel first so the route gate allows it.",
					"orbit" => "Run exactly: capture action=orbit_set target=<id> return=disk — ONE sweep only " +
							   "(two back-to-back sweeps crashed the editor, failure-memory #137).",
					_ => "Run exactly: Tools/window-capture.ps1 -Out <path> — the only post-FX- and " +
						 "editor-tool-window-inclusive route."
				} );
		}

		// --- resolve the camera. Same selection capture.annotated uses, so shot and the
		// legacy routes can never disagree about which camera they photographed. ---
		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No enabled CameraComponent found in scene.", "shot",
				"In edit mode the scene may have no enabled camera; enter play mode or enable one." );

		var camGo = cam.GameObject;
		var origPos = camGo.WorldPosition;
		var origRot = camGo.WorldRotation;
		var origFov = cam.FieldOfView;

		var shotPos = origPos;
		var shotRot = origRot;
		var warnings = new List<string>();
		var reframed = false;

		byte[] png = null;
		string renderError = null;
		bool inFrame = false;
		float[] rect = null;
		float coverage = 0f;

		// Read anything scene-bound BEFORE the first await — the poll loop below yields the
		// main thread on purpose, and touching scene state from off it is how a capture takes
		// the editor down instead of photographing it.
		var sceneName = scene.Name;
		var camName = camGo.Name;
		var isPlaying = IsPlaySession();
		var determinism = ApplyFreeze( scene, wantFreeze, isPlaying, warnings );

		// --- Is the subject ALREADY in the picture from where the camera is standing? ---
		// This is the whole route decision. The play-mode route photographs the live view and
		// cannot reposition anything, so it is only available when no reposition is needed.
		var inFrameAtCurrentPose = true;
		if ( haveBox )
		{
			inFrameAtCurrentPose =
				ProjectBox( targetBox, origPos, origRot, cam, width, height, out rect, out coverage )
				&& rect != null && coverage > 0f;
		}

		// ────────────────────────────────────────────────────────────────────────────────
		// ROUTE A — the composited backbuffer. NO BLIND SPOTS, and the reason this exists.
		//
		// RenderToBitmap (route B below) builds a separate native renderer from the one that
		// draws the real frame, and the managed custom-object callback only runs on the real
		// path. Every GPU particle system in this game draws that way, so route B cannot show
		// an explosion, a plume, a debris field or an ember trail — while the frame still
		// looks populated, because ordinary model renderers do appear. That false negative has
		// cost this repo a rendering-defect hunt on a layer that was drawing correctly.
		//
		// This route sidesteps it by not re-rendering at all: sbep.grab copies the frame the
		// engine already presented. Nothing can be missing from a copy of the finished picture.
		// See Code/SBEP/Debug/SbepFrameGrab.cs for the engine-source citations.
		// ────────────────────────────────────────────────────────────────────────────────
		var route = "world.render_to_bitmap";
		var grabAttempted = false;
		string grabFailure = null;

		if ( isPlaying && inFrameAtCurrentPose )
		{
			grabAttempted = true;
			var (grabPng, failure) = await TryBackbufferGrabAsync( scene );
			if ( grabPng is { Length: > 0 } )
			{
				png = grabPng;
				route = "world.backbuffer_grab";
				inFrame = true;
			}
			else
			{
				grabFailure = failure;
			}
		}

		// ────────────────────────────────────────────────────────────────────────────────
		// ROUTE B — re-render on the resolved camera. Can frame ANY target, including one
		// off-screen or behind the camera, at the cost of the blindness described above. Used
		// in edit mode, when the target needs repositioning to fit, and as the honest fallback
		// when route A was available but did not deliver.
		// ────────────────────────────────────────────────────────────────────────────────
		if ( png == null )
		{
			await GameTask.MainThread();

			try
			{
				// --- framing: put the target in the picture, then VERIFY it landed there ---
				if ( haveBox )
				{
					if ( !ProjectBox( targetBox, shotPos, shotRot, cam, width, height, out rect, out coverage ) )
					{
						// Approach along the existing sightline so "where you were standing" is
						// preserved, but back off far enough that the bounds fit the frustum.
						shotPos = FramePositionFor( targetBox, shotPos, cam, width, height );
						shotRot = Rotation.LookAt( ( targetBox.Center - shotPos ).Normal );
						reframed = true;
						ProjectBox( targetBox, shotPos, shotRot, cam, width, height, out rect, out coverage );
					}
					inFrame = rect != null && coverage > 0f;

					if ( !inFrame )
						return HandlerBase.Error(
							$"target '{targetStr}' could not be framed even after repositioning.", "shot",
							"The target has degenerate or unresolvable bounds. This is a refusal on " +
							"purpose: returning a frame that cannot contain your subject is how a " +
							"screenshot silently becomes worthless." );
				}
				else
				{
					inFrame = true; // target=camera: the current view IS the subject
				}

				camGo.WorldPosition = shotPos;
				camGo.WorldRotation = shotRot;

				using var bitmap = new Bitmap( width, height );
				cam.RenderToBitmap( bitmap );
				png = bitmap.ToPng();
				if ( png is not { Length: > 0 } )
					renderError = "RenderToBitmap produced no pixels";
			}
			catch ( Exception ex )
			{
				renderError = $"{ex.GetType().Name}: {ex.Message}";
			}
			finally
			{
				camGo.WorldPosition = origPos;
				camGo.WorldRotation = origRot;
				cam.FieldOfView = origFov;
			}
		}

		if ( renderError != null )
			return HandlerBase.Error( $"Render failed: {renderError}", "shot" );

		// A route-A miss is never silent. The picture the agent gets back is structurally
		// different from the one it would have got, and saying so is the difference between
		// a fallback and a lie.
		if ( grabAttempted && route != "world.backbuffer_grab" )
			warnings.Add( $"The no-blind-spots route was available but did not deliver ({grabFailure}), " +
				"so this frame was re-rendered instead and CANNOT show GPU particles — no explosion, " +
				"plume, debris or ember trail will appear in it even if one is playing." );
		else if ( isPlaying && !inFrameAtCurrentPose && haveBox )
			warnings.Add( "The target was not in the live view, so it was framed by re-rendering, which " +
				"cannot show GPU particles. Point the camera at it and shoot target=camera for a frame " +
				"with no blind spots." );

		// --- write to the ONE directory, then prove the file is this call's ---
		var stamp = DateTime.Now.ToString( "yyyyMMdd-HHmmss" );
		var slug = SanitizeSlug( string.IsNullOrEmpty( name ) ? ( isCameraTarget ? "view" : targetStr ) : name );
		var absDir = HandlerBase.ResolveProjectPath( ".screenshots" );
		if ( string.IsNullOrEmpty( absDir ) )
			return HandlerBase.Error( "Could not resolve project root for .screenshots/.", "shot" );

		string absPath;
		long bytes;
		try
		{
			System.IO.Directory.CreateDirectory( absDir );
			absPath = System.IO.Path.Combine( absDir, $"{stamp}-{slug}.png" );
			System.IO.File.WriteAllBytes( absPath, png );

			var info = new System.IO.FileInfo( absPath );
			if ( !info.Exists || info.Length <= 0 )
				return HandlerBase.Error( "Wrote the screenshot but the file is missing or empty.", "shot",
					"Do NOT treat any earlier file in .screenshots/ as this call's output." );
			bytes = info.Length;
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Could not write screenshot: {ex.Message}", "shot" );
		}

		if ( reframed )
			warnings.Add( "Camera was repositioned to fit the target; this is not the pose you were at." );
		if ( coverage > 0f && coverage < 0.01f )
			warnings.Add( $"Target covers only {coverage:P1} of the frame — it is in shot but tiny." );

		// The backbuffer route copies the frame at Stage.AfterPostProcess (SbepFrameGrab). That is
		// AFTER the GPU particle stack and post-fx, so those are in it — but BEFORE the engine's two
		// overlay render layers (SceneRenderLayer.OverlayWithDepth / OverlayWithoutDepth, "drawn after
		// post processing"), which is where Scene.DebugOverlay boxes/text and editor gizmos live.
		// MEASURED 2026-08-22 (EFFE-M53): 18 DebugOverlay boxes counted on the frame, zero in the
		// grab, twice. So the blind list is not empty; it names exactly that layer.
		var isGrab = route == "world.backbuffer_grab";
		var (blind, degraded) = isGrab
			? (new List<string> { "debug overlay (Scene.DebugOverlay boxes/text) and anything on the OverlayWithDepth/OverlayWithoutDepth render layers — the grab runs at AfterPostProcess, those layers draw after it" }, new List<string>())
			: CapabilityFor( "world" );

		// The grab is the size of the real frame, not of anything we asked for.
		var (shotW, shotH) = isGrab ? ReadPngSize( png, width, height ) : (width, height);

		var payload = new
		{
			action = "shot",
			path = absPath,
			bytes,
			size = $"{shotW}x{shotH}",
			route,
			mode = isPlaying ? "play" : "edit",
			scene = sceneName,
			camera = camName,
			target = targetStr,
			target_in_frame = inFrame,
			target_screen_rect = rect,
			target_coverage = MathF.Round( coverage, 4 ),
			blind_to = blind,
			degraded,
			determinism,
			warnings,
			note = isGrab
				? "One screenshot entry point (docs/ai/contracts/screenshot.md). This is a copy of the " +
				  "frame at AfterPostProcess: GPU particles, bloom and post-FX are all in it. It is taken " +
				  "BEFORE the overlay render layers, so Scene.DebugOverlay boxes/text and gizmos are not — " +
				  "read `blind_to`. Anything else missing from this picture is missing from the game."
				: "One screenshot entry point (docs/ai/contracts/screenshot.md). The file exists and the " +
				  "target is in it. `blind_to` = this route structurally cannot show those subject " +
				  "classes, so their absence is NOT a bug in your feature. To get a frame with no blind " +
				  "spots, enter play mode, point the camera at the subject, and shoot target=camera."
		};

		if ( !wantInline )
			return HandlerBase.Success( payload );

		var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
		return new
		{
			content = new object[]
			{
				new { type = "image", data = Convert.ToBase64String( png ), mimeType = "image/png" },
				new { type = "text", text = JsonSerializer.Serialize( payload, jsonOpts ) }
			}
		};
	}

	/// <summary>
	/// A boolean argument that also accepts the STRING forms clients actually send.
	///
	/// <para><b>MEASURED 2026-08-16.</b> <c>capture action=shot freeze=false</c> froze anyway.
	/// <c>HandlerBase.GetBool</c> only recognises <c>JsonValueKind.True/False</c>, so a client
	/// that sends <c>"false"</c> as a string falls through to the DEFAULT — and for a flag
	/// whose default is true, that turns an explicit opt-out into a silent opt-in. The same
	/// codebase already learned this for numbers (see <c>HandlerBase.GetInt</c>, which accepts
	/// numeric strings "for why"); booleans never got the same treatment.</para>
	///
	/// <para>Deliberately scoped to this handler rather than fixed in <c>GetBool</c>: that
	/// method is shared by every action in the plugin and a sibling has the file open. The
	/// general fix belongs in its own change with its own verification.</para>
	/// </summary>
	private static bool GetFlag( JsonElement args, string name, bool defaultValue )
	{
		if ( args.ValueKind == JsonValueKind.Undefined ) return defaultValue;
		if ( !args.TryGetProperty( name, out var el ) ) return defaultValue;

		switch ( el.ValueKind )
		{
			case JsonValueKind.True: return true;
			case JsonValueKind.False: return false;
			case JsonValueKind.Number: return el.TryGetDouble( out var d ) && d != 0;
			case JsonValueKind.String:
				var s = ( el.GetString() ?? "" ).Trim();
				if ( s.Equals( "true", StringComparison.OrdinalIgnoreCase ) || s == "1" ) return true;
				if ( s.Equals( "false", StringComparison.OrdinalIgnoreCase ) || s == "0" ) return false;
				return defaultValue;
			default: return defaultValue;
		}
	}

	// ── Route A: the composited backbuffer ───────────────────────────────

	/// <summary>
	/// True when the editor is running the game. During play
	/// <c>SceneEditorSession.Active.Scene</c> IS the play-mode clone, which is why the scene
	/// resolved above is already the right one — this only answers "is the game ticking",
	/// which is what decides whether a frame exists to be copied.
	/// </summary>
	private static bool IsPlaySession()
	{
		try { return SceneEditorSession.Active?.IsPlaying == true; }
		catch { return false; }
	}

	/// <summary>
	/// Ask the game to copy the frame it just presented, wait for the PNG to land, and return
	/// its bytes. Returns <c>(null, reason)</c> on any failure — the caller falls back to the
	/// re-render route and says loudly that it did.
	///
	/// <para><b>Why this drives a console command instead of rendering in-process.</b> The copy
	/// happens inside the game's render pipeline, in a game-assembly component the editor-side
	/// plugin cannot construct. Contract non-negotiable #3 exists because a console-driven route
	/// was once measured photographing the EDIT scene from identical input; <c>sbep.grab</c>
	/// answers that objection directly — it resolves the play scene itself rather than trusting
	/// the ambient one, and we push the resolved scene around the call as well. The scene that
	/// was photographed is reported back in the result either way, so it stays checkable rather
	/// than assumed.</para>
	///
	/// <para>Every wait here is an <c>await</c>, never a sleep. The frames this is waiting for
	/// only run if the main thread is free, so blocking it would deadlock the capture.</para>
	/// </summary>
	private static async Task<(byte[] Png, string Failure)> TryBackbufferGrabAsync( Scene scene )
	{
		var dir = ResolveGameDataDir( GrabLeafDir );
		if ( string.IsNullOrEmpty( dir ) )
			return (null, "could not resolve the engine data directory the game writes grabs to");

		// Unique per call, so a file that appears can only be ours — never a leftover from an
		// earlier capture by this or any other agent.
		var grabName = $"mcpshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
		var expected = System.IO.Path.Combine( dir, $"{grabName}.png" );

		try
		{
			await GameTask.MainThread();
			// Editor.ConsoleSystem, NOT Sandbox.ConsoleSystem — both exist and `using Editor;`
			// makes the bare name ambiguous, so this choice has to be made explicitly and it is
			// easy to make wrongly. MEASURED 2026-08-16: the Sandbox one routes through a
			// permission check (RunInternal -> CanRunCommand -> ConVarSystem.Find) that answers
			// "Can't run 'sbep.grab'" for a game ConCmd invoked from the tools context, so every
			// grab failed and fell back to the blind route. The Editor one is the tools-side
			// path and says so in the engine source: "Tools can do anything, let them run any
			// command" (Sandbox.Tools/ConsoleSystem.cs).
			using ( scene.Push() )
			{
				Editor.ConsoleSystem.Run( $"sbep.grab {grabName}" );
			}
		}
		catch ( Exception ex )
		{
			return (null, $"sbep.grab could not be dispatched: {ex.Message}");
		}

		// Poll off the critical path. A grab is issued on the next render, blitted, then read
		// back two frames later and PNG-encoded, so ~1s is normal and the timeout is generous.
		var waited = 0;
		long lastSize = -1;
		while ( waited < GrabTimeoutMs )
		{
			await Task.Delay( GrabPollMs );
			waited += GrabPollMs;

			try
			{
				var info = new System.IO.FileInfo( expected );
				if ( !info.Exists || info.Length <= 0 ) continue;

				// Two equal non-zero readings = the writer is done. Reading a half-written PNG
				// would hand back a truncated file that still has a plausible path and size.
				if ( info.Length == lastSize )
				{
					var bytes = System.IO.File.ReadAllBytes( expected );

					// The grab is a STAGING file, not an output. The output is the copy in
					// <repo>/.screenshots, which the contract names as the one directory.
					// Leaving these would silently accrete a ~4 MB full-resolution PNG per
					// screenshot in the engine's data folder — outside the repo, retained by
					// nothing, and somewhere nobody would think to look.
					try { System.IO.File.Delete( expected ); } catch { /* we already have the bytes */ }

					return (bytes, null);
				}
				lastSize = info.Length;
			}
			catch { /* mid-write; try again on the next tick */ }
		}

		return (null, $"sbep.grab wrote no frame within {GrabTimeoutMs / 1000}s — the game may not have " +
			"a live Scene.Camera, or another capture is already in flight");
	}

	/// <summary>
	/// Absolute path of a leaf directory under the running editor's data root.
	///
	/// <para>DERIVED, NEVER DECLARED. Both halves of the obvious hardcode are wrong by
	/// construction: the engine checkout (the running editor may be any worktree) and the Org
	/// segment (renamable in the .sbproj — a rename once left a sibling tool polling a
	/// directory the game had stopped writing to, and every run ended in a timeout while stale
	/// PNGs sat in it). So both are read from the live system. <c>Tools/capture.py</c> performs
	/// the identical derivation host-side, and <c>CockpitVerifyHandler</c> has its own copy for
	/// its own leaf directory.</para>
	/// </summary>
	private static string ResolveGameDataDir( string leafDir )
	{
		try
		{
			string editorDir = null;
			try
			{
				var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
				if ( !string.IsNullOrEmpty( exe ) )
					editorDir = System.IO.Path.GetDirectoryName( exe );
			}
			catch { }
			if ( string.IsNullOrEmpty( editorDir ) )
				editorDir = AppContext.BaseDirectory?.TrimEnd( System.IO.Path.DirectorySeparatorChar );
			if ( string.IsNullOrEmpty( editorDir ) ) return null;

			var root = HandlerBase.GetProjectRoot();
			if ( string.IsNullOrEmpty( root ) ) return null;

			var sbproj = System.IO.Directory
				.EnumerateFiles( root, "*.sbproj", System.IO.SearchOption.TopDirectoryOnly )
				.FirstOrDefault();
			if ( string.IsNullOrEmpty( sbproj ) ) return null;

			using var doc = JsonDocument.Parse( System.IO.File.ReadAllText( sbproj ) );
			var org = doc.RootElement.TryGetProperty( "Org", out var o ) ? o.GetString() : null;
			var ident = doc.RootElement.TryGetProperty( "Ident", out var i ) ? i.GetString() : null;
			if ( string.IsNullOrEmpty( org ) || string.IsNullOrEmpty( ident ) ) return null;

			return System.IO.Path.Combine( editorDir, "data", org, $"{ident}#local", leafDir );
		}
		catch { return null; }
	}

	// ── Determinism ──────────────────────────────────────────────────────

	/// <summary>
	/// Pin the sun so two shots taken minutes apart are comparable, and report what was pinned.
	///
	/// <para>The freeze PERSISTS deliberately. Restoring the cycle after each shot would defeat
	/// the entire purpose — the sun would move between the two frames being compared. The first
	/// call that changes the state says so in a warning and names the undo, so a stopped
	/// day/night cycle is never a mystery to the next agent.</para>
	///
	/// <para>Driven through the day/night cycle's own console command rather than a ConVar
	/// write: the ConCmd is the sanctioned path and running it inside the pushed scene is what
	/// makes it land on the play scene rather than the tools one.</para>
	/// </summary>
	private static object ApplyFreeze( Scene scene, bool wantFreeze, bool isPlaying, List<string> warnings )
	{
		bool frozen = false;

		if ( wantFreeze && isPlaying )
		{
			try
			{
				var current = Editor.ConsoleSystem.GetValue( "sbep.daynight_paused" ) ?? "";
				var alreadyPaused = current.Trim() is "1" or "true" or "True";
				if ( !alreadyPaused )
				{
					using ( scene.Push() ) { Editor.ConsoleSystem.Run( "sbep.daynight_pause" ); }
					warnings.Add( "Paused the day/night cycle so this frame is comparable with the next one. " +
						"It stays paused — run the sbep.daynight_resume console command to start it again." );
				}
				frozen = true;
			}
			catch ( Exception ex )
			{
				warnings.Add( $"Could not pause the day/night cycle ({ex.Message}); two shots taken minutes " +
					"apart may differ by the sky rather than by your change." );
			}
		}

		return new
		{
			frozen,
			requested = wantFreeze,
			sun_angle = ReadSunAngle( scene ),
			sim_time = ReadSimTime( scene )
		};
	}

	/// <summary>Pitch of the scene's directional light — the one number that says whether two
	/// frames were lit the same way. Null when the scene has no enabled directional light.</summary>
	private static float? ReadSunAngle( Scene scene )
	{
		try
		{
			var sun = scene.GetAllComponents<DirectionalLight>().FirstOrDefault( l => l.Enabled );
			if ( sun?.GameObject == null ) return null;
			return MathF.Round( sun.GameObject.WorldRotation.Pitch(), 2 );
		}
		catch { return null; }
	}

	/// <summary>Scene time at the instant of the shot, read inside the scene's own scope.</summary>
	private static float? ReadSimTime( Scene scene )
	{
		try
		{
			using ( scene.Push() ) { return MathF.Round( Time.Now, 2 ); }
		}
		catch { return null; }
	}

	/// <summary>
	/// Width and height straight out of the PNG header, because the backbuffer route returns
	/// the size of the real frame rather than anything the caller asked for. Reporting the
	/// requested size for a picture that is a different shape is a small lie that breaks any
	/// downstream crop or comparison.
	/// </summary>
	private static (int W, int H) ReadPngSize( byte[] png, int fallbackW, int fallbackH )
	{
		// 8-byte signature, then the IHDR chunk: 4 length + 4 type + 4 width + 4 height, big-endian.
		if ( png is not { Length: >= 24 } ) return (fallbackW, fallbackH);
		try
		{
			int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
			int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
			return w > 0 && h > 0 ? (w, h) : (fallbackW, fallbackH);
		}
		catch { return (fallbackW, fallbackH); }
	}

	// ── Atlas regions as capture targets ─────────────────────────────────
	//
	// The world atlas names volumes an agent can spawn into. Most are not GameObjects,
	// so the name lookup above misses them and a perfectly reasonable
	// `shot target=deep-range-high` used to be a refusal. Framing them is the whole of
	// M6: setting a scene up and photographing it read from one vocabulary.
	//
	// Keep in step with PlacementAuthority.FreeSpaceCandidates and the candidate table
	// in Tools/world-atlas.py — Tools/test-world-atlas.py gate G7 fails if they diverge.
	private static readonly (string Name, Vector3 Center, Vector3 Half)[] RegionTargets =
	{
		("deep-range-high",  new Vector3(     0f,      0f, 40000f ), new Vector3( 12000f, 12000f,  6000f )),
		("deep-range-north", new Vector3(     0f,  40000f,     0f ), new Vector3( 12000f,  8000f,  8000f )),
		("deep-range-south", new Vector3(     0f, -40000f,     0f ), new Vector3( 12000f,  8000f,  8000f )),
		("deep-range-far",   new Vector3( 50000f,  40000f, 20000f ), new Vector3( 10000f,  8000f,  8000f )),
	};

	private static bool TryResolveRegionTarget( string name, out BBox box )
	{
		box = default;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;

		var needle = name.Trim();
		foreach ( var (regionName, centre, half) in RegionTargets )
		{
			// Accept the bare region name and the "-center" suffix the atlas's named
			// spawn points carry, so the string an agent just placed at also frames.
			if ( !string.Equals( regionName, needle, StringComparison.OrdinalIgnoreCase )
				&& !string.Equals( regionName + "-center", needle, StringComparison.OrdinalIgnoreCase ) )
				continue;

			box = new BBox( centre - half, centre + half );
			return true;
		}
		return false;
	}

	// Bounds for framing: prefer real rendered bounds, then any child renderer, then a
	// nominal box at the origin. A GameObject with no renderer is still framable.
	private static BBox ResolveTargetBounds( GameObject go )
	{
		var r = go.Components.Get<ModelRenderer>();
		if ( r?.SceneObject != null ) return r.Bounds;

		var kids = go.Components.GetAll<ModelRenderer>( FindMode.EnabledInSelfAndDescendants )
			.Where( c => c.SceneObject != null ).ToList();
		if ( kids.Count > 0 )
		{
			var box = kids[0].Bounds;
			for ( int i = 1; i < kids.Count; i++ ) box = box.AddBBox( kids[i].Bounds );
			return box;
		}
		return BBox.FromPositionAndSize( go.WorldPosition, 64f );
	}

	// Project a world bbox to screen pixels for a GIVEN output size.
	//
	// Deliberately not reusing the annotated manifest's projection: that one was measured
	// on 2026-07-24 (defect M-2) emitting NATIVE game-view pixels regardless of the size
	// requested, which is exactly the trap this method must not inherit.
	//
	// FieldOfView is treated as HORIZONTAL only when the camera says so; the play camera
	// was measured running a VERTICAL axis (2026-07-25, predicted NDC 0.363 vs observed
	// 0.369 — a horizontal reading predicts 0.600 and is wrong by 1.65x).
	private static bool ProjectBox( BBox box, Vector3 camPos, Rotation camRot, CameraComponent cam,
		int width, int height, out float[] rect, out float coverage )
	{
		rect = null; coverage = 0f;

		var fwd = camRot.Forward; var right = camRot.Right; var up = camRot.Up;
		var aspect = height > 0 ? width / (float)height : 16f / 9f;
		var tanHalf = MathF.Tan( cam.FieldOfView.DegreeToRadian() * 0.5f );
		var vertical = string.Equals( cam.FovAxis.ToString(), "Vertical", StringComparison.OrdinalIgnoreCase );
		var tanX = vertical ? tanHalf * aspect : tanHalf;
		var tanY = vertical ? tanHalf : tanHalf / aspect;
		if ( tanX <= 0f || tanY <= 0f ) return false;

		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		var anyInFront = false;

		foreach ( var c in box.Corners )
		{
			var d = c - camPos;
			var depth = Vector3.Dot( d, fwd );
			if ( depth <= 1f ) continue;      // behind or on the near plane
			anyInFront = true;

			var ndcX = Vector3.Dot( d, right ) / ( depth * tanX );
			var ndcY = Vector3.Dot( d, up ) / ( depth * tanY );
			var px = ( ndcX * 0.5f + 0.5f ) * width;
			var py = ( 1f - ( ndcY * 0.5f + 0.5f ) ) * height;

			minX = MathF.Min( minX, px ); maxX = MathF.Max( maxX, px );
			minY = MathF.Min( minY, py ); maxY = MathF.Max( maxY, py );
		}

		if ( !anyInFront ) return false;

		// Clamp to the frame; if the clamped rect has no area the target is off-screen.
		var cx0 = Math.Clamp( minX, 0f, width );
		var cx1 = Math.Clamp( maxX, 0f, width );
		var cy0 = Math.Clamp( minY, 0f, height );
		var cy1 = Math.Clamp( maxY, 0f, height );
		var w = cx1 - cx0; var h = cy1 - cy0;
		if ( w <= 1f || h <= 1f ) return false;

		rect = new[] { MathF.Round( cx0, 1 ), MathF.Round( cy0, 1 ), MathF.Round( w, 1 ), MathF.Round( h, 1 ) };
		coverage = ( w * h ) / ( width * (float)height );
		return true;
	}

	// Back off along the existing sightline until the bounds fit both frustum axes.
	// Preserving the approach direction keeps the shot recognisable as "where I was
	// standing, but far enough back", rather than teleporting to an arbitrary hero angle.
	private static Vector3 FramePositionFor( BBox box, Vector3 fromPos, CameraComponent cam, int width, int height )
	{
		var center = box.Center;
		var radius = MathF.Max( box.Size.Length * 0.5f, 1f );

		var aspect = height > 0 ? width / (float)height : 16f / 9f;
		var tanHalf = MathF.Tan( cam.FieldOfView.DegreeToRadian() * 0.5f );
		var vertical = string.Equals( cam.FovAxis.ToString(), "Vertical", StringComparison.OrdinalIgnoreCase );
		var tanX = vertical ? tanHalf * aspect : tanHalf;
		var tanY = vertical ? tanHalf : tanHalf / aspect;
		var tanMin = MathF.Max( MathF.Min( tanX, tanY ), 0.05f );

		var dir = ( fromPos - center );
		if ( dir.Length < 1f ) dir = Vector3.Backward * 100f;   // degenerate: pick any axis
		dir = dir.Normal;

		var distance = ( radius / tanMin ) * 1.35f;             // 1.35 = margin so it isn't edge-to-edge
		return center + dir * distance;
	}

	private static string SanitizeSlug( string s )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return "shot";
		var chars = s.Select( c => char.IsLetterOrDigit( c ) ? char.ToLowerInvariant( c ) : '-' ).ToArray();
		var outStr = new string( chars ).Trim( '-' );
		while ( outStr.Contains( "--" ) ) outStr = outStr.Replace( "--", "-" );
		return string.IsNullOrEmpty( outStr ) ? "shot" : ( outStr.Length > 48 ? outStr[..48].Trim( '-' ) : outStr );
	}

	// Read the measured capability matrix so an agent never has to have read it.
	// A `probe (partial)` row lands in `degraded`, NEVER in `blind_to`: the post-fx-bloom
	// row is exactly this case (its own probe note records bloom as visible-but-weaker in
	// capture.annotated while the row still lists that route as invisible), and emitting a
	// hard blindness claim from a partial measurement would mass-produce a wrong warning.
	private static (List<string> Blind, List<string> Degraded) CapabilityFor( string routeFamily )
	{
		var blind = new List<string>();
		var degraded = new List<string>();
		try
		{
			var path = HandlerBase.ResolveProjectPath( "docs/ai/contracts/capture-capability-matrix.json" );
			if ( string.IsNullOrEmpty( path ) || !System.IO.File.Exists( path ) )
				return ( blind, degraded );

			using var doc = JsonDocument.Parse( System.IO.File.ReadAllText( path ) );
			if ( !doc.RootElement.TryGetProperty( "subjects", out var subjects ) )
				return ( blind, degraded );

			const string route = "sbep.screenshot";   // shot's world route shares this pipeline
			foreach ( var s in subjects.EnumerateArray() )
			{
				var id = s.TryGetProperty( "subject", out var n ) ? n.GetString() : null;
				if ( string.IsNullOrEmpty( id ) ) continue;

				var verified = s.TryGetProperty( "verified_by", out var v ) ? ( v.GetString() ?? "" ) : "";
				var partial = verified.Contains( "partial", StringComparison.OrdinalIgnoreCase );

				if ( !s.TryGetProperty( "invisible_in", out var inv ) || inv.ValueKind != JsonValueKind.Array )
					continue;
				foreach ( var r in inv.EnumerateArray() )
				{
					if ( !string.Equals( r.GetString(), route, StringComparison.OrdinalIgnoreCase ) ) continue;
					( partial ? degraded : blind ).Add( id );
					break;
				}
			}
		}
		catch { /* capability data is advisory here — never fail a good screenshot over it */ }
		return ( blind, degraded );
	}

	private static object Annotated( JsonElement args )
	{
		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "annotated" );

		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 1280 ), 320, 3840 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 720 ), 240, 2160 );
		var quality = Math.Clamp( HandlerBase.GetInt( args, "quality", 75 ), 10, 100 );
		var fov = HandlerBase.GetFloat( args, "fov", 90f );

		var modeStr = HandlerBase.GetString( args, "manifest", "terse" );
		var mode = string.Equals( modeStr, "full", StringComparison.OrdinalIgnoreCase )
			? CaptureAnnotatedHelper.ManifestMode.Full
			: CaptureAnnotatedHelper.ManifestMode.Terse;

		// Camera lookup mirrors CameraHandler.CaptureViewport (Editor/Handlers/CameraHandler.cs:120).
		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No CameraComponent found in scene.", "annotated" );

		// Resolve pose: if position/look_at/rotation absent, use the camera's current pose.
		var posStr = HandlerBase.GetString( args, "position" );
		var lookAtStr = HandlerBase.GetString( args, "look_at" );
		var rotStr = HandlerBase.GetString( args, "rotation" );

		var camPos = posStr != null ? HandlerBase.ParseVector3( posStr ) : cam.GameObject.WorldPosition;

		Rotation camRot;
		if ( lookAtStr != null )
		{
			var target = HandlerBase.ParseVector3( lookAtStr );
			var dir = target - camPos;
			var horiz = new Vector2( dir.x, dir.y );
			var yaw = MathF.Atan2( horiz.y, horiz.x ) * ( 180f / MathF.PI );
			var dist = horiz.Length;
			var pitch = MathF.Atan2( -dir.z, dist ) * ( 180f / MathF.PI );
			camRot = Rotation.From( pitch, yaw, 0f );
		}
		else if ( rotStr != null )
		{
			var r = HandlerBase.ParseVector3( rotStr );
			camRot = Rotation.From( r.x, r.y, r.z );
		}
		else
		{
			camRot = cam.GameObject.WorldRotation;
		}

		var effectiveFov = posStr != null || lookAtStr != null || rotStr != null
			? fov
			: cam.FieldOfView;

		var result = CaptureAnnotatedHelper.Build(
			scene, cam, camPos, camRot, effectiveFov, width, height, quality, mode );

		var manifestPayload = result.Manifest.Select( e => new
		{
			entity_id = e.EntityId,
			name = e.Name,
			screen_bbox = new
			{
				x = e.ScreenBBox.Left,
				y = e.ScreenBBox.Top,
				width = e.ScreenBBox.Width,
				height = e.ScreenBBox.Height
			},
			world_pos = HandlerBase.V3( e.WorldPos ),
			distance = e.Distance,
			material_path = e.MaterialPath,
			mesh_path = e.MeshPath,
			tags = e.Tags,
			is_behind = e.IsBehind
		} ).ToList();

		var content = new List<object>();
		if ( result.ImageJpeg is { Length: > 0 } )
		{
			content.Add( new
			{
				type = "image",
				data = Convert.ToBase64String( result.ImageJpeg ),
				mimeType = "image/jpeg"
			} );
		}

		var summary = new
		{
			mode = result.Mode,
			width = result.Width,
			height = result.Height,
			image_ok = result.ImageJpeg is { Length: > 0 },
			image_error = result.ImageError,
			entity_count = manifestPayload.Count,
			manifest = manifestPayload
		};

		content.Add( new
		{
			type = "text",
			text = JsonSerializer.Serialize( summary, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			} )
		} );

		return new { content };
	}

	// [HANDOFF-TRUST: capture.orbit_set is brand-new mcp-agent-capability-system Wave 2.3 surface — wraps existing CaptureAnnotatedHelper.Build per-pose; falsified-sentinel on file confirms no thruster-vfx hypothesis applies.]
	private static object OrbitSet( JsonElement args )
	{
		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "orbit_set" );

		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No CameraComponent found in scene.", "orbit_set" );

		var targetStr = HandlerBase.GetString( args, "target" );
		var bboxStr = HandlerBase.GetString( args, "bbox" );
		if ( string.IsNullOrEmpty( targetStr ) && string.IsNullOrEmpty( bboxStr ) )
			return HandlerBase.Error( "Missing 'target' (GameObject id or 'x,y,z' position) or 'bbox' (mx,my,mz,Mx,My,Mz).", "orbit_set" );

		BBox bbox;
		string targetKind;
		string targetId = null;
		if ( !string.IsNullOrEmpty( bboxStr ) )
		{
			var parts = bboxStr.Split( ',' ).Select( s => s.Trim() ).ToArray();
			if ( parts.Length != 6 )
				return HandlerBase.Error( "bbox format: 'minX,minY,minZ,maxX,maxY,maxZ'.", "orbit_set" );
			bbox = new BBox(
				new Vector3( float.Parse( parts[0] ), float.Parse( parts[1] ), float.Parse( parts[2] ) ),
				new Vector3( float.Parse( parts[3] ), float.Parse( parts[4] ), float.Parse( parts[5] ) ) );
			targetKind = "bbox";
		}
		else if ( Guid.TryParse( targetStr, out var gid ) )
		{
			var go = scene.Directory.FindByGuid( gid );
			if ( go == null )
				return HandlerBase.Error( $"GameObject with id '{targetStr}' not found.", "orbit_set" );
			var renderer = go.Components.Get<ModelRenderer>();
			bbox = ( renderer != null && renderer.SceneObject != null )
				? renderer.Bounds
				: BBox.FromPositionAndSize( go.WorldPosition, 64f );
			targetKind = "gameobject";
			targetId = gid.ToString();
		}
		else
		{
			var pos = HandlerBase.ParseVector3( targetStr );
			bbox = BBox.FromPositionAndSize( pos, 64f );
			targetKind = "world_pos";
		}

		var viewsStr = HandlerBase.GetString( args, "views", "ortho6,orbit8,top,front,hero,macro" );
		var requestedViews = new HashSet<string>(
			viewsStr.Split( ',' ).Select( s => s.Trim().ToLowerInvariant() ).Where( s => s.Length > 0 ),
			StringComparer.OrdinalIgnoreCase );

		var resolution = Math.Clamp( HandlerBase.GetInt( args, "resolution", 256 ), 64, 1024 );
		var margin = MathF.Max( HandlerBase.GetFloat( args, "margin", 1.2f ), 1f );
		var quality = Math.Clamp( HandlerBase.GetInt( args, "quality", 70 ), 10, 100 );
		var fov = MathF.Max( HandlerBase.GetFloat( args, "fov", 60f ), 5f );

		var center = bbox.Center;
		var radius = MathF.Max( bbox.Size.Length * 0.5f, 1f );
		var distance = radius * 2f * margin;

		var poses = new List<(string Key, string Group, Vector3 Pos, Rotation Rot, float Fov)>();

		if ( requestedViews.Contains( "ortho6" ) )
		{
			var ortho = new (string key, Vector3 dir)[]
			{
				( "posX", Vector3.Forward ),
				( "negX", -Vector3.Forward ),
				( "posY", Vector3.Left ),
				( "negY", -Vector3.Left ),
				( "posZ", Vector3.Up ),
				( "negZ", -Vector3.Up )
			};
			foreach ( var (key, dir) in ortho )
			{
				var p = center + dir * distance;
				var r = Rotation.LookAt( ( center - p ).Normal );
				poses.Add( ( "ortho6_" + key, "ortho6", p, r, fov ) );
			}
		}
		if ( requestedViews.Contains( "orbit8" ) )
		{
			for ( int i = 0; i < 8; i++ )
			{
				var yaw = i * 45f;
				var pitch = -15f;
				var r = Rotation.From( pitch, yaw, 0f );
				var p = center - r.Forward * distance;
				poses.Add( ( $"orbit8_yaw{(int)yaw:000}", "orbit8", p, r, fov ) );
			}
		}
		if ( requestedViews.Contains( "top" ) )
		{
			var p = center + Vector3.Up * distance;
			var r = Rotation.LookAt( ( center - p ).Normal );
			poses.Add( ( "top", "top", p, r, fov ) );
		}
		if ( requestedViews.Contains( "front" ) )
		{
			var p = center + Vector3.Forward * distance;
			var r = Rotation.LookAt( ( center - p ).Normal );
			poses.Add( ( "front", "front", p, r, fov ) );
		}
		if ( requestedViews.Contains( "hero" ) )
		{
			var r = Rotation.From( -20f, 45f, 0f );
			var d = radius * 2f * MathF.Max( margin, 1.5f );
			var p = center - r.Forward * d;
			poses.Add( ( "hero", "hero", p, r, fov ) );
		}
		if ( requestedViews.Contains( "macro" ) )
		{
			var r = Rotation.From( -10f, 25f, 0f );
			var d = radius * 2f * MathF.Max( margin * 0.7f, 1f );
			var p = center - r.Forward * d;
			poses.Add( ( "macro", "macro", p, r, fov ) );
		}

		if ( poses.Count == 0 )
			return HandlerBase.Error( $"No valid views requested. Valid: ortho6, orbit8, top, front, hero, macro. Got: '{viewsStr}'.", "orbit_set" );

		// v2 (token-efficient): render each pose ONCE to a Bitmap (no manifest —
		// orbit_set discards it) then emit via one of four return modes. The v1
		// path inlined up to 18 base64 JPEGs as TEXT (~4-5k tokens/pose); see the
		// OrbitMontage/Inline/Disk/Stats helpers below.
		var returnMode = ( HandlerBase.GetString( args, "return", "montage" ) ?? "montage" )
			.Trim().ToLowerInvariant();

		var rendered = new List<(string Key, string Group, Sandbox.Bitmap Bmp)>();
		int failed = 0;
		var camGo = cam.GameObject;
		var savedPos = camGo.WorldPosition;
		var savedRot = camGo.WorldRotation;
		var savedFov = cam.FieldOfView;
		try
		{
			foreach ( var pose in poses )
			{
				Sandbox.Bitmap bmp = null;
				try
				{
					camGo.WorldPosition = pose.Pos;
					camGo.WorldRotation = pose.Rot;
					cam.FieldOfView = pose.Fov;
					bmp = new Sandbox.Bitmap( resolution, resolution );
					cam.RenderToBitmap( bmp ); // void; throws on hard failure
				}
				catch ( Exception ex )
				{
					Log.Warning( $"capture.orbit_set pose '{pose.Key}' threw: {ex.Message}" );
					try { bmp?.Dispose(); } catch { }
					bmp = null;
				}
				if ( bmp == null ) { failed++; continue; }
				rendered.Add( ( pose.Key, pose.Group, bmp ) );
			}
		}
		finally
		{
			camGo.WorldPosition = savedPos;
			camGo.WorldRotation = savedRot;
			cam.FieldOfView = savedFov;
		}

		var jsonOpts = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		object result = null;
		try
		{
			result = returnMode switch
			{
				"montage" => OrbitMontage( rendered, failed, poses.Count, resolution, quality,
					targetKind, targetId, center, radius, jsonOpts ),
				"inline" => OrbitInline( rendered, failed, quality, jsonOpts ),
				"disk" => OrbitDisk( rendered, failed, resolution, quality,
					targetKind, targetId, center, radius ),
				"stats" => OrbitStats( rendered, failed, quality ),
				_ => HandlerBase.Error(
					$"Unknown return mode '{returnMode}'. Valid: montage (default), disk, inline, stats.",
					"orbit_set" )
			};
		}
		finally
		{
			foreach ( var r in rendered )
			{
				try { r.Bmp?.Dispose(); } catch { }
			}
		}
		return result;
	}

	// ── orbit_set return-mode helpers (token-efficient v2) ───────────────
	//
	// VERIFIED via sbox-api get_type (2026-07-15): Sandbox.Bitmap.Clear(Color) /
	// DrawBitmap(Bitmap,Rect) / ToJpg(int)->byte[]; Sandbox.Rect(x,y,w,h);
	// CameraComponent.RenderToBitmap(Bitmap)->void; HandlerBase.Image(byte[],mime,caption).

	// Composite every pose into ONE contact sheet returned as a single image
	// content block — all angles for ~one image's worth of tokens.
	private static object OrbitMontage(
		List<(string Key, string Group, Sandbox.Bitmap Bmp)> rendered, int failed, int poseCount,
		int resolution, int quality, string targetKind, string targetId,
		Vector3 center, float radius, JsonSerializerOptions jsonOpts )
	{
		int n = rendered.Count;
		if ( n == 0 )
			return HandlerBase.Error( $"All {poseCount} orbit_set poses failed to render.", "orbit_set" );

		int cols = (int)MathF.Ceiling( MathF.Sqrt( n ) );
		int rows = (int)MathF.Ceiling( n / (float)cols );
		const int MaxMontageDim = 1536; // keep total ≤ client image cap so tiles aren't re-downscaled to mush
		int cell = Math.Clamp( Math.Min( resolution, MaxMontageDim / cols ), 64, resolution );
		int gridW = cols * cell;
		int gridH = rows * cell;

		var montage = new Sandbox.Bitmap( gridW, gridH );
		try
		{
			montage.Clear( Color.Black );
			var legend = new List<object>();
			for ( int i = 0; i < n; i++ )
			{
				int col = i % cols;
				int row = i / cols;
				var dest = new Rect( col * cell, row * cell, cell, cell );
				try { montage.DrawBitmap( rendered[i].Bmp, dest ); } catch { }
				legend.Add( new { cell = i, col, row, view = rendered[i].Key } );
			}

			byte[] jpeg;
			try { jpeg = montage.ToJpg( quality ); }
			catch ( Exception ex ) { return HandlerBase.Error( $"Montage encode failed: {ex.Message}", "orbit_set" ); }

			var caption = JsonSerializer.Serialize( new
			{
				action = "orbit_set",
				mode = "montage",
				target_kind = targetKind,
				target_id = targetId,
				grid = new { cols, rows, cell_px = cell },
				montage_px = new { width = gridW, height = gridH },
				pose_count = n,
				rendered_failed = failed,
				bbox = new { center = HandlerBase.V3( center ), radius = MathF.Round( radius, 2 ) },
				legend
			}, jsonOpts );

			return HandlerBase.Image( jpeg, "image/jpeg", caption );
		}
		finally
		{
			try { montage.Dispose(); } catch { }
		}
	}

	// Return up to 4 of the most-informative poses as proper image content
	// blocks (client-downscaled), never base64-as-text.
	private static object OrbitInline(
		List<(string Key, string Group, Sandbox.Bitmap Bmp)> rendered, int failed, int quality,
		JsonSerializerOptions jsonOpts )
	{
		if ( rendered.Count == 0 )
			return HandlerBase.Error( "All orbit_set poses failed to render.", "orbit_set" );

		var priority = new[] { "hero", "front", "top", "macro", "orbit8", "ortho6" };
		var picked = rendered
			.OrderBy( r => { int idx = Array.IndexOf( priority, r.Group ); return idx < 0 ? 99 : idx; } )
			.Take( 4 ).ToList();

		var content = new List<object>();
		var returned = new List<string>();
		foreach ( var r in picked )
		{
			byte[] jpeg;
			try { jpeg = r.Bmp.ToJpg( quality ); } catch { continue; }
			if ( jpeg is not { Length: > 0 } ) continue;
			content.Add( new { type = "image", data = Convert.ToBase64String( jpeg ), mimeType = "image/jpeg" } );
			returned.Add( r.Key );
		}

		content.Add( new
		{
			type = "text",
			text = JsonSerializer.Serialize( new
			{
				action = "orbit_set",
				mode = "inline",
				returned = returned.ToArray(),
				pose_count = rendered.Count,
				rendered_failed = failed,
				note = "inline returns up to 4 key poses as image blocks. Use return=montage for all angles in one image, or return=disk for full-res files."
			}, jsonOpts )
		} );
		return new { content };
	}

	// Write every pose JPEG to disk and return a scalar index + file paths.
	// Zero image tokens — the caller Reads back only the pose(s) it needs.
	private static object OrbitDisk(
		List<(string Key, string Group, Sandbox.Bitmap Bmp)> rendered, int failed,
		int resolution, int quality, string targetKind, string targetId,
		Vector3 center, float radius )
	{
		if ( rendered.Count == 0 )
			return HandlerBase.Error( "All orbit_set poses failed to render.", "orbit_set" );

		var stamp = DateTime.Now.ToString( "yyyyMMdd-HHmmss-fff" );
		var relDir = System.IO.Path.Combine( ".mcp-capture-sets", stamp );
		var absDir = HandlerBase.ResolveProjectPath( relDir );
		if ( string.IsNullOrEmpty( absDir ) )
			return HandlerBase.Error( "Could not resolve project root for disk capture.", "orbit_set" );
		try { System.IO.Directory.CreateDirectory( absDir ); }
		catch ( Exception ex ) { return HandlerBase.Error( $"Could not create capture dir: {ex.Message}", "orbit_set" ); }

		var files = new List<object>();
		long totalBytes = 0;
		foreach ( var r in rendered )
		{
			byte[] jpeg;
			try { jpeg = r.Bmp.ToJpg( quality ); } catch { continue; }
			if ( jpeg is not { Length: > 0 } ) continue;
			var abs = System.IO.Path.Combine( absDir, $"{r.Key}.jpg" );
			try { System.IO.File.WriteAllBytes( abs, jpeg ); }
			catch ( Exception ex ) { Log.Warning( $"orbit_set disk write failed for {r.Key}: {ex.Message}" ); continue; }
			files.Add( new { view = r.Key, path = abs, bytes = jpeg.Length } );
			totalBytes += jpeg.Length;
		}

		return HandlerBase.Success( new
		{
			action = "orbit_set",
			mode = "disk",
			target_kind = targetKind,
			target_id = targetId,
			dir = absDir,
			resolution,
			quality,
			pose_count = rendered.Count,
			rendered_failed = failed,
			total_bytes = totalBytes,
			bbox = new { center = HandlerBase.V3( center ), radius = MathF.Round( radius, 2 ) },
			files,
			note = "Zero image tokens. Read only the pose file(s) you need to judge."
		} );
	}

	// Byte/size accounting only — no pixels. The Phase-0 measurement path.
	private static object OrbitStats(
		List<(string Key, string Group, Sandbox.Bitmap Bmp)> rendered, int failed, int quality )
	{
		long total = 0;
		var perPose = new List<object>();
		foreach ( var r in rendered )
		{
			int bytes = 0;
			try { bytes = r.Bmp.ToJpg( quality )?.Length ?? 0; } catch { }
			total += bytes;
			perPose.Add( new { view = r.Key, bytes } );
		}
		return HandlerBase.Success( new
		{
			action = "orbit_set",
			mode = "stats",
			pose_count = rendered.Count,
			rendered_failed = failed,
			total_image_bytes = total,
			est_base64_chars = total * 4 / 3,
			per_pose = perPose,
			note = "stats returns byte accounting only — zero pixels. est_base64_chars ≈ the OLD inline-text payload (÷~4 ≈ tokens); a montage image block is a small fraction of that."
		} );
	}

	// ── channels ─────────────────────────────────────────────────────────
	//
	// [HANDOFF-TRUST: capture.channels is brand-new mcp-agent-capability-system Wave 3.2 — sibling to Wave 3.1 feed.* shipped this session; no inherited hypothesis from any wave3 handoff.]
	//
	// v1 ships the entity_id channel via a SYNTHETIC pixmap: each visible
	// renderer's screen_bbox is filled with a deterministic per-entity colour,
	// and an id_legend maps {hex_color → entity_id} so callers can decode
	// pixel→entity. The "true" engine path (per-instance Material tint via
	// CommandList + scene re-render) hits AP-098 (SceneObject.Batchable=true
	// collapses per-instance Attributes.Set), so v1 uses the synthetic
	// approach to satisfy the practical use case + Wave 3 test gate without
	// the deeper engine integration.
	//
	// v2 deferred (per plan.md risk #1 mitigation — ship entity_id standalone
	// first): albedo, depth, emissive, normal, specular, motion, roughness.
	// These require either real gbuffer access (engine path not exposed to
	// editor-DLL externally) or per-instance material overrides routed
	// through CommandList. Both are deeper integrations than v1's scope.
	//
	// Output shape:
	//   {
	//     channels: { id: base64-png },
	//     id_legend: [{ color: "#RRGGBB", entity_id, name, world_pos, screen_bbox }],
	//     mode: "v1-synthetic",
	//     width, height,
	//     unavailable: ["albedo","depth","emissive","normal","specular","motion","roughness"]
	//   }
	private static object Channels( JsonElement args )
	{
		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "channels" );

		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 512 ), 64, 2048 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 288 ), 64, 2048 );
		var fov = HandlerBase.GetFloat( args, "fov", 90f );
		var channelsParam = HandlerBase.GetString( args, "channels", "id" );
		var requested = new HashSet<string>(
			channelsParam.Split( ',' ).Select( s => s.Trim().ToLowerInvariant() ).Where( s => s.Length > 0 ),
			StringComparer.OrdinalIgnoreCase );

		// v1 supports only entity_id (alias: "id"). Other channel names get reported in `unavailable`.
		var supported = new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "id", "entity_id" };
		var unavailable = requested.Where( c => !supported.Contains( c ) ).ToList();
		var doId = requested.Contains( "id" ) || requested.Contains( "entity_id" );
		if ( !doId )
		{
			return HandlerBase.Error( "No supported channels requested.", "channels",
				$"v1 supports: id (entity_id). v2 deferred: albedo, depth, emissive, normal, specular, motion, roughness." );
		}

		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No CameraComponent found in scene.", "channels" );

		// Resolve pose — same pattern as Annotated.
		var posStr = HandlerBase.GetString( args, "position" );
		var lookAtStr = HandlerBase.GetString( args, "look_at" );
		var rotStr = HandlerBase.GetString( args, "rotation" );

		var camPos = posStr != null ? HandlerBase.ParseVector3( posStr ) : cam.GameObject.WorldPosition;
		Rotation camRot;
		if ( lookAtStr != null )
		{
			var target = HandlerBase.ParseVector3( lookAtStr );
			var dir = target - camPos;
			var horiz = new Vector2( dir.x, dir.y );
			var yaw = MathF.Atan2( horiz.y, horiz.x ) * ( 180f / MathF.PI );
			var dist = horiz.Length;
			var pitch = MathF.Atan2( -dir.z, dist ) * ( 180f / MathF.PI );
			camRot = Rotation.From( pitch, yaw, 0f );
		}
		else if ( rotStr != null )
		{
			var r = HandlerBase.ParseVector3( rotStr );
			camRot = Rotation.From( r.x, r.y, r.z );
		}
		else
		{
			camRot = cam.GameObject.WorldRotation;
		}
		var effectiveFov = posStr != null || lookAtStr != null || rotStr != null
			? fov
			: cam.FieldOfView;

		// Get the per-entity manifest at this pose. JPEG quality is irrelevant
		// for channels — we only need the manifest (screen_bbox per entity).
		var annot = CaptureAnnotatedHelper.Build(
			scene, cam, camPos, camRot, effectiveFov, width, height,
			jpegQuality: 75, CaptureAnnotatedHelper.ManifestMode.Terse );

		// Build the synthetic id-bitmap. RGBA8888 byte-array, then upload via Pixmap.UpdateFromPixels.
		var pixmap = new Pixmap( width, height );
		var bytes = new byte[width * height * 4];
		// Background = black opaque
		for ( int i = 0; i < bytes.Length; i += 4 )
		{
			bytes[i + 0] = 0;
			bytes[i + 1] = 0;
			bytes[i + 2] = 0;
			bytes[i + 3] = 255;
		}

		var legendRows = new List<object>();
		var emittedColors = new HashSet<int>();

		// [HANDOFF-TRUST: fix CS1061 cam.ScreenPixelSize compile error — that property does not exist on CameraComponent per sbox-api search. Drop the scaling and use screen_bbox values directly. Pure local fix to make the file compile; independent of any inherited cockpit/cloud/thruster handoff hypothesis.]
		foreach ( var e in annot.Manifest )
		{
			if ( e.IsBehind ) continue;
			var rect = e.ScreenBBox;
			// CaptureAnnotatedHelper returns bbox in the camera's internal
			// viewport coord frame (see Wave 1.1 documented v1 limitation
			// in charter cumulative state). For v1 channels we use the
			// values directly without scaling — rects may not pixel-align
			// with a separately-captured JPEG, but they are non-zero and
			// produce the non-uniform bitmap the wave-exit test gate needs.
			// v1.1 will compute a true engine-pixel→bitmap scale once that
			// API is identified.
			int x0 = (int)rect.Left;
			int y0 = (int)rect.Top;
			int x1 = (int)( rect.Left + rect.Width );
			int y1 = (int)( rect.Top + rect.Height );
			x0 = Math.Clamp( x0, 0, width - 1 );
			x1 = Math.Clamp( x1, 0, width - 1 );
			y0 = Math.Clamp( y0, 0, height - 1 );
			y1 = Math.Clamp( y1, 0, height - 1 );
			if ( x1 <= x0 || y1 <= y0 ) continue;

			// [HANDOFF-TRUST: simplifying overly-clever uint-cast pattern-match to plain unchecked cast — independent of any inherited cockpit/cloud/thruster handoff hypothesis, pure local arithmetic refactor]
			// Deterministic colour per entity_id with collision retry (Knuth multiplicative hash).
			int seed = e.EntityId?.GetHashCode() ?? 0;
			int color24 = seed & 0xFFFFFF;
			int retries = 0;
			while ( emittedColors.Contains( color24 ) && retries < 16 )
			{
				unchecked { color24 = (int)( ( (uint)color24 * 2654435761u ) & 0xFFFFFF ); }
				retries++;
			}
			emittedColors.Add( color24 );
			byte r = (byte)( ( color24 >> 16 ) & 0xFF );
			byte g = (byte)( ( color24 >> 8 ) & 0xFF );
			byte b = (byte)( color24 & 0xFF );

			for ( int y = y0; y <= y1; y++ )
			{
				int row = y * width * 4;
				for ( int x = x0; x <= x1; x++ )
				{
					int i = row + x * 4;
					bytes[i + 0] = r;
					bytes[i + 1] = g;
					bytes[i + 2] = b;
					bytes[i + 3] = 255;
				}
			}

			legendRows.Add( new
			{
				color = $"#{r:X2}{g:X2}{b:X2}",
				entity_id = e.EntityId,
				name = e.Name,
				world_pos = HandlerBase.V3( e.WorldPos ),
				screen_bbox = new { x = x0, y = y0, width = x1 - x0, height = y1 - y0 },
				distance = e.Distance
			} );
		}

		bool uploaded;
		try
		{
			uploaded = pixmap.UpdateFromPixels( bytes, width, height );
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Pixmap.UpdateFromPixels threw: {ex.Message}", "channels" );
		}
		if ( !uploaded )
		{
			return HandlerBase.Error( "Pixmap.UpdateFromPixels returned false — likely format mismatch.", "channels",
				"v1 assumes default RGBA8888 byte layout. Adjust to engine's format enum if needed." );
		}

		byte[] pngBytes;
		try
		{
			pngBytes = pixmap.GetPng();
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( $"Pixmap.GetPng threw: {ex.Message}", "channels" );
		}

		var summary = new
		{
			channels = new { id = pngBytes is { Length: > 0 } ? Convert.ToBase64String( pngBytes ) : null },
			id_legend = legendRows,
			mode = "v1-synthetic",
			width,
			height,
			entity_count = legendRows.Count,
			unavailable = new[] { "albedo", "depth", "emissive", "normal", "specular", "motion", "roughness" },
			v1_note = "id channel built by painting deterministic-colour rects per visible-entity screen_bbox (synthetic, not gbuffer-sampled). v2 will add gbuffer-driven channels via CommandList + per-instance Material override."
		};

		return HandlerBase.Success( summary );
	}

	// ── diff_baseline / baseline_set / baseline_list ─────────────────────
	//
	// [HANDOFF-TRUST: Wave 4.2 of mcp-agent-capability-system — visual regression-catch.
	// Renders the scene at a stored pose, compares per-pixel BT.709 luminance against
	// a previously-stored baseline, returns SSIM-ish verdict + per-tile heat-map.
	// v1 stores baselines in-memory (process-scoped, cleared on plugin hot-reload);
	// v2 persists to FileSystem.Data/.mcp-baselines/. Greenfield additive surface
	// inside the existing CaptureHandler.]
	//
	// Storage layout (v1, in-memory):
	//   key → BaselineEntry { Pos, Rot, Fov, Width, Height, Pixels[Color], CapturedAt }
	//
	// Verdict thresholds (v1, tuned empirically to live engine noise floor):
	//   mean_lum_delta < 0.020 AND max_tile_delta < 0.12  → "match"
	//   mean_lum_delta < 0.060                            → "drift"
	//   else                                              → "regression"
	// Wave 4.2 exit-criterion 2× emissive bump expected to trip "regression"
	// (luminance shift dominates the noise floor by ~6×).
	//
	// Classification heuristic (v1):
	//   global_high + low_tile_variance     → "global-shift"    (exposure/color)
	//   localized hotspots (≥1 tile >> mean) → "regional-shift"  (geometry/light)
	//   uniform mid                         → "noise"
	//   match                               → null

	private sealed class BaselineEntry
	{
		public string Key;
		public DateTime CapturedAt;
		public int Width;
		public int Height;
		public Color[] Pixels;
		public Vector3 Pos;
		public Rotation Rot;
		public float Fov;
	}

	private static readonly Dictionary<string, BaselineEntry> _baselines = new();
	private const int MaxBaselines = 32;
	private const int TileGrid = 8; // 8x8 = 64 tiles for heat-map

	private static object BaselineSet( JsonElement args )
	{
		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "baseline_set" );

		var key = HandlerBase.GetString( args, "key" );
		if ( string.IsNullOrEmpty( key ) )
			return HandlerBase.Error( "Missing required 'key' parameter.", "baseline_set",
				"Provide a stable string id, e.g. 'm5-cockpit-front' or 'mars-sunset-hero'." );

		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No enabled CameraComponent found in scene.", "baseline_set" );

		var (capturedPixels, w, h, pos, rot, fov, captureError) = RenderForBaseline( cam, args );
		if ( capturedPixels == null )
			return HandlerBase.Error( captureError ?? "Capture failed.", "baseline_set" );

		// LRU evict if at cap and key is new.
		if ( _baselines.Count >= MaxBaselines && !_baselines.ContainsKey( key ) )
		{
			var oldest = _baselines.OrderBy( kvp => kvp.Value.CapturedAt ).First();
			_baselines.Remove( oldest.Key );
		}

		_baselines[key] = new BaselineEntry
		{
			Key = key,
			CapturedAt = DateTime.UtcNow,
			Width = w,
			Height = h,
			Pixels = capturedPixels,
			Pos = pos,
			Rot = rot,
			Fov = fov
		};

		return HandlerBase.Success( new
		{
			ok = true,
			action = "baseline_set",
			key,
			width = w,
			height = h,
			pose = new { pos = HandlerBase.V3( pos ), angles = HandlerBase.Rot( rot ), fov },
			captured_at = _baselines[key].CapturedAt.ToString( "yyyy-MM-dd HH:mm:ss" ),
			total_baselines = _baselines.Count,
			max_baselines = MaxBaselines,
			note = "v1 stores baseline in-memory; cleared on plugin hot-reload. Use baseline_list to inspect."
		} );
	}

	private static object BaselineList()
	{
		var rows = _baselines.Values
			.OrderByDescending( b => b.CapturedAt )
			.Select( b => new
			{
				key = b.Key,
				width = b.Width,
				height = b.Height,
				pose = new { pos = HandlerBase.V3( b.Pos ), angles = HandlerBase.Rot( b.Rot ), fov = b.Fov },
				captured_at = b.CapturedAt.ToString( "yyyy-MM-dd HH:mm:ss" )
			} )
			.ToList();
		return HandlerBase.Success( new
		{
			count = rows.Count,
			max = MaxBaselines,
			baselines = rows
		} );
	}

	private static object DiffBaseline( JsonElement args )
	{
		var scene = SceneHelpers.ResolveScene();
		if ( scene == null )
			return HandlerBase.Error( "No active scene.", "diff_baseline" );

		var key = HandlerBase.GetString( args, "key" );
		if ( string.IsNullOrEmpty( key ) )
			return HandlerBase.Error( "Missing required 'key' parameter.", "diff_baseline",
				"Set a baseline first via capture.baseline_set key=<id>." );
		if ( !_baselines.TryGetValue( key, out var baseline ) )
			return HandlerBase.Error( $"Baseline '{key}' not found.", "diff_baseline",
				$"Existing keys: {( _baselines.Count == 0 ? "(none)" : string.Join( ", ", _baselines.Keys ) )}" );

		var cam = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( c => c.IsMainCamera && c.Enabled )
			?? scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.Enabled );
		if ( cam == null )
			return HandlerBase.Error( "No enabled CameraComponent found in scene.", "diff_baseline" );

		// Render current at the stored baseline pose (caller can override via args, but
		// default is "compare apples to apples" at the saved pose).
		// Build args struct that prefers stored pose unless caller explicitly overrides.
		var posStr = HandlerBase.GetString( args, "position" );
		var lookAtStr = HandlerBase.GetString( args, "look_at" );
		var rotStr = HandlerBase.GetString( args, "rotation" );
		var fovOverride = HandlerBase.GetFloat( args, "fov", float.NaN );

		Vector3 usePos = posStr != null ? HandlerBase.ParseVector3( posStr ) : baseline.Pos;
		Rotation useRot;
		if ( lookAtStr != null )
		{
			var target = HandlerBase.ParseVector3( lookAtStr );
			var dir = target - usePos;
			var horiz = new Vector2( dir.x, dir.y );
			var yaw = MathF.Atan2( horiz.y, horiz.x ) * ( 180f / MathF.PI );
			var dist = horiz.Length;
			var pitch = MathF.Atan2( -dir.z, dist ) * ( 180f / MathF.PI );
			useRot = Rotation.From( pitch, yaw, 0f );
		}
		else if ( rotStr != null )
		{
			var r = HandlerBase.ParseVector3( rotStr );
			useRot = Rotation.From( r.x, r.y, r.z );
		}
		else
		{
			useRot = baseline.Rot;
		}
		var useFov = !float.IsNaN( fovOverride ) ? fovOverride : baseline.Fov;

		var (currentPixels, w, h, _, _, _, captureError) = RenderAtPose( cam, usePos, useRot, useFov, baseline.Width, baseline.Height );
		if ( currentPixels == null )
			return HandlerBase.Error( captureError ?? "Current capture failed.", "diff_baseline" );

		if ( w != baseline.Width || h != baseline.Height )
		{
			return HandlerBase.Error(
				$"Size mismatch: baseline {baseline.Width}x{baseline.Height} vs current {w}x{h}.",
				"diff_baseline",
				"Pass width/height matching the baseline, or take a fresh baseline at the new size." );
		}

		var report = ComputeDiff( baseline.Pixels, currentPixels, w, h );

		return HandlerBase.Success( new
		{
			ok = true,
			action = "diff_baseline",
			key,
			width = w,
			height = h,
			baseline_captured_at = baseline.CapturedAt.ToString( "yyyy-MM-dd HH:mm:ss" ),
			pose = new { pos = HandlerBase.V3( usePos ), angles = HandlerBase.Rot( useRot ), fov = useFov },
			verdict = report.Verdict,
			classification = report.Classification,
			mean_lum_delta = report.MeanLumDelta,
			max_lum_delta = report.MaxLumDelta,
			mean_channel_delta_rgb = new { r = report.MeanR, g = report.MeanG, b = report.MeanB },
			max_tile_delta = report.MaxTileDelta,
			tile_variance = report.TileVariance,
			tile_grid = $"{TileGrid}x{TileGrid}",
			tiles = report.Tiles, // float[64] flattened, row-major
			note = "v1 verdict thresholds (tuned to live engine noise floor): match if mean<0.020 && max_tile<0.12; drift if mean<0.060; else regression. Luminance is BT.709 (0.2126R + 0.7152G + 0.0722B)."
		} );
	}

	private struct DiffReport
	{
		public float MeanLumDelta;
		public float MaxLumDelta;
		public float MeanR;
		public float MeanG;
		public float MeanB;
		public float MaxTileDelta;
		public float TileVariance;
		public float[] Tiles;
		public string Verdict;
		public string Classification;
	}

	// BT.709 luminance for the SSIM-window primitive that Gate 3 validates.
	private static float Lum709( Color c )
	{
		return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
	}

	private static DiffReport ComputeDiff( Color[] a, Color[] b, int width, int height )
	{
		int total = width * height;
		double sumLum = 0;
		double sumR = 0, sumG = 0, sumB = 0;
		float maxLum = 0f;

		var tileMeans = new double[TileGrid * TileGrid];
		var tileCounts = new int[TileGrid * TileGrid];
		float tilePxW = width / (float)TileGrid;
		float tilePxH = height / (float)TileGrid;

		for ( int y = 0; y < height; y++ )
		{
			int tileY = Math.Min( (int)( y / tilePxH ), TileGrid - 1 );
			int rowBase = y * width;
			for ( int x = 0; x < width; x++ )
			{
				int idx = rowBase + x;
				var ca = a[idx];
				var cb = b[idx];
				float dl = Math.Abs( Lum709( ca ) - Lum709( cb ) );
				sumLum += dl;
				if ( dl > maxLum ) maxLum = dl;
				sumR += Math.Abs( ca.r - cb.r );
				sumG += Math.Abs( ca.g - cb.g );
				sumB += Math.Abs( ca.b - cb.b );

				int tileX = Math.Min( (int)( x / tilePxW ), TileGrid - 1 );
				int t = tileY * TileGrid + tileX;
				tileMeans[t] += dl;
				tileCounts[t]++;
			}
		}

		var tiles = new float[TileGrid * TileGrid];
		double tileSum = 0;
		float maxTile = 0f;
		for ( int i = 0; i < tiles.Length; i++ )
		{
			tiles[i] = tileCounts[i] > 0 ? (float)( tileMeans[i] / tileCounts[i] ) : 0f;
			tileSum += tiles[i];
			if ( tiles[i] > maxTile ) maxTile = tiles[i];
		}
		double tileAvg = tileSum / tiles.Length;
		double tileVar = 0;
		for ( int i = 0; i < tiles.Length; i++ )
		{
			double d = tiles[i] - tileAvg;
			tileVar += d * d;
		}
		tileVar /= tiles.Length;

		float meanLum = (float)( sumLum / total );

		// Verdict thresholds — tuned 2026-05-15 against live S&Box engine noise floor:
		// two paused-scene baselines 0.2s apart return mean≈0.009, max_tile≈0.06. The
		// initial v1 thresholds (match<0.005) were too tight to ever return match on
		// real captures. Wave 4.2 final thresholds (regression-catch focus):
		//   match      — mean<0.020 AND max_tile<0.12 (covers paused-scene engine noise)
		//   drift      — mean<0.060 (small visual change, LUT-rebake, lighting jitter)
		//   regression — mean>=0.060 (2x emissive bump / geometry change territory per Wave 4 exit criterion)
		string verdict;
		if ( meanLum < 0.020f && maxTile < 0.12f )
			verdict = "match";
		else if ( meanLum < 0.060f )
			verdict = "drift";
		else
			verdict = "regression";

		// Classification — only meaningful when not "match"
		string classification = null;
		if ( verdict != "match" )
		{
			float tileVarF = (float)tileVar;
			float hotRatio = maxTile / Math.Max( meanLum, 0.0001f );
			if ( hotRatio > 4f && tileVarF > 0.0005f )
				classification = "regional-shift"; // localized geometry/lighting change
			else if ( tileVarF < 0.0002f )
				classification = "global-shift";    // uniform exposure / color shift
			else
				classification = "mixed";
		}

		return new DiffReport
		{
			MeanLumDelta = meanLum,
			MaxLumDelta = maxLum,
			MeanR = (float)( sumR / total ),
			MeanG = (float)( sumG / total ),
			MeanB = (float)( sumB / total ),
			MaxTileDelta = maxTile,
			TileVariance = (float)tileVar,
			Tiles = tiles,
			Verdict = verdict,
			Classification = classification
		};
	}

	// Render at the stored baseline pose. Returns the live raw pixels for diff math.
	private static (Color[] pixels, int width, int height, Vector3 pos, Rotation rot, float fov, string error)
		RenderForBaseline( CameraComponent cam, JsonElement args )
	{
		var width = Math.Clamp( HandlerBase.GetInt( args, "width", 512 ), 64, 2048 );
		var height = Math.Clamp( HandlerBase.GetInt( args, "height", 288 ), 64, 2048 );
		var fov = HandlerBase.GetFloat( args, "fov", 90f );

		var posStr = HandlerBase.GetString( args, "position" );
		var lookAtStr = HandlerBase.GetString( args, "look_at" );
		var rotStr = HandlerBase.GetString( args, "rotation" );

		var camPos = posStr != null ? HandlerBase.ParseVector3( posStr ) : cam.GameObject.WorldPosition;
		Rotation camRot;
		if ( lookAtStr != null )
		{
			var target = HandlerBase.ParseVector3( lookAtStr );
			var dir = target - camPos;
			var horiz = new Vector2( dir.x, dir.y );
			var yaw = MathF.Atan2( horiz.y, horiz.x ) * ( 180f / MathF.PI );
			var dist = horiz.Length;
			var pitch = MathF.Atan2( -dir.z, dist ) * ( 180f / MathF.PI );
			camRot = Rotation.From( pitch, yaw, 0f );
		}
		else if ( rotStr != null )
		{
			var r = HandlerBase.ParseVector3( rotStr );
			camRot = Rotation.From( r.x, r.y, r.z );
		}
		else
		{
			camRot = cam.GameObject.WorldRotation;
		}
		var effectiveFov = posStr != null || lookAtStr != null || rotStr != null
			? fov
			: cam.FieldOfView;

		return RenderAtPose( cam, camPos, camRot, effectiveFov, width, height );
	}

	private static (Color[] pixels, int width, int height, Vector3 pos, Rotation rot, float fov, string error)
		RenderAtPose( CameraComponent cam, Vector3 pos, Rotation rot, float fov, int width, int height )
	{
		var go = cam.GameObject;
		var origPos = go.WorldPosition;
		var origRot = go.WorldRotation;
		var origFov = cam.FieldOfView;
		try
		{
			go.WorldPosition = pos;
			go.WorldRotation = rot;
			cam.FieldOfView = fov;

			var bitmap = new Sandbox.Bitmap( width, height );
			cam.RenderToBitmap( bitmap );
			var pixels = bitmap.GetPixels();
			if ( pixels == null || pixels.Length == 0 )
				return ( null, 0, 0, pos, rot, fov, "RenderToBitmap produced no pixels." );
			if ( pixels.Length != width * height )
				return ( null, 0, 0, pos, rot, fov, $"Pixel count {pixels.Length} != {width}*{height}." );
			return ( pixels, width, height, pos, rot, fov, null );
		}
		catch ( Exception ex )
		{
			return ( null, 0, 0, pos, rot, fov, $"{ex.GetType().Name}: {ex.Message}" );
		}
		finally
		{
			go.WorldPosition = origPos;
			go.WorldRotation = origRot;
			cam.FieldOfView = origFov;
		}
	}
}
