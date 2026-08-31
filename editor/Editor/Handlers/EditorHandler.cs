// Editor/Handlers/EditorHandler.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Structured log entry captured by MCP. Mirrors the non-redundant fields
/// of Sandbox.LogEvent plus a Source tag identifying which capture path produced it.
/// </summary>
internal readonly record struct McpLogEntry(
    DateTime Time,
    string   Level,
    string   Logger,
    string   Message,
    string   Stack,
    string   Source,
    int      Repeats,
    string   OperationId
);

/// <summary>
/// editor tool (non-console actions): select, get_selected, set_selected, clear_selection,
/// frame_selection, get_play_state, start_play, stop_play, get_log, save_scene,
/// save_scene_as, undo, redo.
/// Console actions (console_list, console_run) are handled by ConsoleHandler.
/// New actions: open_code_file, get_preferences, set_preference.
/// Ported from Ozmium OzmiumEditorHandlers + OzmiumWriteHandlers.
/// </summary>
internal static class EditorHandler
{
    // Ring buffer of structured log entries. Trimmed to _cap on every append —
    // WHY: unbounded growth during a busy SBEP session would eat a lot of managed heap
    // (stack traces are the dominant cost). 2000 is 4x the legacy cap, ~20% of the
    // engine Console widget's 10000, and comfortably holds a full play session.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<McpLogEntry> _log = new();
    private static bool _callbackFaulted;

    [ConVar( "mcp_log_cap", ConVarFlags.Saved, Help = "Max entries retained in the MCP structured log buffer." )]
    public static int LogCap { get; set; } = 2000;

    /// <summary>
    /// Wire up log capture on first use. Subscribes to the engine logger (the same
    /// feed the editor Console widget consumes), the MCP server's internal event,
    /// and a Console.Out interceptor (stdout fallback for any line that somehow
    /// bypasses the logger pipeline, e.g. compile banner entries).
    /// </summary>
    internal static void EnsureInitialized()
    {
        // 2026-07-12 friction-audit self-heal: do NOT gate registration behind the
        // static bool alone — hot-reload MIGRATES `_initialized = true` into the
        // fresh assembly while the fresh ring + fresh OnEngineLog delegate start
        // UNREGISTERED, so get_log goes permanently dark until an editor restart
        // (AP static-flag-guarded-logger-registration-leaks-per-hotload; FeedHandler
        // was cured of the same disease on 2026-07-09, this handler was not —
        // observed live 2026-07-12: ring held only MCP-op chatter, total=5).
        //
        // Cure: every MAIN-THREAD entry defensively re-registers (RemoveLogger is
        // an idempotent no-op when not subscribed, so no double-delivery), which
        // makes a reload-broken ring self-heal on the next get_log / console_run.
        // Off-main-thread callers (mcp.health answers on the HTTP thread) skip
        // registration entirely because AddLogger/RemoveLogger is not proven
        // thread-safe (see RpcDispatcher.cs) — the next main-thread call heals.
        // Engine source: S&Box-Pub/engine/Sandbox.System (ThreadSafe.IsMainThread).
        if ( !ThreadSafe.IsMainThread )
            return;

        // Canonical engine log feed (EditorUtility.AddLogger is what ConsoleWidget uses).
        // Defensive RemoveLogger first — idempotent, no-op if not currently subscribed —
        // so re-entry and hot-reload cycles don't cause double-delivery.
        try { EditorUtility.RemoveLogger( OnEngineLog ); } catch { }
        EditorUtility.AddLogger( OnEngineLog );

        // MCP server internal logs (SSE connections, RPC dispatches). These also
        // fire through Log.* and therefore arrive via OnEngineLog too — we keep
        // both so the 'mcp' source tag makes server-internal entries easy to filter.
        // Defensive -= mirrors the RemoveLogger above — idempotent, never stacks.
        ArenulaMcpServer.OnLogMessage -= AppendLogFromMcpServer;
        ArenulaMcpServer.OnLogMessage += AppendLogFromMcpServer;
    }

    // ── Engine logger callback ─────────────────────────────────────────────
    // IMPORTANT: this runs on the main thread (ThreadSafe.AssertIsMainThread on the
    // underlying dispatcher). Keep the body fast + allocation-light; never re-enter
    // the logger by calling Log.* from here.
    private static void OnEngineLog( LogEvent ev )
    {
        try
        {
            string message = ev.Message;
            if ( string.IsNullOrEmpty( message ) && ev.Exception != null )
                message = ev.Exception.Message;
            if ( string.IsNullOrEmpty( message ) ) return;

            string stack = ev.Stack;
            if ( string.IsNullOrEmpty( stack ) && ev.Exception != null )
                stack = ev.Exception.ToString();

            var entry = new McpLogEntry(
                Time:    ev.Time == default ? DateTime.Now : ev.Time,
                Level:   MapLevel( ev.Level ),
                Logger:  ev.Logger ?? "",
                Message: message,
                Stack:   stack,
                Source:  "engine",
                Repeats: ev.Repeats > 0 ? ev.Repeats : 1,
                OperationId: McpOperationCoordinator.CurrentOperationId
            );
            _log.Enqueue( entry );
            TrimBuffer();
        }
        catch ( Exception ex )
        {
            // Never let the callback faulting spiral. Log once (via the original
            // logger — this re-entry is contained because _callbackFaulted gates it).
            if ( !_callbackFaulted )
            {
                _callbackFaulted = true;
                try { Log.Error( $"MCP OnEngineLog callback faulted: {ex.Message}" ); } catch { }
            }
        }
    }

    private static string MapLevel( LogLevel level ) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Info  => "info",
        LogLevel.Warn  => "warn",
        LogLevel.Error => "error",
        _ => level.ToString().ToLowerInvariant()
    };

    private static void TrimBuffer()
    {
        var cap = LogCap > 0 ? LogCap : 2000;
        while ( _log.Count > cap ) _log.TryDequeue( out _ );
    }

    /// <summary>Called by the MCP server's OnLogMessage event — wraps the plain string.</summary>
    internal static void AppendLogFromMcpServer( string msg ) => AppendLog( msg, "mcp" );

    /// <summary>Generic string entry point. Level defaults to "info"; callers that know
    /// the level should populate McpLogEntry directly.</summary>
    internal static void AppendLog( string msg, string source = "mcp" )
    {
        if ( string.IsNullOrWhiteSpace( msg ) ) return;
        _log.Enqueue( new McpLogEntry(
            Time:    DateTime.Now,
            Level:   "info",
            Logger:  source,
            Message: msg,
            Stack:   null,
            Source:  source,
            Repeats: 1,
            OperationId: McpOperationCoordinator.CurrentOperationId
        ) );
        TrimBuffer();
    }

    /// <summary>Structured snapshot for new consumers (editor.get_log).</summary>
    internal static McpLogEntry[] LogSnapshot() => _log.ToArray();

    /// <summary>Legacy text snapshot — preserved for ConsoleHandler's before/after diff
    /// so console_run's `output` payload stays byte-for-byte identical to prior behaviour.</summary>
    internal static string[] LogSnapshotText()
    {
        var entries = _log.ToArray();
        var result = new string[entries.Length];
        for ( int i = 0; i < entries.Length; i++ )
            result[i] = FormatEntry( entries[i] );
        return result;
    }

    /// <summary>Canonical text format for a log entry — `[HH:mm:ss] message`, matching
    /// the pre-structured output shape expected by existing console_run consumers.</summary>
    internal static string FormatEntry( McpLogEntry e ) =>
        $"[{e.Time:HH:mm:ss}] {e.Message}";

    internal static object Handle( string action, JsonElement args )
    {
        EnsureInitialized();
        try
        {
            return action switch
            {
                "select"           => Select( args ),
                "get_selected"     => GetSelected(),
                "set_selected"     => SetSelected( args ),
                "clear_selection"  => ClearSelection(),
                "frame_selection"  => FrameSelection( args ),
                "get_play_state"   => GetPlayState(),
                "start_play"       => StartPlay(),
                "stop_play"        => StopPlay(),
                "get_log"          => GetLog( args ),
                "get_hotload_summary" => GetHotloadSummary( args ),
                "save_scene"       => SaveScene( args ),
                "save_all"         => SaveAll( args ),
                "save_scene_as"    => SaveSceneAs( args ),
                "undo"             => Undo(),
                "redo"             => Redo(),
                "open_code_file"   => OpenCodeFile( args ),
                "get_preferences"  => GetPreferences(),
                "set_preference"   => SetPreference( args ),
                "console_list"     => ConsoleHandler.HandleAsync( action, args ).GetAwaiter().GetResult(),
                "console_run"      => ConsoleHandler.HandleAsync( action, args ).GetAwaiter().GetResult(),
                "restart_mcp"      => RestartMcp(),
                "reload_scene"     => ReloadScene( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: select, get_selected, set_selected, clear_selection, frame_selection, get_play_state, start_play, stop_play, get_log, get_hotload_summary, save_scene, save_scene_as, undo, redo, open_code_file, get_preferences, set_preference, restart_mcp, reload_scene",
                    null, McpErrorCode.UnknownAction )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action, null, null, McpErrorCode.InternalException );
        }
    }

    // ── select ───────────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.SelectGameObject

    private static object Select( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null ) return HandlerBase.Error( "No active scene.", "select" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "select" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "select" );
        SceneHelpers.SelectGameObject( go );
        return HandlerBase.WriteConfirm( go, $"Selected '{go.Name}'." );
    }

    // ── get_selected ─────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.GetSelectedObjects

    private static object GetSelected()
    {
        var selected = SceneHelpers.GetSelectedGameObjects();
        if ( selected.Count == 0 )
            return HandlerBase.Success( new { count = 0, objects = Array.Empty<object>() } );

        return HandlerBase.Success( new
        {
            count = selected.Count,
            objects = selected.Select( go => new
            {
                id = go.Id.ToString(),
                name = go.Name,
                path = SceneHelpers.GetObjectPath( go ),
                position = HandlerBase.V3( go.WorldPosition )
            } )
        } );
    }

    // ── set_selected ─────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.SetSelectedObjects

    private static object SetSelected( JsonElement args )
    {
        var idsStr = HandlerBase.GetString( args, "ids" );
        if ( string.IsNullOrEmpty( idsStr ) )
            return HandlerBase.Error( "Missing required 'ids' parameter.", "set_selected" );

        var scene = SceneHelpers.ResolveScene();
        if ( scene == null ) return HandlerBase.Error( "No active scene.", "set_selected" );

        SceneHelpers.ClearSelection();

        var ids = idsStr.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        int count = 0;

        foreach ( var idStr in ids )
        {
            var go = SceneHelpers.FindById( scene, idStr );
            if ( go != null )
            {
                SceneHelpers.AddToSelection( go );
                count++;
            }
        }

        return HandlerBase.Confirm( $"Selected {count} object(s)." );
    }

    // ── clear_selection ──────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.ClearSelection

    private static object ClearSelection()
    {
        SceneHelpers.ClearSelection();
        return HandlerBase.Confirm( "Selection cleared." );
    }

    // ── frame_selection ──────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.FrameSelection

    private static object FrameSelection( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null ) return HandlerBase.Error( "No active scene.", "frame_selection" );

        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session.", "frame_selection" );

        var id = HandlerBase.GetString( args, "id" );
        List<GameObject> targets;

        if ( !string.IsNullOrEmpty( id ) )
        {
            var go = SceneHelpers.FindByIdOrThrow( scene, id, "frame_selection" );
            targets = new List<GameObject> { go };
        }
        else
        {
            targets = SceneHelpers.GetSelectedGameObjects();
            if ( targets.Count == 0 )
                return HandlerBase.Error( "No selection to frame. Provide 'id' or select objects first.", "frame_selection" );
        }

        // Compute combined bounds
        var first = true;
        BBox bounds = default;
        foreach ( var go in targets )
        {
            var b = SceneHelpers.GetGameObjectBounds( go );
            if ( first ) { bounds = b; first = false; }
            else bounds = new BBox( Vector3.Min( bounds.Mins, b.Mins ), Vector3.Max( bounds.Maxs, b.Maxs ) );
        }

        // 2026-04-21 — wrap the direct SceneEditorSession.FrameTo call. The
        // reflection-based fallback was removed; a future engine rename or
        // signature change would throw Method/BindingException here and
        // obscure the cause behind a generic 500. Explicit catch surfaces
        // the underlying exception message to the MCP client.
        try
        {
            session.FrameTo( in bounds );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"FrameTo failed: {ex.Message}", "frame_selection",
                "The SceneEditorSession.FrameTo API may have changed. Verify via sbox-api." );
        }
        return HandlerBase.Confirm( $"Framed camera to {targets.Count} object(s)." );
    }

    // ── get_play_state ───────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.GetPlayState

    private static object GetPlayState()
    {
        var session = SceneEditorSession.Active;
        var state = session?.IsPlaying == true ? "playing" : "stopped";
        return HandlerBase.Success( new { playState = state } );
    }

    // ── start_play ───────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.StartPlayMode

    private static object StartPlay()
    {
        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session.", "start_play" );
        if ( session.IsPlaying ) return HandlerBase.Success( new
        {
            message = "Already playing.",
            play_state = "playing",
            pawn_readiness = PawnHandler.GetReadinessSnapshot()
        } );
        // Use EditorScene.Play (the canonical toolbar handler) instead of the low-level
        // session.SetPlaying. Play() clones the scene via CreateSceneFile → new Scene →
        // Load, fires "scene.startplay"/"scene.play" EditorEvents (widget binding
        // subscribers hook these), and sets Game.IsPlaying. Calling session.SetPlaying
        // with session.Scene shares the editor's Scene reference — on StopPlaying,
        // GameEditorSession.Destroy → base.Destroy → Scene?.Destroy destroys the
        // editor's own scene. Ref: engine/Sandbox.Tools/Scene/EditorScene.cs Play().
        EditorScene.Play( session );
        var playState = session.IsPlaying ? "playing" : "stopped";
        return HandlerBase.Success( new
        {
            message = "Play mode started.",
            play_state = playState,
            pawn_readiness = PawnHandler.GetReadinessSnapshot(),
            readiness_note = "Play startup is asynchronous. If pawn_readiness is not ready, retry pawn.get_state after initialization or complete team/deployment selection."
        } );
    }

    // ── stop_play ────────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.StopPlayMode

    private static object StopPlay()
    {
        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session.", "stop_play" );
        if ( !session.IsPlaying ) return HandlerBase.Confirm( "Already stopped." );
        // Paired with EditorScene.Play in StartPlay — calls the canonical toolbar Stop
        // handler, which destroys the cloned Game.ActiveScene (not the editor's scene),
        // fires "scene.stop" EditorEvent, clears Game.IsPlaying, and restores mixer
        // state. Ref: engine/Sandbox.Tools/Scene/EditorScene.cs Stop().
        EditorScene.Stop();
        return HandlerBase.Confirm( "Play mode stopped." );
    }

    // ── get_log ──────────────────────────────────────────────────────────
    // Structured log query. Subscribes to Editor.EditorUtility.AddLogger — the same
    // feed the editor Console widget consumes — so every Log.Info/Warning/Error,
    // asset-import error, runtime exception, and engine diagnostic flows through.
    //
    // Params:
    //   count  — trim to last N entries before filtering (default 50, 0 = no trim)
    //   filter — case-insensitive substring match against message
    //   level  — comma-separated severity filter ("error", "warn,error", etc.)
    //   source — comma-separated source filter ("engine", "engine,mcp", "stdout")
    //   offset — pagination offset after trim/filter
    //   limit  — pagination page size (default 50)
    //   format — "structured" (default) or "text" for legacy [HH:mm:ss] msg shape
    //   include_stacks — default FALSE (findings #6 payload diet): inline stack traces
    //                    are ~2KB per item, so count=200 shipped 250KB+ payloads. The
    //                    envelope reports stacksOmitted so agents know a re-call with
    //                    include_stacks=true will surface them.

    private static object GetLog( JsonElement args )
    {
        var count  = HandlerBase.GetInt(    args, "count",  50 );
        var filter = HandlerBase.GetString( args, "filter"     );
        var level  = HandlerBase.GetString( args, "level"      );
        var source = HandlerBase.GetString( args, "source"     );
        var offset = HandlerBase.GetInt(    args, "offset", 0  );
        var limit  = HandlerBase.GetInt(    args, "limit",  50 );
        var format = HandlerBase.GetString( args, "format"     ) ?? "structured";
        var includeStacks = HandlerBase.GetBool( args, "include_stacks", false );

        var bufferSize = _log.Count;
        var entries = _log.ToArray();

        // count: trim to last N (legacy behaviour — happens before filter)
        if ( count > 0 && count < entries.Length )
            entries = entries[^count..];

        // level filter
        if ( !string.IsNullOrEmpty( level ) )
        {
            var wanted = level.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( s => s.ToLowerInvariant() ).ToHashSet();
            entries = entries.Where( e => wanted.Contains( e.Level ) ).ToArray();
        }

        // source filter
        if ( !string.IsNullOrEmpty( source ) )
        {
            var wanted = source.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( s => s.ToLowerInvariant() ).ToHashSet();
            entries = entries.Where( e => wanted.Contains( ( e.Source ?? "" ).ToLowerInvariant() ) ).ToArray();
        }

        // message substring filter (case-insensitive, matches legacy semantics)
        if ( !string.IsNullOrEmpty( filter ) )
        {
            entries = entries.Where( e =>
                ( e.Message ?? "" ).IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0 ).ToArray();
        }

        var total = entries.Length;
        // Clamp limit sanely — negative or zero means "all remaining after offset".
        var effectiveLimit = limit > 0 ? limit : Math.Max( 0, total - offset );
        var page = entries.Skip( offset ).Take( effectiveLimit ).ToArray();

        bool textFormat = string.Equals( format, "text", StringComparison.OrdinalIgnoreCase );

        // Stacks are opt-in: null fields are dropped by the serializer, so the default
        // response carries no stack keys at all. stacksOmitted tells the agent how many
        // entries in this page have a trace available behind include_stacks=true.
        int stacksOmitted = includeStacks ? 0 : page.Count( e => !string.IsNullOrEmpty( e.Stack ) );

        object items = textFormat
            ? (object) page.Select( FormatEntry ).ToList()
            : (object) page.Select( e => new
                {
                    time    = e.Time.ToString( "yyyy-MM-ddTHH:mm:ss.fffK" ),
                    level   = e.Level,
                    logger  = e.Logger,
                    message = e.Message,
                    stack   = includeStacks && !string.IsNullOrEmpty( e.Stack ) ? e.Stack : null,
                    source  = e.Source,
                    repeats = e.Repeats
                } ).ToList();

        return HandlerBase.Success( new
        {
            items,
            count      = page.Length,
            total,
            offset,
            limit      = effectiveLimit,
            has_more   = offset + effectiveLimit < total,
            bufferSize,
            bufferCap  = LogCap,
            format     = textFormat ? "text" : "structured",
            stacksOmitted = stacksOmitted > 0 ? stacksOmitted : (int?)null
        } );
    }

    // ── get_hotload_summary ──────────────────────────────────────────────
    // Per-generation hotload-result snapshots from HotReloadProbe. Useful for the
    // /diagnose-stale-dll protocol (replaces "scan engine log for upgrade warnings"
    // ad-hoc step) and the /audit health check (returns currentGeneration so callers
    // can detect "MCP plugin is stale, restart editor" — currentGeneration that hasn't
    // advanced after a known compile is the signal).
    //
    // Engine offers no public access path to live Sandbox.HotloadResult instances
    // (verified 2026-05-09 via mcp__sbox-editor__reflection probes — no static
    // accessor on Sandbox.Hotload, [EditorEvent.Hotload] is parameterless). Snapshot
    // payload is therefore log-derived and intentionally compact.
    //
    // Params:
    //   count — trim to last N snapshots before filtering (default = all, max = cap)
    //   level — comma-separated status filter ("errors" or "warnings,errors")
    private static object GetHotloadSummary( JsonElement args )
    {
        var count = HandlerBase.GetInt(    args, "count", 0 );
        var level = HandlerBase.GetString( args, "level"    );

        var snaps = HotReloadProbe.RecentResults();
        // Newest first for the response.
        var arr = new HotReloadProbe.HotloadResultSnapshot[snaps.Count];
        for ( int i = 0; i < snaps.Count; i++ ) arr[i] = snaps[snaps.Count - 1 - i];

        if ( !string.IsNullOrEmpty( level ) )
        {
            var wanted = level.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( s => s.ToLowerInvariant() ).ToHashSet();
            arr = arr.Where( s => wanted.Contains( ( s.Status ?? "" ).ToLowerInvariant() ) ).ToArray();
        }

        if ( count > 0 && count < arr.Length )
            arr = arr[..count];

        var items = arr.Select( s => new
        {
            generation        = s.Generation,
            utc               = s.Utc.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
            status            = s.Status,
            warningTokens     = s.WarningTokens,
            failedTypes       = s.FailedTypes,
            scanWallMs        = Math.Round( s.ScanWallMs, 3 ),
            logEntriesScanned = s.LogEntriesScanned
        } ).ToList();

        return HandlerBase.Success( new
        {
            ok                = true,
            currentGeneration = HotReloadProbe.Generation,
            lastHotloadUtc    = HotReloadProbe.LastHotloadUtc == DateTime.MinValue
                                  ? null
                                  : HotReloadProbe.LastHotloadUtc.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
            total             = snaps.Count,
            cap               = HotReloadProbe.RecentResultsCap,
            recent            = items
        } );
    }

    // ── save_scene ───────────────────────────────────────────────────────
    // Ported from OzmiumWriteHandlers.SaveScene.
    // Integrity-guarded 2026-06-11 (findings #5): SceneEditorSession.Save serializes
    // whatever is in memory — after a stale/partial reload that has destroyed
    // thousands of disk lines (2x ~8K-line losses, 2026-05-02). Refuse to write
    // when (a) a reload hold exists for this scene, or (b) the in-memory object
    // count is dramatically below the parsed disk count. force:true overrides both.

    private static object SaveScene( JsonElement args )
    {
        var session = SceneEditorSession.Active;
        if ( session == null )
            return HandlerBase.Error( "No editor session active.", "save_scene", null, null, McpErrorCode.NoActiveSession );

        var force = HandlerBase.GetBool( args, "force", false );
        var scenePath = TryGetScenePath( session );
        var live = SceneIntegrityGuard.CountLive( session.Scene );

        SceneIntegrityGuard.DiskSnapshot disk = null;
        if ( !string.IsNullOrEmpty( scenePath ) )
            disk = SceneIntegrityGuard.SnapshotDisk( SceneIntegrityGuard.ResolveAbsolutePath( scenePath ) );

        if ( !force )
        {
            // Tripwire from a prior reload_scene whose verdict was partial_load /
            // disk_overwritten — refuse regardless of counts.
            if ( SceneIntegrityGuard.TryGetHold( scenePath, out var hold ) )
            {
                return HandlerBase.Error(
                    $"REFUSED: a reload of '{scenePath}' at {hold.PlacedUtc:HH:mm:ss}Z returned '{hold.Reason}' " +
                    $"({hold.MemoryGameObjects}/{hold.DiskGameObjects} GameObjects loaded). Writing now would serialize " +
                    $"that incomplete in-memory state over the disk file.",
                    "save_scene",
                    "Re-run editor.reload_scene until integrity.verified=true (stop play mode first; let the asset " +
                    "watcher reimport the disk edit). If you are CERTAIN the in-memory state is what you want on disk, " +
                    "pass force:true.",
                    new
                    {
                        holdReason = hold.Reason,
                        holdPlacedUtc = hold.PlacedUtc.ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
                        diskGameObjects = hold.DiskGameObjects,
                        memoryGameObjects = hold.MemoryGameObjects
                    },
                    McpErrorCode.SaveRefusedIntegrityHold );
            }

            // Stateless integrity assertion: in-memory count must not be dramatically
            // below what the disk file holds. memory > disk is normal (prefab
            // expansion, MCP-created objects) and never blocks.
            if ( disk is { Parsed: true } )
            {
                int goDeficit = disk.GameObjectCount - live.GameObjects;
                int goThreshold = SceneIntegrityGuard.SaveDeficitThreshold( disk.GameObjectCount );
                int compDeficit = disk.ComponentCount - live.Components;
                int compThreshold = Math.Max( 5, disk.ComponentCount / 10 );
                if ( goDeficit > goThreshold || compDeficit > compThreshold )
                {
                    return HandlerBase.Error(
                        $"REFUSED: in-memory scene holds {live.GameObjects} GameObjects / {live.Components} components, " +
                        $"but '{scenePath}' on disk holds {disk.GameObjectCount} / {disk.ComponentCount}. Saving would " +
                        $"overwrite that disk content with the smaller in-memory state (the 8K-line-loss class).",
                        "save_scene",
                        "If the in-memory state is stale, run editor.reload_scene until integrity.verified=true before " +
                        "saving. If you intentionally deleted this many objects/components, pass force:true.",
                        new
                        {
                            diskGameObjects = disk.GameObjectCount,
                            memoryGameObjects = live.GameObjects,
                            diskComponents = disk.ComponentCount,
                            memoryComponents = live.Components,
                            blockedByGameObjectDeficit = goDeficit > goThreshold,
                            blockedByComponentDeficit = compDeficit > compThreshold,
                            gameObjectDeficitThreshold = goThreshold,
                            componentDeficitThreshold = compThreshold
                        },
                        McpErrorCode.SaveRefusedCountDeficit );
                }
            }
        }

        session.Save( false );
        SceneIntegrityGuard.ClearHold( scenePath );
        return HandlerBase.Success( new
        {
            message = $"Saved '{session.Scene?.Name}'.",
            forced = force,
            memoryGameObjects = live.GameObjects,
            memoryComponents = live.Components,
            diskGameObjectsBeforeSave = disk is { Parsed: true } ? disk.GameObjectCount : (int?)null
        } );
    }

    // ── save_scene_as ────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.SaveSceneAs

    private static object SaveSceneAs( JsonElement args )
    {
        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "save_scene_as" );

        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session.", "save_scene_as" );

        // The s&box Save(bool forceSaveAs) API opens a dialog — it doesn't accept a path.
        // We note this limitation in the response.
        var saveMethod = session.GetType().GetMethod( "Save", new[] { typeof( bool ) } );
        if ( saveMethod != null )
        {
            saveMethod.Invoke( session, new object[] { true } );
            return HandlerBase.Confirm( $"Save As dialog opened. Choose '{path}' in the dialog." );
        }

        return HandlerBase.Error( "Save As method not available on this session type.", "save_scene_as" );
    }

    // ── undo ─────────────────────────────────────────────────────────────
    // Ported from OzmiumWriteHandlers.Undo

    private static object Undo()
    {
        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session active.", "undo" );
        var us = session.UndoSystem;
        if ( us == null ) return HandlerBase.Error( "UndoSystem not available.", "undo" );
        us.Undo();
        return HandlerBase.Confirm( "Undo performed." );
    }

    // ── redo ─────────────────────────────────────────────────────────────
    // Ported from OzmiumWriteHandlers.Redo

    private static object Redo()
    {
        var session = SceneEditorSession.Active;
        if ( session == null ) return HandlerBase.Error( "No editor session active.", "redo" );
        var us = session.UndoSystem;
        if ( us == null ) return HandlerBase.Error( "UndoSystem not available.", "redo" );
        us.Redo();
        return HandlerBase.Confirm( "Redo performed." );
    }

    // ── open_code_file ───────────────────────────────────────────────────
    // NEW action — Research API first
    // Query: mcp__api__search_members "CodeEditor" + "Open"
    // Query: mcp__api__search_members "Editor" + "OpenFile"
    // Fallback: use Process.Start with line/column args for VS Code

    private static object OpenCodeFile( JsonElement args )
    {
        var path = HandlerBase.GetString( args, "path" );
        var line = HandlerBase.GetInt( args, "line", 0 );
        var column = HandlerBase.GetInt( args, "column", 0 );

        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "open_code_file" );

        try
        {
            // Try s&box's built-in code editor launch
            // The editor typically opens .cs files in the configured external editor
            var asset = AssetSystem.FindByPath( path );
            if ( asset != null )
            {
                asset.OpenInEditor();
                return HandlerBase.Confirm( $"Opened '{path}' in editor." );
            }

            // Fallback: try opening the file directly
            // This works for .cs files not registered in the asset system
            var absPath = HandlerBase.ResolveProjectPath( path ) ?? path;

            if ( !System.IO.File.Exists( absPath ) )
                return HandlerBase.Error( $"File not found: '{path}'.", "open_code_file" );

            // Open with default application
            System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo
            {
                FileName = absPath,
                UseShellExecute = true
            } );

            return HandlerBase.Confirm( $"Opened '{path}'" + ( line > 0 ? $" at line {line}" : "" ) + "." );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to open file: {ex.Message}", "open_code_file" );
        }
    }

    // ── get_preferences ──────────────────────────────────────────────────
    // NEW action — reads ConVars as editor preferences

    private static object GetPreferences()
    {
        var prefs = new Dictionary<string, string>();
        try
        {
            foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
            {
                try
                {
                    foreach ( var type in asm.GetTypes() )
                    {
                        foreach ( var prop in type.GetProperties(
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Static ) )
                        {
                            var attr = prop.GetCustomAttributes( typeof( ConVarAttribute ), false )
                                .FirstOrDefault() as ConVarAttribute;
                            if ( attr == null ) continue;
                            if ( !attr.Flags.HasFlag( ConVarFlags.Saved ) ) continue;

                            var name = !string.IsNullOrEmpty( attr.Name ) ? attr.Name : prop.Name.ToLowerInvariant();
                            try
                            {
                                var val = Sandbox.ConsoleSystem.GetValue( name );
                                prefs[name] = val;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            count = prefs.Count,
            preferences = prefs.OrderBy( kv => kv.Key )
                .Select( kv => new { key = kv.Key, value = kv.Value } )
        } );
    }

    // ── restart_mcp ─────────────────────────────────────────────────────
    // Stops and restarts the MCP SSE server to pick up recompiled code.

    private static object RestartMcp()
    {
        try
        {
            ArenulaMcpServer.StopServer();

            // Brief pause to let the listener release the port
            System.Threading.Thread.Sleep( 250 );

            ArenulaMcpServer.StartServer();

            return HandlerBase.Confirm( "MCP server restarted successfully." );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to restart MCP server: {ex.Message}", "restart_mcp" );
        }
    }

    // ── reload_scene ─────────────────────────────────────────────────────
    // NEW action — Safe scene reload from disk. The escape hatch for the
    // "scene blanks to 0 objects after asset recompile" bug. Stops play mode,
    // then either calls SceneEditorSession.Reload() (preferred — same session,
    // preserves tab/window state) or re-opens via CreateFromPath when an
    // explicit path is given that differs from the active session.

    internal static object ReloadScene( JsonElement args )
    {
        try
        {
            var explicitPath = HandlerBase.GetString( args, "path" );

            // Stop play mode first — reloading while playing leaves the play scene orphaned.
            // Use EditorScene.Stop (matches start_play/stop_play semantics) so we go through
            // the canonical teardown that destroys the cloned Game.ActiveScene rather than
            // the editor's own scene. Ref: engine/Sandbox.Tools/Scene/EditorScene.cs Stop().
            var session = SceneEditorSession.Active;
            if ( session != null && session.IsPlaying )
            {
                try { EditorScene.Stop(); } catch { /* tolerate */ }
            }

            SceneEditorSession target = null;
            string resolvedPath = null;

            if ( !string.IsNullOrEmpty( explicitPath ) )
            {
                // Match against active or any open session by ResourcePath.
                var activePath = TryGetScenePath( session );
                if ( !string.IsNullOrEmpty( activePath ) &&
                     PathsMatch( activePath, explicitPath ) )
                {
                    target = session;
                    resolvedPath = activePath;
                }
                else
                {
                    foreach ( var s in SceneEditorSession.All ?? new List<SceneEditorSession>() )
                    {
                        var sp = TryGetScenePath( s );
                        if ( !string.IsNullOrEmpty( sp ) && PathsMatch( sp, explicitPath ) )
                        {
                            target = s;
                            resolvedPath = sp;
                            break;
                        }
                    }
                }

                // No matching open session — open the file fresh from disk.
                if ( target == null )
                {
                    var normalized = SceneHelpers.NormalizePath( explicitPath );
                    var preDiskOpened = SceneIntegrityGuard.SnapshotDisk(
                        SceneIntegrityGuard.ResolveAbsolutePath( normalized ) );
                    target = SceneEditorSession.CreateFromPath( normalized );
                    if ( target == null && !normalized.EndsWith( ".scene" ) && !normalized.EndsWith( ".prefab" ) )
                        target = SceneEditorSession.CreateFromPath( normalized + ".scene" );

                    if ( target == null )
                        return HandlerBase.Error( $"Could not open scene '{explicitPath}'.", "reload_scene",
                            "Verify the path exists. Use asset_query.search type=scene to list scenes." );

                    target.MakeActive();
                    resolvedPath = TryGetScenePath( target ) ?? explicitPath;
                    return BuildReloadResult( target, resolvedPath, "opened", preDiskOpened );
                }
            }
            else
            {
                target = session;
                if ( target == null )
                    return HandlerBase.Error( "No active editor session and no 'path' provided.", "reload_scene" );
                resolvedPath = TryGetScenePath( target );
            }

            // Snapshot the disk file BEFORE the reload so BuildReloadResult can
            // (a) detect the reload itself mutating the file and (b) compare the
            // loaded in-memory state against the disk truth.
            var preDisk = SceneIntegrityGuard.SnapshotDisk(
                SceneIntegrityGuard.ResolveAbsolutePath( resolvedPath ) );

            // Reload the matched session in place. CAUTION: SceneEditorSession.Reload()
            // is Scene.Load( Scene.Source ) — the CACHED SceneFile resource, NOT a fresh
            // disk read (engine: Sandbox.Tools/Scene/Session/SceneEditorSession.cs:299).
            // If the asset watcher hasn't reimported a fresh disk edit (play mode), this
            // loads stale state — BuildReloadResult detects that and reports partial_load.
            try
            {
                target.MakeActive();
                target.Reload();
            }
            catch ( Exception ex )
            {
                // Reload failed — fall back to close + re-open via CreateFromPath
                if ( !string.IsNullOrEmpty( resolvedPath ) )
                {
                    try { target.Destroy(); } catch { }
                    var fresh = SceneEditorSession.CreateFromPath( SceneHelpers.NormalizePath( resolvedPath ) );
                    if ( fresh == null )
                        return HandlerBase.Error(
                            $"Reload() failed ({ex.Message}) and CreateFromPath fallback failed for '{resolvedPath}'.",
                            "reload_scene" );
                    fresh.MakeActive();
                    return BuildReloadResult( fresh, resolvedPath, "reopened", preDisk );
                }
                return HandlerBase.Error( $"Reload failed: {ex.Message}", "reload_scene" );
            }

            return BuildReloadResult( target, resolvedPath, "reloaded", preDisk );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"reload_scene failed: {ex.Message}", "reload_scene" );
        }
    }

    // Truth verdict added 2026-06-11 (findings #5). The legacy version reported
    // mode:"reloaded" + a sane-looking totalObjects regardless of whether the load
    // actually matched disk — which is exactly how two agents lost ~8K lines each
    // (memories feedback_reload_scene_overwrites_disk_with_stale_memory,
    // feedback_save_scene_after_failed_reload_loses_thousands_of_lines).
    private static object BuildReloadResult( SceneEditorSession session, string path, string loadAction,
        SceneIntegrityGuard.DiskSnapshot preDisk )
    {
        var scene = session?.Scene;
        int rootObjects = 0;
        if ( scene != null )
        {
            try { rootObjects = scene.Children.Count; } catch { }
        }

        // Full uncapped walk — the legacy WalkAll skips ignore-marked objects and
        // large subtrees, which would corrupt the disk comparison.
        var live = SceneIntegrityGuard.CountLive( scene );

        // Re-snapshot disk AFTER the load: (a) compare memory against what is on
        // disk right now, (b) detect the reload itself having written to the file
        // (the stale-merge clobber class).
        var absPath = preDisk?.AbsolutePath ?? SceneIntegrityGuard.ResolveAbsolutePath( path );
        var postDisk = SceneIntegrityGuard.SnapshotDisk( absPath );

        bool diskWriteDetected = preDisk is { Exists: true } && postDisk.Exists &&
                                 preDisk.ContentHash != null && postDisk.ContentHash != null &&
                                 !string.Equals( preDisk.ContentHash, postDisk.ContentHash, StringComparison.OrdinalIgnoreCase );

        string mode = loadAction;
        string warning = null;
        bool holdPlaced = false;
        bool verified = false;

        if ( diskWriteDetected )
        {
            mode = "disk_overwritten";
            warning = $"reload_scene MUTATED '{path}' on disk during the load " +
                      $"({preDisk.FileLength} -> {postDisk.FileLength} bytes). If you edited this file on disk before " +
                      $"reloading, your edit may have just been overwritten by stale in-memory state. Run " +
                      $"`git diff -- <scene file>` NOW and recover via `git checkout` if needed. save_scene will " +
                      $"refuse this scene until a clean verified reload (or force:true).";
            SceneIntegrityGuard.PlaceHold( path, "disk_overwritten", postDisk.GameObjectCount, live.GameObjects, warning );
            holdPlaced = true;
        }
        else if ( postDisk.Parsed &&
                  ( live.GameObjects < postDisk.GameObjectCount || live.Components < postDisk.ComponentCount ) )
        {
            mode = "partial_load";
            warning = $"PARTIAL LOAD: in-memory scene holds {live.GameObjects}/{postDisk.GameObjectCount} GameObjects " +
                      $"and {live.Components}/{postDisk.ComponentCount} components present on disk. The engine loaded a " +
                      $"stale cached SceneFile (disk edit not yet reimported — typical in/just after play mode). " +
                      $"Do NOT save_scene (it will refuse). Stop play mode, give the asset watcher a moment to " +
                      $"reimport, then reload_scene again until integrity.verified=true.";
            SceneIntegrityGuard.PlaceHold( path, "partial_load", postDisk.GameObjectCount, live.GameObjects, warning );
            holdPlaced = true;
        }
        else if ( postDisk.Parsed )
        {
            verified = true;
            SceneIntegrityGuard.ClearHold( path );
        }
        // Disk missing/unparseable: leave any existing hold in place — health unproven.

        return HandlerBase.Success( new
        {
            message = warning ?? $"Scene {loadAction}: '{scene?.Name ?? "(unnamed)"}' — verified against disk " +
                      $"({live.GameObjects} GameObjects, {live.Components} components).",
            mode,
            loadAction,
            sceneName = scene?.Name,
            scenePath = path,
            totalObjects = live.GameObjects,
            rootObjects,
            integrity = new
            {
                verified,
                holdPlaced,
                diskWriteDetectedDuringReload = diskWriteDetected,
                diskGameObjects   = postDisk.Exists && postDisk.Parsed ? postDisk.GameObjectCount : (int?)null,
                memoryGameObjects = live.GameObjects,
                diskComponents    = postDisk.Exists && postDisk.Parsed ? postDisk.ComponentCount : (int?)null,
                memoryComponents  = live.Components,
                diskParseError    = postDisk.Exists && !postDisk.Parsed ? postDisk.ParseError : null,
                diskFile          = absPath
            }
        } );
    }

    // ── save_all ──────────────────────────────────────────────────────
    //
    // Ported from upstream Arenula 2a4ca97 (2026-08-05), but NOT verbatim.
    // Upstream calls session.Save(false) on every dirty session directly. Here
    // that would be a hole straight through SceneIntegrityGuard: save_scene
    // refuses to write when a reload left a partial scene in memory, or when the
    // in-memory object count is far below what the file on disk holds (the
    // 8K-line-loss class). A save_all that skipped those checks would overwrite
    // exactly the scenes save_scene protects, and in bulk.
    //
    // So each session runs the same two gates. A session that fails is SKIPPED
    // and reported in refused[] — never silently, and never force-written. Pass
    // force:true to bypass, matching save_scene's escape hatch.

    private static object SaveAll( JsonElement args )
    {
        var force = HandlerBase.GetBool( args, "force", false );

        var dirty = SceneEditorSession.All?
            .Where( s => s != null && s.HasUnsavedChanges )
            .ToList() ?? new List<SceneEditorSession>();

        if ( dirty.Count == 0 )
            return HandlerBase.Success( new
            {
                message = "No sessions with unsaved changes.",
                saved_count = 0,
                refused_count = 0,
                failure_count = 0,
                saved = Array.Empty<object>(),
                refused = Array.Empty<object>(),
                failures = Array.Empty<object>()
            } );

        var saved = new List<object>();
        var refused = new List<object>();
        var failures = new List<object>();

        foreach ( var session in dirty )
        {
            var sceneName = session.Scene?.Name ?? "(unnamed)";
            var isPrefab = session.IsPrefabSession;
            var scenePath = TryGetScenePath( session );

            try
            {
                if ( !force )
                {
                    var refusal = IntegrityRefusalFor( session, scenePath );
                    if ( refusal != null )
                    {
                        refused.Add( new { name = sceneName, isPrefabSession = isPrefab, file = scenePath, reason = refusal } );
                        continue;
                    }
                }

                session.Save( false );
                SceneIntegrityGuard.ClearHold( scenePath );

                // Post-condition, same shape as save_scene: the file exists, is
                // non-empty, and the dirty flag cleared. Any of those missing
                // means the engine dropped the save without throwing.
                string absPath = null;
                long fileSize = 0;
                bool fileExists = false;

                if ( !string.IsNullOrEmpty( scenePath ) )
                {
                    var asset = AssetSystem.FindByPath( scenePath );
                    absPath = asset?.AbsolutePath;
                    fileExists = !string.IsNullOrEmpty( absPath ) && File.Exists( absPath );
                    fileSize = fileExists ? new FileInfo( absPath ).Length : 0;
                }

                bool stillDirty = session.HasUnsavedChanges;

                if ( !fileExists || fileSize == 0 || stillDirty )
                    failures.Add( new
                    {
                        name = sceneName,
                        isPrefabSession = isPrefab,
                        file = absPath,
                        reason = stillDirty ? "still dirty after save"
                            : !fileExists ? "file missing after save"
                            : "file empty after save"
                    } );
                else
                    saved.Add( new { name = sceneName, isPrefabSession = isPrefab, file = absPath, size_bytes = fileSize } );
            }
            catch ( Exception ex )
            {
                failures.Add( new { name = sceneName, isPrefabSession = isPrefab, error = ex.Message } );
            }
        }

        var parts = new List<string> { $"Saved {saved.Count}/{dirty.Count} session(s)." };
        if ( refused.Count > 0 )
            parts.Add( $"{refused.Count} refused by the scene-integrity guard — see refused[]; pass force:true only if the in-memory state is what you want on disk." );
        if ( failures.Count > 0 )
            parts.Add( $"{failures.Count} failed — see failures[] and call get_log for engine errors." );

        return HandlerBase.Success( new
        {
            message = string.Join( " ", parts ),
            saved_count = saved.Count,
            refused_count = refused.Count,
            failure_count = failures.Count,
            forced = force,
            saved,
            refused,
            failures
        } );
    }

    /// <summary>
    /// Run save_scene's two integrity gates against one session. Returns a
    /// human-readable refusal reason, or null when the session is safe to write.
    /// </summary>
    private static string IntegrityRefusalFor( SceneEditorSession session, string scenePath )
    {
        if ( SceneIntegrityGuard.TryGetHold( scenePath, out var hold ) )
            return $"a reload of '{scenePath}' at {hold.PlacedUtc:HH:mm:ss}Z returned '{hold.Reason}' " +
                   $"({hold.MemoryGameObjects}/{hold.DiskGameObjects} GameObjects loaded); writing now would " +
                   "serialize that incomplete in-memory state over the disk file";

        if ( string.IsNullOrEmpty( scenePath ) ) return null;

        var disk = SceneIntegrityGuard.SnapshotDisk( SceneIntegrityGuard.ResolveAbsolutePath( scenePath ) );
        if ( disk is not { Parsed: true } ) return null;

        var live = SceneIntegrityGuard.CountLive( session.Scene );
        int goDeficit = disk.GameObjectCount - live.GameObjects;
        int goThreshold = SceneIntegrityGuard.SaveDeficitThreshold( disk.GameObjectCount );
        int compDeficit = disk.ComponentCount - live.Components;
        int compThreshold = Math.Max( 5, disk.ComponentCount / 10 );

        if ( goDeficit > goThreshold || compDeficit > compThreshold )
            return $"in-memory holds {live.GameObjects} GameObjects / {live.Components} components but the disk file " +
                   $"holds {disk.GameObjectCount} / {disk.ComponentCount}; saving would overwrite that with the " +
                   "smaller in-memory state";

        return null;
    }

    private static string TryGetScenePath( SceneEditorSession session )
    {
        try { return session?.Scene?.Source?.ResourcePath; }
        catch { return null; }
    }

    private static bool PathsMatch( string a, string b )
    {
        if ( string.IsNullOrEmpty( a ) || string.IsNullOrEmpty( b ) ) return false;
        var na = SceneHelpers.NormalizePath( a )?.Replace( '\\', '/' ).TrimStart( '/' );
        var nb = SceneHelpers.NormalizePath( b )?.Replace( '\\', '/' ).TrimStart( '/' );
        if ( string.Equals( na, nb, StringComparison.OrdinalIgnoreCase ) ) return true;
        // Tolerate ".scene" / no-extension variants
        if ( !na.EndsWith( ".scene" ) ) na += ".scene";
        if ( !nb.EndsWith( ".scene" ) ) nb += ".scene";
        return string.Equals( na, nb, StringComparison.OrdinalIgnoreCase );
    }

    // ── set_preference ───────────────────────────────────────────────────
    // NEW action — sets a ConVar value

    private static object SetPreference( JsonElement args )
    {
        var key = HandlerBase.GetString( args, "key" );
        var value = HandlerBase.GetString( args, "value" );

        if ( string.IsNullOrEmpty( key ) )
            return HandlerBase.Error( "Missing required 'key' parameter.", "set_preference" );
        if ( value == null )
            return HandlerBase.Error( "Missing required 'value' parameter.", "set_preference" );

        // Verify the convar exists
        string current = null;
        try { current = Sandbox.ConsoleSystem.GetValue( key ); } catch { }

        if ( current == null )
            return HandlerBase.Error( $"Unknown preference key '{key}'. Use get_preferences to list available keys.", "set_preference" );

        Sandbox.ConsoleSystem.SetValue( key, value );

        string readback = null;
        try { readback = Sandbox.ConsoleSystem.GetValue( key ); } catch { }

        return HandlerBase.Success( new
        {
            key,
            oldValue = current,
            newValue = readback ?? value
        } );
    }
}
