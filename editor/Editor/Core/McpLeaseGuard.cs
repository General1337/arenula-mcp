// Editor/Core/McpLeaseGuard.cs
//
// Wave 3 of the multi-agent collision-hardening plan
// (docs/ai/plans/multi-agent-collision-hardening-plan.md).
//
// THE PROBLEM THIS SOLVES
//   Every safety mechanism protecting the shared S&Box editor is enforced on the
//   CLIENT side, by hooks. Claude Code runs them because its harness makes the
//   model unable to skip a PreToolUse hook. Codex is merely *instructed in prose*
//   to hand-call its dispatcher, and silently bypasses everything the moment it
//   forgets. A Gemini / Cursor / Copilot agent, or a plain script, runs no hooks
//   at all — and the editor happily serves it, because the editor has never been
//   asked to check.
//
//   So the guarantee "concurrent agents cannot corrupt the editor" rests entirely
//   on the caller's good manners. That is not a guarantee. It is a hope.
//
// THE FIX
//   Move the check to the RESOURCE. Before performing any mutation, the editor
//   asks: does this caller actually hold the editor lease? The lease is granted by
//   the mutex server (Tools/mcp-editor-mutex-server.py) and mirrored to
//   .claude/session-state/mcp-editor-mutex-holder.json, which any process in any
//   language can read.
//
//   The precedent is already here and it works: McpOperationCoordinator's
//   per-GameObject lock returns GAMEOBJECT-LOCKED-BY-<agent> to ANY client from
//   ANY runtime with zero adapter work on the caller's side. It is the one
//   mechanism in this whole system a ghost agent cannot route around. This class
//   generalises that from GameObjects to the editor as a whole.
//
//   Once enforcing, an agent that never ran a hook CANNOT corrupt the editor. It
//   gets a structured refusal telling it how to acquire. That is what makes
//   "as many agents, from as many AIs, as possible" a safe proposition rather
//   than an aspiration.
//
// WHY THIS SHIPS IN AUDIT MODE
//   Rejecting an unleased mutation is exactly as powerful as it sounds, which
//   means a false positive takes the editor away from EVERY agent at once. Two
//   things are not yet proven in production:
//     1. that every legitimate caller's MCP session carries an AgentId at all
//        (McpSession.AgentId is explicitly nullable — "anonymous sessions" are a
//        supported case today), and
//     2. that the AgentId the plugin sees is byte-identical to the agent_id the
//        acquire hook used.
//   If either is false, Enforce would reject everyone. So the default is Audit:
//   log every violation, block nothing. Read the log, confirm it is quiet, THEN
//   flip to Enforce. Shipping this straight to Enforce would be precisely the
//   "coordination primitive shipped without cross-agent dogfood" anti-pattern
//   (AP-20260518) that this very plan cites.
//
//   Mode lives in .claude/session-state/mcp-lease-enforcement:
//     off      — disabled entirely
//     audit    — DEFAULT. Allow, but log every unleased mutation.
//     enforce  — reject unleased mutations with WAIT-AND-RETRY.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Sandbox;

namespace Arenula;

internal static class McpLeaseGuard
{
    internal enum Mode { Off, Audit, Enforce }
    internal enum ShadowDecision { Allow, Deny, CannotEvaluate }
    internal enum ShadowCause
    {
        None,
        MissingHolderState,
        TornHolderState,
        AnonymousCaller,
        CoordinatorUnreachable,
        CoordinatorHealthBlocked,
    }

    private readonly record struct HolderSnapshot(
        string AgentId, string Class, string RequestId, string Operation,
        string RuntimeBootNonce, int ServerPid, ShadowCause Cause )
    {
        internal string RuntimeSessionId { get; init; }
        internal long CoordinatorEpoch { get; init; }
        internal int AdmissionServerPid { get; init; }
        internal long PublisherEpoch { get; init; }
    }

    internal readonly record struct OuterLeaseBinding(
        string RequestId, string AgentId, string RuntimeBootNonce,
        string RuntimeSessionId, string Operation, long CoordinatorEpoch,
        int AdmissionServerPid, int ServerPid )
    {
        internal bool IsEpochBound => CoordinatorEpoch > 0;

        internal bool IsValid =>
            Guid.TryParse( RequestId, out _ )
            && !string.IsNullOrWhiteSpace( AgentId )
            && !string.IsNullOrWhiteSpace( RuntimeBootNonce )
            && !string.IsNullOrWhiteSpace( Operation )
            && CoordinatorEpoch >= 0
            && (!IsEpochBound
                || (!string.IsNullOrWhiteSpace( RuntimeSessionId )
                    && AdmissionServerPid > 0))
            && ServerPid > 0;
    }

    internal readonly record struct HolderObservation(
        string RequestId, string AgentId, string RuntimeBootNonce,
        string Operation, string Class, int ServerPid )
    {
        internal bool IsValid =>
            Guid.TryParse( RequestId, out _ )
            && !string.IsNullOrWhiteSpace( AgentId )
            && !string.IsNullOrWhiteSpace( RuntimeBootNonce )
            && ServerPid > 0;
    }

    internal readonly record struct FenceRequest(
        string RequestId, string AgentId, string RuntimeBootNonce,
        int ServerPid, double IssuedTs )
    {
        internal bool IsValid =>
            Guid.TryParse( RequestId, out _ )
            && !string.IsNullOrWhiteSpace( AgentId )
            && !string.IsNullOrWhiteSpace( RuntimeBootNonce )
            && ServerPid > 0 && IssuedTs > 0;
    }

    // The holder file is rewritten on every grant/release. Re-reading it per MCP
    // call would be wasteful; a short cache keeps this off the hot path while
    // staying far fresher than any lock could turn over.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds( 250 );
    private static readonly TimeSpan ModeCacheTtl = TimeSpan.FromSeconds( 5 );
    private static readonly object Sync = new();

    private static DateTime _holderReadUtc = DateTime.MinValue;
    private static string _cachedHolderAgentId;
    private static string _cachedHolderClass;
    private static string _cachedHolderRequestId;
    private static string _cachedHolderOperation;
    private static string _cachedHolderRuntimeBootNonce;
    private static string _cachedHolderRuntimeSessionId;
    private static long _cachedHolderCoordinatorEpoch;
    private static int _cachedHolderAdmissionServerPid;
    private static int _cachedHolderServerPid;
    private static long _cachedHolderPublisherEpoch;
    private static ShadowCause _cachedHolderCause = ShadowCause.MissingHolderState;

    private static DateTime _modeReadUtc = DateTime.MinValue;
    private static Mode _cachedMode = Mode.Audit;

    private static string ProjectRoot => ResolveProjectRoot(
        HandlerBase.GetProjectRoot(), Directory.GetCurrentDirectory() );

    // Exposed to the in-editor promotion test. The editor executable's CWD is the
    // engine game directory after a cold restart, not necessarily the active
    // project, so CWD must remain a fallback rather than the primary authority.
    internal static string CurrentProjectRoot => ProjectRoot;

    internal static string ResolveProjectRoot( string activeProjectRoot, string currentDirectory )
    {
        if ( !string.IsNullOrWhiteSpace( activeProjectRoot ) )
            return activeProjectRoot;

        var dir = currentDirectory;
        for ( var i = 0; i < 6 && !string.IsNullOrEmpty( dir ); i++ )
        {
            if ( Directory.Exists( Path.Combine( dir, ".claude" ) ) ) return dir;
            dir = Path.GetDirectoryName( dir );
        }
        return currentDirectory;
    }

    private static string StateDir => Path.Combine( ProjectRoot, ".claude", "session-state" );
    private static string HolderPath => Path.Combine( StateDir, "mcp-editor-mutex-holder.json" );
    private static string AddressPath => Path.Combine( StateDir, "mcp-editor-server.address" );
    private static string ModePath => Path.Combine( StateDir, "mcp-lease-enforcement" );
    private static string EditorTerminalDir => Path.Combine( StateDir, "mcp-editor-terminals" );
    private static string FenceRequestPath => Path.Combine( StateDir, "mcp-editor-fence-request.json" );
    private static string FenceAckPath => Path.Combine( StateDir, "mcp-editor-fence-ack.json" );

    internal static Mode CurrentMode
    {
        get
        {
            lock ( Sync )
            {
                if ( DateTime.UtcNow - _modeReadUtc < ModeCacheTtl ) return _cachedMode;
                _modeReadUtc = DateTime.UtcNow;
                _cachedMode = ReadMode();
                return _cachedMode;
            }
        }
    }

    private static Mode ReadMode()
    {
        try
        {
            if ( !File.Exists( ModePath ) ) return Mode.Audit;
            var raw = File.ReadAllText( ModePath ).Trim().ToLowerInvariant();
            return raw switch
            {
                "off" => Mode.Off,
                "enforce" => Mode.Enforce,
                _ => Mode.Audit,
            };
        }
        catch ( Exception )
        {
            // An unreadable mode file must never harden into a total editor
            // lockout. Fail to the safe-but-noisy setting, not the loud one.
            return Mode.Audit;
        }
    }

    /// <summary>
    /// The agent_id currently holding the editor lease, or null if the editor is
    /// free / the mutex server is not running.
    /// </summary>
    internal static bool CoordinatorMirrorMatches(
        int addressPid, long addressEpoch, int holderPublisherPid, long holderPublisherEpoch )
        => addressPid > 0
           && addressEpoch >= 0
           && addressPid == holderPublisherPid
           && addressEpoch == holderPublisherEpoch;

    private static bool TryReadCoordinatorIdentity(
        out int serverPid, out long coordinatorEpoch )
    {
        serverPid = 0;
        coordinatorEpoch = 0;
        try
        {
            if ( !File.Exists( AddressPath ) ) return false;
            using var doc = JsonDocument.Parse( File.ReadAllText( AddressPath ) );
            if ( !doc.RootElement.TryGetProperty( "pid", out var pidElement )
                 || !pidElement.TryGetInt32( out serverPid ) || serverPid <= 0 )
                return false;
            if ( doc.RootElement.TryGetProperty( "coordinator_epoch", out var epoch )
                 && !epoch.TryGetInt64( out coordinatorEpoch ) )
                return false;
            using var process = System.Diagnostics.Process.GetProcessById( serverPid );
            return !process.HasExited;
        }
        catch ( Exception )
        {
            serverPid = 0;
            coordinatorEpoch = 0;
            return false;
        }
    }

    private static HolderSnapshot ReadHolder( bool forceFresh = false )
    {
        lock ( Sync )
        {
            if ( !forceFresh && DateTime.UtcNow - _holderReadUtc < CacheTtl )
                return new HolderSnapshot(
                    _cachedHolderAgentId, _cachedHolderClass, _cachedHolderRequestId,
                    _cachedHolderOperation, _cachedHolderRuntimeBootNonce,
                    _cachedHolderServerPid, _cachedHolderCause )
                {
                    RuntimeSessionId = _cachedHolderRuntimeSessionId,
                    CoordinatorEpoch = _cachedHolderCoordinatorEpoch,
                    AdmissionServerPid = _cachedHolderAdmissionServerPid,
                    PublisherEpoch = _cachedHolderPublisherEpoch,
                };

            _holderReadUtc = DateTime.UtcNow;
            _cachedHolderAgentId = null;
            _cachedHolderClass = null;
            _cachedHolderRequestId = null;
            _cachedHolderOperation = null;
            _cachedHolderRuntimeBootNonce = null;
            _cachedHolderRuntimeSessionId = null;
            _cachedHolderCoordinatorEpoch = 0;
            _cachedHolderAdmissionServerPid = 0;
            _cachedHolderServerPid = 0;
            _cachedHolderPublisherEpoch = 0;
            _cachedHolderCause = ShadowCause.None;

            if ( !TryReadCoordinatorIdentity(
                    out var coordinatorPid, out var coordinatorEpoch ) )
            {
                _cachedHolderCause = ShadowCause.CoordinatorUnreachable;
                return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
            }

            try
            {
                if ( !File.Exists( HolderPath ) )
                {
                    _cachedHolderCause = ShadowCause.MissingHolderState;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                using var doc = JsonDocument.Parse( File.ReadAllText( HolderPath ) );
                if ( !doc.RootElement.TryGetProperty( "server_pid", out var p )
                     || !p.TryGetInt32( out _cachedHolderServerPid ) )
                {
                    _cachedHolderCause = ShadowCause.TornHolderState;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                if ( doc.RootElement.TryGetProperty( "coordinator_epoch", out var pe )
                     && !pe.TryGetInt64( out _cachedHolderPublisherEpoch ) )
                {
                    _cachedHolderCause = ShadowCause.TornHolderState;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                if ( !CoordinatorMirrorMatches(
                        coordinatorPid, coordinatorEpoch,
                        _cachedHolderServerPid, _cachedHolderPublisherEpoch ) )
                {
                    _cachedHolderCause = ShadowCause.TornHolderState;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                if ( doc.RootElement.TryGetProperty( "admission_blocked", out var blocked )
                     && blocked.ValueKind == JsonValueKind.True )
                {
                    _cachedHolderCause = ShadowCause.CoordinatorHealthBlocked;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                if ( !doc.RootElement.TryGetProperty( "holder", out var h )
                     || h.ValueKind == JsonValueKind.Null )
                    return new HolderSnapshot( null, null, null, null, null, 0, ShadowCause.None );
                if ( h.ValueKind != JsonValueKind.Object )
                {
                    _cachedHolderCause = ShadowCause.TornHolderState;
                    return new HolderSnapshot( null, null, null, null, null, 0, _cachedHolderCause );
                }
                if ( h.TryGetProperty( "agent_id", out var a ) )
                    _cachedHolderAgentId = a.GetString();
                if ( h.TryGetProperty( "class", out var c ) )
                    _cachedHolderClass = c.GetString();
                if ( h.TryGetProperty( "request_id", out var r ) )
                    _cachedHolderRequestId = r.GetString();
                if ( h.TryGetProperty( "operation", out var o ) )
                    _cachedHolderOperation = o.GetString();
                if ( h.TryGetProperty( "runtime_boot_nonce", out var b ) )
                    _cachedHolderRuntimeBootNonce = b.GetString();
                if ( h.TryGetProperty( "runtime_session_id", out var s ) )
                    _cachedHolderRuntimeSessionId = s.GetString();
                if ( h.TryGetProperty( "coordinator_epoch", out var e ) )
                    e.TryGetInt64( out _cachedHolderCoordinatorEpoch );
                if ( h.TryGetProperty( "admission_server_pid", out var ap ) )
                    ap.TryGetInt32( out _cachedHolderAdmissionServerPid );
                if ( string.IsNullOrEmpty( _cachedHolderAgentId ) )
                    _cachedHolderCause = ShadowCause.TornHolderState;
            }
            catch ( Exception )
            {
                _cachedHolderAgentId = null;
                _cachedHolderClass = null;
                _cachedHolderRequestId = null;
                _cachedHolderOperation = null;
                _cachedHolderRuntimeBootNonce = null;
                _cachedHolderRuntimeSessionId = null;
                _cachedHolderCoordinatorEpoch = 0;
                _cachedHolderAdmissionServerPid = 0;
                _cachedHolderServerPid = 0;
                _cachedHolderPublisherEpoch = 0;
                _cachedHolderCause = ShadowCause.TornHolderState;
            }

            return new HolderSnapshot(
                _cachedHolderAgentId, _cachedHolderClass, _cachedHolderRequestId,
                _cachedHolderOperation, _cachedHolderRuntimeBootNonce,
                _cachedHolderServerPid, _cachedHolderCause )
            {
                RuntimeSessionId = _cachedHolderRuntimeSessionId,
                CoordinatorEpoch = _cachedHolderCoordinatorEpoch,
                AdmissionServerPid = _cachedHolderAdmissionServerPid,
                PublisherEpoch = _cachedHolderPublisherEpoch,
            };
        }
    }

    internal static (ShadowDecision Decision, ShadowCause Cause) EvaluateShadow(
        string agentId, bool readOnly )
    {
        var holder = ReadHolder();
        return EvaluateShadowSnapshot(
            agentId, readOnly, holder.AgentId, holder.Cause );
    }

    /// <summary>
    /// Pure decision seam used by the promotion test to inject every uncertainty
    /// cause without rewriting production coordinator state.
    /// </summary>
    internal static (ShadowDecision Decision, ShadowCause Cause) EvaluateShadowSnapshot(
        string agentId, bool readOnly, string holderAgentId, ShadowCause holderCause )
    {
        if ( readOnly ) return (ShadowDecision.Allow, ShadowCause.None);
        if ( holderCause != ShadowCause.None )
            return (ShadowDecision.CannotEvaluate, holderCause);
        if ( string.IsNullOrEmpty( agentId ) )
            return (ShadowDecision.CannotEvaluate, ShadowCause.AnonymousCaller);
        if ( string.IsNullOrEmpty( holderAgentId ) || agentId == holderAgentId )
            return (ShadowDecision.Allow, ShadowCause.None);
        return (ShadowDecision.Deny, ShadowCause.None);
    }

    internal static bool ShouldFailClosedUncertainty(
        Mode mode, ShadowDecision decision )
        => mode == Mode.Enforce && decision == ShadowDecision.CannotEvaluate;

    /// <summary>
    /// Should this mutating MCP call be refused because the caller does not hold
    /// the editor lease?
    ///
    /// Returns false (allow) whenever we cannot be SURE the call is illegitimate.
    /// A guard on the resource is powerful enough to take the editor away from
    /// every agent at once; it must only ever fire on certainty.
    /// </summary>
    internal static bool ShouldReject(
        string agentId, string tool, string action, bool readOnly,
        out OuterLeaseBinding outerLease, out HolderObservation observation,
        out object leaseError )
    {
        outerLease = default;
        observation = default;
        leaseError = null;

        var mode = CurrentMode;
        if ( mode == Mode.Off ) return false;

        // Reads cannot corrupt anything. Only mutations need a lease.
        if ( readOnly ) return false;

        // ── The identity bootstrap must never need identity (2026-08-31) ──────
        //
        // session.identify is classified as a mutation, so in Enforce it was
        // refused for exactly the reason it exists to fix: the caller had no
        // authenticated identity yet. The gateway only attaches
        // X-SBPP-Agent-Id AFTER a successful identify, so a caller that arrives
        // anonymous for any reason could never become non-anonymous — and every
        // later mutation was refused too. Measured 2026-08-31: 25+ refusals in
        // one day, `cause=AnonymousCaller`, across every live Claude and Codex
        // agent, surfacing to the agent as EMPTY OUTPUT. It is unescapable from
        // inside the MCP connection, because the escape is itself the call being
        // refused. Three separate ways into that deadlock are recorded in
        // agent memory (truncated id, MCP server restart mid-hotload, /clear).
        //
        // Exempting it is safe: identify mutates only this connection's own
        // identity binding. It touches no scene, no assembly and no play state,
        // so it cannot produce the corruption this guard exists to prevent —
        // and letting it through is what makes every SUBSEQUENT call
        // attributable, which is the guard's whole purpose.
        if ( string.Equals( tool, "session", StringComparison.OrdinalIgnoreCase )
             && string.Equals( action, "identify", StringComparison.OrdinalIgnoreCase ) )
        {
            return false;
        }

        // Mutating admission needs an exact, current request binding. A 250 ms
        // shadow cache is acceptable for telemetry, but could bind the new
        // handler to a just-released predecessor and manufacture a false proof.
        var holder = ReadHolder( forceFresh: true );
        var (decision, cause) = EvaluateShadowSnapshot(
            agentId, readOnly, holder.AgentId, holder.Cause );
        if ( decision == ShadowDecision.Allow )
        {
            observation = BuildHolderObservation( agentId, holder );
            if ( IsFenceRequested( observation ) )
            {
                leaseError = BuildFenceError( tool, action, observation );
                return true;
            }
            outerLease = BindOuterLeaseSnapshot(
                agentId, $"{tool}.{action}", holder.RequestId,
                holder.AgentId, holder.Class, holder.Operation,
                holder.RuntimeBootNonce, holder.RuntimeSessionId,
                holder.CoordinatorEpoch, holder.AdmissionServerPid,
                holder.ServerPid );
            return false;
        }
        var holderAgentId = holder.AgentId;
        var holderClass = holder.Class;
        var anonymous = string.IsNullOrEmpty( agentId );

        var detail =
            $"tool={tool}.{action} caller={(anonymous ? "<anonymous>" : Short( agentId ))} " +
            $"holder={Short( holderAgentId )} class={holderClass} " +
            $"shadow={decision.ToString().ToUpperInvariant()} cause={cause}";

        if ( mode == Mode.Audit )
        {
            Log.Warning(
                $"[mcp-lease-guard AUDIT] {decision.ToString().ToUpperInvariant()}: {detail}. " +
                "CANNOT_EVALUATE is never counted as agreement." );
            return false;
        }

        if ( ShouldFailClosedUncertainty( mode, decision ) )
        {
            // Built through HandlerBase.Error so it reaches the agent as a real
            // error. Before 2026-08-31 this was a bare anonymous object the
            // dispatcher could not serialize, so the refusal surfaced to agents
            // as EMPTY OUTPUT ("completed with no output") and the only evidence
            // was this log line on disk. A guard nobody can see is a guard
            // nobody can obey.
            //
            // It is also NOT retryable, whatever the old payload claimed. An
            // unauthenticated caller stays unauthenticated no matter how many
            // times the identical call is re-issued; the remedy is to re-identify.
            leaseError = HandlerBase.Error(
                $"This mutating call ({tool}.{action}) was refused because the caller's "
                + $"identity could not be authenticated ({cause}). Unknown ownership blocks mutation.",
                $"{tool}.{action}",
                "Re-issue `session action=identify` with the FULL agent id from "
                + "`bash .claude/hooks/lib/agent-id.sh` and your session slug, then repeat this call. "
                + "Do NOT blind-retry: an unauthenticated caller stays unauthenticated. "
                + "If identify itself fails, set .claude/session-state/mcp-lease-enforcement to "
                + "`audit` (its documented default), re-identify, and report it.",
                new
                {
                    caller = anonymous ? null : Short( agentId ),
                    cause = cause.ToString(),
                },
                McpErrorCode.EditorLeaseStateUnknown );
            Log.Warning(
                $"[mcp-lease-guard ENFORCE] coordinator uncertainty REFUSED: {detail}" );
            return true;
        }

        // Enforce known-other denial. Same reasoning as the uncertainty branch
        // above: built through HandlerBase.Error so the agent actually sees it.
        leaseError = HandlerBase.Error(
            $"This mutating call ({tool}.{action}) was made without holding the editor lease. "
            + $"The S&Box editor is a single shared process and is currently leased by agent "
            + $"{Short( holderAgentId )} for '{holderClass}'. Concurrent mutation is what produces "
            + "the stale-DLL / half-loaded-assembly corruption this lease prevents.",
            $"{tool}.{action}",
            "Acquire the lease before mutating: `python Tools/agent-hook.py pre-mcp "
            + $"--tool {tool} --action {action}` (and `post-mcp` afterwards). If that exits 2 the "
            + "lease is genuinely held by someone else — THAT is contention, and re-issuing the "
            + "identical call is right. Re-issuing THIS call without acquiring first is not. "
            + "See AGENTS.md and docs/ai/contracts/concurrency.md Rule 6.",
            new
            {
                holder = Short( holderAgentId ),
                holder_class = holderClass,
                caller = anonymous ? null : Short( agentId ),
            },
            McpErrorCode.EditorLeaseRequired );

        Log.Warning( $"[mcp-lease-guard ENFORCE] unleased mutation REFUSED: {detail}" );
        return true;
    }

    private static HolderObservation BuildHolderObservation(
        string callerAgentId, HolderSnapshot holder )
    {
        if ( string.IsNullOrWhiteSpace( callerAgentId )
             || callerAgentId != holder.AgentId )
            return default;
        var observation = new HolderObservation(
            holder.RequestId, holder.AgentId, holder.RuntimeBootNonce,
            holder.Operation, holder.Class, holder.ServerPid );
        return observation.IsValid ? observation : default;
    }

    internal static bool TryReadCurrentHolderObservation(
        out HolderObservation observation )
    {
        var holder = ReadHolder( forceFresh: true );
        observation = new HolderObservation(
            holder.RequestId, holder.AgentId, holder.RuntimeBootNonce,
            holder.Operation, holder.Class, holder.ServerPid );
        if ( observation.IsValid ) return true;
        observation = default;
        return false;
    }

    internal static bool IsFenceRequested( HolderObservation observation )
        => observation.IsValid
           && TryReadFenceRequest( observation, out _ );

    internal static object BuildFenceError(
        string tool, string action, HolderObservation observation ) => new
    {
        error = "EDITOR-RUNTIME-EPOCH-FENCED",
        message = $"Runtime boot for {tool}.{action} is retired; no new editor mutation is admitted.",
        request_id = observation.RequestId,
        runtime_boot_nonce = observation.RuntimeBootNonce,
        retryable = false,
    };

    internal static bool TryReadFenceRequest(
        HolderObservation observation, out FenceRequest request )
    {
        request = default;
        if ( !observation.IsValid ) return false;
        try
        {
            using var doc = JsonDocument.Parse( File.ReadAllText( FenceRequestPath ) );
            var root = doc.RootElement;
            if ( !root.TryGetProperty( "schema_version", out var schema )
                 || schema.GetInt32() != 1
                 || !root.TryGetProperty( "kind", out var kind )
                 || kind.GetString() != "runtime_dead_fence"
                 || !root.TryGetProperty( "request_id", out var requestId )
                 || requestId.GetString() != observation.RequestId
                 || !root.TryGetProperty( "agent_id", out var agent )
                 || agent.GetString() != observation.AgentId
                 || !root.TryGetProperty( "runtime_boot_nonce", out var boot )
                 || boot.GetString() != observation.RuntimeBootNonce
                 || !root.TryGetProperty( "server_pid", out var serverPid )
                 || serverPid.GetInt32() != observation.ServerPid
                 || !root.TryGetProperty( "issued_ts", out var issued )
                 || !issued.TryGetDouble( out var issuedTs ) )
                return false;
            request = new FenceRequest(
                observation.RequestId, observation.AgentId,
                observation.RuntimeBootNonce, observation.ServerPid, issuedTs );
            return request.IsValid;
        }
        catch ( Exception )
        {
            return false;
        }
    }

    internal static void WriteFenceAck(
        FenceRequest request, IReadOnlyList<string> activeOperationIds )
    {
        if ( !request.IsValid ) return;
        string tmp = null;
        try
        {
            Directory.CreateDirectory( StateDir );
            tmp = FenceAckPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            var payload = new
            {
                schema_version = 1,
                kind = "runtime_dead_fence_ack",
                request_id = request.RequestId,
                agent_id = request.AgentId,
                runtime_boot_nonce = request.RuntimeBootNonce,
                server_pid = request.ServerPid,
                editor_pid = Environment.ProcessId,
                enforcement_mode = CurrentMode.ToString().ToLowerInvariant(),
                active_mutations = activeOperationIds?.Count ?? 0,
                active_operation_ids = activeOperationIds ?? Array.Empty<string>(),
                ack_ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            };
            File.WriteAllText( tmp, JsonSerializer.Serialize( payload ) );
            File.Move( tmp, FenceAckPath, overwrite: true );
        }
        catch ( Exception ex )
        {
            if ( !string.IsNullOrEmpty( tmp ) )
            {
                try { File.Delete( tmp ); } catch ( Exception ) { }
            }
            Log.Warning( $"[mcp-lease-guard] Failed to acknowledge runtime fence: {ex.Message}" );
        }
    }

    /// <summary>
    /// Exact direct-call binding used by the editor terminal receipt. Session
    /// parents are never bound, and operation mismatch is a hard no: completing
    /// a child handler cannot release the skill/playtest lease that authorized it.
    /// </summary>
    internal static OuterLeaseBinding BindOuterLeaseSnapshot(
        string callerAgentId, string qualifiedOperation, string requestId,
        string holderAgentId, string holderClass, string holderOperation,
        string runtimeBootNonce, string runtimeSessionId, long coordinatorEpoch,
        int admissionServerPid, int serverPid )
    {
        if ( string.IsNullOrWhiteSpace( callerAgentId )
             || callerAgentId != holderAgentId
             || string.Equals( holderClass, "session", StringComparison.OrdinalIgnoreCase )
             || qualifiedOperation != holderOperation )
            return default;

        var binding = new OuterLeaseBinding(
            requestId, holderAgentId, runtimeBootNonce, runtimeSessionId,
            holderOperation, coordinatorEpoch, admissionServerPid, serverPid );
        return binding.IsValid ? binding : default;
    }

    /// <summary>
    /// Best-effort, source-bound proof that RpcDispatcher's handler lease really
    /// reached Dispose. The daemon remains the sole release authority and rejects
    /// any receipt that does not match its exact live holder and process epoch.
    /// </summary>
    internal static string BuildTerminalPayloadJson(
        OuterLeaseBinding binding, string editorOperationId, string tool, string action )
    {
        var payload = new
        {
            schema_version = binding.IsEpochBound ? 2 : 1,
            kind = "handler_terminal",
            request_id = binding.RequestId,
            agent_id = binding.AgentId,
            runtime_boot_nonce = binding.RuntimeBootNonce,
            runtime_session_id = binding.IsEpochBound ? binding.RuntimeSessionId : null,
            operation = binding.Operation,
            coordinator_epoch = binding.IsEpochBound ? binding.CoordinatorEpoch : 0,
            admission_server_pid = binding.IsEpochBound ? binding.AdmissionServerPid : 0,
            server_pid = binding.ServerPid,
            editor_operation_id = editorOperationId,
            tool,
            action,
            completed_ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        return JsonSerializer.Serialize( payload );
    }

    internal static string TerminalPipeNameFromAddress( string address )
    {
        const string prefix = @"\\.\pipe\";
        if ( string.IsNullOrWhiteSpace( address )
             || !address.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
             || address.Length <= prefix.Length )
            return null;
        return address[prefix.Length..];
    }

    private static string ReadEditorTerminalAddress()
    {
        using var doc = JsonDocument.Parse( File.ReadAllText( AddressPath ) );
        return doc.RootElement.TryGetProperty( "editor_terminal_address", out var address )
            ? address.GetString()
            : null;
    }

    private static void RecordEpochTerminalOverChannel(
        OuterLeaseBinding binding, string editorOperationId, string tool, string action )
    {
        var address = ReadEditorTerminalAddress();
        var pipeName = TerminalPipeNameFromAddress( address );
        if ( string.IsNullOrWhiteSpace( pipeName ) )
            throw new InvalidOperationException( "coordinator has no authenticated editor terminal channel" );

        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.None );
        pipe.Connect( 500 );
        pipe.ReadTimeout = 1000;
        pipe.WriteTimeout = 1000;
        using var writer = new StreamWriter(
            pipe, new UTF8Encoding( false ), 1024, leaveOpen: true )
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(
            pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true );
        writer.WriteLine( BuildTerminalPayloadJson(
            binding, editorOperationId, tool, action ) );
        var response = reader.ReadLine();
        using var responseDoc = JsonDocument.Parse( response ?? "{}" );
        if ( !responseDoc.RootElement.TryGetProperty( "ok", out var ok )
             || ok.ValueKind != JsonValueKind.True )
        {
            var reason = responseDoc.RootElement.TryGetProperty( "reason", out var r )
                ? r.GetString()
                : "no terminal acknowledgement";
            throw new InvalidOperationException( reason );
        }
    }

    internal static void RecordTerminal(
        OuterLeaseBinding binding, string editorOperationId, string tool, string action )
    {
        if ( !binding.IsValid ) return;

        if ( binding.IsEpochBound )
        {
            try
            {
                RecordEpochTerminalOverChannel(
                    binding, editorOperationId, tool, action );
            }
            catch ( Exception ex )
            {
                Log.Warning(
                    $"[mcp-lease-guard] Authenticated editor terminal channel failed for "
                    + $"{tool}.{action} request {binding.RequestId}: {ex.Message}" );
            }
            return;
        }

        string tmp = null;
        try
        {
            Directory.CreateDirectory( EditorTerminalDir );
            var path = Path.Combine( EditorTerminalDir, $"{binding.RequestId}.json" );
            tmp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText( tmp, BuildTerminalPayloadJson(
                binding, editorOperationId, tool, action ) );
            File.Move( tmp, path, overwrite: true );
        }
        catch ( Exception ex )
        {
            if ( !string.IsNullOrEmpty( tmp ) )
            {
                try { File.Delete( tmp ); } catch ( Exception ) { }
            }
            Log.Warning(
                $"[mcp-lease-guard] Failed to record legacy editor terminal proof for "
                + $"{tool}.{action} request {binding.RequestId}: {ex.Message}" );
        }
    }

    private static string Short( string id ) =>
        string.IsNullOrEmpty( id ) ? "?" : (id.Length <= 8 ? id : id[..8]);
}
