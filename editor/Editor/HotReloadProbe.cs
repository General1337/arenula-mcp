using System;
using Editor;

namespace Arenula;

/// <summary>
/// Detects whether DLL hot-reload actually occurred after a compile, and whether the
/// engine's InstanceUpgrader emitted "couldn't upgrade" warnings during the swap.
///
/// S&amp;Box hotload has two silent-failure modes we need to catch:
///   1. Swap stalls — compile succeeds but the DLL doesn't swap. Detected by comparing
///      pre/post values of <see cref="Generation"/>.
///   2. Swap "succeeds" with warnings — <c>Sandbox.Hotload.InstanceUpgrader</c> can't
///      migrate a live Component instance. The engine logs "couldn't upgrade" and leaves
///      the instance holding a stale <see cref="Type"/> reference. The swap completes,
///      generation advances, and subsequent <c>Components.Get&lt;T&gt;()</c> lookups return
///      null because TypeLibrary has only the NEW assembly's types. The engine's own
///      <c>HotloadEntryType.Warning</c> doc comment says "you should reload the game
///      to avoid runtime bugs" when this fires — i.e. cold restart is the only recovery.
///      Detected by scanning the engine log around each <c>[EditorEvent.Hotload]</c>
///      event for the known warning tokens.
///
/// Callers snapshot <see cref="Generation"/> pre-compile; post-compile they compare
/// against the current value and check <see cref="LastHotloadHadWarnings"/> for the
/// post-swap generation.
/// </summary>
internal static class HotReloadProbe
{
    private static int _generation;
    private static DateTime _lastHotloadUtc = DateTime.MinValue;

    // Generation number of the most recent hotload we observed InstanceUpgrader
    // warnings on. -1 == no warnings yet. Written only from the [EditorEvent.Hotload]
    // handler (single-threaded, main-thread), read from any caller.
    private static int _lastWarningGeneration = -1;
    private static string _lastWarningToken;

    internal static int Generation => _generation;
    internal static DateTime LastHotloadUtc => _lastHotloadUtc;

    /// <summary>
    /// True if the hotload at <paramref name="generation"/> emitted a token indicating
    /// the InstanceUpgrader failed to migrate one or more live instances. When true,
    /// the editor's TypeLibrary is in the stale-ref state documented in
    /// <c>docs/sbep-reference/58-typelibrary-hotreload-deep-investigation.md</c>;
    /// cold restart is required before any new Play session.
    /// </summary>
    internal static bool LastHotloadHadWarnings( int generation ) =>
        generation > 0 && _lastWarningGeneration == generation;

    /// <summary>
    /// Token that tripped the warning detector for the most recent corrupting swap,
    /// or <c>null</c> if none. Surfaced in the pipeline response so the agent sees the
    /// engine's own wording instead of a generic hint.
    /// </summary>
    internal static string LastWarningToken => _lastWarningToken;

    [EditorEvent.Hotload]
    private static void OnHotload()
    {
        _generation++;
        _lastHotloadUtc = DateTime.UtcNow;

        // Engine emits "couldn't upgrade" warnings inline with the hotload work, so
        // they are already in the log ring buffer by the time this handler fires.
        // A short time window defends against off-by-one clock skew between the log
        // timestamps (logger thread) and DateTime.UtcNow here (main thread).
        var windowStart = _lastHotloadUtc - TimeSpan.FromSeconds( 5 );
        var hit = ScanLogForUpgradeWarnings( windowStart );
        if ( hit != null )
        {
            _lastWarningGeneration = _generation;
            _lastWarningToken = hit;
        }
    }

    /// <summary>
    /// Walk the structured log buffer from newest to oldest and return the first
    /// InstanceUpgrader corruption token we see within the time window.
    /// Tokens chosen from engine source (<c>Sandbox.Hotload.HotloadResult</c>,
    /// <c>Sandbox.Engine/Scene/Components/Component.Serialize.cs</c>):
    ///   - <c>[HOTLOAD_UPGRADE_FAILED]</c> — authoritative user-code signal emitted
    ///     unconditionally by <c>IHotloadManaged.Failed()</c> when migration fails.
    ///   - "couldn't upgrade" — the canonical engine InstanceUpgrader warning.
    ///   - "InstanceUpgrader" — raw class-name in stack traces of upgrader exceptions.
    ///
    /// <b>Previously included "TypeLibrary could not find", removed 2026-04-18.</b>
    /// That token produced false <c>upgrade_failed</c> verdicts because it fires
    /// benignly whenever MCP-side ConCmd handlers call
    /// <c>Game.ActiveScene.GetAllComponents&lt;T&gt;()</c> in a stale editor context
    /// (AP-061 / memory <c>feedback_mcp_concmd_game_activescene.md</c>) — dozens of
    /// hits per verify run on a perfectly healthy DLL swap. The authoritative
    /// corruption signal is the upgrader's own "couldn't upgrade" line; a live
    /// instance that truly can't migrate will also trip <c>[HOTLOAD_UPGRADE_FAILED]</c>
    /// via the Ship Components' <c>IHotloadManaged</c> implementations.
    /// </summary>
    private static string ScanLogForUpgradeWarnings( DateTime windowStart )
    {
        try
        {
            var snap = Arenula.EditorHandler.LogSnapshot();
            if ( snap == null || snap.Length == 0 ) return null;

            for ( int i = snap.Length - 1; i >= 0; i-- )
            {
                var e = snap[i];
                if ( e.Time < windowStart ) break;

                var m = e.Message ?? string.Empty;
                // Explicit IHotloadManaged.Failed() signal from game-assembly user code.
                // More reliable than engine log tokens because Failed() is called
                // unconditionally when a live instance can't be migrated — the engine's
                // own "couldn't upgrade" line is sometimes suppressed at hotload_log=0.
                if ( m.Contains( HotReloadFailureTokens.UpgradeFailedPrefix, StringComparison.OrdinalIgnoreCase ) )
                    return "IHotloadManaged.Failed";
                if ( m.Contains( "couldn't upgrade", StringComparison.OrdinalIgnoreCase ) )
                    return "couldn't upgrade";
                if ( m.Contains( "InstanceUpgrader", StringComparison.OrdinalIgnoreCase ) )
                    return "InstanceUpgrader";

                var s = e.Stack ?? string.Empty;
                if ( s.Contains( "InstanceUpgrader", StringComparison.OrdinalIgnoreCase ) )
                    return "InstanceUpgrader";
            }
        }
        catch { /* best-effort diagnostic — never throw from a [EditorEvent.Hotload] handler */ }
        return null;
    }

    /// <summary>
    /// Re-run the warning scan from a caller's perspective, widening the window to
    /// account for "TypeLibrary could not find" lines that fire on the first Play
    /// after a corrupting swap rather than during the swap itself. Pipeline callers
    /// invoke this just before returning so late-arriving corruption evidence still
    /// flips the status.
    /// </summary>
    internal static void RescanForWarnings( DateTime since )
    {
        if ( _generation <= 0 ) return;
        if ( _lastWarningGeneration == _generation ) return; // already flagged

        var hit = ScanLogForUpgradeWarnings( since );
        if ( hit == null ) return;

        _lastWarningGeneration = _generation;
        _lastWarningToken = hit;
    }

}

/// <summary>
/// Well-known log prefix that user-code <c>IHotloadManaged.Failed()</c> implementations
/// emit to signal to <see cref="HotReloadProbe"/> that an instance upgrade failed.
/// Used instead of a direct cross-assembly call because the game assembly
/// (<c>sandbox.dll</c>) cannot reference the editor-only <c>arenula_mcp.editor.dll</c>
/// where <c>HotReloadProbe</c> lives — the log stream is the neutral channel both sides
/// can write/read without creating an assembly dependency.
///
/// The prefix is matched case-insensitively by <c>HotReloadProbe.ScanLogForUpgradeWarnings</c>.
/// Keep the token in sync with any <c>Failed()</c> implementations in <c>Code/SBEP/</c>.
/// </summary>
internal static class HotReloadFailureTokens
{
    internal const string UpgradeFailedPrefix = "[HOTLOAD_UPGRADE_FAILED]";
}
