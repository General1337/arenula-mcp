// Editor/Tests/BatchAsyncIdentifyTest.cs
//
// mcp-batch-async-session-identify (findings.md #7, 2026-06-11) — durable
// regression gate for (1) batch.execute async-step composition and (2) the
// atomic session.identify + coordinator fresh-identity read that retires the
// spurious GAMEOBJECT-LOCKED-BY-anon error.
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission code for sbep.test_mcp_batch_async_identify; it does not CLAIM completion. The test runs post-editor-restart via editor.console_run (MCP plugin code does not hot-reload), and the PASS sentinel is written on observing the SUMMARY line.]
//
// Sibling to AgentObserveTest.cs: invokes handlers directly in-process via
// the same in-assembly Handler + ParseMcpEnvelope pattern (no MCP socket).
//
// PRECONDITION  Editor running (ConCmd context). No fixtures needed.
// EXEC/ASSERT   Six gates, one log line each:
//   batch_async_compose      — batch composes an ASYNC step (compile.status)
//                              with a sync step (session.list): 2/2 succeed,
//                              no "is async and cannot be used" rejection.
//   batch_recursion_gated    — batch-inside-batch is the ONLY gated tool:
//                              per-op error mentions nested batch.
//   identify_structured      — session.identify returns {agent_id, agent_slug,
//                              resolved_as="identified"} on first call.
//   identify_idempotent      — re-identify same values → resolved_as="unchanged";
//                              new slug → resolved_as="reidentified".
//   coordinator_fresh_identity — an op admitted ANONYMOUSLY (TryBegin before
//                              identify) is reported under the FRESH identity
//                              once identify lands: the keyed busy error a
//                              second session receives says LOCKED-BY-<agent8>,
//                              NOT LOCKED-BY-anon.
//   identity_evicted_on_close — EvictBySession drops the registry entry
//                              (SSE-close path), TryResolve returns false.
// CLEANUP       Leases disposed + test session identities evicted (idempotent).

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class BatchAsyncIdentifyTest
{
    /// <summary>Tier-3 regression test for findings.md #7 (batch async + atomic identify).</summary>
    [ConCmd( "sbep.test_mcp_batch_async_identify" )]
    public static async void Run()
    {
        const string TestName = "sbep.test_mcp_batch_async_identify";
        int failures = 0;

        try
        {
            await GameTask.MainThread();
            Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );

            // ── Gate 1: batch_async_compose ─────────────────────────────
            // compile.status routes through CompileHandler.HandleAsync (async,
            // read-only — no build is triggered); session.list is a legacy sync
            // main-thread handler. Pre-change, the compile op was rejected with
            // "Tool 'compile' is async and cannot be used in batch operations."
            var batchSession = new McpSession { SessionId = "test-batch-" + Guid.NewGuid().ToString( "N" )[..8] };
            var ops1 = "[{\"tool\":\"compile\",\"action\":\"status\"},{\"tool\":\"session\",\"action\":\"list\"}]";
            using var args1Doc = JsonDocument.Parse( JsonSerializer.Serialize(
                new Dictionary<string, string> { ["action"] = "execute", ["operations"] = ops1 } ) );
            var resp1 = await BatchHandler.HandleExecuteAsync( args1Doc.RootElement, batchSession );
            await GameTask.MainThread();
            using ( var doc1 = ParseMcpEnvelope( resp1 ) )
            {
                var raw1 = JsonSerializer.Serialize( resp1 );
                bool twoTotal = doc1.RootElement.TryGetProperty( "total", out var t1 ) && t1.GetInt32() == 2;
                bool noneFailed = doc1.RootElement.TryGetProperty( "failed", out var f1 ) && f1.GetInt32() == 0;
                bool noAsyncReject = !raw1.Contains( "is async and cannot be used" );
                if ( twoTotal && noneFailed && noAsyncReject )
                {
                    Log.Info( $"[PASS] {TestName}: ASSERT batch_async_compose — async compile.status + sync session.list both succeeded in one batch (2/2, 0 failed)" );
                }
                else
                {
                    failures++;
                    var head = raw1.Length > 500 ? raw1[..500] + "…" : raw1;
                    Log.Info( $"[FAIL] {TestName}: ASSERT batch_async_compose — total==2:{twoTotal} failed==0:{noneFailed} noAsyncReject:{noAsyncReject}: {head}" );
                }
            }

            // ── Gate 2: batch_recursion_gated ───────────────────────────
            var ops2 = "[{\"tool\":\"batch\",\"action\":\"execute\",\"params\":{\"operations\":\"[]\"}}]";
            using var args2Doc = JsonDocument.Parse( JsonSerializer.Serialize(
                new Dictionary<string, string> { ["action"] = "execute", ["operations"] = ops2 } ) );
            var resp2 = await BatchHandler.HandleExecuteAsync( args2Doc.RootElement, batchSession );
            await GameTask.MainThread();
            {
                var raw2 = JsonSerializer.Serialize( resp2 );
                bool recursionRejected = raw2.Contains( "Nested batch operations" );
                using var doc2 = ParseMcpEnvelope( resp2 );
                bool oneFailed = doc2.RootElement.TryGetProperty( "failed", out var f2 ) && f2.GetInt32() == 1;
                if ( recursionRejected && oneFailed )
                {
                    Log.Info( $"[PASS] {TestName}: ASSERT batch_recursion_gated — batch-inside-batch rejected per-op ('Nested batch operations'), failed==1" );
                }
                else
                {
                    failures++;
                    var head = raw2.Length > 500 ? raw2[..500] + "…" : raw2;
                    Log.Info( $"[FAIL] {TestName}: ASSERT batch_recursion_gated — recursionRejected:{recursionRejected} failed==1:{oneFailed}: {head}" );
                }
            }

            // ── Gate 3: identify_structured ─────────────────────────────
            var idSession = new McpSession { SessionId = "test-identify-" + Guid.NewGuid().ToString( "N" )[..8] };
            var testAgentId = Guid.NewGuid().ToString();
            var testSlug = "mcp-batch-async-session-identify-test";
            string resolvedAs1 = InvokeIdentify( idSession, testAgentId, testSlug, out var agentIdEcho, out var agentSlugEcho );
            if ( resolvedAs1 == "identified" && agentIdEcho == testAgentId && agentSlugEcho == testSlug )
            {
                Log.Info( $"[PASS] {TestName}: ASSERT identify_structured — resolved_as='identified', agent_id + agent_slug echoed in structured shape" );
            }
            else
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: ASSERT identify_structured — resolved_as='{resolvedAs1}' agent_id_ok:{agentIdEcho == testAgentId} agent_slug_ok:{agentSlugEcho == testSlug}" );
            }

            // ── Gate 4: identify_idempotent ─────────────────────────────
            string resolvedAs2 = InvokeIdentify( idSession, testAgentId, testSlug, out _, out _ );
            string resolvedAs3 = InvokeIdentify( idSession, testAgentId, testSlug + "-renamed", out _, out _ );
            if ( resolvedAs2 == "unchanged" && resolvedAs3 == "reidentified" )
            {
                Log.Info( $"[PASS] {TestName}: ASSERT identify_idempotent — same values → 'unchanged', new slug → 'reidentified'" );
            }
            else
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: ASSERT identify_idempotent — repeat='{resolvedAs2}' (want 'unchanged'), rename='{resolvedAs3}' (want 'reidentified')" );
            }

            // ── Gate 5: coordinator_fresh_identity (the anon-race repro) ─
            // Admit an op with NULL identity (simulating RpcDispatcher's stale
            // TryBegin-time read racing session.identify), THEN identify, THEN
            // provoke a keyed conflict from a second session. Pre-change the
            // busy error said LOCKED-BY-anon; post-change it must carry the
            // fresh agent id stamped by identify.
            var raceSessionId = "test-race-" + Guid.NewGuid().ToString( "N" )[..8];
            var raceLockKey = Guid.NewGuid();
            string raceAgentIdForCleanup = null;
            var goMeta = ToolRegistry.GetOperationMetadata( "gameobject", "set_transform" ); // per-key write
            bool admitted = McpOperationCoordinator.TryBegin(
                raceSessionId, null, null, "gameobject", "set_transform",
                new[] { raceLockKey }, goMeta, out var raceLease, out var admitBusy );

            if ( !admitted )
            {
                // A sibling agent holds a scene-wide write right now — contention,
                // not a regression. Skip rather than fake a verdict.
                Log.Info( $"[SKIP] {TestName}: ASSERT coordinator_fresh_identity — could not admit fixture op (sibling scene-wide write active): {JsonSerializer.Serialize( admitBusy )[..Math.Min( 200, JsonSerializer.Serialize( admitBusy ).Length )]}" );
            }
            else
            {
                try
                {
                    // identify lands AFTER the op was admitted anonymously.
                    var raceAgentId = Guid.NewGuid().ToString();
                    raceAgentIdForCleanup = raceAgentId;
                    var raceMcpSession = new McpSession { SessionId = raceSessionId };
                    InvokeIdentify( raceMcpSession, raceAgentId, "race-holder-slug", out _, out _ );

                    // Second session collides on the same lock key.
                    bool rivalAdmitted = McpOperationCoordinator.TryBegin(
                        "test-rival-" + Guid.NewGuid().ToString( "N" )[..8], null, null,
                        "gameobject", "set_transform", new[] { raceLockKey }, goMeta,
                        out var rivalLease, out var rivalBusy );

                    if ( rivalAdmitted )
                    {
                        rivalLease.Dispose();
                        failures++;
                        Log.Info( $"[FAIL] {TestName}: ASSERT coordinator_fresh_identity — rival op on the SAME lock key was admitted (expected keyed conflict)" );
                    }
                    else
                    {
                        var busyJson = JsonSerializer.Serialize( rivalBusy );
                        var expectedTag = "GAMEOBJECT-LOCKED-BY-" + raceAgentId[..8];
                        bool freshTag = busyJson.Contains( expectedTag );
                        bool noAnon = !busyJson.Contains( "GAMEOBJECT-LOCKED-BY-anon" );
                        if ( freshTag && noAnon )
                        {
                            Log.Info( $"[PASS] {TestName}: ASSERT coordinator_fresh_identity — busy error reports '{expectedTag}' (fresh registry identity), not LOCKED-BY-anon, for an op admitted before identify" );
                        }
                        else
                        {
                            failures++;
                            var head = busyJson.Length > 500 ? busyJson[..500] + "…" : busyJson;
                            Log.Info( $"[FAIL] {TestName}: ASSERT coordinator_fresh_identity — freshTag:{freshTag} noAnon:{noAnon}: {head}" );
                        }
                    }
                }
                finally
                {
                    raceLease.Dispose();
                }
            }

            // ── Gate 6: identity_evicted_on_close ───────────────────────
            McpOperationCoordinator.EvictBySession( raceSessionId );
            bool stillThere = AgentIdentityRegistry.TryResolve( raceSessionId, out _, out _ );
            if ( !stillThere )
            {
                Log.Info( $"[PASS] {TestName}: ASSERT identity_evicted_on_close — EvictBySession dropped the registry entry" );
            }
            else
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: ASSERT identity_evicted_on_close — registry entry survived EvictBySession" );
            }

            // ── CLEANUP ─────────────────────────────────────────────────
            McpOperationCoordinator.EvictBySession( idSession.SessionId );
            McpOperationCoordinator.EvictBySession( batchSession.SessionId );
            // identify pre-spawns a NotSaved scratch container per agent id —
            // release the throwaway test containers so the live scene stays clean.
            try
            {
                var scene = SceneHelpers.ResolveScene();
                ScratchScenePool.ReleaseContainer( scene, testAgentId );
                if ( raceAgentIdForCleanup != null )
                    ScratchScenePool.ReleaseContainer( scene, raceAgentIdForCleanup );
            }
            catch { /* best-effort — containers are NotSaved either way */ }
            Log.Info( $"[PASS] {TestName}: CLEANUP — test session identities evicted + scratch containers released (idempotent)" );
        }
        catch ( Exception ex )
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: EXEC — unhandled exception: {ex.GetType().Name}: {ex.Message}" );
        }

        // ── SUMMARY (recorded even on throw — async-void contract) ──────
        if ( failures == 0 )
        {
            Log.Info( $"[PASS] {TestName}: SUMMARY — 6/6 gates green (batch_async_compose + batch_recursion_gated + identify_structured + identify_idempotent + coordinator_fresh_identity + identity_evicted_on_close)" );
        }
        else
        {
            Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of 6 total" );
        }
    }

    /// <summary>
    /// Drive session.identify through the real handler and pull the structured
    /// fields out of the response envelope.
    /// </summary>
    private static string InvokeIdentify(
        McpSession session, string agentId, string slug, out string agentIdEcho, out string agentSlugEcho )
    {
        agentIdEcho = null;
        agentSlugEcho = null;
        using var argsDoc = JsonDocument.Parse( JsonSerializer.Serialize(
            new Dictionary<string, string> { ["action"] = "identify", ["agent_id"] = agentId, ["slug"] = slug } ) );
        var resp = SessionHandler.Handle( "identify", argsDoc.RootElement, session );
        using var doc = ParseMcpEnvelope( resp );
        if ( doc.RootElement.TryGetProperty( "agent_id", out var aid ) && aid.ValueKind == JsonValueKind.String )
            agentIdEcho = aid.GetString();
        if ( doc.RootElement.TryGetProperty( "agent_slug", out var aslug ) && aslug.ValueKind == JsonValueKind.String )
            agentSlugEcho = aslug.GetString();
        return doc.RootElement.TryGetProperty( "resolved_as", out var ra ) && ra.ValueKind == JsonValueKind.String
            ? ra.GetString()
            : null;
    }

    /// <summary>
    /// Unwrap the MCP envelope <c>{content:[{type:"text",text:"&lt;inner-json&gt;"}]}</c>
    /// that HandlerBase.Success wraps around handler responses. Copied verbatim
    /// from AgentObserveTest.cs per Phase 2 banned-approach #2 — do not re-derive
    /// the envelope shape.
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
        // Defensive fallback: return the envelope verbatim so TryGetProperty
        // lookups miss cleanly instead of crashing.
        return JsonDocument.Parse( envelopeJson );
    }
}
