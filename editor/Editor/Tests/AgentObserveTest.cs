// Editor/Tests/AgentObserveTest.cs
//
// [HANDOFF-TRUST: engine-fork-elite-leverage Phase 2.6 — durable regression gate for agents.observe; greenfield consumer-side reader test, no live hypothesis from any inherited handoff applies. Sibling to FeedAssetsSubscribeTest.cs; uses the same in-process Handler.Handle + ParseMcpEnvelope pattern.]
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission code for sbep.test_agents_observe; it does not CLAIM completion. The actual test run happens via editor.console_run, and the resulting PASS sentinel is written by the test-evidence hook on observing the SUMMARY log line.]
//
// Durable regression gate for the Phase 2.6 agents.observe MCP tool.
// Sibling to FeedAssetsSubscribeTest.cs: invokes the handler directly
// in-process (no MCP socket round-trip — ConCmds run on the main editor
// thread, same as the handler).
//
// Self-targeting: the test discovers this agent's own UUID by scanning
// .claude/session-state/agents/*/active-packet for the file whose content
// matches this slug. Re-runnable from any session that owns the slug.
//
// PRECONDITION  Project root resolvable + agents/ dir exists + own UUID
//               discoverable via active-packet content match. [SKIP] if
//               no agent owns this slug (test runs only from the session
//               that owns it; otherwise self-targeting is undefined).
// EXEC          AgentObserveHandler.HandleObserve({target_agent_id})
//               with the discovered own UUID. Parse the MCP envelope.
// ASSERT        Six gates, one log line each:
//                 response_ok            — ok=true in response
//                 schema_shape           — all 5 top-level keys present
//                 presence_status_active — presence.status is a known status
//                 active_packet_matches  — content equals the session slug
//                 board_row_phase_present — board_row.phase non-empty
//                 payload_under_cap      — response < 16384 chars
// CLEANUP       Idempotent — no fixtures to tear down.

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class AgentObserveTest
{
    /// <summary>Tier-3 regression test for the Phase 2.6 agents.observe MCP tool.</summary>
    [ConCmd( "sbep.test_agents_observe" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_agents_observe";
        const string SessionSlug = "engine-fork-phase2-6-agents-observe";

        await GameTask.MainThread();
        Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );

        // ── PRECONDITION ────────────────────────────────────────────────
        var projectRoot = Project.Current?.GetRootPath();
        if ( string.IsNullOrEmpty( projectRoot ) )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — Project.Current.GetRootPath() returned null/empty (no active editor session?)" );
            return;
        }

        var agentsDir = Path.Combine( projectRoot, ".claude", "session-state", "agents" );
        if ( !Directory.Exists( agentsDir ) )
        {
            Log.Info( $"[FAIL] {TestName}: PRECONDITION — agents dir does not exist at {agentsDir}" );
            return;
        }

        // Self-discover this agent's UUID by scanning for active-packet == SessionSlug.
        string ownAgentId = null;
        foreach ( var dir in Directory.EnumerateDirectories( agentsDir ) )
        {
            var pktPath = Path.Combine( dir, "active-packet" );
            if ( !File.Exists( pktPath ) ) continue;
            try
            {
                var content = File.ReadAllText( pktPath ).Trim();
                if ( string.Equals( content, SessionSlug, StringComparison.Ordinal ) )
                {
                    ownAgentId = Path.GetFileName( dir );
                    break;
                }
            }
            catch { /* keep scanning */ }
        }

        if ( ownAgentId == null )
        {
            Log.Info( $"[SKIP] {TestName}: PRECONDITION — no agents/*/active-packet matched slug '{SessionSlug}'; this test only runs from the session that owns the slug" );
            return;
        }

        var shortId = ownAgentId.Length >= 8 ? ownAgentId.Substring( 0, 8 ) : ownAgentId;
        Log.Info( $"[PASS] {TestName}: PRECONDITION — own agent id discovered via active-packet scan: {shortId}" );

        // ── EXEC ────────────────────────────────────────────────────────
        // Invoke the handler directly in-process — no MCP socket round-trip needed.
        // ConCmd runs on the main thread, same as the handler.
        var argsJson = JsonDocument.Parse( $"{{\"target_agent_id\":\"{ownAgentId}\"}}" ).RootElement;
        // mcp-dynamic-discovery Phase 3 Wave 3.1 — 'agents' migrated to [McpTool];
        // the action switch dissolved into [McpAction("observe")] HandleObserve.
        var resp = AgentObserveHandler.HandleObserve( argsJson );
        using var doc = ParseMcpEnvelope( resp );

        // Measure outer envelope size for the payload_under_cap gate.
        var envelopeJson = JsonSerializer.Serialize( resp );
        int responseChars = envelopeJson.Length;

        Log.Info( $"[PASS] {TestName}: EXEC — handler returned response (envelope size={responseChars} chars)" );

        // ── ASSERT ──────────────────────────────────────────────────────
        int failures = 0;

        // Gate 1: response_ok
        if ( doc.RootElement.TryGetProperty( "ok", out var okProp ) && okProp.ValueKind == JsonValueKind.True )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT response_ok — ok=true" );
        }
        else
        {
            failures++;
            var head = envelopeJson.Length > 400 ? envelopeJson.Substring( 0, 400 ) + "…" : envelopeJson;
            Log.Info( $"[FAIL] {TestName}: ASSERT response_ok — ok missing or not true: {head}" );
        }

        // Gate 2: schema_shape — all 5 required top-level keys present
        var requiredKeys = new[] { "presence", "active_packet", "board_row", "test_evidence", "feed_slice" };
        bool schemaOk = true;
        foreach ( var k in requiredKeys )
        {
            if ( !doc.RootElement.TryGetProperty( k, out _ ) )
            {
                schemaOk = false;
                Log.Info( $"[FAIL] {TestName}: ASSERT schema_shape — required key '{k}' missing" );
            }
        }
        if ( schemaOk )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT schema_shape — all 5 top-level keys present (presence, active_packet, board_row, test_evidence, feed_slice)" );
        }
        else
        {
            failures++;
        }

        // Gate 3: presence_status_active — must be one of the known values from tools/presence.py
        if ( doc.RootElement.TryGetProperty( "presence", out var presEl )
            && presEl.TryGetProperty( "status", out var statusEl )
            && statusEl.ValueKind == JsonValueKind.String )
        {
            var status = statusEl.GetString();
            bool knownStatus = status == "idle" || status == "editing" || status == "compiling"
                || status == "playing" || status == "verifying";
            if ( knownStatus )
                Log.Info( $"[PASS] {TestName}: ASSERT presence_status_active — presence.status='{status}' (this session is live)" );
            else
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: ASSERT presence_status_active — presence.status='{status}' not in known set (idle/editing/compiling/playing/verifying)" );
            }
        }
        else
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: ASSERT presence_status_active — presence.status missing or not a string" );
        }

        // Gate 4: active_packet_matches — content equals this session's slug
        if ( doc.RootElement.TryGetProperty( "active_packet", out var apEl )
            && apEl.TryGetProperty( "content", out var contentEl )
            && contentEl.ValueKind == JsonValueKind.String
            && string.Equals( contentEl.GetString()?.Trim(), SessionSlug, StringComparison.Ordinal ) )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT active_packet_matches — content=='{SessionSlug}'" );
        }
        else
        {
            failures++;
            string got = "<missing>";
            if ( apEl.ValueKind == JsonValueKind.Object && apEl.TryGetProperty( "content", out var c )
                && c.ValueKind == JsonValueKind.String )
                got = c.GetString();
            Log.Info( $"[FAIL] {TestName}: ASSERT active_packet_matches — expected '{SessionSlug}', got '{got}'" );
        }

        // Gate 5: board_row_phase_present — board.py wrote the row at /start
        if ( doc.RootElement.TryGetProperty( "board_row", out var brEl )
            && brEl.TryGetProperty( "phase", out var phaseEl )
            && phaseEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty( phaseEl.GetString() ) )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT board_row_phase_present — phase='{phaseEl.GetString()}'" );
        }
        else
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: ASSERT board_row_phase_present — board_row.phase missing or empty (board.py add at /start should have written phase=phase-01)" );
        }

        // Gate 6: payload_under_cap — full envelope must fit single-shot response
        if ( responseChars < 16384 )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT payload_under_cap — {responseChars} chars < 16384 cap" );
        }
        else
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: ASSERT payload_under_cap — {responseChars} chars >= 16384 cap; agents.observe would fall through to CursorStore pagination and break the single-shot v1 contract" );
        }

        // ── CLEANUP ─────────────────────────────────────────────────────
        // No fixtures to tear down. Re-running yields the same answer (idempotent).
        Log.Info( $"[PASS] {TestName}: CLEANUP — idempotent, no fixtures to remove" );

        // ── SUMMARY ─────────────────────────────────────────────────────
        if ( failures == 0 )
        {
            Log.Info( $"[PASS] {TestName}: SUMMARY — 6/6 gates green (response_ok + schema_shape + presence_status_active + active_packet_matches + board_row_phase_present + payload_under_cap); Phase 2.6 wrapper verified end-to-end (presence.json + agents/<id>/active-packet + INDEX.json + test-evidence/ → AgentObserveHandler.Handle → wire format)" );
        }
        else
        {
            Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gates failed of 6 total" );
        }
    }

    /// <summary>
    /// Unwrap the MCP envelope <c>{content:[{type:"text",text:"&lt;inner-json&gt;"}]}</c>
    /// that HandlerBase.Success wraps around handler responses, returning a
    /// JsonDocument positioned at the inner response payload.
    ///
    /// Copied verbatim from FeedAssetsSubscribeTest.cs (lines 281-302) per
    /// Phase 2 banned-approach #2 — do not re-derive the envelope shape; the
    /// four prior Phase 2 tests validated the exact bytes.
    /// </summary>
    private static JsonDocument ParseMcpEnvelope( object handlerResponse )
    {
        var envelopeJson = JsonSerializer.Serialize( handlerResponse );
        using ( var envelopeDoc = JsonDocument.Parse( envelopeJson ) )
        {
            if ( envelopeDoc.RootElement.TryGetProperty( "content", out var contentArr )
                && contentArr.ValueKind == JsonValueKind.Array
                && contentArr.GetArrayLength() > 0 )
            {
                var first = contentArr[0];
                if ( first.TryGetProperty( "text", out var textProp )
                    && textProp.ValueKind == JsonValueKind.String )
                {
                    return JsonDocument.Parse( textProp.GetString() );
                }
            }
        }
        // Defensive fallback: return the envelope verbatim. The caller's
        // TryGetProperty lookups will simply miss and report a clear failure
        // rather than crash on null.
        return JsonDocument.Parse( envelopeJson );
    }
}
