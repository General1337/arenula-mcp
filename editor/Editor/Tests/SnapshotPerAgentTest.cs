// Editor/Tests/SnapshotPerAgentTest.cs
//
// [HANDOFF-TRUST: engine-fork-elite-leverage Phase 3.2 — durable regression gate
// for the per-agent SnapshotHandler. No live hypothesis from any inherited
// handoff applies: Phase 3.2 is a greenfield library extension (packet.md
// §"Prior Failed Approaches" — none). Sibling test shape: ScratchScenePoolTest.cs.]
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log
// emission code for sbep.test_snapshot_per_agent; it does not CLAIM completion.
// The actual test run happens via editor.console_run, and the resulting PASS
// sentinel is written by the test-evidence hook on observing the SUMMARY line.]
//
// Durable regression gate for the Phase 3.2 per-agent SnapshotHandler. Library-
// only — NO [SKIP] path: runs identically on the fork engine and on stable
// Facepunch (mirrors ScratchScenePoolTest.cs, not the Phase-1.x fork-event tests).
//
// Uses two fixed synthetic agent ids that never belong to real agents, so a
// concurrent live agent's snapshot bucket is never touched. Snapshots are taken
// scoped to throwaway NotSaved fixture GameObjects so every `take` is cheap.
//
// PRECONDITION  Scene resolvable; clear the two test buckets + the anonymous
//               bucket; DestroyImmediate any stale fixtures from a crashed run.
// EXEC          Drive take/list/diff/restore/delete through SnapshotHandler.Handle
//               with synthetic McpSessions (and a null session for anonymous).
// ASSERT        Nine gates: per-agent isolation (distinct buckets, scoped delete),
//               per-agent eviction (caps at 50, evicts oldest only, other agent
//               untouched), Material round-trip, Color round-trip, Vector3
//               round-trip, anonymous-bucket regression.
// CLEANUP       Delete every test snapshot; DestroyImmediate the fixtures;
//               idempotent re-run (PRECONDITION re-clears).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class SnapshotPerAgentTest
{
    const string TestName = "sbep.test_snapshot_per_agent";
    const string AgentA = "test-agent-snapshot-a";
    const string AgentB = "test-agent-snapshot-b";
    const string TinyFixtureName = "snap_test_tiny_fixture";
    const string PropsFixtureName = "snap_test_props_fixture";

    /// <summary>Tier-3 regression test for the Phase 3.2 per-agent snapshot handler.</summary>
    [ConCmd( "sbep.test_snapshot_per_agent" )]
    public static async void Run()
    {
        await GameTask.MainThread();
        Log.Info( $"[INFO] {TestName}: ENTER @ {DateTime.UtcNow:O}" );

        // ── PRECONDITION ────────────────────────────────────────────────
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
        {
            Log.Error( $"[FAIL] {TestName}: PRECONDITION — SceneHelpers.ResolveScene() returned null (no active editor session?)" );
            return;
        }

        var sessionA = new McpSession { SessionId = "test-snap-sess-a", AgentId = AgentA };
        var sessionB = new McpSession { SessionId = "test-snap-sess-b", AgentId = AgentB };

        ClearBucket( sessionA );
        ClearBucket( sessionB );
        ClearBucket( null ); // anonymous bucket
        DestroyStaleFixtures( scene );
        Log.Info( $"[PASS] {TestName}: PRECONDITION — scene resolved; test buckets + stale fixtures cleared" );

        int failures = 0;

        // ── fixtures ────────────────────────────────────────────────────
        // NotSaved|NotNetworked: a crashed run never persists these to disk.
        var tiny = scene.CreateObject();
        tiny.Name = TinyFixtureName;
        tiny.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
        var tinyId = tiny.Id.ToString();

        var props = scene.CreateObject();
        props.Name = PropsFixtureName;
        props.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
        var propsId = props.Id.ToString();
        var mr = props.Components.Create<ModelRenderer>();
        var bc = props.Components.Create<BoxCollider>();

        // ── EXEC + ASSERT ───────────────────────────────────────────────

        // Gate 1 — per-agent isolation: both agents take a snapshot named "iso".
        using ( Invoke( "take", Args( ("name", "iso"), ("scope_id", tinyId) ), sessionA ) ) { }
        using ( Invoke( "take", Args( ("name", "iso"), ("scope_id", tinyId) ), sessionB ) ) { }

        var listAll = ReadList( null, allAgents: true );
        var listA = ReadList( sessionA, allAgents: false );
        var listB = ReadList( sessionB, allAgents: false );

        bool isolationOk =
            listAll.Count >= 2 &&                                   // global storage would show 1
            listA.Count == 1 && listA.Entries[0].AgentId == AgentA &&
            listB.Count == 1 && listB.Entries[0].AgentId == AgentB;
        Gate( ref failures, "isolation_distinct_buckets", isolationOk,
            "agents A and B each hold their own 'iso' snapshot (per-agent buckets, agentId-tagged)",
            $"expected all>=2 & A=1(agentId={AgentA}) & B=1(agentId={AgentB}), got all={listAll.Count} A={listA.Count}({First( listA )}) B={listB.Count}({First( listB )})" );

        // Gate 2 — scoped delete: A deletes its "iso"; B's "iso" must survive.
        using ( Invoke( "delete", Args( ("name", "iso") ), sessionA ) ) { }
        int aAfterDelete = ReadList( sessionA, false ).Count;
        int bAfterDelete = ReadList( sessionB, false ).Count;
        Gate( ref failures, "isolation_delete_scoped", aAfterDelete == 0 && bAfterDelete == 1,
            "A.delete('iso') emptied only A's bucket; B's 'iso' untouched",
            $"expected A=0 & B=1 after A.delete, got A={aAfterDelete} B={bAfterDelete}" );

        // Gates 3-5 — per-agent eviction. B currently holds exactly 1 ('iso').
        int bBeforeStorm = ReadList( sessionB, false ).Count;
        for ( int i = 1; i <= 51; i++ )
        {
            using ( Invoke( "take", Args( ("name", $"evict-{i:D3}"), ("scope_id", tinyId) ), sessionA ) ) { }
        }
        var afterStorm = ReadList( sessionA, false );
        int bAfterStorm = ReadList( sessionB, false ).Count;
        var stormNames = afterStorm.Entries.Select( e => e.Name ).ToHashSet();

        Gate( ref failures, "eviction_caps_at_50", afterStorm.Count == 50,
            "agent A's bucket caps at 50 after 51 takes",
            $"expected 50, got {afterStorm.Count}" );
        Gate( ref failures, "eviction_evicts_oldest_only",
            !stormNames.Contains( "evict-001" ) && stormNames.Contains( "evict-051" ),
            "the oldest snapshot (evict-001) was evicted; the newest (evict-051) survived",
            $"expected evict-001 gone & evict-051 present, got has001={stormNames.Contains( "evict-001" )} has051={stormNames.Contains( "evict-051" )}" );
        Gate( ref failures, "eviction_isolated_from_other_agent", bAfterStorm == bBeforeStorm,
            "agent B's bucket count is unchanged by agent A's 51-take eviction storm",
            $"expected B unchanged at {bBeforeStorm}, got {bAfterStorm}" );

        // Gates 6-8 — Material + value-type long-tail round-trip on the props
        // fixture. The original handler captured these but DROPPED them on restore.
        var colorP = new Color( 0.20f, 0.55f, 0.85f, 1f );
        var colorQ = new Color( 0.85f, 0.15f, 0.35f, 1f );
        var vecP = new Vector3( 11f, 22f, 33f );
        var vecQ = new Vector3( 44f, 55f, 66f );

        bool materialsOk = TryLoadTestMaterials( out var matP, out var matQ, out var matDiag );

        // P-state.
        if ( materialsOk ) mr.MaterialOverride = matP;
        mr.Tint = colorP;
        bc.Scale = vecP;
        string pMatName = materialsOk ? mr.MaterialOverride?.Name : null;

        using ( Invoke( "take", Args( ("name", "props"), ("scope_id", propsId) ), sessionA ) ) { }

        // Q-state — change everything the snapshot just captured.
        if ( materialsOk ) mr.MaterialOverride = matQ;
        mr.Tint = colorQ;
        bc.Scale = vecQ;

        using ( Invoke( "restore", Args( ("name", "props") ), sessionA ) ) { }

        bool materialGate = materialsOk && mr.MaterialOverride != null && mr.MaterialOverride.Name == pMatName;
        Gate( ref failures, "material_round_trip", materialGate,
            $"ModelRenderer.MaterialOverride reverted to '{pMatName}' via snapshot.restore (Material.Load round-trip)",
            materialsOk
                ? $"expected MaterialOverride.Name=='{pMatName}', got '{mr.MaterialOverride?.Name ?? "(null)"}'"
                : $"could not load 2 distinct test materials — {matDiag}" );

        Gate( ref failures, "color_round_trip", ColorClose( mr.Tint, colorP ),
            "ModelRenderer.Tint (Color) reverted to its pre-snapshot value via restore",
            $"expected Tint≈{colorP}, got {mr.Tint}" );

        Gate( ref failures, "vector3_round_trip", VecClose( bc.Scale, vecP ),
            "BoxCollider.Scale (Vector3) reverted to its pre-snapshot value via restore",
            $"expected Scale≈{vecP}, got {bc.Scale}" );

        // Gate 9 — anonymous-bucket regression: a null session (no session.identify)
        // must still take/list/diff/restore/delete against the shared "_anonymous"
        // bucket — the legacy-client path.
        bool anonTake1 = !Invoke2( "take", Args( ("name", "anon"), ("scope_id", tinyId) ), null );
        bool anonTake2 = !Invoke2( "take", Args( ("name", "anon2"), ("scope_id", tinyId) ), null );
        var anonList = ReadList( null, false );
        bool anonListOk = anonList.Count >= 2 && anonList.Entries.All( e => e.AgentId == "_anonymous" );
        bool anonDiff = !Invoke2( "diff", Args( ("name_a", "anon"), ("name_b", "anon2") ), null );
        bool anonRestore = !Invoke2( "restore", Args( ("name", "anon") ), null );
        bool anonDelete = !Invoke2( "delete", Args( ("name", "anon") ), null )
                        && !Invoke2( "delete", Args( ("name", "anon2") ), null );
        bool anonOk = anonTake1 && anonTake2 && anonListOk && anonDiff && anonRestore && anonDelete;
        Gate( ref failures, "anonymous_bucket", anonOk,
            "null-session take/list/diff/restore/delete all succeed against the '_anonymous' bucket",
            $"take1={anonTake1} take2={anonTake2} list={anonListOk} diff={anonDiff} restore={anonRestore} delete={anonDelete}" );

        // ── CLEANUP ─────────────────────────────────────────────────────
        ClearBucket( sessionA );
        ClearBucket( sessionB );
        ClearBucket( null );
        DestroyStaleFixtures( scene );
        Log.Info( $"[PASS] {TestName}: CLEANUP — test buckets emptied; fixtures destroyed. Idempotent re-run safe (PRECONDITION re-clears)." );

        // ── SUMMARY ─────────────────────────────────────────────────────
        if ( failures == 0 )
            Log.Info( $"[PASS] {TestName}: SUMMARY — 9/9 gates green; per-agent snapshot isolation + 50-cap eviction + Material/Color/Vector3 restore + anonymous-bucket regression verified end-to-end" );
        else
            Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of 9 total" );
    }

    // ── Handler invocation ──────────────────────────────────────────────

    /// <summary>
    /// Parsed shape of a SnapshotHandler response. <see cref="Inner"/> is the
    /// unwrapped JSON payload (null for a plain-text Confirm); the caller disposes.
    /// </summary>
    private sealed class InvokeResult : IDisposable
    {
        public bool IsError;
        public JsonDocument Inner;
        public void Dispose() => Inner?.Dispose();
    }

    /// <summary>
    /// Call <see cref="SnapshotHandler.Handle"/> in-process (ConCmds run on the
    /// main editor thread, same as the handler) and unwrap the MCP envelope.
    /// </summary>
    private static InvokeResult Invoke( string action, string argsJson, McpSession session )
    {
        using var argsDoc = JsonDocument.Parse( argsJson );
        var resp = SnapshotHandler.Handle( action, argsDoc.RootElement, session );

        var envelopeJson = JsonSerializer.Serialize( resp );
        using var env = JsonDocument.Parse( envelopeJson );

        bool isError = env.RootElement.TryGetProperty( "isError", out var e ) && e.ValueKind == JsonValueKind.True;

        string text = null;
        if ( env.RootElement.TryGetProperty( "content", out var contentArr )
            && contentArr.ValueKind == JsonValueKind.Array && contentArr.GetArrayLength() > 0
            && contentArr[0].TryGetProperty( "text", out var t ) && t.ValueKind == JsonValueKind.String )
        {
            text = t.GetString();
        }

        JsonDocument inner = null;
        if ( text != null )
        {
            // Success/Error payloads are JSON; a Confirm payload is plain text.
            try { inner = JsonDocument.Parse( text ); } catch { /* plain-text Confirm */ }
        }

        return new InvokeResult { IsError = isError, Inner = inner };
    }

    /// <summary>Invoke and return only the isError flag (disposes the result).</summary>
    private static bool Invoke2( string action, string argsJson, McpSession session )
    {
        using var r = Invoke( action, argsJson, session );
        return r.IsError;
    }

    // ── List reading ────────────────────────────────────────────────────

    private readonly struct ListEntry
    {
        public readonly string Name;
        public readonly string AgentId;
        public ListEntry( string name, string agentId ) { Name = name; AgentId = agentId; }
    }

    private readonly struct ListResult
    {
        public readonly int Count;
        public readonly List<ListEntry> Entries;
        public ListResult( int count, List<ListEntry> entries ) { Count = count; Entries = entries; }
    }

    private static ListResult ReadList( McpSession session, bool allAgents )
    {
        var entries = new List<ListEntry>();
        int count = 0;
        using ( var r = Invoke( "list", allAgents ? Args( ("all_agents", true) ) : "{}", session ) )
        {
            if ( r.Inner != null )
            {
                if ( r.Inner.RootElement.TryGetProperty( "count", out var c ) && c.ValueKind == JsonValueKind.Number )
                    count = c.GetInt32();
                if ( r.Inner.RootElement.TryGetProperty( "snapshots", out var arr )
                    && arr.ValueKind == JsonValueKind.Array )
                {
                    foreach ( var el in arr.EnumerateArray() )
                    {
                        var name = el.TryGetProperty( "name", out var n ) ? n.GetString() : null;
                        var aid = el.TryGetProperty( "agentId", out var a ) ? a.GetString() : null;
                        entries.Add( new ListEntry( name, aid ) );
                    }
                }
            }
        }
        return new ListResult( count, entries );
    }

    private static string First( ListResult r )
        => r.Entries.Count > 0 ? $"name={r.Entries[0].Name},agentId={r.Entries[0].AgentId}" : "(empty)";

    // ── Fixtures / buckets ──────────────────────────────────────────────

    /// <summary>Delete every snapshot in the caller's bucket (test teardown).</summary>
    private static void ClearBucket( McpSession session )
    {
        var names = ReadList( session, false ).Entries.Select( e => e.Name ).Where( n => n != null ).ToList();
        foreach ( var name in names )
            using ( Invoke( "delete", Args( ("name", name) ), session ) ) { }
    }

    private static void DestroyStaleFixtures( Scene scene )
    {
        foreach ( var go in SceneHelpers.WalkAll( scene, true ).ToList() )
        {
            if ( go.Name == TinyFixtureName || go.Name == PropsFixtureName )
                go.DestroyImmediate();
        }
    }

    /// <summary>
    /// Load two distinct disk-backed materials for the MaterialOverride round-trip.
    /// Tries a list of project material paths; needs the first two that resolve
    /// to valid materials with distinct, non-empty names.
    /// </summary>
    private static bool TryLoadTestMaterials( out Material a, out Material b, out string diag )
    {
        a = null;
        b = null;
        string[] candidates =
        {
            "meshy/floor/floor.vmat",
            "meshy/crystal/crystal.vmat",
            "meshy/thruster/thruster.vmat",
            "meshy/gyro/gyro.vmat",
            "meshy/reactor/reactor.vmat",
        };

        var loaded = new List<Material>();
        foreach ( var path in candidates )
        {
            try
            {
                var m = Material.Load( path );
                if ( m != null && m.IsValid && !string.IsNullOrEmpty( m.Name )
                    && loaded.All( x => x.Name != m.Name ) )
                {
                    loaded.Add( m );
                }
            }
            catch { /* try the next candidate */ }
            if ( loaded.Count >= 2 ) break;
        }

        if ( loaded.Count >= 2 )
        {
            a = loaded[0];
            b = loaded[1];
            diag = null;
            return true;
        }

        diag = $"only {loaded.Count} of {candidates.Length} candidate materials loaded valid+distinct";
        return false;
    }

    // ── Comparison + JSON helpers ───────────────────────────────────────

    private static bool ColorClose( Color x, Color y )
        => Math.Abs( x.r - y.r ) < 0.01f && Math.Abs( x.g - y.g ) < 0.01f
        && Math.Abs( x.b - y.b ) < 0.01f && Math.Abs( x.a - y.a ) < 0.01f;

    private static bool VecClose( Vector3 x, Vector3 y )
        => ( x - y ).Length < 0.01f;

    /// <summary>Build a JSON args object from key/value pairs (string/bool values).</summary>
    private static string Args( params (string key, object val)[] kv )
    {
        var dict = new Dictionary<string, object>();
        foreach ( var (key, val) in kv ) dict[key] = val;
        return JsonSerializer.Serialize( dict );
    }

    /// <summary>Emit one PASS/FAIL log line for a gate and bump the failure count.</summary>
    private static void Gate( ref int failures, string gate, bool ok, string passMsg, string failMsg )
    {
        if ( ok )
        {
            Log.Info( $"[PASS] {TestName}: ASSERT {gate} — {passMsg}" );
        }
        else
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: ASSERT {gate} — {failMsg}" );
        }
    }
}
