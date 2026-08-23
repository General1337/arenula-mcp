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
using System.IO;
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
    }

    private readonly record struct HolderSnapshot(
        string AgentId, string Class, ShadowCause Cause );

    // The holder file is rewritten on every grant/release. Re-reading it per MCP
    // call would be wasteful; a short cache keeps this off the hot path while
    // staying far fresher than any lock could turn over.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds( 250 );
    private static readonly TimeSpan ModeCacheTtl = TimeSpan.FromSeconds( 5 );
    private static readonly object Sync = new();

    private static DateTime _holderReadUtc = DateTime.MinValue;
    private static string _cachedHolderAgentId;
    private static string _cachedHolderClass;
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
    private static bool CoordinatorReachable()
    {
        try
        {
            if ( !File.Exists( AddressPath ) ) return false;
            using var doc = JsonDocument.Parse( File.ReadAllText( AddressPath ) );
            if ( !doc.RootElement.TryGetProperty( "pid", out var pidElement )
                 || !pidElement.TryGetInt32( out var pid ) || pid <= 0 )
                return false;
            using var process = System.Diagnostics.Process.GetProcessById( pid );
            return !process.HasExited;
        }
        catch ( Exception )
        {
            return false;
        }
    }

    private static HolderSnapshot ReadHolder()
    {
        lock ( Sync )
        {
            if ( DateTime.UtcNow - _holderReadUtc < CacheTtl )
                return new HolderSnapshot(
                    _cachedHolderAgentId, _cachedHolderClass, _cachedHolderCause );

            _holderReadUtc = DateTime.UtcNow;
            _cachedHolderAgentId = null;
            _cachedHolderClass = null;
            _cachedHolderCause = ShadowCause.None;

            if ( !CoordinatorReachable() )
            {
                _cachedHolderCause = ShadowCause.CoordinatorUnreachable;
                return new HolderSnapshot( null, null, _cachedHolderCause );
            }

            try
            {
                if ( !File.Exists( HolderPath ) )
                {
                    _cachedHolderCause = ShadowCause.MissingHolderState;
                    return new HolderSnapshot( null, null, _cachedHolderCause );
                }
                using var doc = JsonDocument.Parse( File.ReadAllText( HolderPath ) );
                if ( !doc.RootElement.TryGetProperty( "holder", out var h )
                     || h.ValueKind == JsonValueKind.Null )
                    return new HolderSnapshot( null, null, ShadowCause.None );
                if ( h.ValueKind != JsonValueKind.Object )
                {
                    _cachedHolderCause = ShadowCause.TornHolderState;
                    return new HolderSnapshot( null, null, _cachedHolderCause );
                }

                if ( h.TryGetProperty( "agent_id", out var a ) )
                    _cachedHolderAgentId = a.GetString();
                if ( h.TryGetProperty( "class", out var c ) )
                    _cachedHolderClass = c.GetString();
                if ( string.IsNullOrEmpty( _cachedHolderAgentId ) )
                    _cachedHolderCause = ShadowCause.TornHolderState;
            }
            catch ( Exception )
            {
                _cachedHolderAgentId = null;
                _cachedHolderClass = null;
                _cachedHolderCause = ShadowCause.TornHolderState;
            }

            return new HolderSnapshot(
                _cachedHolderAgentId, _cachedHolderClass, _cachedHolderCause );
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

    /// <summary>
    /// Should this mutating MCP call be refused because the caller does not hold
    /// the editor lease?
    ///
    /// Returns false (allow) whenever we cannot be SURE the call is illegitimate.
    /// A guard on the resource is powerful enough to take the editor away from
    /// every agent at once; it must only ever fire on certainty.
    /// </summary>
    internal static bool ShouldReject(
        string agentId, string tool, string action, bool readOnly, out object leaseError )
    {
        leaseError = null;

        var mode = CurrentMode;
        if ( mode == Mode.Off ) return false;

        // Reads cannot corrupt anything. Only mutations need a lease.
        if ( readOnly ) return false;

        var holder = ReadHolder();
        var (decision, cause) = EvaluateShadow( agentId, readOnly );
        if ( decision == ShadowDecision.Allow ) return false;
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

        // Phase 0a records uncertainty but does not claim editor enforcement;
        // fail-closed admission of CANNOT_EVALUATE is the explicit Phase 2 gate.
        if ( decision == ShadowDecision.CannotEvaluate ) return false;

        // Enforce.
        leaseError = new
        {
            error = "EDITOR-LEASE-REQUIRED",
            message =
                $"This mutating call ({tool}.{action}) was made without holding the editor lease. " +
                $"The S&Box editor is a single shared process and is currently leased by agent " +
                $"{Short( holderAgentId )} for '{holderClass}'. Concurrent mutation is what produces " +
                "the stale-DLL / half-loaded-assembly corruption this lease prevents.",
            holder = Short( holderAgentId ),
            holder_class = holderClass,
            caller = anonymous ? null : Short( agentId ),
            remedy =
                "Acquire the lease before mutating: `python Tools/agent-hook.py pre-mcp " +
                $"--tool {tool} --action {action}` (and `post-mcp` afterwards). Exit code 2 means " +
                "WAIT-AND-RETRY the identical call — this is contention, not failure. " +
                "See AGENTS.md and docs/ai/contracts/concurrency.md Rule 6.",
            retryable = true,
        };

        Log.Warning( $"[mcp-lease-guard ENFORCE] unleased mutation REFUSED: {detail}" );
        return true;
    }

    private static string Short( string id ) =>
        string.IsNullOrEmpty( id ) ? "?" : (id.Length <= 8 ? id : id[..8]);
}
