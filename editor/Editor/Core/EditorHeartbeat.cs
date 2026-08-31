// Editor/Core/EditorHeartbeat.cs
// [HANDOFF-TRUST: greenfield Phase-1 liveness component independent of all inherited handoff hypotheses; start-dedup matched unrelated handoffs by substring]
//
// multi-agent-editor-resilience Phase 1 — editor MAIN-THREAD liveness heartbeat.
//
// A [EditorEvent.Frame] handler runs on the editor main thread every frame. It
// (a) stamps an in-memory monotonic tick read by mcp.health on ANY thread as a
// stall self-report, and (b) throttled to ~3s, writes
// .claude/session-state/editor-heartbeat. A frozen main thread stops both, so
// tools/editor-health.py can classify "stalled" (vs "down" = TCP port closed).
//
// WHY main-thread-driven: the MCP HTTP listener answers on background threads
// (ArenulaMcpServer.ListenLoop -> Task.Run), so a TCP/HTTP probe reports "up"
// even when the editor main thread is frozen (the zombie case). Only a tick
// driven by the frame loop proves the editor is actually pumping. EditorEvent.Frame
// ("tool.frame", raised from ManagedTools.RunFrame) is the canonical, STABLE
// per-frame editor seam and fires in EDIT mode (unlike GameObjectSystem Stage.*,
// which only ticks in play mode). It is not a fork patch — degrades gracefully on
// stock engine.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Editor;
using Sandbox;

namespace Arenula;

internal static class EditorHeartbeat
{
	// Disk writes are throttled; the in-memory tick updates every frame.
	private const double WriteIntervalSeconds = 3.0;

	private static long _lastTickUnixMs;
	private static long _lastWriteUnixMs;
	private static bool _loggedWriteError; // [HANDOFF-TRUST: greenfield Phase-1 heartbeat field, independent of all inherited handoff hypotheses]

	/// <summary>
	/// Unix-ms of the last editor main-thread frame. Readable from ANY thread
	/// (e.g. mcp.health on the HTTP thread) as a stall self-report: if this is
	/// stale while the process still answers HTTP, the main thread is frozen.
	/// Returns 0 if no frame has run yet (e.g. plugin loaded but editor idle).
	/// </summary>
	internal static long LastMainThreadTickUnixMs => Volatile.Read( ref _lastTickUnixMs );

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		// EditorEvent contract: handlers MUST swallow all exceptions. An
		// unhandled throw here breaks frame dispatch for every other subscriber
		// (silent editor-wide breakage). This handler is also HOT — keep the
		// per-frame path allocation-free; only allocate on the throttled write.
		try
		{
			var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			Volatile.Write( ref _lastTickUnixMs, nowMs );

			if ( nowMs - _lastWriteUnixMs < (long)(WriteIntervalSeconds * 1000) )
				return;
			_lastWriteUnixMs = nowMs;

			WriteHeartbeatFile( nowMs );
		}
		catch
		{
			// Never propagate out of a frame handler.
		}
	}

	// [HANDOFF-TRUST: greenfield Phase-1 heartbeat writer, independent of all inherited handoff hypotheses]
	private static void WriteHeartbeatFile( long nowMs )
	{
		try
		{
			// Use HandlerBase.GetProjectRoot() (Project.Current.GetRootPath) — the
			// SAME resolver AgentObserveHandler uses to reach .claude/session-state.
			// WorkspacePaths.ProjectRoot uses an asset-path heuristic that can
			// resolve to a base-game/cloud asset (wrong root) — avoided here.
			var root = HandlerBase.GetProjectRoot();
			if ( string.IsNullOrEmpty( root ) )
				return;

			var dir = Path.Combine( root, ".claude", "session-state" );
			Directory.CreateDirectory( dir );

			string sceneName = null;
			bool playing = false;
			try
			{
				var session = SceneEditorSession.Active;
				sceneName = session?.Scene?.Name;
				playing = session?.IsPlaying == true;
			}
			catch { }

			var json = JsonSerializer.Serialize( new
			{
				ts_ms = nowMs,
				pid = Environment.ProcessId,
				scene = sceneName,
				playing,
				// Game-assembly hotload generations since editor boot — the
				// editor-decay signal read by tools/hotload-gen.py (2026-07-12
				// agent-friction-audit F1). See FeedHandler.HotloadGeneration.
				hotload_gen = FeedHandler.HotloadGeneration,
				writer = "EditorHeartbeat",
			} );

			// Atomic: write a temp sibling then move-overwrite so a concurrent
			// reader (editor-health.py) never sees a torn file. One editor
			// process per project => a fixed temp name is safe.
			var path = Path.Combine( dir, "editor-heartbeat" );
			var tmp = path + ".tmp";
			File.WriteAllText( tmp, json );
			File.Move( tmp, path, overwrite: true );
		}
		catch ( Exception ex )
		{
			// Log ONCE (not every 3s) so a persistent failure is diagnosable
			// instead of silently swallowed.
			if ( !_loggedWriteError )
			{
				_loggedWriteError = true;
				try { Log.Warning( $"[EditorHeartbeat] heartbeat file write failed (logged once): {ex.Message}" ); } catch { }
			}
		}
	}
}
