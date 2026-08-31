// [HANDOFF-TRUST: additive AgentId+AgentSlug fields for engine-fork-phase3-1-library-mvp Path C, independent of any inherited handoff hypothesis — see Phase 1 re-verification at docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md.]
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Arenula;

// [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — additive AgentId+AgentSlug fields stamped by session.identify; no inherited handoff hypothesis applies (see Phase 1 falsification at docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md).]
/// <summary>Per-connection SSE session state.</summary>
public class McpSession
{
    public string SessionId { get; set; }
    public string ProtocolVersion { get; set; } = "2025-03-26";
    public HttpListenerResponse SseResponse { get; set; }
    public TaskCompletionSource<bool> Tcs { get; set; } = new();
    public bool Initialized { get; set; }

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 4 — docstring corrected to match the lock-key-aware McpOperationCoordinator shipped this phase; no inherited handoff hypothesis applies, this is an accuracy fix on Phase 2's prose, not a behavior change.]
    /// <summary>
    /// Agent identity stamped by the session.identify action. Null until the
    /// harness calls session.identify(agent_id, slug).
    ///
    /// McpOperationCoordinator's per-key contention check is identity-independent:
    /// it keys on SessionId plus the extracted lock keys, so anonymous sessions
    /// (AgentId == null) still get correct per-key serialization. AgentId only
    /// feeds the human-readable GAMEOBJECT-LOCKED-BY-&lt;agent&gt; error prefix.
    ///
    /// GameObjectLockMap's cross-call locks DO require an AgentId and fall through
    /// as no-ops for anonymous sessions (degraded mode), preserving
    /// stable-engine-equivalent behavior for sessions that never identified.
    /// </summary>
    public string AgentId { get; set; }

    /// <summary>Human-readable session slug for error / log messages.</summary>
    public string AgentSlug { get; set; }

    /// <summary>
    /// Serializes every write to the shared SSE output stream so RPC completions
    /// and keep-alive pings cannot interleave bytes and corrupt event framing.
    /// </summary>
    public SemaphoreSlim WriteLock { get; } = new( 1, 1 );
}
