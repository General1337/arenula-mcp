using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // Per-generation snapshots for the editor.get_hotload_summary MCP tool.
    // LinkedList because we trim from the head and append at the tail; List<>.RemoveAt(0)
    // would be O(n) per hotload. Read from MCP handler thread, mutated only from the
    // [EditorEvent.Hotload] handler — lock keeps enumeration safe.
    private const int _resultsCap = 10;
    internal const int RecentResultsCap = _resultsCap;
    private static readonly LinkedList<HotloadResultSnapshot> _recentResults = new();
    private static readonly object _recentResultsLock = new();

    internal static int Generation => _generation;
    internal static DateTime LastHotloadUtc => _lastHotloadUtc;

    /// <summary>
    /// Snapshot of a single hotload event. Built in <see cref="OnHotload"/> from the
    /// log-scan output (Option B per the wave-2 reflection probe — engine offers no
    /// public access path to the live <c>Sandbox.HotloadResult</c> instance).
    /// </summary>
    internal sealed record HotloadResultSnapshot(
        int Generation,
        DateTime Utc,
        string Status,                      // "ok" | "warnings" | "errors"
        IReadOnlyList<string> WarningTokens,
        IReadOnlyList<string> FailedTypes,
        double ScanWallMs,
        int LogEntriesScanned );

    /// <summary>
    /// Snapshot accessor for the MCP <c>editor.get_hotload_summary</c> handler. Returns
    /// a defensive copy so the JSON serializer can iterate without contention against
    /// a concurrent <see cref="OnHotload"/> mutation. Newest entries last (LinkedList
    /// append-at-tail order).
    /// </summary>
    internal static IReadOnlyList<HotloadResultSnapshot> RecentResults()
    {
        lock ( _recentResultsLock )
        {
            var arr = new HotloadResultSnapshot[_recentResults.Count];
            int i = 0;
            for ( var node = _recentResults.First; node != null; node = node.Next, i++ )
                arr[i] = node.Value;
            return arr;
        }
    }

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

        // Time the log scan so the per-generation snapshot can report wall time.
        // Useful when /audit notices "current generation didn't advance" — we can
        // see whether the scan itself stalled vs the engine never firing the event.
        var sw = Stopwatch.StartNew();
        var scan = BuildSnapshotScan( windowStart );
        sw.Stop();

        if ( scan.FirstHitToken != null )
        {
            _lastWarningGeneration = _generation;
            _lastWarningToken = scan.FirstHitToken;
        }

        // Build + append the per-generation snapshot. Wrap in try/catch — a faulting
        // snapshot must never break the [EditorEvent.Hotload] event chain (the
        // existing warning-detector ScanLogForUpgradeWarnings has the same defense).
        try
        {
            var status = scan.HasErrors ? "errors" : ( scan.HasWarnings ? "warnings" : "ok" );
            var snap = new HotloadResultSnapshot(
                Generation:        _generation,
                Utc:               _lastHotloadUtc,
                Status:            status,
                WarningTokens:     scan.DistinctTokens,
                FailedTypes:       scan.FailedTypes,
                ScanWallMs:        sw.Elapsed.TotalMilliseconds,
                LogEntriesScanned: scan.EntriesScanned );
            lock ( _recentResultsLock )
            {
                _recentResults.AddLast( snap );
                while ( _recentResults.Count > _resultsCap ) _recentResults.RemoveFirst();
            }
        }
        catch { /* best-effort snapshot — never throw from [EditorEvent.Hotload] */ }
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
        // Back-compat shim — preserves the original short-circuit "first hit only"
        // contract for callers (RescanForWarnings, pipeline). The richer per-snapshot
        // scan is BuildSnapshotScan below.
        return BuildSnapshotScan( windowStart ).FirstHitToken;
    }

    /// <summary>Result of a single log-scan over the post-hotload window. Contains
    /// every signal the snapshot record needs: distinct hit tokens, parsed failed-type
    /// names, error/warning aggregation, and how many log entries we walked.</summary>
    private readonly struct ScanResult
    {
        public string FirstHitToken { get; init; }            // back-compat with ScanLogForUpgradeWarnings
        public IReadOnlyList<string> DistinctTokens { get; init; }
        public IReadOnlyList<string> FailedTypes { get; init; }
        public bool HasErrors { get; init; }                  // [HOTLOAD_UPGRADE_FAILED] hit
        public bool HasWarnings { get; init; }                // any warning-class token hit
        public int EntriesScanned { get; init; }
    }

    private static ScanResult BuildSnapshotScan( DateTime windowStart )
    {
        var emptyStrings = Array.Empty<string>();
        try
        {
            var snap = Arenula.EditorHandler.LogSnapshot();
            if ( snap == null || snap.Length == 0 )
                return new ScanResult { DistinctTokens = emptyStrings, FailedTypes = emptyStrings };

            string firstHit = null;
            var tokens = new List<string>( 4 );
            var failedTypes = new List<string>( 4 );
            bool hasErrors = false, hasWarnings = false;
            int entriesScanned = 0;

            for ( int i = snap.Length - 1; i >= 0; i-- )
            {
                var e = snap[i];
                if ( e.Time < windowStart ) break;
                entriesScanned++;

                var m = e.Message ?? string.Empty;
                var s = e.Stack ?? string.Empty;

                // [HOTLOAD_UPGRADE_FAILED] — explicit IHotloadManaged.Failed() user-code signal.
                if ( m.Contains( HotReloadFailureTokens.UpgradeFailedPrefix, StringComparison.OrdinalIgnoreCase ) )
                {
                    AddIfMissing( tokens, "IHotloadManaged.Failed" );
                    hasErrors = true;
                    firstHit ??= "IHotloadManaged.Failed";
                    var t = ExtractTypeAfter( m, "type=" );
                    if ( t != null ) AddIfMissing( failedTypes, t );
                }

                // "couldn't upgrade <Type>" — engine InstanceUpgrader warning.
                int idx = m.IndexOf( "couldn't upgrade", StringComparison.OrdinalIgnoreCase );
                if ( idx >= 0 )
                {
                    AddIfMissing( tokens, "couldn't upgrade" );
                    hasWarnings = true;
                    firstHit ??= "couldn't upgrade";
                    // Token after "couldn't upgrade " (15-char prefix + space).
                    var rest = m.Substring( idx + "couldn't upgrade".Length ).TrimStart();
                    var t = ExtractFirstWord( rest );
                    if ( t != null ) AddIfMissing( failedTypes, t );
                }

                // "InstanceUpgrader" in message or stack — generic exception trace fallback.
                if ( m.Contains( "InstanceUpgrader", StringComparison.OrdinalIgnoreCase ) ||
                     s.Contains( "InstanceUpgrader", StringComparison.OrdinalIgnoreCase ) )
                {
                    AddIfMissing( tokens, "InstanceUpgrader" );
                    hasWarnings = true;
                    firstHit ??= "InstanceUpgrader";
                }
            }

            return new ScanResult
            {
                FirstHitToken   = firstHit,
                DistinctTokens  = tokens,
                FailedTypes     = failedTypes,
                HasErrors       = hasErrors,
                HasWarnings     = hasWarnings,
                EntriesScanned  = entriesScanned
            };
        }
        catch
        {
            // best-effort diagnostic — never throw from a [EditorEvent.Hotload] handler
            return new ScanResult { DistinctTokens = emptyStrings, FailedTypes = emptyStrings };
        }
    }

    private static void AddIfMissing( List<string> list, string value )
    {
        for ( int i = 0; i < list.Count; i++ )
            if ( string.Equals( list[i], value, StringComparison.OrdinalIgnoreCase ) ) return;
        list.Add( value );
    }

    private static string ExtractTypeAfter( string message, string marker )
    {
        int idx = message.IndexOf( marker, StringComparison.OrdinalIgnoreCase );
        if ( idx < 0 ) return null;
        return ExtractFirstWord( message.Substring( idx + marker.Length ) );
    }

    private static string ExtractFirstWord( string s )
    {
        if ( string.IsNullOrEmpty( s ) ) return null;
        int start = 0;
        while ( start < s.Length && char.IsWhiteSpace( s[start] ) ) start++;
        int end = start;
        while ( end < s.Length && !char.IsWhiteSpace( s[end] ) && s[end] != ',' && s[end] != ';' ) end++;
        return end > start ? s.Substring( start, end - start ) : null;
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
