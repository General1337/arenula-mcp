// Editor/Handlers/AgentObserveHandler.cs
//
// [HANDOFF-TRUST: greenfield Phase 2.6 of engine-fork-elite-leverage initiative — first cross-agent capability built on top of the four feed.* channels shipped this morning (2.1/2.2/2.3/2.4). No live hypothesis from any inherited handoff applies: 226-engine-fork-elite-leverage-handoff is the INITIATIVE-level handoff (not a phase-2.6-specific hypothesis), and the other matched handoffs (129 hotload, 187 thruster, 200 command-engineering, 26 earth-solar, 163/164 trees, 176 drawbridge) are domain-orthogonal substring collisions. This file is a consumer-side reader aggregating already-shipped dispatcher/board/presence state — there is no fix-on-existing-bug semantic to fall victim to handoff-hypothesis-misalignment.]
//
// engine-fork-elite-leverage Phase 2.6 — agents.observe cross-agent observation tool.
//
// First cross-agent capability built on top of the four feed.* channels shipped in
// Phase 2.1/2.2/2.3/2.4. Aggregates four file-system sources into a single response:
//   * presence row from .claude/session-state/presence.json
//   * active-packet file from .claude/session-state/agents/<id>/active-packet
//   * board row from docs/ai/sessions/INDEX.json
//   * test-evidence sentinel count from .claude/session-state/agents/<id>/test-evidence/
//
// Library-only — no engine fork work. The "producer side" already exists (dispatcher
// daemon, presence hook, board.py, /start, /test-harness). This file is the consumer-
// side reader that aggregates them into one MCP call.
//
// Banned approaches inherited from Phase 2.1/2.3/2.4 (BINDING, thrice-validated):
//   1. Response payload < HandlerBase.MaxResponseChars (16384).
//      active_packet.content truncated to ActivePacketBudgetChars; truncated=true flag set.
//   2. snake_case literals in anonymous records (JsonNamingPolicy.CamelCase only
//      lowercases the first char — snake_case survives verbatim).
//   3. NO FeedHandler.cs mutation. feed_slice is null in v1; v1.1 adds the slice
//      without contract break.
//   4. Library hot-reload first — no cold-boot required for this file. The
//      ToolRegistry anonymous-record entry is known stale post-hot-reload
//      (feedback_toolregistry_anonymous_record_stale_after_hot_reload) but
//      RpcDispatcher routes the new "agents" case correctly; mcp.capabilities
//      drift is cosmetic.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// agents.observe — return a single structured snapshot of another agent's
/// working state. Read-only; no static state; no event subscription.
/// Sibling pattern: McpHandler.cs (request/response read-only).
///
/// Migrated to the [McpTool] attribute scan in mcp-dynamic-discovery Phase 3
/// Wave 3.1 — single-action tool, MainThread dispatch.
/// </summary>
[McpTool( "agents",
	"Cross-agent observation. Returns a structured snapshot of another agent's working state by aggregating four sources: presence row (.claude/session-state/presence.json), active-packet file (.claude/session-state/agents/<id>/active-packet), board row (docs/ai/sessions/INDEX.json), and test-evidence sentinel count (.claude/session-state/agents/<id>/test-evidence/). Accepts the target agent's full UUID or an 8-char prefix. Read-only; no scene mutation. Library-only — built on top of the existing dispatcher daemon + presence hook + board.py + /start + /test-harness producer side. v1 ships observe; v1.1 will add list + subscribe + feed_slice (bounded windows into the compile/hotload/scene/assets feed.* channels filtered by target agent).",
	ReadOnlyHint = true )]
internal static class AgentObserveHandler
{
    /// <summary>Max chars of active-packet content embedded in the response.
    /// Leaves headroom under HandlerBase.MaxResponseChars (16384) for the rest
    /// of the envelope (presence + board_row + test_evidence + JSON overhead).</summary>
    private const int ActivePacketBudgetChars = 8000;

    /// <summary>
    /// 'observe' action handler. The try/catch preserves the legacy
    /// <c>HandlerBase.Error</c> response shape — the pre-migration
    /// <c>Handle()</c> switch wrapped <see cref="Observe"/> the same way.
    /// </summary>
    [McpAction( "observe", ReadOnly = true )]
    internal static object HandleObserve( JsonElement args )
    {
        try
        {
            return Observe( args );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, "observe" );
        }
    }

    /// <summary>
    /// tools/list inputSchema fragment — verbatim copy of the legacy
    /// <c>ToolRegistry.AgentObserve.inputSchema</c> literal (mcp-dynamic-discovery
    /// Phase 3). The scanner invokes this once at plugin load and caches it.
    /// </summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "observe" } },
            ["target_agent_id"] = new { type = "string", description = "Target agent identifier. Required for: observe. Accepts full UUID (e.g. e6775839-2120-4252-b19c-dc22c3d8b949) or 8-char prefix (e.g. e6775839). Prefix collisions resolve to the most recent last_seen." }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    private static object Observe( JsonElement args )
    {
        var input = HandlerBase.GetString( args, "target_agent_id" );
        if ( string.IsNullOrWhiteSpace( input ) )
        {
            return HandlerBase.Error(
                "Missing required 'target_agent_id' parameter.", "observe",
                "Provide either the full UUID (e.g. e6775839-2120-4252-b19c-dc22c3d8b949) or an 8-char prefix (e.g. e6775839)." );
        }

        var projectRoot = HandlerBase.GetProjectRoot();
        if ( string.IsNullOrEmpty( projectRoot ) )
        {
            return HandlerBase.Error(
                "Could not resolve project root via Project.Current.GetRootPath().", "observe",
                "agents.observe requires file-system access under the project root; ensure an editor session is active." );
        }

        // Canonicalize the agent id — accept full UUID or 8-char prefix.
        // Falls back to verbatim input (with found=false on all sub-objects) if unresolvable.
        var canonical = ResolveAgentId( input, projectRoot ) ?? input;

        var presence = ReadPresence( canonical, projectRoot );
        var activePacket = ReadActivePacket( canonical, projectRoot );
        var boardRow = ReadBoardRow( canonical, presence?.Slug, projectRoot );
        var testEvidence = CountTestEvidence( canonical, projectRoot );

        // [HANDOFF-TRUST: feed_slice non-null sentinel — DefaultIgnoreCondition.WhenWritingNull strips null fields from the wire, which would break the v1 contract shape; non-null { available: false } reserves the slot for v1.1.]
        return HandlerBase.Success( new
        {
            ok = true,
            target_agent_id = canonical,
            presence = presence?.ToWire() ?? PresenceNotFound(),
            active_packet = activePacket?.ToWire() ?? ActivePacketNotFound(),
            board_row = boardRow?.ToWire() ?? BoardRowNotFound(),
            test_evidence = testEvidence.ToWire(),
            // v1 reserves feed_slice for v1.1 (bounded window into compile/hotload/scene/assets feed.* channels
            // filtered by target agent). Non-null sentinel survives HandlerBase.Json's
            // DefaultIgnoreCondition.WhenWritingNull so v1.1 clients can check feed_slice.available.
            feed_slice = new { available = false, reason = "deferred to v1.1" }
        } );
    }

    // ── Agent id resolution ────────────────────────────────────────────

    /// <summary>Accept full UUID or 8-char prefix. Returns canonical full UUID,
    /// or null if unresolvable. Prefix collisions resolve to the most recent
    /// last_seen.</summary>
    private static string ResolveAgentId( string input, string projectRoot )
    {
        // Full UUID — pass through if it looks UUID-shaped (contains dashes + ≥36 chars).
        if ( input.Length >= 36 && input.Contains( '-' ) )
            return input;

        // 8-char prefix — match against presence.json first (preferred — last_seen tiebreak).
        try
        {
            var presencePath = Path.Combine( projectRoot, ".claude", "session-state", "presence.json" );
            if ( File.Exists( presencePath ) )
            {
                using var doc = JsonDocument.Parse( File.ReadAllText( presencePath ) );
                if ( doc.RootElement.TryGetProperty( "agents", out var agentsArr )
                    && agentsArr.ValueKind == JsonValueKind.Array )
                {
                    string bestMatch = null;
                    long bestLastSeen = -1;
                    foreach ( var row in agentsArr.EnumerateArray() )
                    {
                        if ( !row.TryGetProperty( "agent_id", out var idProp ) ) continue;
                        var id = idProp.GetString();
                        if ( id == null ) continue;
                        if ( !id.StartsWith( input, StringComparison.OrdinalIgnoreCase ) ) continue;

                        long lastSeen = 0;
                        if ( row.TryGetProperty( "last_seen", out var lsProp ) && lsProp.TryGetInt64( out var ls ) )
                            lastSeen = ls;
                        if ( lastSeen > bestLastSeen )
                        {
                            bestMatch = id;
                            bestLastSeen = lastSeen;
                        }
                    }
                    if ( bestMatch != null )
                        return bestMatch;
                }
            }
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.ResolveAgentId: presence read failed: {ex.Message}" );
        }

        // Fall back to agents/ directory listing.
        try
        {
            var agentsDir = Path.Combine( projectRoot, ".claude", "session-state", "agents" );
            if ( Directory.Exists( agentsDir ) )
            {
                foreach ( var dir in Directory.EnumerateDirectories( agentsDir ) )
                {
                    var name = Path.GetFileName( dir );
                    if ( name != null && name.StartsWith( input, StringComparison.OrdinalIgnoreCase ) )
                        return name;
                }
            }
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.ResolveAgentId: agents dir scan failed: {ex.Message}" );
        }

        return null;
    }

    // ── Presence read ──────────────────────────────────────────────────

    private sealed class PresenceData
    {
        public string Status;
        public string Slug;
        public long LastSeen;
        public long Started;
        public string Mode;

        public object ToWire() => new
        {
            found = true,
            status = Status ?? "unknown",
            slug = Slug ?? "",
            last_seen = LastSeen,
            last_seen_human = HumanizeAge( LastSeen ),
            started = Started,
            mode = Mode
        };
    }

    private static object PresenceNotFound() => new
    {
        found = false,
        status = "unknown",
        slug = (string)null,
        last_seen = 0L,
        last_seen_human = "(not in presence)",
        started = 0L,
        mode = (string)null
    };

    private static PresenceData ReadPresence( string agentId, string projectRoot )
    {
        try
        {
            var path = Path.Combine( projectRoot, ".claude", "session-state", "presence.json" );
            if ( !File.Exists( path ) ) return null;
            using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
            if ( !doc.RootElement.TryGetProperty( "agents", out var arr )
                || arr.ValueKind != JsonValueKind.Array )
                return null;
            foreach ( var row in arr.EnumerateArray() )
            {
                if ( !row.TryGetProperty( "agent_id", out var idProp ) ) continue;
                if ( !string.Equals( idProp.GetString(), agentId, StringComparison.OrdinalIgnoreCase ) )
                    continue;
                return new PresenceData
                {
                    Status = row.TryGetProperty( "status", out var s ) ? s.GetString() : null,
                    Slug = row.TryGetProperty( "slug", out var sl ) ? sl.GetString() : null,
                    LastSeen = row.TryGetProperty( "last_seen", out var ls ) && ls.TryGetInt64( out var lsv ) ? lsv : 0,
                    Started = row.TryGetProperty( "started", out var st ) && st.TryGetInt64( out var stv ) ? stv : 0,
                    Mode = row.TryGetProperty( "mode", out var m ) && m.ValueKind == JsonValueKind.String ? m.GetString() : null
                };
            }
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.ReadPresence: {ex.Message}" );
        }
        return null;
    }

    private static string HumanizeAge( long epochSeconds )
    {
        if ( epochSeconds <= 0 ) return "(unknown)";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var age = now - epochSeconds;
        if ( age < 0 ) return "in the future";
        if ( age < 60 ) return $"{age}s ago";
        if ( age < 3600 ) return $"{age / 60}m ago";
        if ( age < 86400 ) return $"{age / 3600}h ago";
        return $"{age / 86400}d ago";
    }

    // ── Active packet read ────────────────────────────────────────────

    private sealed class ActivePacketData
    {
        public string Content;
        public long Mtime;
        public bool Truncated;

        public object ToWire() => new
        {
            found = true,
            content = Content ?? "",
            mtime = Mtime,
            truncated = Truncated
        };
    }

    private static object ActivePacketNotFound() => new
    {
        found = false,
        content = (string)null,
        mtime = 0L,
        truncated = false
    };

    private static ActivePacketData ReadActivePacket( string agentId, string projectRoot )
    {
        try
        {
            var path = Path.Combine( projectRoot, ".claude", "session-state", "agents", agentId, "active-packet" );
            if ( !File.Exists( path ) ) return null;
            var raw = File.ReadAllText( path );
            var mtime = new DateTimeOffset( File.GetLastWriteTimeUtc( path ), TimeSpan.Zero ).ToUnixTimeSeconds();
            bool truncated = false;
            if ( raw != null && raw.Length > ActivePacketBudgetChars )
            {
                raw = raw.Substring( 0, ActivePacketBudgetChars );
                truncated = true;
            }
            return new ActivePacketData
            {
                Content = raw,
                Mtime = mtime,
                Truncated = truncated
            };
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.ReadActivePacket: {ex.Message}" );
        }
        return null;
    }

    // ── Board row read ────────────────────────────────────────────────

    private sealed class BoardRowData
    {
        public string Slug;
        public string Date;
        public string Phase;
        public string Status;
        public string WorkType;
        public string Domain;
        public string BriefTarget;
        public string ParentInitiative;
        public string Mode;
        public long StartedTs;
        public long LastHeartbeat;

        public object ToWire() => new
        {
            found = true,
            slug = Slug,
            date = Date,
            phase = Phase,
            status = Status,
            work_type = WorkType,
            domain = Domain,
            brief_target = BriefTarget,
            parent_initiative = ParentInitiative,
            mode = Mode,
            started_ts = StartedTs,
            last_heartbeat = LastHeartbeat
        };
    }

    private static object BoardRowNotFound() => new
    {
        found = false,
        slug = (string)null,
        date = (string)null,
        phase = (string)null,
        status = (string)null,
        work_type = (string)null,
        domain = (string)null,
        brief_target = (string)null,
        parent_initiative = (string)null,
        mode = (string)null,
        started_ts = 0L,
        last_heartbeat = 0L
    };

    private static BoardRowData ReadBoardRow( string agentId, string fallbackSlug, string projectRoot )
    {
        try
        {
            var path = Path.Combine( projectRoot, "docs", "ai", "sessions", "INDEX.json" );
            if ( !File.Exists( path ) ) return null;
            using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
            if ( !doc.RootElement.TryGetProperty( "sessions", out var arr )
                || arr.ValueKind != JsonValueKind.Array )
                return null;

            // Prefer active-row match by agent_id (handles agents with multiple historical rows).
            JsonElement? match = null;
            foreach ( var row in arr.EnumerateArray() )
            {
                if ( !row.TryGetProperty( "agent_id", out var idProp ) ) continue;
                if ( !string.Equals( idProp.GetString(), agentId, StringComparison.OrdinalIgnoreCase ) ) continue;
                // [HANDOFF-TRUST: rename-local is mechanical CS0136 fix; independent of any inherited handoff hypothesis]
                // Local renamed from 'st' to 'rowStatus' to avoid CS0136 collision with the
                // out var st in the BoardRowData initializer below (out-var scope = enclosing try block).
                var rowStatus = row.TryGetProperty( "status", out var stProp ) ? stProp.GetString() : null;
                if ( rowStatus == "active" )
                {
                    match = row.Clone();
                    break;
                }
                // Hold the first agent_id match as a candidate if no active row found later.
                if ( !match.HasValue ) match = row.Clone();
            }

            // Fallback: slug match (useful when agent_id has rotated but slug persists).
            if ( !match.HasValue && !string.IsNullOrEmpty( fallbackSlug ) )
            {
                foreach ( var row in arr.EnumerateArray() )
                {
                    if ( !row.TryGetProperty( "slug", out var slProp ) ) continue;
                    if ( string.Equals( slProp.GetString(), fallbackSlug, StringComparison.OrdinalIgnoreCase ) )
                    {
                        match = row.Clone();
                        break;
                    }
                }
            }

            if ( !match.HasValue ) return null;
            var r = match.Value;
            return new BoardRowData
            {
                Slug = r.TryGetProperty( "slug", out var s ) ? s.GetString() : null,
                Date = r.TryGetProperty( "date", out var d ) ? d.GetString() : null,
                Phase = r.TryGetProperty( "phase", out var p ) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                Status = r.TryGetProperty( "status", out var st ) ? st.GetString() : null,
                WorkType = r.TryGetProperty( "work_type", out var wt ) && wt.ValueKind == JsonValueKind.String ? wt.GetString() : null,
                Domain = r.TryGetProperty( "domain", out var dom ) && dom.ValueKind == JsonValueKind.String ? dom.GetString() : null,
                BriefTarget = r.TryGetProperty( "brief_target", out var bt ) && bt.ValueKind == JsonValueKind.String ? bt.GetString() : null,
                ParentInitiative = r.TryGetProperty( "parent_initiative", out var pi ) && pi.ValueKind == JsonValueKind.String ? pi.GetString() : null,
                Mode = r.TryGetProperty( "mode", out var m ) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                StartedTs = r.TryGetProperty( "started_ts", out var sts ) && sts.TryGetInt64( out var stsv ) ? stsv : 0,
                LastHeartbeat = r.TryGetProperty( "last_heartbeat", out var lh ) && lh.TryGetInt64( out var lhv ) ? lhv : 0
            };
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.ReadBoardRow: {ex.Message}" );
        }
        return null;
    }

    // ── Test evidence count ───────────────────────────────────────────

    private sealed class TestEvidenceData
    {
        public int Count;
        public string MostRecentName;
        public long MostRecentMtime;

        public object ToWire() => new
        {
            count = Count,
            most_recent = Count > 0 ? (object)new { name = MostRecentName, mtime = MostRecentMtime } : null
        };
    }

    private static TestEvidenceData CountTestEvidence( string agentId, string projectRoot )
    {
        var data = new TestEvidenceData();
        try
        {
            var dir = Path.Combine( projectRoot, ".claude", "session-state", "agents", agentId, "test-evidence" );
            if ( !Directory.Exists( dir ) ) return data;
            string mostRecent = null;
            long mostRecentMtime = 0;
            int count = 0;
            foreach ( var f in Directory.EnumerateFiles( dir, "*.json" ) )
            {
                count++;
                var mt = new DateTimeOffset( File.GetLastWriteTimeUtc( f ), TimeSpan.Zero ).ToUnixTimeSeconds();
                if ( mt > mostRecentMtime )
                {
                    mostRecentMtime = mt;
                    mostRecent = Path.GetFileNameWithoutExtension( f );
                }
            }
            data.Count = count;
            data.MostRecentName = mostRecent;
            data.MostRecentMtime = mostRecentMtime;
        }
        catch ( Exception ex )
        {
            Log.Warning( $"AgentObserveHandler.CountTestEvidence: {ex.Message}" );
        }
        return data;
    }
}
