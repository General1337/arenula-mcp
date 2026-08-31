using System;
using System.Collections.Generic;
using System.Linq;

namespace Arenula;

/// <summary>
/// All 21 Arenula tool schemas for the MCP tools/list response.
/// Each tool uses the omnibus pattern: required action enum + flat params.
/// Descriptions follow Anthropic best practices: 3-4 sentences, negative guidance.
/// </summary>
internal static class ToolRegistry
{
    internal static readonly string Version = "1.0.0";

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Path C — additive LockKey field on ToolActionMetadata, no inherited handoff hypothesis applies; see docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md §"Path C — committed design".]
    internal sealed class ToolActionMetadata
    {
        public bool ReadOnly { get; init; }
        public string WriteKind { get; init; } = "none";
        public string LifecycleKind { get; init; } = "none";

        /// <summary>
        /// Per-call lock-key surface, consumed by McpOperationCoordinator.FindConflict
        /// to decide whether two concurrent non-readonly ops actually conflict. Default
        /// LockKeyStrategy.None means "legacy scene-wide write — conflicts with everything."
        /// Per-key writes (e.g. GameObjectId) conflict only when their extracted key sets
        /// overlap, enabling cross-session parallelism on disjoint targets.
        /// </summary>
        public LockKeyStrategy LockKey { get; init; } = LockKeyStrategy.None;
    }

    /// <summary>
    /// Effective metadata plus its authority trace. Existing coordinator callers
    /// continue to consume <see cref="ToolActionMetadata"/> through
    /// <see cref="GetOperationMetadata"/>; capability and drift surfaces use this
    /// richer result so an explicit declaration cannot be confused with a
    /// conservative fallback.
    /// </summary>
    internal sealed class ResolvedToolActionMetadata
    {
        public ToolActionMetadata Metadata { get; init; }
        public string Provenance { get; init; }
        public string Source { get; init; }
        public bool IsFallback { get; init; }
    }

    // [HANDOFF-TRUST: registering the brand-new 'capture' tool for mcp-agent-capability-system Wave 1.1 opened 2026-05-15 — additive registration in the All array; no inherited thruster-handoff hypothesis applies.]
    // [HANDOFF-TRUST: appending 'Gizmo' for mcp-agent-capability-system Wave 1.2 (sibling to Wave 1.1 capture.annotated SHIPPED 2026-05-15) — additive entry; bare 'wave1' substring match against unrelated thruster-vfx wave1 handoffs does not inherit any hypothesis.]
    // [HANDOFF-TRUST: appending 'Attention' for mcp-agent-capability-system Wave 1.3 (sibling to Wave 1.1 + 1.2 SHIPPED 2026-05-15); additive entry; bare 'wave1' substring collision against unrelated thruster-vfx handoffs does not inherit any hypothesis.]
    // [HANDOFF-TRUST: appending 'Pawn' for mcp-agent-capability-system Wave 2.1 — sibling to Wave 1 SHIPPED 2026-05-15 (3/3 features live); additive entry, bare 'wave2' substring collision against unrelated thruster-vfx wave2 handoffs (haze-shake, shock-diamonds, smoke-embergen) does NOT inherit any hypothesis — those touch Code/SBEP/Vfx/** + Assets/shaders/thruster_*.shader.]
    // [HANDOFF-TRUST: appending 'Eye' for mcp-agent-capability-system Wave 2.2 — sibling to Wave 2.1 pawn.* shipped this session; additive entry, no inherited thruster-vfx hypothesis applies.]
    // [HANDOFF-TRUST: appending 'Feed' for mcp-agent-capability-system Wave 3.1 — greenfield event-bus infrastructure sibling to Wave 1 + Wave 2 SHIPPED 2026-05-15; additive entry. Bare 'wave3' substring against any prior thruster/cloud wave3 handoff does NOT inherit hypothesis.]
    // [HANDOFF-TRUST: appending 'Time' for mcp-agent-capability-system Wave 4.1 — greenfield time-control sibling to Waves 1+2+3 SHIPPED 2026-05-15; additive entry. Bare 'wave4' substring against any prior wave4 handoff does NOT inherit hypothesis.]
    // [HANDOFF-TRUST: appending 'AgentObserve' for engine-fork-elite-leverage Phase 2.6 — first cross-agent capability built on top of the four feed.* channels (2.1/2.2/2.3/2.4) shipped this morning; additive entry; no inherited hypothesis from any prior phase2-named handoff applies.]
    // mcp-dynamic-discovery Phase 2 — 'mcp.health' and 'attention' migrated to the
    // [McpTool] attribute scan and dropped from this array. They survive as field
    // literals in MigratedToolLiterals (the parity test's pre-migration reference);
    // RpcDispatcher's tools/list re-combines them via McpToolScanner output.
    // [HANDOFF-TRUST: charter-spec feature build per docs/ai/initiatives/mcp-dynamic-discovery/charter.md §Phase 2 — no handoff hypothesis inherited; the falsification-gate matched 5 unrelated 'phase2' substring-collision handoffs with zero arenula_mcp file scope.]
    // mcp-dynamic-discovery Phase 3 Wave 3.1 — 'agents', 'continue',
    // 'cockpit_verify' and 'batch' migrated to the [McpTool] attribute scan and
    // dropped from this array. They survive as field literals in
    // MigratedToolLiterals (the parity test's pre-migration reference);
    // RpcDispatcher's tools/list re-combines them via McpToolScanner output.
    // mcp-dynamic-discovery Phase 3 Wave 3.2 — 'mcp.capabilities', 'mcp.server_log',
    // 'schema' and 'reflection' migrated to the [McpTool] attribute scan and dropped
    // from this array (same MigratedToolLiterals + re-combine pattern as Wave 3.1).
    /// <summary>
    /// Legacy discovery-wave literals retained as the verified rollback catalogue.
    /// They are no longer advertised: tools/list is scanner-only. The migration
    /// generator reads this property and emits direct attributed handler bindings.
    /// </summary>
    internal static object[] LegacyDiscoveryMigrationLiterals => new object[]
    {
        Scene, GameObject, Component, Prefab,
        AssetQuery, AssetManage, Editor, Compile,
        Mesh, Lighting, Physics, Audio,
        Effects, Camera, Capture, Gizmo, Pawn, Eye, Feed, Time, Navmesh, Session,
        Project, Cloud, Terrain, Code, Trace,
        // [HANDOFF-TRUST: appending 'Qa' for autonomous-qa-verifier Layer 1 / P1.3 — greenfield initiative MCP tool, additive entry; no inherited handoff hypothesis applies.]
        // [HANDOFF-TRUST: appending 'Diagnose' for autonomous-qa-verifier Layer 2 / P2.4 — greenfield initiative MCP tool, additive entry; no inherited handoff hypothesis applies.]
        SbepVerify, Qa, Diagnose, Pipeline,
        Workflow, VerifyChangeSet, Playtest,
        Operation, Transaction, Autofix, CrossIndex,
        Search, Snapshot, Validate, Blueprint, Animation
    };

    /// <summary>
    /// Compatibility surface for callers compiled against ToolRegistry.All.
    /// Final-wave tools/list advertisement is scanner-only; this must stay empty.
    /// </summary>
    internal static object[] All => Array.Empty<object>();

    private static readonly ToolActionMetadata ReadOnly = new() { ReadOnly = true };
    private static readonly ToolActionMetadata SceneWrite = new() { ReadOnly = false, WriteKind = "scene_write" };
    private static readonly ToolActionMetadata FileWrite = new() { ReadOnly = false, WriteKind = "file_write" };
    private static readonly ToolActionMetadata Lifecycle = new() { ReadOnly = false, LifecycleKind = "lifecycle" };

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Path C — additive per-key SceneWrite statics; no inherited handoff hypothesis applies, Phase 1 falsified the RFC's editor-mutex-layer claim and confirmed McpOperationCoordinator is the actual serialization surface (see docs/ai/sessions/2026-05-19-engine-fork-phase3-1-library-mvp/phase-01.md).]
    // [PERF-OK: greenfield correctness primitives for multi-agent contention check — not perf optimization of any existing hot path; these statics are simply additive metadata templates registered against actions in Phase 5; the runtime cost is one LockKey enum compare in Coordinator FindConflict, equivalent to the existing ReadOnly bool check.]
    // Per-key scene writes tag mutating actions with their lock-key extraction
    // strategy so McpOperationCoordinator.FindConflict can serialize only on
    // overlapping keys instead of cross-session-blanket-blocking every scene
    // write. Action registrations land in Phase 5.
    internal static readonly ToolActionMetadata SceneWritePerGoId =
        new() { ReadOnly = false, WriteKind = "scene_write_per_go", LockKey = LockKeyStrategy.GameObjectId };
    internal static readonly ToolActionMetadata SceneWritePerGoParent =
        new() { ReadOnly = false, WriteKind = "scene_write_per_go", LockKey = LockKeyStrategy.GameObjectParentId };
    internal static readonly ToolActionMetadata SceneWritePerComponentOwner =
        new() { ReadOnly = false, WriteKind = "scene_write_per_go", LockKey = LockKeyStrategy.ComponentOwningGameObject };
    internal static readonly ToolActionMetadata SceneWritePerBatchTransform =
        new() { ReadOnly = false, WriteKind = "scene_write_per_go", LockKey = LockKeyStrategy.BatchTransformTargets };
    internal static readonly ToolActionMetadata SceneWritePerTransaction =
        new() { ReadOnly = false, WriteKind = "scene_write_per_go", LockKey = LockKeyStrategy.TransactionTargets };

    private static readonly HashSet<string> ReadOnlyTools = new( StringComparer.OrdinalIgnoreCase )
    {
        "mcp.health", "mcp.capabilities", "mcp.server_log",
        "scene", "asset_query", "trace", "reflection", "schema",
        "search", "validate", "continue"
    };

    private static readonly Dictionary<string, ToolActionMetadata> ActionMetadata = new( StringComparer.OrdinalIgnoreCase )
    {
        ["compile.trigger"] = Lifecycle,
        ["compile.generate_solution"] = Lifecycle,
        ["compile.wait"] = Lifecycle,
        ["compile.smart_trigger"] = Lifecycle,
        ["compile.check"] = Lifecycle,
        ["batch.execute"] = Lifecycle,
        ["compile.status"] = ReadOnly,
        ["compile.errors"] = ReadOnly,
        ["compile.fix_context"] = ReadOnly,

        ["code.read_file"] = ReadOnly,
        ["code.list_files"] = ReadOnly,
        ["code.search_code"] = ReadOnly,
        ["code.write_file"] = FileWrite,

        ["editor.get_selected"] = ReadOnly,
        ["editor.get_play_state"] = ReadOnly,
        ["editor.get_log"] = ReadOnly,
        ["editor.get_hotload_summary"] = ReadOnly,
        ["asset_query.get_compile_errors"] = ReadOnly,
        ["editor.console_list"] = ReadOnly,
        ["editor.get_preferences"] = ReadOnly,
        ["editor.open_code_file"] = ReadOnly,
        ["editor.select"] = SceneWrite,
        ["editor.set_selected"] = SceneWrite,
        ["editor.clear_selection"] = SceneWrite,
        ["editor.frame_selection"] = SceneWrite,
        ["editor.save_scene"] = SceneWrite,
        ["editor.save_all"] = SceneWrite,
        ["editor.save_scene_as"] = SceneWrite,
        ["editor.undo"] = SceneWrite,
        ["editor.redo"] = SceneWrite,
        ["editor.set_preference"] = FileWrite,
        ["editor.console_run"] = Lifecycle,
        ["editor.start_play"] = Lifecycle,
        ["editor.stop_play"] = Lifecycle,
        ["editor.restart_mcp"] = Lifecycle,
        ["editor.reload_scene"] = Lifecycle,

        // [HANDOFF-TRUST: Wave 3.1 feed.* action metadata for mcp-agent-capability-system — greenfield event-bus, none of the inherited handoffs (cockpit-debugger, cloud-altitude, thruster-smoke) overlap this initiative's file scope. Same bare-substring collision pattern Wave 1 + Wave 2 hit and resolved with inline markers per charter cumulative state.]
        ["feed.subscribe"] = Lifecycle,
        ["feed.poll"] = ReadOnly,
        ["feed.unsubscribe"] = Lifecycle,
        ["feed.channels"] = ReadOnly,

        // workflow is a legacy omnibus tool, but its actions span pure reads,
        // file mutation, and compile lifecycle work. Explicit entries replace
        // the former blanket classification; unknown actions stay conservative.
        ["workflow.list"] = ReadOnly,
        ["workflow.create_prefab_pair"] = FileWrite,
        ["workflow.fix_compile_errors"] = ReadOnly,
        ["workflow.verify_networked_component"] = Lifecycle,
        ["workflow.implement_spawnable_entity"] = ReadOnly,
        ["workflow.add_tool_mode"] = ReadOnly,

        ["session.list"] = ReadOnly,
        ["session.set_active"] = Lifecycle,
        ["session.load_scene"] = Lifecycle,

        ["project.get_collision"] = ReadOnly,
        ["project.get_input"] = ReadOnly,
        ["project.get_info"] = ReadOnly,
        ["project.set_collision_rule"] = FileWrite,

        ["asset_manage.create"] = FileWrite,
        ["asset_manage.delete"] = FileWrite,
        ["asset_manage.rename"] = FileWrite,
        ["asset_manage.move"] = FileWrite,
        ["asset_manage.save"] = FileWrite,
        ["asset_manage.reload"] = Lifecycle,
        ["asset_manage.get_references"] = ReadOnly,

        ["blueprint.validate"] = ReadOnly,
        ["blueprint.create"] = SceneWrite,
        ["snapshot.take"] = ReadOnly,
        ["snapshot.list"] = ReadOnly,
        ["snapshot.diff"] = ReadOnly,
        ["snapshot.restore"] = SceneWrite,
        ["snapshot.delete"] = SceneWrite,
        ["animation.list_parameters"] = ReadOnly,
        ["animation.get_model_animations"] = ReadOnly,
        ["animation.set_parameters"] = SceneWrite,

        ["pipeline.compile_and_verify"] = Lifecycle,
        ["pipeline.probe_typelibrary"] = ReadOnly,
        ["verify_change_set.run"] = Lifecycle,
        ["playtest.boot_scene"] = Lifecycle,
        ["playtest.start"] = Lifecycle,
        ["playtest.stop"] = Lifecycle,
        ["playtest.run_assertions"] = Lifecycle,
        ["cockpit_verify.run"] = Lifecycle,
        ["sbep_verify.full_verify"] = Lifecycle,

        // [HANDOFF-TRUST: autonomous-qa-verifier Layer 1 / P1.3 — qa tool action
        // metadata; additive entries, greenfield initiative, no inherited handoff
        // hypothesis applies. The run_* actions dispatch play-mode test ConCmds →
        // Lifecycle; the two in-memory registry / catalogue reads → ReadOnly.]
        ["qa.run_suite"] = Lifecycle,
        ["qa.rerun_test"] = Lifecycle,
        ["qa.measure_variance"] = Lifecycle,
        ["qa.bisect_pollution"] = Lifecycle,
        ["qa.get_verdicts"] = ReadOnly,
        ["qa.list_suites"] = ReadOnly,

        // [HANDOFF-TRUST: autonomous-qa-verifier Layer 2 / P2.4 — diagnose tool
        // action metadata; additive entries, greenfield initiative, no inherited
        // handoff hypothesis applies. test / suite dispatch play-mode test ConCmds
        // through the closed diagnosis loop → Lifecycle; bundle is a pure in-memory
        // read of the stored diagnosis bundles → ReadOnly.]
        ["diagnose.test"] = Lifecycle,
        ["diagnose.suite"] = Lifecycle,
        ["diagnose.bundle"] = ReadOnly,

        ["operation.validate"] = ReadOnly,
        ["operation.preview"] = ReadOnly,
        ["operation.apply"] = SceneWrite,
        ["operation.rollback"] = SceneWrite,
        // mcp-operation-lifecycle-hardening: coordinator control plane. These are
        // dispatched BEFORE McpOperationCoordinator.TryBegin in RpcDispatcher (a
        // wedged scene-wide lease would otherwise block the very call meant to
        // clear it) — registered ReadOnly so capability listings stay honest;
        // they never take a lease and never touch the scene.
        ["operation.cancel"] = ReadOnly,
        ["operation.list"] = ReadOnly,
        ["operation.locks_held"] = ReadOnly,
        ["transaction.preview"] = ReadOnly,
        ["transaction.apply"] = SceneWrite,
        ["transaction.rollback"] = SceneWrite,

        ["autofix.prefab_fixups"] = FileWrite,
        ["autofix.create_sent_pair"] = FileWrite,
        ["cross_index.build"] = FileWrite,
        ["cross_index.stats"] = ReadOnly,
        ["cross_index.query"] = ReadOnly,

        // [HANDOFF-TRUST: engine-fork-elite-leverage Phase 2.6 agents.observe action metadata — read-only cross-agent state aggregator, additive entry; no inherited hypothesis applies from any prior phase2-named handoff.]
        ["agents.observe"] = ReadOnly,

        // [HANDOFF-TRUST: engine-fork-elite-leverage Phase 3.3 scratch-scene-pool action metadata — additive entries for the scanned 'scratch' [McpTool]; GetOperationMetadata reads this dict even for scanned tools (precedent: agents.observe). No inherited handoff hypothesis applies — clean greenfield feature.]
        ["scratch.ensure"]  = SceneWrite,
        ["scratch.get"]     = ReadOnly,
        ["scratch.clear"]   = SceneWrite,
        ["scratch.release"] = SceneWrite,
        ["scratch.list"]    = ReadOnly,

        // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 5 — per-key lock metadata for 18 gameobject/component write actions (Path C committed design, phase-01.md item 1). Activates McpOperationCoordinator's per-key contention check so disjoint-target edits from different agents stop false-serializing. No inherited handoff hypothesis applies. transaction.apply intentionally stays SceneWrite: the preview/apply split means apply args carry only transaction_id, so inline per-transaction keying is deferred (see LockKeyExtractor.ParseTransactionTargets docstring).]
        ["gameobject.create"]            = SceneWritePerGoParent,
        ["gameobject.destroy"]           = SceneWritePerGoId,
        ["gameobject.destroy_by_name"]   = SceneWritePerGoId,
        ["gameobject.destroy_children"]  = SceneWritePerGoId,
        ["gameobject.duplicate"]         = SceneWritePerGoId,
        ["gameobject.reparent"]          = SceneWritePerGoId,
        ["gameobject.rename"]            = SceneWritePerGoId,
        ["gameobject.enable"]            = SceneWritePerGoId,
        ["gameobject.set_tags"]          = SceneWritePerGoId,
        ["gameobject.set_transform"]     = SceneWritePerGoId,
        ["gameobject.batch_transform"]   = SceneWritePerBatchTransform,
        ["component.add"]                = SceneWritePerComponentOwner,
        ["component.remove"]             = SceneWritePerComponentOwner,
        ["component.set_property"]       = SceneWritePerComponentOwner,
        ["component.set_enabled"]        = SceneWritePerComponentOwner,
        ["component.copy"]               = SceneWritePerComponentOwner,
        ["component.bulk_set"]           = SceneWritePerComponentOwner,
        ["component.apply_properties"]   = SceneWritePerComponentOwner
    };

    internal static ToolActionMetadata GetOperationMetadata( string tool, string action )
        => ResolveOperationMetadata( tool, action ).Metadata;

    internal static ResolvedToolActionMetadata ResolveOperationMetadata( string tool, string action )
    {
        if ( !string.IsNullOrWhiteSpace( tool )
             && McpToolScanner.Cached.TryGetValue( tool, out var scannedTool ) )
        {
            var scannedAction = ResolveScannedAction( scannedTool, action );
            if ( scannedAction != null )
            {
                var scannedMetadata = new ToolActionMetadata
                {
                    ReadOnly = scannedAction.ReadOnly,
                    WriteKind = scannedAction.WriteKind,
                    LifecycleKind = scannedAction.LifecycleKind,
                    LockKey = scannedAction.LockKey,
                };

                var duplicateKey = BuildOperationKey( tool, scannedAction.Action );
                if ( duplicateKey != null && ActionMetadata.TryGetValue( duplicateKey, out var legacyDuplicate ) )
                    EnsureCompatibleDuplicate( duplicateKey, scannedMetadata, legacyDuplicate );

                return Resolution( scannedMetadata, "explicit", "mcp_action", isFallback: false );
            }

            // Once a tool is scanner-owned, an unrecognized action must not fall
            // through to a stale legacy registration or a permissive tool default.
            return Resolution( SceneWrite, "fallback", "unknown_default", isFallback: true );
        }

        var key = BuildOperationKey( tool, action );
        if ( key != null && ActionMetadata.TryGetValue( key, out var metadata ) )
            return Resolution( metadata, "explicit", "legacy_action", isFallback: false );

        if ( !string.IsNullOrWhiteSpace( tool ) && ReadOnlyTools.Contains( tool ) )
            return Resolution( ReadOnly, "fallback", "tool_default", isFallback: true );

        return Resolution( SceneWrite, "fallback", "unknown_default", isFallback: true );
    }

    private static ResolvedToolActionMetadata Resolution(
        ToolActionMetadata metadata, string provenance, string source, bool isFallback )
        => new()
        {
            Metadata = metadata,
            Provenance = provenance,
            Source = source,
            IsFallback = isFallback,
        };

    private static string BuildOperationKey( string tool, string action )
    {
        if ( string.IsNullOrWhiteSpace( tool ) ) return null;
        return string.IsNullOrEmpty( action ) ? tool : $"{tool}.{action}";
    }

    private static ScannedAction ResolveScannedAction( ScannedTool tool, string action )
    {
        if ( string.IsNullOrEmpty( action )
             && tool.Actions.Count == 1
             && tool.Actions.TryGetValue( "", out var bareAction ) )
            return bareAction;

        return tool.Actions.TryGetValue( action ?? "", out var found ) ? found : null;
    }

    internal static void EnsureCompatibleDuplicate(
        string key, ToolActionMetadata scanned, ToolActionMetadata legacy )
    {
        if ( MetadataEquivalent( scanned, legacy ) ) return;

        throw new InvalidOperationException(
            $"Conflicting operation metadata for scanned action '{key}'. " +
            $"[McpAction]={DescribeMetadata( scanned )}; legacy ActionMetadata={DescribeMetadata( legacy )}. " +
            "Scanned metadata is authoritative; update or remove the stale duplicate." );
    }

    private static bool MetadataEquivalent( ToolActionMetadata left, ToolActionMetadata right )
        => left != null && right != null
           && left.ReadOnly == right.ReadOnly
           && string.Equals( left.WriteKind, right.WriteKind, StringComparison.Ordinal )
           && string.Equals( left.LifecycleKind, right.LifecycleKind, StringComparison.Ordinal )
           && left.LockKey == right.LockKey;

    private static string DescribeMetadata( ToolActionMetadata metadata )
        => metadata == null
            ? "<null>"
            : $"read_only={metadata.ReadOnly},write_kind={metadata.WriteKind}," +
              $"lifecycle_kind={metadata.LifecycleKind},lock_key={LockKeyWireName( metadata.LockKey )}";

    internal static object[] DescribeActions( object tool )
    {
        var name = GetStringProperty( tool, "name" );
        var actions = GetActionNames( tool );
        if ( actions.Length == 0 )
        {
            var resolved = ResolveOperationMetadata( name, null );
            var metadata = resolved.Metadata;
            return new object[]
            {
                new
                {
                    tool = name,
                    action = (string)null,
                    qualified_action = name,
                    read_only = metadata.ReadOnly,
                    write_kind = metadata.WriteKind,
                    lifecycle_kind = metadata.LifecycleKind,
                    lock_key = LockKeyWireName( metadata.LockKey ),
                    provenance = resolved.Provenance,
                    source = resolved.Source,
                    is_fallback = resolved.IsFallback,
                }
            };
        }

        return actions
            .OrderBy( action => action, StringComparer.OrdinalIgnoreCase )
            .ThenBy( action => action, StringComparer.Ordinal )
            .Select( action =>
        {
            var resolved = ResolveOperationMetadata( name, action );
            var metadata = resolved.Metadata;
            return (object)new
            {
                tool = name,
                action,
                qualified_action = $"{name}.{action}",
                read_only = metadata.ReadOnly,
                write_kind = metadata.WriteKind,
                lifecycle_kind = metadata.LifecycleKind,
                lock_key = LockKeyWireName( metadata.LockKey ),
                provenance = resolved.Provenance,
                source = resolved.Source,
                is_fallback = resolved.IsFallback,
            };
        } ).ToArray();
    }

    internal static string LockKeyWireName( LockKeyStrategy strategy ) => strategy switch
    {
        LockKeyStrategy.None => "none",
        LockKeyStrategy.GameObjectId => "game_object_id",
        LockKeyStrategy.GameObjectParentId => "game_object_parent_id",
        LockKeyStrategy.ComponentOwningGameObject => "component_owning_game_object",
        LockKeyStrategy.BatchTransformTargets => "batch_transform_targets",
        LockKeyStrategy.TransactionTargets => "transaction_targets",
        _ => "unknown",
    };

    internal static object DescribeUnknownActionFallback()
    {
        var resolved = Resolution( SceneWrite, "fallback", "unknown_default", isFallback: true );
        return new
        {
            read_only = resolved.Metadata.ReadOnly,
            write_kind = resolved.Metadata.WriteKind,
            lifecycle_kind = resolved.Metadata.LifecycleKind,
            lock_key = LockKeyWireName( resolved.Metadata.LockKey ),
            provenance = resolved.Provenance,
            source = resolved.Source,
            is_fallback = resolved.IsFallback,
        };
    }

    internal static string GetStringProperty( object value, string propertyName )
    {
        return value?.GetType().GetProperty( propertyName )?.GetValue( value )?.ToString();
    }

    /// <summary>
    /// Return the exact inputSchema object from one frozen tool literal. Generated
    /// migration adapters call this instead of duplicating hundreds of schema
    /// properties; the full parity harness still hashes the resulting payload.
    /// </summary>
    internal static object GetInputSchema( object tool )
    {
        if ( tool is null )
            throw new InvalidOperationException( "Tool literal is null while resolving inputSchema." );
        var property = tool.GetType().GetProperty( "inputSchema" );
        return property?.GetValue( tool )
            ?? throw new InvalidOperationException( "Tool literal has no inputSchema property." );
    }

    internal static string[] GetActionNames( object tool )
    {
        var inputSchema = tool?.GetType().GetProperty( "inputSchema" )?.GetValue( tool );
        var properties = inputSchema?.GetType().GetProperty( "properties" )?.GetValue( inputSchema );
        if ( properties is not IDictionary<string, object> dict || !dict.TryGetValue( "action", out var actionSchema ) )
            return Array.Empty<string>();

        var enumValue = actionSchema.GetType().GetProperty( "enum" )?.GetValue( actionSchema );
        return enumValue is IEnumerable<string> actions ? actions.ToArray() : Array.Empty<string>();
    }

    internal static readonly object McpHealth = new
    {
        name = "mcp.health",
        description = "Return MCP server health and live editor context. Use this before a tool sequence to confirm transport, port, session count, play mode, active scene, and retained log health. This is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>(),
            additionalProperties = false
        }
    };

    internal static readonly object McpCapabilities = new
    {
        name = "mcp.capabilities",
        description = "Return the current Arenula tool inventory with advertised actions and basic server capabilities. Use this when a client needs a compact capability summary without downloading and inspecting the full tools/list schema. This is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>(),
            additionalProperties = false
        }
    };

    internal static readonly object McpServerLog = new
    {
        name = "mcp.server_log",
        description = "Read the MCP/editor structured log ring buffer. Use this to inspect recent server errors, tool calls, compile messages, and runtime log entries captured by the live editor. Supports count, filter, offset, and limit; this is read-only and has no required action parameter.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["count"] = new { type = "integer", description = "Trim to the last N retained entries before filtering. Default: 200." },
                ["filter"] = new { type = "string", description = "Case-insensitive substring filter over messages." },
                ["level"] = new { type = "string", description = "Comma-separated severity filter such as 'error' or 'warn,error'." },
                ["source"] = new { type = "string", description = "Comma-separated source filter such as 'engine' or 'mcp'." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max entries to return. Default: 100." },
                ["include_stacks"] = new { type = "boolean", description = "Include full inline stack traces per entry (~2KB each). Default: false — the envelope's stacks_omitted reports how many entries have one available." }
            },
            additionalProperties = false
        }
    };

    // ── Tool 1: scene ────────────────────────────────────────────────

    internal static readonly object Scene = new
    {
        name = "scene",
        description = "Read and query the s&box editor scene. Use this to understand what's in the scene before making changes. Actions: 'summary' returns a high-level overview with root object list, 'hierarchy' returns the full GameObject tree, 'statistics' returns object/component counts, 'find' searches by name/tag/component type with pagination, 'find_in_radius' searches spatially around a point, 'get_details' returns full info on a specific GameObject including all components, 'prefab_instances' lists all prefab instances. This tool only reads scene state — use 'gameobject' or 'component' to create or modify objects.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "summary", "hierarchy", "statistics", "find", "find_in_radius", "get_details", "prefab_instances" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_details." },
                ["query"] = new { type = "string", description = "Search term. Used by: find." },
                ["tag"] = new { type = "string", description = "Tag filter. Used by: find." },
                ["component_type"] = new { type = "string", description = "Component type filter. Used by: find." },
                ["position"] = new { type = "string", description = "Center point as 'x,y,z'. Required for: find_in_radius." },
                ["radius"] = new { type = "number", description = "Search radius in units. Required for: find_in_radius." },
                ["prefab_path"] = new { type = "string", description = "Filter by prefab source path. Used by: prefab_instances." },
                ["max_depth"] = new { type = "integer", description = "Max tree depth. Used by: hierarchy. Default: unlimited." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: find, find_in_radius, prefab_instances. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: find, find_in_radius, prefab_instances. Default: 50." },
                ["format"] = new { type = "string", description = "Response detail level. Used by: find, get_details. Values: 'concise' (default), 'detailed'.", @enum = new[] { "concise", "detailed" } }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 2: gameobject ───────────────────────────────────────────

    internal static readonly object GameObject = new
    {
        name = "gameobject",
        description = "Create, modify, and destroy GameObjects in the scene. Use 'scene' to find objects first, then this tool to change them. For adding or configuring components, use 'component' instead. All write actions return the GUID and name of the affected object. Use 'batch_transform' to move multiple objects in a single call. Use 'destroy_by_name' to bulk-destroy objects by name (optionally under a parent), and 'destroy_children' to destroy all children of an object.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "destroy", "destroy_by_name", "destroy_children", "duplicate", "reparent", "rename", "enable", "set_tags", "set_transform", "batch_transform" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: destroy, duplicate, reparent, rename, enable, set_tags, set_transform. Also used as parent GUID for: destroy_by_name, destroy_children." },
                ["ids"] = new { type = "string", description = "Comma-separated GUIDs. Required for: batch_transform." },
                ["name"] = new { type = "string", description = "Object name. Used by: create, rename, destroy_by_name (required — name filter for objects to destroy)." },
                ["parent_id"] = new { type = "string", description = "Parent GUID. Used by: create (optional), reparent (required)." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create, set_transform, batch_transform." },
                ["rotation"] = new { type = "string", description = "Rotation as 'pitch,yaw,roll'. Used by: create, set_transform, batch_transform." },
                ["scale"] = new { type = "string", description = "Scale as 'x,y,z'. Used by: create, set_transform, batch_transform." },
                ["space"] = new { type = "string", description = "Coordinate space. Used by: set_transform. Values: 'world' (default), 'local'.", @enum = new[] { "world", "local" } },
                ["enabled"] = new { type = "boolean", description = "Enable/disable state. Required for: enable." },
                ["tags"] = new { type = "string", description = "Comma-separated tags. Required for: set_tags." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 3: component ────────────────────────────────────────────

    internal static readonly object Component = new
    {
        name = "component",
        description = "Manage components on GameObjects. Use 'scene' to find objects, then this tool to add, remove, or configure their components. 'add' returns similar type suggestions if the exact name doesn't match. Use 'get_types' with a filter to discover available component types before adding. Use 'get_properties' to inspect a component's current values before setting them. Use 'bulk_set' or 'apply_properties' to set multiple properties on a single component in one call via a JSON object of {property: value} pairs.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "add", "remove", "set_property", "set_enabled", "get_properties", "get_types", "copy", "bulk_set", "apply_properties" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: add, remove, set_property, set_enabled, get_properties, bulk_set, apply_properties." },
                ["component_id"] = new { type = "string", description = "Component GUID on the target object. Required for: remove, set_property, set_enabled, get_properties, bulk_set, apply_properties." },
                ["type"] = new { type = "string", description = "Component type name (e.g. 'Rigidbody', 'ModelRenderer'). Required for: add." },
                ["property"] = new { type = "string", description = "Property name. Required for: set_property." },
                ["value"] = new { type = "string", description = "Property value (auto-converted to correct type). Required for: set_property." },
                ["properties"] = new { description = "Object or JSON string of {property: value} pairs. Required for: bulk_set, apply_properties. Example: {\"Model\": \"path.vmdl\", \"Tint\": \"1,0,0,1\"}", oneOf = new object[] { new { type = "object", additionalProperties = true }, new { type = "string" } } },
                ["enabled"] = new { type = "boolean", description = "Enable/disable state. Required for: set_enabled." },
                ["source_component_id"] = new { type = "string", description = "Source component GUID to copy from. Required for: copy." },
                ["target_id"] = new { type = "string", description = "Target GameObject GUID to copy to. Required for: copy." },
                ["filter"] = new { type = "string", description = "Type name filter. Used by: get_types." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 4: prefab ───────────────────────────────────────────────

    internal static readonly object Prefab = new
    {
        name = "prefab",
        description = "Manage prefabs: instantiate into scenes, create from GameObjects, and manage instance overrides. Use 'create' to convert a scene GameObject into a reusable .prefab file on disk. Use 'save_overrides' to push instance changes back to the prefab source file. Use 'revert' to discard instance changes and match the source prefab. Use 'get_overrides' to see which properties differ from the source.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "instantiate", "get_structure", "get_instances", "break", "update", "create", "save_overrides", "revert", "get_overrides" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: break, update, create, save_overrides, revert, get_overrides." },
                ["path"] = new { type = "string", description = "Prefab file path. Required for: instantiate, get_structure. Optional filter for: get_instances." },
                ["save_path"] = new { type = "string", description = "Where to save the new prefab file. Required for: create." },
                ["position"] = new { type = "string", description = "Spawn position as 'x,y,z'. Used by: instantiate." },
                ["rotation"] = new { type = "string", description = "Spawn rotation as 'pitch,yaw,roll'. Used by: instantiate." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: get_instances. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: get_instances. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 5: asset_query ──────────────────────────────────────────

    internal static readonly object AssetQuery = new
    {
        name = "asset_query",
        description = "Browse, search, and inspect project assets. Use 'browse' to list a directory, 'search' to find assets by name or type, and the various 'get_*' actions to inspect specific asset details. Use 'get_compile_errors' for the LRU map of recent per-asset resource-compile failures (.vmat / .vmdl / .shader / .png) keyed by source path — the resource-compiler complement to the 'compile' tool's C# error surface. This tool only reads — use 'asset_manage' to create, rename, move, or delete assets. For cloud/workshop assets, use 'cloud' instead.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "browse", "search", "open", "get_dependencies", "get_model_info", "get_material_properties", "get_mesh_info", "get_bounds", "get_unsaved", "get_status", "get_json", "get_references", "get_compile_errors" } },
                ["path"] = new { type = "string", description = "Asset path. Required for: open, get_dependencies, get_model_info, get_material_properties, get_status, get_json, get_references. Used as case-insensitive prefix filter for: get_compile_errors." },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_mesh_info, get_bounds." },
                ["query"] = new { type = "string", description = "Search term. Required for: search." },
                ["type"] = new { type = "string", description = "Asset type filter. Used by: search." },
                ["directory"] = new { type = "string", description = "Directory to browse. Used by: browse." },
                ["deep"] = new { type = "boolean", description = "Include transitive references. Used by: get_references. Default: false." },
                ["format"] = new { type = "string", description = "Response detail level. Used by: search, get_model_info. Values: 'concise' (default), 'detailed'.", @enum = new[] { "concise", "detailed" } },
                ["kind"] = new { type = "string", description = "Asset extension class filter. Used by: get_compile_errors. Values: 'vmat', 'vmdl', 'shader', 'tmat', 'texture', 'fbx', 'sound', 'other'." },
                ["count"] = new { type = "integer", description = "Trim to last N entries after filtering. Used by: get_compile_errors. Default: all (capped at 100)." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: browse, search. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: browse, search. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 6: asset_manage ─────────────────────────────────────────

    internal static readonly object AssetManage = new
    {
        name = "asset_manage",
        description = "Create, rename, move, delete, save, and reload project assets. Use 'create' to make a new GameResource of any type (material, sound event, etc.). Use 'reload' to force-recompile an asset from disk. Destructive actions (delete) send files to the recycle bin, not permanent deletion. For browsing or inspecting assets, use 'asset_query' instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "delete", "rename", "move", "save", "reload", "get_references" } },
                ["path"] = new { type = "string", description = "Source asset path. Required for: delete, rename, move, save, reload, get_references." },
                ["type"] = new { type = "string", description = "GameResource type name. Required for: create." },
                ["new_name"] = new { type = "string", description = "New name for the asset. Required for: rename." },
                ["destination"] = new { type = "string", description = "Target directory path. Required for: move." },
                ["force"] = new { type = "boolean", description = "Used by: reload. Default: false — bypass the loaded-scene-reference fail-fast and recompile anyway." },
                ["deep"] = new { type = "boolean", description = "Include transitive references. Used by: get_references. Default: false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 7: editor ───────────────────────────────────────────────

    internal static readonly object Editor = new
    {
        name = "editor",
        description = "Control the s&box editor: manage object selection, play mode, scene saving, undo/redo, console commands, code editor, preferences, and MCP server lifecycle. For scene content operations use 'scene' and 'gameobject'. Use 'get_log' to read compiler output and runtime errors. Use 'get_hotload_summary' for per-generation hotload snapshots (failed types, warning tokens, scan wall time) — feeds /diagnose-stale-dll and /audit. Use 'undo' and 'redo' to step through edit history. Use 'console_run' to execute console commands — both ConVar (get/set) and ConCmd (execute with output capture). Use 'console_list' with filter to discover available commands. Use 'restart_mcp' after hot-reload to restart the SSE server with recompiled code. Use 'reload_scene' to safely reload the active scene (or a scene by path) from disk — recovery for the post-asset-recompile blank-scene bug; stops play mode automatically.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "select", "get_selected", "set_selected", "clear_selection", "frame_selection", "get_play_state", "start_play", "stop_play", "get_log", "get_hotload_summary", "save_scene", "save_all", "save_scene_as", "undo", "redo", "console_list", "console_run", "open_code_file", "get_preferences", "set_preference", "restart_mcp", "reload_scene" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Used by: select, frame_selection." },
                ["ids"] = new { type = "string", description = "Comma-separated GUIDs. Required for: set_selected." },
                ["path"] = new { type = "string", description = "File or scene path. Required for: save_scene_as, open_code_file. Optional for: reload_scene (defaults to active scene)." },
                ["command"] = new { type = "string", description = "Console command string. Required for: console_run. Supports both ConVar ('sbep.debug 1') and ConCmd ('sbep.dump_resources')." },
                ["filter"] = new { type = "string", description = "Filter text. Used by: get_log, console_list." },
                ["wait_ms"] = new { type = "integer", description = "Milliseconds to wait for ConCmd output capture. Used by: console_run. Default: 100. Max: 5000." },
                ["bind_scene"] = new { type = "boolean", description = "Used by: console_run. Default: true — the ConCmd executes inside a Scene.Push() scope bound to the live session scene (play-mode clone when playing), so Game.ActiveScene is never stale/null post-compile. Set false to run in the raw tools context. Response reports scene_bound + scene_is_play_session." },
                ["force"] = new { type = "boolean", description = "Used by: save_scene, save_all. Default: false — bypass the scene-integrity refusal (reload hold / disk-vs-memory count deficit) when the in-memory state is intentionally what should be written to disk. On save_all this applies to every dirty session, so prefer saving the one scene you mean." },
                ["count"] = new { type = "integer", description = "Number of log entries. Used by: get_log. Trim to last N hotload snapshots after filtering for: get_hotload_summary." },
                ["line"] = new { type = "integer", description = "Line number. Used by: open_code_file." },
                ["column"] = new { type = "integer", description = "Column number. Used by: open_code_file." },
                ["key"] = new { type = "string", description = "Preference key. Required for: set_preference." },
                ["value"] = new { type = "string", description = "Preference value. Required for: set_preference." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: get_log. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: get_log. Default: 50." },
                ["include_stacks"] = new { type = "boolean", description = "Used by: get_log. Default: false — include full inline stack traces (~2KB per entry). When omitted, items carry no stack field and the envelope's stacksOmitted reports how many entries have a trace available behind this flag." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 8: compile ──────────────────────────────────────────────

    internal static readonly object Compile = new
    {
        name = "compile",
        description = "Trigger code compilation and read build results. PREFER 'check' — one call = trigger + wait + UNMASKED diagnostics (sourced from the engine build events that feed feed.compile, so failed-build errors like CS0136/CS0272 are never hidden by a stale cache) + hotload verdict (tri-state live/confirmed-stale/unknown from a direct TypeLibrary probe, loaded-type hash, ConCmd-registry self-heal report). Use 'smart_trigger' for compile+wait+errors-with-source-context. Use 'trigger' to start a build, 'status' to check state, and 'errors' to read diagnostics (WARNING: 'errors' reads a cache that goes stale when a failed build makes the compile call throw — 'check' has no such masking). Use 'wait' to block until compilation finishes. Use 'fix_context' to retrieve full source context for a specific error index. Use 'verify_hotreload' for the standalone hotload verdict ('generation' from 'snapshot_hotreload' is now optional legacy input — the TypeLibrary probe is authoritative). Runs on background thread — does not block the editor.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "trigger", "status", "errors", "generate_solution", "wait", "smart_trigger", "fix_context", "snapshot_hotreload", "verify_hotreload", "check" } },
                ["severity"] = new { type = "string", description = "Filter diagnostics by severity. Used by: errors. Values: 'error', 'warning', 'info'.", @enum = new[] { "error", "warning", "info" } },
                ["timeout_ms"] = new { type = "integer", description = "Max wait time in milliseconds. Used by: wait (default 60000), check (default 60000, min 5000, max 300000)." },
                ["context_lines"] = new { type = "integer", description = "Lines of source context around each error. Used by: smart_trigger (default 10, max 30), fix_context (default 15, max 50)." },
                ["max_errors"] = new { type = "integer", description = "Max errors to return. Used by: smart_trigger. Default: 5, max: 20." },
                ["error_index"] = new { type = "integer", description = "0-based index into the sorted error list. Used by: fix_context. Default: 0." },
                ["generation"] = new { type = "integer", description = "Pre-compile hotload generation counter from snapshot_hotreload. Used by: verify_hotreload (OPTIONAL legacy input — the TypeLibrary probe verdict does not need it)." },
                ["max_diagnostics"] = new { type = "integer", description = "Max diagnostics to return. Used by: check. Default: 50, max: 200. Totals are always reported; 'truncated' flags when the list was capped." },
                ["type_names"] = new { type = "array", items = new { type = "string" }, description = "Explicit type names for the TypeLibrary live-code probe. Used by: check, verify_hotreload. When omitted, probe types are auto-derived from .cs files edited within recent_seconds." },
                ["recent_seconds"] = new { type = "integer", description = "Lookback window (seconds) for auto-deriving probe types from recently edited .cs files. Used by: check, verify_hotreload. Default: 120, min 10, max 3600." },
                ["agent_id"] = new { type = "string", description = "Caller agent identity for the session-keyed compile-output cache (findings #4). Used by: trigger, smart_trigger, check (attributes the outputs + engine build events), errors, status, fix_context (selects the caller's own cached view — response carries cache_view provenance). Optional; omitted = legacy shared-snapshot behavior." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 9: mesh ─────────────────────────────────────────────────

    internal static readonly object Mesh = new
    {
        name = "mesh",
        description = "Create and edit polygon meshes directly in the scene. Primitives: 'create_block' (box), 'create_plane' (flat quad), 'create_cylinder' (ring-based), 'create_wedge' (triangular prism/ramp), 'create_arch' (semicircular arc), 'create_clutter' (scattered objects). Construction: 'extrude_faces' (push faces outward), 'remove_faces' (cut holes), 'add_face' (polygon from vertices), 'clip_faces' (slice with plane), 'scale_mesh' (resize). Refinement: 'thicken_faces' (give faces depth), 'bevel_edges' (round edges), 'bevel_vertices' (round corners), 'split_edges' (subdivide edges), 'quad_slice_faces' (subdivide faces into grid), 'dissolve_edges' (simplify by merging faces). Topology: 'bridge_edges' (connect two open edges), 'connect_vertices' (split face by connecting two vertices), 'flip_faces' (reverse normals), 'extend_edges' (extrude boundary edges outward). Per-element: vertex position/color/blend, face material/texture. Note: after topology changes, face/vertex/edge indices are invalidated — use returned indices or 'get_info' for subsequent operations.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create_block", "create_clutter", "create_plane", "create_cylinder", "create_wedge", "create_arch", "extrude_faces", "remove_faces", "add_face", "clip_faces", "scale_mesh", "thicken_faces", "bevel_edges", "bevel_vertices", "split_edges", "quad_slice_faces", "dissolve_edges", "bridge_edges", "connect_vertices", "flip_faces", "extend_edges", "set_face_material", "set_texture_params", "set_vertex_position", "set_vertex_color", "set_vertex_blend", "get_info" } },
                ["id"] = new { type = "string", description = "Target mesh GameObject GUID. Required for: extrude_faces, remove_faces, add_face, clip_faces, scale_mesh, thicken_faces, bevel_edges, bevel_vertices, split_edges, quad_slice_faces, dissolve_edges, bridge_edges, connect_vertices, flip_faces, extend_edges, set_face_material, set_texture_params, set_vertex_position, set_vertex_color, set_vertex_blend, get_info." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, create_clutter." },
                ["size"] = new { type = "string", description = "Size as 'x,y,z'. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch. For scale_mesh: scale factor as 'x,y,z'." },
                ["material"] = new { type = "string", description = "Material path. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, set_face_material, add_face." },
                ["name"] = new { type = "string", description = "Object name. Used by: create_block, create_plane, create_cylinder, create_wedge, create_arch, create_clutter." },
                ["definition"] = new { type = "string", description = "Clutter definition asset path. Required for: create_clutter." },
                ["radius"] = new { type = "number", description = "Scatter radius. Used by: create_clutter." },
                ["segments"] = new { type = "integer", description = "Segment count (3-64). Used by: create_cylinder (default 16), create_arch (default 8)." },
                ["face_index"] = new { type = "integer", description = "Single face index. Required for: set_face_material, set_texture_params." },
                ["face_indices"] = new { type = "string", description = "Comma-separated face indices '0,1,2'. Required for: extrude_faces, remove_faces, clip_faces, thicken_faces, quad_slice_faces." },
                ["vertex_index"] = new { type = "integer", description = "Single vertex index. Required for: set_vertex_position, set_vertex_color, set_vertex_blend." },
                ["vertex_indices"] = new { type = "string", description = "Comma-separated vertex indices in winding order. Required for: add_face. Used by: bevel_vertices." },
                ["extrude_offset"] = new { type = "string", description = "Extrusion direction/distance as 'x,y,z'. Used by: extrude_faces. Default: face normal * 50." },
                ["plane_normal"] = new { type = "string", description = "Cutting plane normal as 'x,y,z'. Required for: clip_faces." },
                ["plane_point"] = new { type = "string", description = "Point on cutting plane as 'x,y,z'. Required for: clip_faces." },
                ["remove_behind"] = new { type = "boolean", description = "Remove faces behind cutting plane. Used by: clip_faces. Default: true." },
                ["cap"] = new { type = "boolean", description = "Cap the cut opening with a new face. Used by: clip_faces. Default: true." },
                ["edge_indices"] = new { type = "string", description = "Comma-separated half-edge indices. Required for: bevel_edges, split_edges, dissolve_edges, extend_edges." },
                ["edge_index_a"] = new { type = "integer", description = "First edge index. Required for: bridge_edges." },
                ["edge_index_b"] = new { type = "integer", description = "Second edge index. Required for: bridge_edges." },
                ["vertex_index_a"] = new { type = "integer", description = "First vertex index. Required for: connect_vertices." },
                ["vertex_index_b"] = new { type = "integer", description = "Second vertex index. Required for: connect_vertices." },
                ["amount"] = new { type = "number", description = "Thickness/distance amount. Required for: thicken_faces, extend_edges." },
                ["distance"] = new { type = "number", description = "Bevel distance. Used by: bevel_edges, bevel_vertices. Default: 5." },
                ["bevel_segments"] = new { type = "integer", description = "Bevel smoothness (segment count). Used by: bevel_edges. Default: 1." },
                ["shape"] = new { type = "number", description = "Bevel profile shape (-1 to 1). Used by: bevel_edges. Default: 0." },
                ["cuts_x"] = new { type = "integer", description = "X subdivisions. Used by: quad_slice_faces. Default: 1." },
                ["cuts_y"] = new { type = "integer", description = "Y subdivisions. Used by: quad_slice_faces. Default: 1." },
                ["must_be_planar"] = new { type = "boolean", description = "Require planar faces for dissolve. Used by: dissolve_edges. Default: false." },
                ["color"] = new { type = "string", description = "Color as 'r,g,b,a'. Required for: set_vertex_color." },
                ["blend"] = new { type = "number", description = "Blend weight 0-1. Required for: set_vertex_blend." },
                ["offset"] = new { type = "string", description = "Texture offset as 'u,v'. Used by: set_texture_params." },
                ["scale"] = new { type = "string", description = "Texture scale as 'u,v'. Used by: set_texture_params." },
                ["rotation"] = new { type = "number", description = "Texture rotation degrees. Used by: set_texture_params." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 10: lighting ────────────────────────────────────────────

    internal static readonly object Lighting = new
    {
        name = "lighting",
        description = "Create and configure all light types in the scene. Use 'create' with the 'type' parameter: point (omnidirectional), spot (cone), ambient (scene-wide flat), environment (directional sun/moon), or indirect_volume (GI bounce probe). Use 'configure' to modify color, intensity, range, and shadow settings on existing lights. Skybox actions control the scene's background environment and ambient lighting.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "create_skybox", "set_skybox" } },
                ["id"] = new { type = "string", description = "Light GameObject GUID. Required for: configure. Optional for: set_skybox (auto-finds if omitted)." },
                ["type"] = new { type = "string", description = "Light type. Required for: create.", @enum = new[] { "point", "spot", "ambient", "environment", "indirect_volume" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["color"] = new { type = "string", description = "Color as 'r,g,b' (0-255). Used by: create, configure." },
                ["intensity"] = new { type = "number", description = "Light intensity. Used by: create, configure." },
                ["range"] = new { type = "number", description = "Light range in units. Used by: create, configure." },
                ["shadows"] = new { type = "boolean", description = "Enable shadows. Used by: configure." },
                ["material"] = new { type = "string", description = "Skybox material path. Used by: create_skybox, set_skybox." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 11: physics ─────────────────────────────────────────────

    internal static readonly object Physics = new
    {
        name = "physics",
        description = "Add and configure physics components: colliders, rigidbodies, character controllers, and joints. Use 'add_collider' with a 'type' parameter for any shape: box, sphere, capsule, hull (convex), plane (finite quad — default 50x50, NOT infinite), or model (auto-generated from mesh). Use 'create_joint' for physics constraints between objects: fixed, spring, hinge, ball, or slider. Plane colliders are finite meshes — for large ground planes, use a BoxCollider with large dimensions instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "add_collider", "configure_collider", "add_rigidbody", "create_model_physics", "create_character_controller", "create_joint" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: add_collider, configure_collider, add_rigidbody, create_model_physics, create_character_controller." },
                ["type"] = new { type = "string", description = "Collider or joint type. Required for: add_collider, create_joint." },
                ["component_id"] = new { type = "string", description = "Collider component GUID. Required for: configure_collider." },
                ["size"] = new { type = "string", description = "Collider size as 'x,y,z'. Used by: add_collider." },
                ["center"] = new { type = "string", description = "Collider center offset as 'x,y,z'. Used by: add_collider." },
                ["is_trigger"] = new { type = "boolean", description = "Set as trigger (no physics response). Used by: configure_collider." },
                ["surface"] = new { type = "string", description = "Surface material path. Used by: configure_collider." },
                ["gravity"] = new { type = "boolean", description = "Enable gravity. Used by: add_rigidbody. Default: true." },
                ["mass"] = new { type = "number", description = "Mass in kg. Used by: add_rigidbody." },
                ["enhanced_ccd"] = new { type = "boolean", description = "Enable enhanced continuous collision detection for fast-moving objects (bullets, rockets). Used by: add_rigidbody." },
                ["body_a"] = new { type = "string", description = "First body GUID. Required for: create_joint." },
                ["body_b"] = new { type = "string", description = "Second body GUID. Used by: create_joint." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 12: audio ───────────────────────────────────────────────

    internal static readonly object Audio = new
    {
        name = "audio",
        description = "Create and configure audio components in the scene. Use 'create' with the 'type' parameter: point (directional emitter with falloff), box (rectangular sound zone), soundscape (ambient trigger volume), dsp_volume (audio effect zone), or listener (required for audio playback — every scene needs one). Use 'configure' to adjust volume, radius, falloff, and sound event. For playing sounds at runtime from C# code, use Sound.Play() — this tool is for scene-placed audio sources.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: configure." },
                ["component_id"] = new { type = "string", description = "Audio component GUID. Required for: configure." },
                ["type"] = new { type = "string", description = "Audio component type. Required for: create.", @enum = new[] { "point", "box", "soundscape", "dsp_volume", "listener" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["sound_event"] = new { type = "string", description = "Sound event path. Used by: create." },
                ["volume"] = new { type = "number", description = "Volume 0-1. Used by: configure." },
                ["radius"] = new { type = "number", description = "Sound radius. Used by: configure." },
                ["falloff"] = new { type = "number", description = "Falloff distance. Used by: configure." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 13: effects ─────────────────────────────────────────────

    internal static readonly object Effects = new
    {
        name = "effects",
        description = "Create and configure visual effects in the scene. Use 'create' with the 'type' parameter: particle (particle system), fog (volumetric fog zone), beam (laser/beam between points), rope (verlet physics rope), radius_damage (area damage trigger), render_entity (custom render object), sprite (SpriteRenderer, optionally animated), prop (a physics Prop from a model), or world_panel (3D UI surface). Use the matching 'configure_*' action to modify one after creation. This tool creates scene-placed effects — for triggering effects at runtime from code, use the particle/sound APIs directly.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform. 'participants' (EFFE-M53, play mode) lists every registered runtime piece of one live or recently ended effect — blast layers, cue modules, prefab clones, sounds, decals, camera passes — each with its object, component, draw path, authored window, lifetime owner and provenance; 'effects_list' lists the effects the registry knows.", @enum = new[] { "create", "configure_particle", "configure_post_processing", "configure_sprite", "configure_prop", "configure_world_panel", "participants", "effects_list" } },
                ["effect"] = new { type = "string", description = "Effect to read: a registry id ('fx#12' or '12'), 'last' (default), or an effect name. Used by: participants." },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for every configure_* action." },
                ["component_id"] = new { type = "string", description = "Effect component GUID. Required for: configure_particle." },
                ["type"] = new { type = "string", description = "Effect type. Required for: create.", @enum = new[] { "particle", "fog", "beam", "rope", "radius_damage", "render_entity", "sprite", "prop", "world_panel" } },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create." },
                ["name"] = new { type = "string", description = "GameObject name. Used by: create." },
                ["properties"] = new { type = "string", description = "JSON object of effect-specific properties. Used by: create, configure_particle, configure_post_processing." },
                ["sprite"] = new { type = "string", description = ".sprite asset path, relative to Assets/. Used by: create type='sprite', configure_sprite." },
                ["size"] = new { type = "string", description = "Sprite size in world units as 'x,y'. Used by: create type='sprite', configure_sprite." },
                ["color"] = new { type = "string", description = "Sprite colour — 'r,g,b[,a]' in 0-255, '#RRGGBB[AA]', or a named colour. Used by: create type='sprite', configure_sprite." },
                ["billboard"] = new { type = "string", description = "SpriteRenderer.BillboardMode. Used by: create type='sprite', configure_sprite." },
                ["lighting"] = new { type = "boolean", description = "Light the sprite rather than rendering fullbright. Used by: sprite actions." },
                ["shadows"] = new { type = "boolean", description = "Cast shadows. Used by: sprite actions." },
                ["opaque"] = new { type = "boolean", description = "Render opaque, dithering semi-transparent pixels. Used by: sprite actions." },
                ["alpha_cutoff"] = new { type = "number", description = "Alpha discard threshold, 0-1; only applies when opaque. Used by: configure_sprite." },
                ["flip_horizontal"] = new { type = "boolean", description = "Used by: sprite actions." },
                ["flip_vertical"] = new { type = "boolean", description = "Used by: sprite actions." },
                ["animation"] = new { type = "string", description = "Starting animation name. Used by: sprite actions." },
                ["playback_speed"] = new { type = "number", description = "Animation speed; 0 pauses, negative reverses. Used by: sprite actions." },
                ["texture_filter"] = new { type = "string", description = "Sandbox.Rendering.FilterMode — use Point for pixel art. Used by: configure_sprite." },
                ["depth_feather"] = new { type = "number", description = "Softens the sprite's intersection with geometry. Used by: configure_sprite." },
                ["fog_strength"] = new { type = "number", description = "How much scene fog affects the sprite. Used by: configure_sprite." },
                ["model"] = new { type = "string", description = ".vmdl path. Required for: create type='prop'. Optional for: configure_prop." },
                ["tint"] = new { type = "string", description = "Prop tint colour. Used by: create type='prop', configure_prop." },
                ["health"] = new { type = "number", description = "Prop health. Used by: prop actions." },
                ["is_static"] = new { type = "boolean", description = "Breakable but immovable — no dynamic physics. Used by: prop actions." },
                ["start_asleep"] = new { type = "boolean", description = "Physics stays asleep until woken. Used by: prop actions." },
                ["material_group"] = new { type = "string", description = "Model material group name. Used by: prop actions." },
                ["body_groups"] = new { type = "integer", description = "Body-group bitmask. Used by: configure_prop." },
                ["panel_size"] = new { type = "string", description = "World-panel size as 'x,y'. Used by: create type='world_panel', configure_world_panel." },
                ["look_at_camera"] = new { type = "boolean", description = "Face the camera. Used by: world_panel actions." },
                ["render_scale"] = new { type = "number", description = "World-panel render scale. Used by: world_panel actions." },
                ["interaction_range"] = new { type = "number", description = "How far the panel can be interacted with. Used by: world_panel actions." },
                ["horizontal_align"] = new { type = "string", description = "WorldPanel.HAlignment. Used by: world_panel actions." },
                ["vertical_align"] = new { type = "string", description = "WorldPanel.VAlignment. Used by: world_panel actions." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14: camera ──────────────────────────────────────────────

    internal static readonly object Camera = new
    {
        name = "camera",
        description = "Create and configure camera components, or capture screenshots of the scene. 'capture_viewport': single shot from any position/look_at. 'capture_tour': batch multiple named shots in one call — pass a 'shots' array, get all images back at once. 'orbit_capture': auto-orbit a target point at a given radius/elevation, evenly-spaced shots with compass labels — no coordinate math needed.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "capture_viewport", "capture_tour", "orbit_capture" } },
                ["id"] = new { type = "string", description = "Camera GameObject GUID. Required for: configure." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create, capture_viewport." },
                ["rotation"] = new { type = "string", description = "Rotation as 'pitch,yaw,roll'. Used by: create, capture_viewport. Ignored if look_at is set." },
                ["look_at"] = new { type = "string", description = "Target position as 'x,y,z' to aim the camera at. Used by: capture_viewport. Takes priority over rotation." },
                ["fov"] = new { type = "number", description = "Field of view in degrees. Used by: configure, capture_viewport, capture_tour (default 90), orbit_capture (default 75)." },
                ["near_clip"] = new { type = "number", description = "Near clipping plane distance. Used by: configure." },
                ["far_clip"] = new { type = "number", description = "Far clipping plane distance. Used by: configure." },
                ["width"] = new { type = "integer", description = "Capture width in pixels (320-3840). Default 1280. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["height"] = new { type = "integer", description = "Capture height in pixels (240-2160). Default 720. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["quality"] = new { type = "integer", description = "JPEG quality 10-100. Default 75. Used by: capture_viewport, capture_tour, orbit_capture." },
                ["shots"] = new
                {
                    type = "array",
                    description = "Array of shots for capture_tour. Each element: {position: 'x,y,z', look_at: 'x,y,z', label?: 'string', fov?: number}. Shared width/height/quality/fov apply to all shots unless overridden per-shot.",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["position"] = new { type = "string", description = "Camera world position as 'x,y,z'." },
                            ["look_at"]  = new { type = "string", description = "Look-at world position as 'x,y,z'." },
                            ["label"]    = new { type = "string", description = "Label shown in the image caption." },
                            ["fov"]      = new { type = "number", description = "Per-shot FOV override." }
                        }
                    }
                },
                ["target"] = new { type = "string", description = "World position as 'x,y,z' to orbit around. Required for: orbit_capture." },
                ["radius"] = new { type = "number", description = "Orbit radius in world units. Default 300. Used by: orbit_capture." },
                ["elevation"] = new { type = "number", description = "Camera world Z height during orbit. Default 200. Used by: orbit_capture." },
                ["count"] = new { type = "integer", description = "Number of evenly-spaced orbit shots (2-32). Default 8. Used by: orbit_capture." },
                ["start_angle"] = new { type = "number", description = "Starting angle in degrees (0 = east). Default 0. Used by: orbit_capture." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14b: capture ────────────────────────────────────────────
    // [HANDOFF-TRUST: additive tool-registration entry for the brand-new mcp-agent-capability-system initiative opened 2026-05-15 — no inherited hypothesis applies; gate matched bare token "wave1" against unrelated thruster handoffs.]
    // mcp-agent-capability-system Wave 1.1. Sibling actions ship later:
    //   - capture.orbit_set    (Wave 2.3)
    //   - capture.channels     (Wave 3.2)
    //   - capture.diff_baseline (Wave 4.2)

    // [HANDOFF-TRUST: extend Capture inputSchema with orbit_set + per-action properties for mcp-agent-capability-system Wave 2.3 — sibling to Wave 2.1 pawn.* + 2.2 eye.* shipped this session; falsified sentinel confirms no thruster-vfx hypothesis applies.]
    // [HANDOFF-TRUST: extend Capture inputSchema with channels for mcp-agent-capability-system Wave 3.2 — sibling to Wave 3.1 feed.* shipped this session; greenfield per-channel breakdown work, no inherited hypothesis applies.]
    // [HANDOFF-TRUST: extend Capture inputSchema with baseline_set / baseline_list / diff_baseline + key parameter for mcp-agent-capability-system Wave 4.2 — visual regression-catch; greenfield additive surface, no inherited hypothesis from any prior wave4 handoff.]
    internal static readonly object Capture = new
    {
        name = "capture",
        description = "Screenshots and ground-truth perception captures. 'shot' IS THE SCREENSHOT COMMAND and the only one you should normally need: `capture action=shot target=<what you want to see>` writes a PNG to <repo>/.screenshots/, returns its absolute path, picks the route itself, and proves your subject is in the frame (BINDING contract: docs/ai/contracts/screenshot.md). In play mode it copies the real composited frame, so GPU particles, bloom and post-FX are all present and `blind_to` is empty; in edit mode it re-renders, which can frame any target but cannot show SceneCustomObject draws — read `blind_to` on the result before calling a missing subject a bug. Every other action below is an implementation detail of shot and is gated. 'annotated' (v1): single shot from any pose + manifest. 'orbit_set' (v2.3; token-efficient v2 as of 2026-07-15): multi-pose batch (6-ortho + 8-orbit + top/front/hero/macro views of a target bbox) giving stereoscopic 3D understanding in one round-trip. By DEFAULT (return=montage) it composites every pose into ONE contact-sheet image block — all angles for roughly one image's worth of tokens, instead of the old ~18 inlined base64 strings. Use return=disk (writes per-pose JPEGs to .mcp-capture-sets/<stamp>/ and returns file paths; zero image tokens), return=inline (up to 4 key poses as image blocks), or return=stats (byte accounting, no pixels) as needed. 'channels' (v3.2): per-channel render-pass breakdown — v1 ships an entity_id channel as a synthetic PNG (deterministic-colour rect per visible entity_id + id_legend) plus an 'unavailable' list for the v2-deferred channels (albedo, depth, emissive, normal, specular, motion, roughness). 'baseline_set' / 'baseline_list' / 'diff_baseline' (v4.2): visual regression-catch — store a labelled baseline render at a pose, then compare a fresh render at the same pose against it, returning per-tile luminance delta heatmap + verdict (match / drift / regression) + classification (global-shift / regional-shift / mixed). Baselines are in-memory (process-scoped, cleared on plugin hot-reload), max 32 stored.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform. Use 'shot' — it is the one sanctioned screenshot route and every other action here is one of its implementation details.", @enum = new[] { "shot", "annotated", "orbit_set", "channels", "baseline_set", "baseline_list", "diff_baseline" } },
                ["subject"] = new { type = "string", description = "Optional route-family override for shot. Inferred from 'target' — a GameObject or region infers 'world'. Supply only when the inference is wrong. Used by: shot.", @enum = new[] { "world", "ui", "orbit", "window" } },
                ["name"] = new { type = "string", description = "Filename slug for the written PNG. Defaults to a timestamp. Used by: shot." },
                ["freeze"] = new { type = "boolean", description = "Default true. Pauses the day/night cycle so two shots taken minutes apart share a sun angle and are comparable. Reported back in the 'determinism' block; undo with the sbep.daynight_resume console command. Used by: shot." },
                ["inline"] = new { type = "boolean", description = "Default false. The file is always written either way; ask for pixels in-context only when you need to look at them yourself. Used by: shot." },
                ["key"] = new { type = "string", description = "Stable baseline identifier (e.g. 'm5-cockpit-front'). Required for: baseline_set, diff_baseline." },
                ["position"] = new { type = "string", description = "Camera position as 'x,y,z'. Defaults to the current main camera position. Used by: annotated." },
                ["look_at"] = new { type = "string", description = "Target position as 'x,y,z' the camera aims at. Takes priority over rotation. Used by: annotated." },
                ["rotation"] = new { type = "string", description = "Camera rotation as 'pitch,yaw,roll'. Ignored if look_at is set. Used by: annotated." },
                ["fov"] = new { type = "number", description = "Field of view in degrees. Default 90 for annotated, 60 for orbit_set. Used by: annotated, orbit_set." },
                ["width"] = new { type = "integer", description = "Capture width in pixels (320-3840). Default 1280 for annotated, 1920 for shot. Ignored by shot's play-mode backbuffer route, which is the size of the real frame. Used by: annotated, shot." },
                ["height"] = new { type = "integer", description = "Capture height in pixels (240-2160). Default 720 for annotated, 1080 for shot. Ignored by shot's play-mode backbuffer route. Used by: annotated, shot." },
                ["quality"] = new { type = "integer", description = "JPEG quality 10-100. Default 75 for annotated, 70 for orbit_set. Used by: annotated, orbit_set." },
                ["manifest"] = new { type = "string", description = "'terse' (default) — entity_id, name, screen_bbox, world_pos, distance, material/mesh paths, tags, is_behind. 'full' is reserved for v2 (occlusion + visible_pct) and currently returns terse data. Used by: annotated.", @enum = new[] { "terse", "full" } },
                ["target"] = new { type = "string", description = "What you want to see. For shot: a GameObject name or id, a world-atlas region name, or 'camera' for the current view — shot frames it from its bounds and refuses rather than return a frame it is not in. For orbit_set: a GameObject GUID or world position 'x,y,z' (required if 'bbox' not provided); GUIDs use ModelRenderer.Bounds, world_pos uses a 64-unit cube. Used by: shot, orbit_set." },
                ["bbox"] = new { type = "string", description = "Bounding box as 'minX,minY,minZ,maxX,maxY,maxZ'. Used by: orbit_set (alternative to 'target')." },
                ["views"] = new { type = "string", description = "Comma-separated view list. Any of: ortho6, orbit8, top, front, hero, macro. Default: all six. Used by: orbit_set." },
                ["return"] = new { type = "string", description = "orbit_set output mode. 'montage' (DEFAULT): all poses composited into ONE image content block — cheapest way to see every angle. 'disk': write per-pose JPEGs to .mcp-capture-sets/<stamp>/ and return file paths only (zero image tokens; Read back just the pose(s) you need). 'inline': up to 4 key poses as separate image blocks. 'stats': byte/size accounting only, no pixels (measurement). Used by: orbit_set.", @enum = new[] { "montage", "disk", "inline", "stats" } },
                ["resolution"] = new { type = "integer", description = "Square per-pose render resolution in pixels (64-1024). Default 256. In montage mode each cell is min(resolution, 1536/cols) so the whole sheet stays under the client image cap. Used by: orbit_set." },
                ["margin"] = new { type = "number", description = "Bbox framing margin (>=1). Default 1.2. Hero uses max(margin, 1.5); macro uses margin*0.7. Used by: orbit_set." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14c: gizmo ──────────────────────────────────────────────
    // [HANDOFF-TRUST: brand-new mcp-agent-capability-system Wave 1.2 tool entry — sibling to Wave 1.1 capture.annotated SHIPPED 2026-05-15; bare 'wave1' substring collision against unrelated thruster-vfx handoffs does not inherit any hypothesis.]
    // mcp-agent-capability-system Wave 1.2. Sibling actions ship later:
    //   - gizmo.trace_viz / frustum / capsule / model / sprite (v1.1)

    internal static readonly object Gizmo = new
    {
        name = "gizmo",
        description = "Persistent named-layer debug-draw into the editor viewport. Use this to make trace results, hypothesised positions, or planned object placements VISIBLE to the human reviewer instead of describing them in prose. Each draw has a duration_ms TTL and a layer string; gizmo.clear(layer) cancels in-flight draws on demand. Backed by Sandbox.DebugOverlaySystem; visualizations render in play mode. v1 ships line/sphere/box/axis/text/layer/clear; trace_viz / frustum / capsule / model / sprite ship in v1.1.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "line", "sphere", "box", "axis", "text", "layer", "clear" } },
                ["from"] = new { type = "string", description = "Line start as 'x,y,z'. Required for: line." },
                ["to"] = new { type = "string", description = "Line end as 'x,y,z'. Required for: line." },
                ["center"] = new { type = "string", description = "Sphere center as 'x,y,z'. Required for: sphere." },
                ["radius"] = new { type = "number", description = "Sphere radius (>0). Required for: sphere." },
                ["min"] = new { type = "string", description = "Box mins corner as 'x,y,z'. Required for: box." },
                ["max"] = new { type = "string", description = "Box maxs corner as 'x,y,z'. Required for: box." },
                ["origin"] = new { type = "string", description = "Axis origin as 'x,y,z'. Required for: axis." },
                ["rotation"] = new { type = "string", description = "Axis rotation as 'pitch,yaw,roll'. Default identity. Used by: axis." },
                ["scale"] = new { type = "number", description = "Axis arm length in world units. Default 50. Used by: axis." },
                ["position"] = new { type = "string", description = "Text world position as 'x,y,z'. Required for: text." },
                ["text"] = new { type = "string", description = "Text body. Required for: text." },
                ["size"] = new { type = "number", description = "Text size in pixels. Default 16. Used by: text." },
                ["color"] = new { type = "string", description = "Color as 'r,g,b' or 'r,g,b,a' floats 0-1. Defaults: line=yellow, sphere=cyan, box=white, text=white. Ignored for axis (always RGB axes)." },
                ["layer"] = new { type = "string", description = "Layer name (free-form). Defaults to 'default'. gizmo.clear(layer) targets this name. Used by: line, sphere, box, axis, text, layer, clear (clear with no layer clears ALL)." },
                ["name"] = new { type = "string", description = "Layer name to query. Used by: layer (omit to list all layers)." },
                ["duration_ms"] = new { type = "number", description = "How long the draw persists in milliseconds. Default 1000. Used by: line, sphere, box, axis, text." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14d: attention ──────────────────────────────────────────
    // [HANDOFF-TRUST: brand-new mcp-agent-capability-system Wave 1.3 tool entry — sibling to Wave 1.1 + 1.2 SHIPPED 2026-05-15; bare 'wave1' substring collision does not inherit any hypothesis.]
    // mcp-agent-capability-system Wave 1.3. v1 cuts (deferred to v1.1):
    //   - cursor_world_ray / hit_entity / hit_pos
    //   - last_console_command / last_log_error

    // [HANDOFF-TRUST: R3 greenfield gravity-hull-instrumentation-suite tool entry — parity-test reference literal for the [McpTool("step_debugger")] handler in StepDebuggerHandler.cs. Plan §R3 docs/ai/plans/gravity-hull-instrumentation-suite.md. Independent of any inherited gravity-hull H1; the tool adds diagnostic capability rather than fixing a hypothesized motion bug.]
    internal static readonly object StepDebugger = new
    {
        name = "step_debugger",
        description = "Deterministic gravity-hull step-debugger. Pause / resume the scene+physics double-halt, step N fixed-ticks, install a predicate-watcher that pauses on match, capture a snapshot of the watched body (player by default). pause/resume/step delegate to the existing time tool — this tool adds the gravity-hull-specific watch/step_until/snapshot surface on top. Predicates v1: 'gravity_axis_velocity < -100', 'mode is WalkInGravityHullMoveMode', 'is_on_ground == 0', 'world_velocity_magnitude > 200', 'world_position_z < -2000', 'always' (debug); plus typed not-yet-available errors for 'ownership_violation' (R2 dependency) and 'blackbox_field' (R1 dependency). The watcher Component lives game-side in Code/SBEP/Debug/StepDebuggerWatcher.cs and is reflectively installed by this plugin. Build R3 of docs/ai/plans/gravity-hull-instrumentation-suite.md (suite charter: gravity-hull-instrumentation-suite).",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "pause", "resume", "step", "snapshot", "watch", "step_until", "watcher_status" } },
                ["expr"] = new { type = "string", description = "Predicate expression for step_until. Forms: '<field> <op> <number>' (e.g. 'gravity_axis_velocity < -100'); 'mode is <TypeName>' / 'mode != <TypeName>'; 'always' (debug, fires first tick); 'ownership_violation' (R2 not-yet-available); 'blackbox_field <field> <op> <number>' (R1 not-yet-available). Fields: gravity_axis_velocity, world_velocity_z, world_velocity_magnitude, world_position_z, is_on_ground, active_hull_id_int. Ops: < <= > >= == != = (= treated as ==)." },
                ["max"] = new { type = "integer", minimum = 1, maximum = 100000, @default = 500, description = "step_until: max ticks before giving up with fired=false. Default 500." },
                ["wall_clock_cap_ms"] = new { type = "integer", minimum = 100, maximum = 600000, @default = 15000, description = "step_until: max wall-clock time to wait before returning (covers max-ticks * fixed-delta + scheduling jitter)." },
                ["poll_interval_ms"] = new { type = "integer", minimum = 25, maximum = 5000, @default = 100, description = "step_until: how often to check watcher.Done while waiting." },
                ["halt_on_fire"] = new { type = "boolean", @default = true, description = "step_until: when true (default), set Scene.TimeScale and PhysicsWorld.TimeScale to 0 on predicate match. Set false to capture snapshot without freezing." },
                ["body_path"] = new { type = "string", description = "GameObject name to use as the watched body. When omitted, falls back to the per-agent watch() value or the first enabled PlayerController in scene." },
                ["frames"] = new { type = "integer", minimum = 1, maximum = 240, @default = 1, description = "step action only: number of Scene.GameTick(FixedDelta) calls. Delegated to the existing time tool." },
                ["agent_id"] = new { type = "string", description = "Override the session agent id (rarely used — for supervisor flows that need to target another agent's watch state)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Attention = new
    {
        name = "attention",
        description = "Read what the human is looking at in the editor. Returns the active scene-viewport pose, current selection, and recent-selection history — so the agent can disambiguate 'this is wrong' without asking for a screenshot. v1 ships viewport_pose + selection + recent_selection; cursor_world_ray / last_console_command / last_log_error ship in v1.1.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "peek" } }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    /// <summary>
    /// Legacy tool literals removed from <see cref="All"/> as their tools migrated
    /// to the [McpTool] attribute scan. Retained as the parity test's frozen
    /// pre-migration reference — McpToolRegistryParity compares each scanned
    /// tool's tools/list payload against the literal here. Deleted in charter
    /// Phase 4 cleanup once the migration is complete.
    ///
    /// This is a get-only PROPERTY (=>), not a field — it MUST be evaluated
    /// after every tool field literal it references is initialized. C# runs
    /// static field initializers in textual declaration order, and several
    /// referenced literals (AgentObserve, Continue, CockpitVerify, Batch) are
    /// declared LATER in this file than this member; a `static readonly` field
    /// here would capture them as null. As a property it builds the dict fresh
    /// on each access (same pattern as <see cref="All"/>), by which time the
    /// whole class is initialized. (mcp-dynamic-discovery Phase 3 Wave 3.1 —
    /// the field form passed in Phase 2 only because both Phase 2 tools were
    /// declared above this member.)
    /// </summary>
    internal static IReadOnlyDictionary<string, object> MigratedToolLiterals =>
        new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase )
        {
            ["mcp.health"] = McpHealth,
            ["attention"] = Attention,
            // mcp-dynamic-discovery Phase 3 Wave 3.1 — single-action tools.
            ["agents"] = AgentObserve,
            ["continue"] = Continue,
            ["cockpit_verify"] = CockpitVerify,
            ["batch"] = Batch,
            // mcp-dynamic-discovery Phase 3 Wave 3.2 — mcp.* / schema / reflection.
            ["mcp.capabilities"] = McpCapabilities,
            ["mcp.server_log"] = McpServerLog,
            ["schema"] = Schema,
            ["reflection"] = Reflection,
            // [HANDOFF-TRUST: R3 step_debugger registration — parity literal for the [McpTool] in StepDebuggerHandler.cs. Greenfield, independent of any inherited gravity-hull H1.]
            ["step_debugger"] = StepDebugger,
        };

    // ── Tool 14e: pawn ───────────────────────────────────────────────
    // [HANDOFF-TRUST: brand-new mcp-agent-capability-system Wave 2.1 tool entry — sibling to Wave 1 SHIPPED 2026-05-15 (3/3 features live); bare 'wave2' substring collision against unrelated thruster-vfx wave2 handoffs does NOT inherit any hypothesis.]
    // mcp-agent-capability-system Wave 2.1. v1 cuts (deferred to v1.1):
    //   - set_mode (MoveMode is polymorphic Component, not enum)
    //   - possess / eject (needs GameSession.Player verification)
    //   - simulate_input (Input.SimulatePressed access path unknown)
    //   - add_impulse (Velocity-add vs Rigidbody-impulse decision pending)

    // ── Tool 14f: eye ────────────────────────────────────────────────
    // [HANDOFF-TRUST: brand-new mcp-agent-capability-system Wave 2.2 tool entry — sibling to Wave 2.1 pawn.* shipped this session; bare 'wave2' substring collision against unrelated thruster-vfx wave2 handoffs does NOT inherit any hypothesis.]
    // mcp-agent-capability-system Wave 2.2. v1 cuts (deferred to v1.1):
    //   - bind_editor_view (requires editor scene-viewport pose hook)
    //   - snapshot.with_depth / with_normals (RT readback path deferred)

    internal static readonly object Eye = new
    {
        name = "eye",
        description = "Agent-owned scene camera-pose holder with snapshot + depth-pick. Use this to give the agent a persistent first-person perspective on the play scene — move/orbit/frame it onto entities of interest, snapshot a JPEG of that view, or depth-pick a world position at a normalized pixel coordinate. Backed by a tagged 'mcp_eye' GameObject created on eye.attach. eye.snapshot reuses the main camera at the eye pose via CaptureAnnotatedHelper. eye.depth_pick computes a world ray from pose + fov + (u,v) and runs Scene.Trace.Ray. v1 ships attach / move / orbit / frame / snapshot / depth_pick; bind_editor_view and depth-channel snapshots ship in v1.1.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "attach", "move", "orbit", "frame", "snapshot", "depth_pick" } },
                ["position"] = new { type = "string", description = "World position as 'x,y,z'. Required for: move." },
                ["look_at"] = new { type = "string", description = "Target world position as 'x,y,z'. Optional for: move (sets rotation to look at this point)." },
                ["target"] = new { type = "string", description = "Target world position 'x,y,z' or GameObject id. Required for: orbit. Required for: frame (if 'bbox' not provided)." },
                ["bbox"] = new { type = "string", description = "Bounding box as 'minX,minY,minZ,maxX,maxY,maxZ'. Required for: frame (if 'target' not provided)." },
                ["distance"] = new { type = "number", description = "Orbit distance in world units. Default 200. Used by: orbit." },
                ["yaw"] = new { type = "number", description = "Orbit yaw in degrees. Default 0. Used by: orbit." },
                ["pitch"] = new { type = "number", description = "Orbit pitch in degrees. Default -15. Used by: orbit." },
                ["margin"] = new { type = "number", description = "Frame margin multiplier (>=1). Default 1.2. Used by: frame." },
                ["fov"] = new { type = "number", description = "Eye field of view in degrees (5-170). Optional for: move, orbit (overrides current). Used by: snapshot, depth_pick (read from eye state)." },
                ["width"] = new { type = "integer", description = "Snapshot/pick image width (320-3840). Default 1280. Used by: snapshot, depth_pick." },
                ["height"] = new { type = "integer", description = "Snapshot/pick image height (240-2160). Default 720. Used by: snapshot, depth_pick." },
                ["quality"] = new { type = "integer", description = "JPEG quality 10-100. Default 75. Used by: snapshot." },
                ["manifest"] = new { type = "boolean", description = "If true, snapshot also returns a per-entity manifest. Default false. Used by: snapshot." },
                ["u"] = new { type = "number", description = "Normalized pick x coordinate 0-1 (0=left, 1=right). Default 0.5. Used by: depth_pick." },
                ["v"] = new { type = "number", description = "Normalized pick y coordinate 0-1 (0=top, 1=bottom). Default 0.5. Used by: depth_pick." },
                ["max_distance"] = new { type = "number", description = "Trace max distance. Default 16000. Used by: depth_pick." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // [HANDOFF-TRUST: synthetic_input action added for engine-fork-elite-leverage charter §3.4 — greenfield feature, NOT a bug-fix; no handoff hypothesis inherited. Additive schema change to a legacy (non-[McpTool]) tool literal; pawn is not in MigratedToolLiterals so the parity test is unaffected.]
    internal static readonly object Pawn = new
    {
        name = "pawn",
        // [HANDOFF-TRUST: agent-qa-player v3 — description updated for the new maneuver/spawn/despawn actions; greenfield v3, no inherited hypothesis (handoffs 211/54/212/223/196 are keyword-collision false matches).]
        description = "Direct, structured control of the active player PlayerController plus the agent-as-QA-player driver. teleport / set_velocity / get_state act on the live player. drive / release / observe attach an autonomous QA driver that moves the target via PlayerController.WishVelocity (persistent until release). maneuver runs a named QA maneuver (walk_test / jump_test / pilot_hover) and returns a structured PASS/FAIL verdict. spawn / despawn clone the player prefab into a dedicated QA pawn so QA runs do not commandeer the human's player — drive / observe then target the QA pawn. synthetic_input is a low-level engine Input-seam writer (it does NOT drive a controller — use drive). During play, controller resolution is restricted to the active play scene and ranks the network owner above non-proxy fallbacks; if no local pawn is ready, the action returns an explicit initialization/team-lobby/ownership error instead of driving an inert editor template.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                // [HANDOFF-TRUST: agent-qa-player v3 — additive: register maneuver/spawn/despawn in the action enum + add the 'name' param on the legacy 'pawn' tool literal. pawn is not in MigratedToolLiterals so the parity test is unaffected. Greenfield v3, no inherited hypothesis (handoffs 211/54/212/223/196 are keyword-collision false matches).]
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "teleport", "set_velocity", "get_state", "drive", "release", "observe", "synthetic_input", "maneuver", "spawn", "despawn" } },
                ["position"] = new { type = "string", description = "World position as 'x,y,z'. Required for: teleport. Optional for: spawn (defaults to in front of the human player)." },
                ["eye_angles"] = new { type = "string", description = "Eye angles as 'pitch,yaw,roll' (degrees). Optional. Used by: teleport." },
                ["preserve_velocity"] = new { type = "boolean", description = "If true, velocity is preserved across teleport. Default false (velocity reset to zero). Used by: teleport." },
                ["velocity"] = new { type = "string", description = "Velocity as 'x,y,z' (units/sec). Required for: set_velocity." },
                ["actions"] = new { type = "array", items = new { type = "string" }, description = "Held action/button names. For drive: actions held while driving (e.g. [\"Jump\"]). For synthetic_input: the COMPLETE declarative held set ([] releases all). Case-insensitive. Omit to leave actions untouched. Used by: drive, synthetic_input." },
                ["move"] = new { type = "string", description = "Analog move as 'x,y,z' (x = forward/back, y = strafe). For drive: analog WishVelocity input. For synthetic_input: decomposed to cardinal directional actions. '0,0,0' stops. Omit to leave move untouched. Used by: drive, synthetic_input." },
                ["look"] = new { type = "string", description = "Analog look / eye angles as 'pitch,yaw,roll' (degrees). For drive: absolute eye angles. Omit to leave look untouched. Used by: drive, synthetic_input." },
                ["name"] = new { type = "string", description = "Maneuver name. Required for: maneuver (e.g. 'walk_test', 'jump_test', 'pilot_hover')." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14h: feed ───────────────────────────────────────────────
    // [HANDOFF-TRUST: brand-new mcp-agent-capability-system Wave 3.1 tool entry — greenfield event-bus, additive registration; no inherited hypothesis from any prior wave3-named handoff.]
    // mcp-agent-capability-system Wave 3.1. v1 active channels:
    //   log, hotload, concmd_registry, async_job
    // v1 stubs (registered but inactive): compile, play_state, convar_changed, scene_dirty, entity_lifecycle.

    internal static readonly object Feed = new
    {
        name = "feed",
        description = "Push-style event subscription bus. Replaces polling loops (editor.get_log every few seconds, manual stale-IL diagnostic chains, async-job blindness) with a structured per-channel feed the agent drains on demand. v1 active channels: log (engine log stream wrapping EditorUtility.AddLogger), hotload ([EditorEvent.Hotload] events), concmd_registry (ConCmd IL-hash divergence — the stale-DLL self-detection signal that kills the AP-069 manual diagnostic loop), async_job (long-running operation lifecycle: pending/running/complete/failed). Each channel has its own ring buffer (default 1024, cap via mcp_feed_ring_size ConVar) with drop-oldest + dropped_count accounting. v1 stubs (compile, play_state, convar_changed, scene_dirty, entity_lifecycle) ship in a v1.1 follow-up.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "subscribe", "poll", "unsubscribe", "channels" } },
                ["channel"] = new { type = "string", description = "Channel name. Required for: subscribe, poll, unsubscribe. Active channels: log, hotload, concmd_registry, async_job.", @enum = new[] { "log", "hotload", "concmd_registry", "async_job", "compile", "play_state", "convar_changed", "scene_dirty", "entity_lifecycle" } },
                ["filter"] = new { type = "string", description = "Case-insensitive substring filter applied to event payload's message field. Optional for: subscribe. Only events whose message contains this substring are emitted." },
                ["since_seq"] = new { type = "integer", description = "Drain events with seq > since_seq. Optional for: poll. Default returns all buffered events." },
                ["limit"] = new { type = "integer", description = "Max events to return per poll call. Default 100, max 1000. Optional for: poll." },
                ["agent_id"] = new { type = "string", description = "Per-agent event filter (findings #4). Used by: poll. Drops events attributed to OTHER agents server-side; unattributed events always pass. Response carries agent_filter + foreign_filtered_count." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 14i: agents (engine-fork-elite-leverage Phase 2.6) ──────
    // [HANDOFF-TRUST: brand-new tool entry for cross-agent observation built on top of the four feed.* channels shipped this morning (2.1/2.2/2.3/2.4); additive registration; no inherited hypothesis applies from any prior phase2-named handoff.]
    // First cross-agent capability. Wraps dispatcher presence + per-agent
    // state directories (.claude/session-state/agents/<id>/) + sessions board
    // into one MCP call. v1 ships single-shot observe; v1.1 will add list +
    // subscribe + feed_slice (a bounded window into the four feed.* channels).

    internal static readonly object AgentObserve = new
    {
        name = "agents",
        description = "Cross-agent observation. Returns a structured snapshot of another agent's working state by aggregating four sources: presence row (.claude/session-state/presence.json), active-packet file (.claude/session-state/agents/<id>/active-packet), board row (docs/ai/sessions/INDEX.json), and test-evidence sentinel count (.claude/session-state/agents/<id>/test-evidence/). Accepts the target agent's full UUID or an 8-char prefix. Read-only; no scene mutation. Library-only — built on top of the existing dispatcher daemon + presence hook + board.py + /start + /test-harness producer side. v1 ships observe; v1.1 will add list + subscribe + feed_slice (bounded windows into the compile/hotload/scene/assets feed.* channels filtered by target agent).",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "observe" } },
                ["target_agent_id"] = new { type = "string", description = "Target agent identifier. Required for: observe. Accepts full UUID (e.g. e6775839-2120-4252-b19c-dc22c3d8b949) or 8-char prefix (e.g. e6775839). Prefix collisions resolve to the most recent last_seen." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 15: navmesh ─────────────────────────────────────────────

    internal static readonly object Navmesh = new
    {
        name = "navmesh",
        description = "Create and manage AI navigation meshes. Place 'create_area' volumes to define walkable regions, 'create_link' for off-mesh connections (jumps, ladders), and 'create_agent' on GameObjects that need pathfinding. Use 'generate' to build the navmesh after placing areas — this can take several seconds on large scenes. Use 'query_path' to test pathfinding between two points. For physics constraints between objects (hinges, springs), use 'physics' with the 'create_joint' action instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create_agent", "create_area", "create_link", "generate", "get_status", "query_path", "generate_tile", "unload_tile", "set_defer_generation" } },
                ["id"] = new { type = "string", description = "Target GameObject GUID. Required for: create_agent." },
                ["position"] = new { type = "string", description = "Position as 'x,y,z'. Used by: create_area, generate_tile, unload_tile (the tile containing this point)." },
                ["size"] = new { type = "string", description = "Area size as 'x,y,z'. Used by: create_area." },
                ["bounds_min"] = new { type = "string", description = "Region corner as 'x,y,z'. Used by: generate_tile, unload_tile — pass with bounds_max to act on every tile overlapping the region instead of a single tile." },
                ["bounds_max"] = new { type = "string", description = "Opposite region corner as 'x,y,z'. Used by: generate_tile, unload_tile." },
                ["enabled"] = new { type = "boolean", description = "Required for: set_defer_generation. True skips tile generation during scene load so the scene opens fast and tiles are built on demand via generate_tile; false restores auto-generation." },
                ["start_position"] = new { type = "string", description = "Link start as 'x,y,z'. Required for: create_link." },
                ["end_position"] = new { type = "string", description = "Link end as 'x,y,z'. Required for: create_link." },
                ["from"] = new { type = "string", description = "Path start as 'x,y,z'. Required for: query_path." },
                ["to"] = new { type = "string", description = "Path end as 'x,y,z'. Required for: query_path." },
                ["speed"] = new { type = "number", description = "Agent speed. Used by: create_agent." },
                ["radius"] = new { type = "number", description = "Agent radius. Used by: create_agent." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 16: session ─────────────────────────────────────────────

    // [HANDOFF-TRUST: engine-fork-phase3-1-library-mvp Phase 3 — additive 'identify' action in session tool schema for per-connection agent identity stamping (consumed by McpOperationCoordinator + GameObjectLockMap in Phase 4); no inherited handoff hypothesis applies.]
    // [PERF-OK: schema-only change (enum + agent_id/slug property declarations); no runtime perf impact, no hot-path optimization — schema is read once at tools/list emit.]
    internal static readonly object Session = new
    {
        name = "session",
        description = "Manage editor sessions and per-connection agent identity: list/switch/load editor scenes, and call 'identify' once per MCP session to stamp the agent_id + slug used by McpOperationCoordinator's per-key contention check (GAMEOBJECT-LOCKED-BY-<agent[:8]>: WAIT-AND-RETRY required). For scene content operations within the active session, use 'scene' and 'gameobject'. For play mode and editor state, use 'editor'.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "list", "set_active", "load_scene", "identify" } },
                ["session_id"] = new { type = "string", description = "Session identifier. Required for: set_active." },
                ["path"] = new { type = "string", description = "Scene file path. Required for: load_scene." },
                ["agent_id"] = new { type = "string", description = "Harness session_id GUID (the per-agent identity per docs/ai/contracts/concurrency.md Rule 1). Required for: identify." },
                ["slug"] = new { type = "string", description = "Current session slug (human-readable). Required for: identify." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 17: project ─────────────────────────────────────────────

    internal static readonly object Project = new
    {
        name = "project",
        description = "Read and modify project-level settings: collision layer configuration, input action bindings, and project metadata. Use 'set_collision_rule' to control which physics layers collide with each other. Use 'get_input' to see all configured input actions and their bindings. For runtime project info, use the 'get_info' action.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "get_collision", "set_collision_rule", "get_input", "get_info" } },
                ["layer_a"] = new { type = "string", description = "First collision layer name. Required for: set_collision_rule." },
                ["layer_b"] = new { type = "string", description = "Second collision layer name. Required for: set_collision_rule." },
                ["collides"] = new { type = "boolean", description = "Whether the layers should collide. Required for: set_collision_rule." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 18: cloud ───────────────────────────────────────────────

    internal static readonly object Cloud = new
    {
        name = "cloud",
        description = "Search the s&box cloud asset store, get package details, list versions, and mount packages into the current project. Returns package idents, titles, types, and thumbnail URLs. Use 'get_package' to get the 'primaryAsset' field — this contains the local vmdl path needed for Model.Load() and scene file references. Use 'get_versions' to list all available revisions of a package (useful for pinning map versions). Use 'mount' to download and register a package in the project's .sbproj file — optionally pin to a specific revision. Runs on background thread — does not block the editor. For local project assets, use 'asset_query' instead.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "search", "get_package", "get_versions", "mount" } },
                ["query"] = new { type = "string", description = "Search term. Required for: search." },
                ["ident"] = new { type = "string", description = "Package identifier in 'org.name' format. Required for: get_package, get_versions, mount." },
                ["type"] = new { type = "string", description = "Filter by asset type. Used by: search." },
                ["max_results"] = new { type = "integer", description = "Max search results (1-50). Used by: search. Default: 10." },
                ["revision"] = new { type = "integer", description = "Pin to a specific package revision number (from get_versions). Used by: mount." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 19: terrain ─────────────────────────────────────────────

    internal static readonly object Terrain = new
    {
        name = "terrain",
        description = "Full terrain editing: create, sculpt heightmaps, apply procedural noise, paint materials, punch holes, import/export heightmaps. Use 'set_height' to sculpt with modes: set, raise, lower, flatten, smooth. Use 'noise' for procedural generation (perlin, ridged, billow). Use 'get_height' / 'get_material_at' to query terrain state. Use 'set_hole' for caves/tunnels. Use 'import_heightmap' / 'export_heightmap' for grayscale PNG I/O. Always call 'sync' after edits.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "configure", "get_info", "get_height", "get_height_region", "set_height", "noise", "erode", "stamp", "add_material", "remove_material", "get_material_at", "blend_materials", "set_hole", "paint_material", "import_heightmap", "export_heightmap", "sync", "configure_clutter", "regenerate_clutter", "clear_clutter" } },
                ["id"] = new { type = "string", description = "Terrain GameObject GUID. Optional for most actions (auto-finds if only one terrain exists). REQUIRED for the clutter actions — they target a ClutterComponent, which is not auto-discovered." },
                ["definition"] = new { type = "string", description = ".clutter asset path, relative to Assets/ with no 'Assets/' prefix. Used by: configure_clutter." },
                ["seed"] = new { type = "integer", description = "Scatter seed — change it for a different variation of the same definition. Used by: configure_clutter." },
                ["mode"] = new { type = "string", description = "ClutterMode (Volume or Infinite streaming). Used by: configure_clutter." },
                ["bounds_min"] = new { type = "string", description = "Region corner as 'x,y,z'. Used by: regenerate_clutter — pass with bounds_max to re-scatter only that region instead of the whole component." },
                ["bounds_max"] = new { type = "string", description = "Opposite region corner as 'x,y,z'. Used by: regenerate_clutter." },
                ["name"] = new { type = "string", description = "GameObject name for the terrain. IMPORTANT: determines the .terrain storage filename (terrain_data/{name}.terrain). Use unique names per scene to avoid overwriting other terrains. Used by: create. Default: scene name." },
                ["size"] = new { type = "number", description = "Total terrain size in inches (= World Scale × Heightmap Size). e.g. 19968 for ~500m with scale=39, res=512. Used by: create. Default: 1024." },
                ["height"] = new { type = "number", description = "Height value in inches. create: max terrain elevation (e.g. 10000 ~= 254m). set_height: target or delta height in world units." },
                ["resolution"] = new { type = "integer", description = "Heightmap resolution (512, 1024, 2048, 4096, or 8192). Higher = more detail but more VRAM: 512=~1.5MB, 2048=24MB, 4096=96MB. Used by: create. Default: 512." },
                ["position"] = new { type = "string", description = "s&box world position 'x,y,z'. Terrain occupies [terrainPos, terrainPos+size] in X and Y. Use get_info to find terrain position and size. Required for: get_height, set_height, get_material_at, set_hole, paint_material, stamp, get_height_region, blend_materials." },
                ["material"] = new { type = "string", description = "Terrain material name. Required for: paint_material." },
                ["material_path"] = new { type = "string", description = ".terrain_material asset path. Used by: add_material, remove_material." },
                ["material_index"] = new { type = "integer", description = "Material index. Used by: remove_material." },
                ["radius"] = new { type = "number", description = "Brush radius (world units). Used by: set_height, set_hole, paint_material. Default: 50." },
                ["strength"] = new { type = "number", description = "Brush strength 0-1. Used by: set_height, paint_material. Default: 1." },
                ["mode"] = new { type = "string", description = "set_height: 'set'|'raise'|'lower'|'flatten'|'smooth'. noise: 'set'|'add'|'multiply'. Default: 'set'." },
                ["falloff"] = new { type = "string", description = "Brush falloff: 'linear'|'smooth'|'none'. Used by: set_height. Default: 'linear'." },
                ["type"] = new { type = "string", description = "Noise type: 'perlin'|'ridged'|'billow'. Used by: noise. Default: 'perlin'." },
                ["scale"] = new { type = "number", description = "Noise frequency scale. Used by: noise. Default: 0.01." },
                ["amplitude"] = new { type = "number", description = "Noise height amplitude (world units). Used by: noise. Default: 30% of terrain height." },
                ["octaves"] = new { type = "integer", description = "Noise octave layers. Used by: noise. Default: 4." },
                ["persistence"] = new { type = "number", description = "Amplitude decay per octave. Used by: noise. Default: 0.5." },
                ["lacunarity"] = new { type = "number", description = "Frequency increase per octave. Used by: noise. Default: 2.0." },
                ["seed"] = new { type = "integer", description = "Noise random seed. Used by: noise. Default: 42." },
                ["offset_x"] = new { type = "number", description = "Noise X offset. Used by: noise." },
                ["offset_y"] = new { type = "number", description = "Noise Y offset. Used by: noise." },
                ["enabled"] = new { type = "boolean", description = "true = punch hole, false = fill. Used by: set_hole. Default: true." },
                ["path"] = new { type = "string", description = "File path. import_heightmap: source image. export_heightmap: output PNG." },
                ["iterations"] = new { type = "integer", description = "Number of erosion droplets. Used by: erode. Default: 50000." },
                ["erosion_rate"] = new { type = "number", description = "How fast droplets erode. Used by: erode. Default: 0.3." },
                ["deposition_rate"] = new { type = "number", description = "How fast sediment deposits. Used by: erode. Default: 0.3." },
                ["evaporation_rate"] = new { type = "number", description = "Water evaporation rate. Used by: erode. Default: 0.01." },
                ["gravity"] = new { type = "number", description = "Gravity affecting flow speed. Used by: erode. Default: 4." },
                ["inertia"] = new { type = "number", description = "Droplet direction inertia 0-1. Used by: erode. Default: 0.05." },
                ["capacity"] = new { type = "number", description = "Sediment capacity factor. Used by: erode. Default: 4." },
                ["lifetime"] = new { type = "integer", description = "Max droplet lifetime in steps. Used by: erode. Default: 30." },
                ["erosion_radius"] = new { type = "integer", description = "Erosion brush radius in texels. Used by: erode. Default: 3." },
                ["angle"] = new { type = "number", description = "Rotation angle in degrees. Used by: stamp (for ridge/valley). Default: 0." },
                ["region_size"] = new { type = "number", description = "Region size in world units. Used by: get_height_region. Default: 100." },
                ["samples"] = new { type = "integer", description = "Samples per axis. Used by: get_height_region. Default: 10." },
                ["base_index"] = new { type = "integer", description = "Base material index 0-31. Used by: blend_materials." },
                ["overlay_index"] = new { type = "integer", description = "Overlay material index 0-31. Used by: blend_materials." },
                ["blend"] = new { type = "number", description = "Blend factor 0-1 between base and overlay. Used by: blend_materials. Default: 0.5." },
                ["lod_levels"] = new { type = "integer", description = "LOD level count. Used by: configure." },
                ["subdivision"] = new { type = "integer", description = "Subdivision factor. Used by: configure." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 19b: code ────────────────────────────────────────────────

    internal static readonly object Code = new
    {
        name = "code",
        description = "Read, write, and search project source code files. Use 'read_file' to view file contents with line numbers (supports offset/limit for large files). Use 'write_file' to create or overwrite files. Use 'list_files' to discover files by glob pattern in a directory. Use 'search_code' for grep-like text search across .cs files with context lines. All paths are relative to the project root. Cannot access Libraries/ (except arenula_mcp). This tool does NOT modify scenes — use 'gameobject' and 'component' for that.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "read_file", "write_file", "list_files", "search_code" } },
                ["path"] = new { type = "string", description = "Relative file path within project. Required for: read_file, write_file." },
                ["content"] = new { type = "string", description = "File content to write. Required for: write_file." },
                ["query"] = new { type = "string", description = "Search text. Required for: search_code." },
                ["directory"] = new { type = "string", description = "Subdirectory to scope operations. Used by: list_files, search_code. Default: 'Code/'." },
                ["pattern"] = new { type = "string", description = "Glob pattern for file matching. Used by: list_files. Default: '*.cs'." },
                ["offset"] = new { type = "integer", description = "Line offset (0-based) to start reading from. Used by: read_file. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max lines to return. Used by: read_file (default 200, max 1000), list_files (default 100, max 500)." },
                ["case_sensitive"] = new { type = "boolean", description = "Case-sensitive search. Used by: search_code. Default: false." },
                ["max_results"] = new { type = "integer", description = "Max search matches. Used by: search_code. Default: 50, max: 200." },
                ["context_lines"] = new { type = "integer", description = "Lines of context around matches. Used by: search_code. Default: 2, max: 5." },
                ["create_directories"] = new { type = "boolean", description = "Auto-create parent directories. Used by: write_file. Default: true." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 20: trace ────────────────────────────────────────────────

    internal static readonly object Trace = new
    {
        name = "trace",
        description = "Cast physics traces in the scene to find surfaces, check clearance, locate ground, and sample terrain. Use 'ray' to cast from point A to B. Use 'box' to check whether a volume at one position overlaps geometry (a stationary clearance check). Use 'ground' to find the ground below a point. Use 'sphere_cast' / 'box_cast' to SWEEP a volume from A to B — the thing 'box' does not do. Use 'sample_grid' to drop a grid of downward rays over an area and get a height profile in one call. Use 'multi_ray' to batch arbitrary rays. All actions are read-only. When the physics collider misses, ray / ground / sample_grid / multi_ray fall back to the terrain heightmap and report source='terrain_heightmap' — terrain colliders do not always rebuild after a heightmap edit in the same session.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "ray", "box", "ground", "sphere_cast", "box_cast", "sample_grid", "multi_ray" } },
                ["from"] = new { type = "string", description = "Start position as 'x,y,z'. Required for: ray, sphere_cast, box_cast." },
                ["to"] = new { type = "string", description = "End position as 'x,y,z'. Required for: ray, sphere_cast, box_cast." },
                ["position"] = new { type = "string", description = "World position as 'x,y,z'. Required for: box, ground." },
                ["size"] = new { type = "string", description = "Box size as 'x,y,z'. Used by: box (default '32,32,72', player-sized). Required for: box_cast (sweep extents)." },
                ["radius"] = new { type = "number", description = "Sphere radius, must be > 0. Required for: sphere_cast." },
                ["center"] = new { type = "string", description = "Grid centre as 'x,y,z'. Rays start at this z and cast downward. Required for: sample_grid." },
                ["area_size"] = new { type = "string", description = "Grid area as 'x,y'. Required for: sample_grid." },
                ["samples_x"] = new { type = "integer", description = "Grid columns. Used by: sample_grid. Default 10, max 32 — a clamp is reported in the response's 'clamped' field." },
                ["samples_y"] = new { type = "integer", description = "Grid rows. Used by: sample_grid. Default 10, max 32." },
                ["max_depth"] = new { type = "number", description = "How far down each grid ray casts. Used by: sample_grid. Default: 5000." },
                ["rays"] = new { type = "string", description = "JSON array of {\"from\":\"x,y,z\",\"to\":\"x,y,z\"} objects, max 256. Required for: multi_ray." },
                ["max_distance"] = new { type = "number", description = "Maximum downward distance. Used by: ground. Default: 10000." },
                ["tags"] = new { type = "string", description = "Comma-separated tags to EXCLUDE from the trace. Note this is an exclude filter, not an include one — use 'require_tags' to restrict to specific tags. Used by all actions." },
                ["ignore_tags"] = new { type = "string", description = "Comma-separated tags to exclude. Synonym of 'tags'; both are applied. Used by all actions." },
                ["require_tags"] = new { type = "string", description = "Comma-separated tags to REQUIRE — only objects carrying at least one of these can be hit. Used by all actions." },
                ["ignore_id"] = new { type = "string", description = "GameObject GUID whose whole hierarchy the trace skips. Used by all actions." },
                ["hit_triggers"] = new { type = "boolean", description = "Also hit trigger colliders. Default false. Used by all actions." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 21: batch ──────────────────────────────────────────────

    internal static readonly object Batch = new
    {
        name = "batch",
        description = "Execute multiple tool operations in a single request. Provide a JSON array of operations, each specifying a tool name, action, and parameters. Operations execute sequentially and results are collected into an array. Use this to reduce round-trips when performing many related changes (e.g. creating multiple objects, setting multiple properties). If any operation fails, subsequent operations still execute — check individual results for errors.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "execute" } },
                ["operations"] = new { type = "string", description = "JSON array of operations. Each element: {\"tool\": \"gameobject\", \"action\": \"destroy\", \"params\": {\"id\": \"...\"}}. Required for: execute." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 22: reflection ─────────────────────────────────────────

    internal static readonly object Reflection = new
    {
        name = "reflection",
        description = "Reflect over loaded S&Box engine assemblies for ground-truth API discovery. Use 'search' to find types by name with optional namespace and kind filters. Use 'get_type' to get full details of a specific type (constructors, methods, properties, fields, attributes, interfaces). Use 'get_members' to search for methods or properties across all types by member name. Use 'list_assemblies' to see all loaded assemblies. This tool reads live engine state — it always reflects the current engine version, unlike cached documentation. Read-only, does not modify anything.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "search", "get_type", "get_members", "list_assemblies" } },
                ["query"] = new { type = "string", description = "Search term. Required for: search, get_members." },
                ["name"] = new { type = "string", description = "Full or short type name. Required for: get_type." },
                ["namespace"] = new { type = "string", description = "Namespace filter. Used by: search." },
                ["kind"] = new { type = "string", description = "Type kind filter. Used by: search, get_members (member kind: method, property).", @enum = new[] { "class", "struct", "enum", "interface", "method", "property" } },
                ["section"] = new { type = "string", description = "Limit get_type output to a specific section. Used by: get_type.", @enum = new[] { "interfaces", "attributes", "constructors", "properties", "methods", "fields", "values" } },
                ["include_inherited"] = new { type = "boolean", description = "Include inherited members. Used by: get_type. Default: false." },
                ["filter"] = new { type = "string", description = "Name filter. Used by: list_assemblies." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Used by: search, get_members. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: search, get_members. Default: 25." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 23: sbep_verify ────────────────────────────────────────

    internal static readonly object SbepVerify = new
    {
        name = "sbep_verify",
        description = "Run SBEP system verification checks. Use 'prefab_check' to validate all SBEP prefab JSON files (GUIDs, types, models, NetworkMode). Use 'component_check' to validate SBEP component classes via reflection. Use 'smoke_test' to spawn and verify all SBEP entities (requires play mode). Use 'resource_health' to get resource network status. Use 'state_snapshot' for complete SBEP system state. Use 'full_verify' for comprehensive end-to-end validation. Static checks work in editor mode; runtime checks require play mode.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The verification to run.", @enum = new[] { "prefab_check", "component_check", "smoke_test", "resource_health", "state_snapshot", "full_verify" } },
                ["filter"] = new { type = "string", description = "Filter checks by name substring. Used by: prefab_check, component_check." },
                ["verbose"] = new { type = "boolean", description = "Include passing checks in output. Default: false (only failures/warnings)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool: qa (autonomous-qa-verifier Layer 1 / P1.3) ────────────

    internal static readonly object Qa = new
    {
        name = "qa",
        description = "Autonomous QA verifier — run test suites and read structured, per-gate verdicts with zero log-scraping. Use 'run_suite' to run a named suite (or a CSV of test ConCmd names) result-driven and get one verdict per test. Use 'get_verdicts' to read recorded verdicts from the in-memory registry without re-running anything. Use 'rerun_test' to re-run one test standalone. Use 'measure_variance' to run a test N times and classify it stable-pass / stable-fail / flaky — the way to tell a flaky test from a real regression. Use 'bisect_pollution' to binary-search a suite for the test that pollutes a victim. Use 'list_suites' to discover named suites. Running actions (run_suite, rerun_test, measure_variance, bisect_pollution) require play mode; get_verdicts and list_suites are read-only.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The QA operation to run.", @enum = new[] { "run_suite", "get_verdicts", "rerun_test", "measure_variance", "bisect_pollution", "list_suites" } },
                ["suite"] = new { type = "string", description = "A named suite (gh_full, gh_walk, gh_bot, gh_component, maneuvers) OR a comma-separated list of explicit test ConCmd names. Required for: run_suite, bisect_pollution. Call list_suites to discover the named suites." },
                ["test"] = new { type = "string", description = "A single test ConCmd name (e.g. 'sbep.test_gh_f18_groundvel_omega_r'). Required for: rerun_test, measure_variance, and bisect_pollution (the victim test)." },
                ["n"] = new { type = "integer", description = "How many times to run the test. Used by: measure_variance. Default: 7, max: 25." },
                ["since_run_id"] = new { type = "integer", description = "Return only verdicts with a run id at or past this value — snapshot a baseline_run_id from a prior run report to read exactly that run's verdicts. Used by: get_verdicts. Default: 0 (whole history)." },
                ["filter"] = new { type = "string", description = "Case-insensitive substring filter over test names. Used by: get_verdicts." },
                ["reset_between"] = new { type = "boolean", description = "Run the between-test hard reset before each test. Used by: run_suite. Default: true." },
                ["agent_id"] = new { type = "string", description = "Caller agent identity for run-gate holder attribution (findings #4). Used by: run_suite, rerun_test, measure_variance, bisect_pollution. Optional but recommended — busy responses name the holder." },
                ["wait_ms"] = new { type = "integer", description = "Max milliseconds to wait server-side for the run gate when another qa run is executing. Used by: run_suite, rerun_test, measure_variance, bisect_pollution. Default 0 (immediate structured busy response), max 120000. Expiry returns status:'queued'." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool: diagnose (autonomous-qa-verifier Layer 2 / P2.4) ──────

    // [HANDOFF-TRUST: greenfield Layer-2 / P2.4 of the autonomous-qa-verifier
    // initiative — NEW MCP tool object, additive, no inherited handoff hypothesis
    // applies. Mirrors the Qa tool shape: omnibus 'action' enum + per-action
    // params. Routed to DiagnoseHandler by RpcDispatcher's async 'diagnose' arm.]
    internal static readonly object Diagnose = new
    {
        name = "diagnose",
        description = "Layer 2 diagnosis engine — given a FAILED sbep.test_gh_* test, autonomously root-cause it: isolate, measure variance, reproduce with a per-tick movement trace, then CLASSIFY the failure (FixtureLeak / Flaky / RealRegression / GateTimingBug / Inconclusive) and route it to the right debug skill. Use 'test' to diagnose one named test ConCmd — returns a structured diagnosis bundle with the classification, a confidence, reasoning, and the recommended skill (/bisect, /debug, /probe-first, /hypotheses). Use 'suite' to run a named suite (or CSV) and diagnose every failing test in one call. Use 'bundle' to read the stored diagnosis bundles without re-running anything. 'test' and 'suite' require play mode (they dispatch test ConCmds); 'bundle' is a read-only in-memory query. This does NOT replace the debug skills — it picks the right one and pre-fills the evidence. Prefer the /diagnose-test skill, which wraps this tool and performs the routing.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The diagnosis operation to run.", @enum = new[] { "test", "suite", "bundle" } },
                ["test"] = new { type = "string", description = "A single failed test ConCmd name (e.g. 'sbep.test_gh_f18_groundvel_omega_r'). Required for: test." },
                ["suite"] = new { type = "string", description = "A named suite (gh_full, gh_walk, gh_bot, gh_component, maneuvers) OR a comma-separated list of explicit test ConCmd names. Required for: suite. Call qa list_suites to discover the named suites." },
                ["filter"] = new { type = "string", description = "Case-insensitive substring filter over test names — returns only matching stored bundles. Used by: bundle." },
                ["n"] = new { type = "integer", description = "How many standalone variance runs to measure during a diagnosis. Used by: test. Default: 5, max: 15. Each run boots fixtures (~2-5s) — keep it small." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 24: pipeline ───────────────────────────────────────────

    internal static readonly object Pipeline = new
    {
        name = "pipeline",
        description = "Automated build-and-verify workflow. Use 'compile_and_verify' after writing code to save the scene, compile, check for errors, run a TypeLibrary probe to confirm new types are live, and optionally run SBEP smoke tests — all in a single call. Use 'probe_typelibrary' to cheaply verify that one or more types resolve in TypeLibrary after a compile — the authoritative 'is my new code live?' check that bypasses the unreliable [EditorEvent.Hotload] counter. Returns structured pass/fail with actionable error messages.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The pipeline action to run.", @enum = new[] { "compile_and_verify", "probe_typelibrary" } },
                ["timeout_ms"] = new { type = "integer", description = "Max compilation wait time in milliseconds. Used by: compile_and_verify. Default: 120000 (2 minutes)." },
                ["skip_verify"] = new { type = "boolean", description = "Skip SBEP smoke test even if in play mode. Used by: compile_and_verify. Default: false." },
                ["pre_reload"] = new { type = "boolean", description = "Reload the scene before compile if playing + hotload-risky .cs file touched in last 60s. Used by: compile_and_verify. Default: true." },
                ["type_names"] = new { type = "array", items = new { type = "string" }, description = "Type names to resolve via Sandbox.TypeLibrary.GetType(). Used by: probe_typelibrary. If omitted, auto-derives from type declarations in .cs files edited in the last 60s." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Workflow = new
    {
        name = "workflow",
        description = "High-level AI workflows built on top of the lower-level scene, component, prefab, compile, and verification tools. Use this when the task is bigger than one primitive operation and you want a one-shot helper. It returns structured plans, checklists, or orchestration results for common s&box tasks such as prefab pairing, compile triage, and networked component verification.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The workflow to run.", @enum = new[] { "list", "create_prefab_pair", "fix_compile_errors", "verify_networked_component", "implement_spawnable_entity", "add_tool_mode" } },
                ["prefab_path"] = new { type = "string", description = "Prefab path. Used by: create_prefab_pair, implement_spawnable_entity." },
                ["type_name"] = new { type = "string", description = "Type name. Required for: verify_networked_component." },
                ["title"] = new { type = "string", description = "Spawn menu title. Optional for: create_prefab_pair." },
                ["description"] = new { type = "string", description = "Spawn menu description. Optional for: create_prefab_pair." },
                ["name"] = new { type = "string", description = "Tool mode name. Optional for: add_tool_mode." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object VerifyChangeSet = new
    {
        name = "verify_change_set",
        description = "Map a set of touched files to the right verification plan, and optionally run the matching checks. Use this after code or asset changes when you want the MCP server to pick compile, SBEP, asset, or playtest validation automatically. This reduces both under-verification and unnecessary checks.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The verification mode.", @enum = new[] { "plan", "run" } },
                ["paths"] = new { type = "string", description = "Comma-separated repo-relative file paths." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Playtest = new
    {
        name = "playtest",
        description = "Deterministic play-mode orchestration with assertions over play state, logs, scene queries, and console output. Use 'boot_scene' to load a scene and optionally start play mode. Use 'run_assertions' with a JSON array to smoke-test gameplay and runtime behavior in a structured way.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The playtest operation.", @enum = new[] { "boot_scene", "run_assertions" } },
                ["scene_path"] = new { type = "string", description = "Scene path. Used by: boot_scene." },
                ["start_play"] = new { type = "boolean", description = "Start play mode after booting scene. Used by: boot_scene. Default: true." },
                ["ensure_playing"] = new { type = "boolean", description = "Ensure play mode before assertions. Used by: run_assertions. Default: true." },
                ["wait_ms"] = new { type = "integer", description = "Delay before evaluating assertions. Used by: run_assertions. Default: 250." },
                ["assertions"] = new { type = "string", description = "JSON array of assertions. Used by: run_assertions." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Schema = new
    {
        name = "schema",
        description = "Inspect component, property, resource, and enum metadata from the live editor TypeLibrary. Use this before component writes to discover exact type names, writable properties, accepted value kinds, and enum values. This tool is read-only.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Schema lookup operation.", @enum = new[] { "components", "component", "property", "resources", "enum" } },
                ["type"] = new { type = "string", description = "Component/resource/enum type name. Required for: component, property, enum." },
                ["property"] = new { type = "string", description = "Property name. Required for: property." },
                ["filter"] = new { type = "string", description = "Case-insensitive name or namespace filter. Used by: components, resources." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Default: 100." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Operation = new
    {
        name = "operation",
        description = "Compatibility wrapper for validate, preview, apply, and rollback write operations, plus the coordinator control plane: 'list' shows in-flight operation ids with elapsed times, 'cancel' force-releases a wedged operation lease by id, and 'locks_held' shows per-GameObject lock holders with TTLs. Control-plane actions bypass the operation coordinator so they work even while a stuck operation is blocking every other call. For multi-operation flows, pass operations as the same JSON array string accepted by transaction.preview.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Operation lifecycle action.", @enum = new[] { "validate", "preview", "apply", "rollback", "cancel", "list", "locks_held" } },
                ["operation_id"] = new { type = "string", description = "In-flight operation id (from operation.list, or the rejected_operation_id / holder.operation_id fields of a gameobject_locked / mcp_busy error). Required for: cancel." },
                ["operations"] = new { type = "string", description = "JSON array of batched write operations. Used by: validate, preview." },
                ["transaction_id"] = new { type = "string", description = "Transaction id returned by preview. Required for apply/rollback of a previewed multi-operation transaction." },
                ["tool"] = new { type = "string", description = "Single-operation tool name such as component or gameobject." },
                ["operation_action"] = new { type = "string", description = "Single-operation action name. Used instead of action to avoid conflicting with the lifecycle action." },
                ["arguments"] = new { type = "object", description = "Single-operation parameters as an object.", additionalProperties = true },
                ["continue_on_error"] = new { type = "boolean", description = "For transaction apply: continue after a failed operation. Default false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Transaction = new
    {
        name = "transaction",
        description = "Preview, apply, and conditionally rollback a sequence of write operations. Use this when an AI agent wants to make bolder scene or asset edits under one exclusive editor operation. Preview records targets and editor revision, apply stops on first failed mutating operation unless continue_on_error=true, and rollback refuses if another write advanced the editor revision after apply. editor.undo remains available but is global and unsafe across agents.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Transaction operation.", @enum = new[] { "preview", "apply", "rollback" } },
                ["operations"] = new { type = "string", description = "JSON array of batched write operations. Required for: preview." },
                ["transaction_id"] = new { type = "string", description = "Transaction id. Required for: apply, rollback." },
                ["continue_on_error"] = new { type = "boolean", description = "For apply: continue after a failed operation. Default false, so apply stops on first error." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object Autofix = new
    {
        name = "autofix",
        description = "Apply safe, targeted repairs for common prefab and spawnable-entity issues. Use 'prefab_fixups' to normalize network mode, clear broken dev models, and add missing Rigidbody/Collider defaults. Use 'create_sent_pair' to generate a sibling .sent file for a spawnable prefab.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Autofix operation.", @enum = new[] { "prefab_fixups", "create_sent_pair" } },
                ["path"] = new { type = "string", description = "Prefab path. Required for: prefab_fixups." },
                ["prefab_path"] = new { type = "string", description = "Prefab path. Required for: create_sent_pair." },
                ["title"] = new { type = "string", description = "Spawn menu title. Optional for: create_sent_pair." },
                ["description"] = new { type = "string", description = "Spawn menu description. Optional for: create_sent_pair." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    internal static readonly object CrossIndex = new
    {
        name = "cross_index",
        description = "Generate and query a repo-to-engine cross index that links code symbols, assets, .sent files, console commands, docs, examples, and live scene objects. Use 'build' to refresh the artifact on disk, 'stats' for a quick summary, and 'query' to search across the unified index. This gives AI agents a single searchable knowledge graph instead of split repo/runtime lookups.",
        annotations = new { readOnlyHint = false },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "Cross-index operation.", @enum = new[] { "build", "stats", "query" } },
                ["query"] = new { type = "string", description = "Search text. Required for: query." },
                ["type"] = new { type = "string", description = "Optional index domain filter. Used by: query. Values: code, asset, sent, console, doc, example, scene." },
                ["include_scene"] = new { type = "boolean", description = "Include live scene objects while building. Used by: build. Default: true." },
                ["limit"] = new { type = "integer", description = "Max results. Used by: query. Default: 25." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 31: search ──────────────────────────────────────────────

    internal static readonly object Search = new
    {
        name = "search",
        description = "Advanced scene search beyond name/tag matching. Use 'by_pattern' for regex matching on object names or hierarchy paths. Use 'by_property_value' to find all objects where a specific component property equals, contains, or compares to a value (e.g. find all ModelRenderers where Model contains 'citizen'). Use 'get_neighborhood' to understand spatial context around an object (what's nearby with distances and directions). Use 'get_related' to discover reference relationships (which objects point to this one, and vice versa). All actions are read-only.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "by_pattern", "by_property_value", "get_neighborhood", "get_related" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: get_related. Used by: get_neighborhood (alternative to position)." },
                ["pattern"] = new { type = "string", description = "Regex pattern. Required for: by_pattern." },
                ["match_path"] = new { type = "boolean", description = "Match against full hierarchy path instead of just name. Used by: by_pattern. Default: false." },
                ["component_type"] = new { type = "string", description = "Component type name to search. Required for: by_property_value." },
                ["property"] = new { type = "string", description = "Property name to compare. Required for: by_property_value." },
                ["value"] = new { type = "string", description = "Value to compare against. Required for: by_property_value." },
                ["operator"] = new { type = "string", description = "Comparison operator.", @enum = new[] { "eq", "gt", "lt", "gte", "lte", "contains", "starts_with" } },
                ["position"] = new { type = "string", description = "Center position as 'x,y,z'. Used by: get_neighborhood (alternative to id)." },
                ["radius"] = new { type = "number", description = "Search radius. Used by: get_neighborhood. Default: 500." },
                ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
                ["limit"] = new { type = "integer", description = "Max results. Default: 50." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 32: snapshot ────────────────────────────────────────────

    internal static readonly object Snapshot = new
    {
        name = "snapshot",
        description = "Take, compare, and restore scene state snapshots. Snapshots are stored PER-AGENT — each calling agent has its own isolated bucket (up to 50 named snapshots), so concurrent agents never overwrite or evict one another's checkpoints. Use 'take' to serialize the current scene state to in-memory storage with a name; the 51st snapshot evicts only the calling agent's own oldest. Use 'diff' to compare a snapshot against the current scene (or two snapshots) — shows added, removed, and modified objects with property-level changes. Use 'restore' to revert transforms, properties, and enabled state on existing objects (cannot recreate destroyed objects); restore round-trips Material references plus Color / Vector2-4 / Rotation / Angles / enum component properties. Use 'list' to see stored snapshots and 'delete' to free memory. The agent_id argument is optional and defaults to the calling session's identified agent (set via session.identify) — un-identified clients share a degraded shared bucket. Note: per-instance RenderAttributes overrides are NOT snapshotted (RenderAttributes is non-enumerable); the Material resource reference carries the baked shader parameters via its .vmat.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "take", "list", "diff", "restore", "delete" } },
                ["name"] = new { type = "string", description = "Snapshot name. Required for: take, delete. Used by: diff (compare vs current), restore." },
                ["name_a"] = new { type = "string", description = "First snapshot name for two-snapshot comparison. Used by: diff." },
                ["name_b"] = new { type = "string", description = "Second snapshot name for two-snapshot comparison. Used by: diff." },
                ["scope_id"] = new { type = "string", description = "GameObject GUID to limit snapshot to a subtree. Used by: take." },
                ["include_disabled"] = new { type = "boolean", description = "Include disabled objects in snapshot. Used by: take. Default: true." },
                ["agent_id"] = new { type = "string", description = "Target agent bucket. Optional — defaults to the calling session's identified agent (set via session.identify), else a shared anonymous bucket. Pass explicitly only to operate on another agent's snapshots (e.g. a supervisor). Used by: take, list, diff, restore, delete." },
                ["all_agents"] = new { type = "boolean", description = "If true, list flattens snapshots across every agent's bucket (each entry carries its agentId). Used by: list. Default: false (caller's bucket only)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 32b: time (Wave 4.1) ────────────────────────────────────

    // [HANDOFF-TRUST: brand-new 'time' tool registration for mcp-agent-capability-system Wave 4.1 — additive entry, no inherited hypothesis from any prior wave4 handoff.]
    internal static readonly object Time = new
    {
        name = "time",
        description = "Control scene time flow for deterministic debugging and frame-stepping. Use 'pause' to halt Scene.TimeScale and PhysicsWorld.TimeScale (double-halt for engines where Scene.TimeScale=0 alone leaks physics ticks). Use 'resume' to restore the prior scale. Use 'is_paused' to query state. Use 'step' to advance N fixed-update ticks via Scene.GameTick(FixedDelta) — pair with pause for deterministic frame-by-frame. Use 'scale' to set arbitrary time-scale factors (0=pause, 1=realtime, 0.5=half-speed, 2=double-speed). State is process-scoped; resets on plugin hot-reload.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "pause", "resume", "is_paused", "step", "scale" } },
                ["frames"] = new { type = "integer", description = "Number of fixed-update ticks to advance. Used by: step. Default: 1. Range: 1..240." },
                ["factor"] = new { type = "number", description = "Time-scale factor. Used by: scale. Range: >=0. 0=pause, 1=realtime." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 33: validate ────────────────────────────────────────────

    internal static readonly object Validate = new
    {
        name = "validate",
        description = "Run diagnostics on scene objects, components, prefabs, and references to find problems before running the game. Use 'scene' for a full scan (null models, colliders without rigidbodies, duplicate components, multiple active cameras). Use 'component' to validate specific component properties (null references, broken asset paths). Use 'prefab' for prefab integrity checks (broken script references). Use 'references' to scan the entire scene for broken GameObject/Component/Model references. All actions are read-only. This is a general scene-health validator — for SBEP-specific validation use 'sbep_verify'.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "scene", "component", "prefab", "references" } },
                ["id"] = new { type = "string", description = "GameObject GUID. Required for: component." },
                ["component_id"] = new { type = "string", description = "Component GUID (validates all if omitted). Used by: component." },
                ["path"] = new { type = "string", description = "Prefab file path. Required for: prefab." },
                ["severity_filter"] = new { type = "string", description = "Filter results by severity.", @enum = new[] { "error", "warning", "info" } }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 34: continue ────────────────────────────────────────────

    internal static readonly object Continue = new
    {
        name = "continue",
        description = "Retrieve the next page of a large result that was automatically paginated. When any tool response includes a '_cursor' block with 'has_more: true', use this tool with the cursor_id to get the next page. Cursors expire after 60 seconds — if expired, re-run the original query. Do not call this tool unless you see a _cursor block in a previous response.",
        annotations = new { readOnlyHint = true },
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "next" } },
                ["cursor_id"] = new { type = "string", description = "Cursor ID from a previous response's _cursor block. Required." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 35: blueprint ───────────────────────────────────────────

    internal static readonly object Blueprint = new
    {
        name = "blueprint",
        description = "Create complex object hierarchies from a single declarative JSON spec. Instead of multiple sequential gameobject.create + component.add + component.set_property calls, describe the full object tree in one 'spec' parameter. Supports nested children, multiple components with properties, tags, and transforms. Use 'validate' to check a spec before creating. On failure, all partially-created objects are rolled back. The 'spec' format: {\"name\": \"...\", \"tags\": [\"...\"], \"position\": \"x,y,z\", \"components\": [{\"type\": \"ModelRenderer\", \"properties\": {\"Model\": \"path.vmdl\"}}], \"children\": [...]}",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "create", "validate" } },
                ["spec"] = new { type = "string", description = "JSON blueprint specification. Required for: create, validate. Describes a hierarchy of objects with components and properties." },
                ["parent_id"] = new { type = "string", description = "Parent GameObject GUID to create under. Used by: create. Default: scene root." },
                ["position"] = new { type = "string", description = "Override root position as 'x,y,z'. Used by: create." },
                ["rotation"] = new { type = "string", description = "Override root rotation as 'pitch,yaw,roll'. Used by: create." },
                ["dry_run"] = new { type = "boolean", description = "Validate spec without creating objects. Used by: create. Default: false." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 36: animation ───────────────────────────────────────────

    internal static readonly object Animation = new
    {
        name = "animation",
        description = "Configure animation parameters on SkinnedModelRenderer and CitizenAnimationHelper components. Use 'list_parameters' to discover available animation parameters on a model (movement speed, duck level, look direction, etc.). Use 'set_parameters' to configure animation values — tries CitizenAnimationHelper first, then SkinnedModelRenderer properties, then AnimGraph parameters via Set(). Use 'get_model_animations' to list animation clips/sequences on a model. Requires a SkinnedModelRenderer on the target GameObject.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "list_parameters", "set_parameters", "get_model_animations" } },
                ["id"] = new { type = "string", description = "GameObject GUID with SkinnedModelRenderer. Required for: list_parameters, set_parameters. Used by: get_model_animations." },
                ["parameters"] = new { type = "string", description = "JSON object of parameter name/value pairs. Required for: set_parameters. Example: '{\"MoveSpeed\": 100, \"DuckLevel\": 0.5}'" },
                ["model_path"] = new { type = "string", description = "Model asset path (.vmdl). Used by: get_model_animations (alternative to id)." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };

    // ── Tool 37: cockpit_verify ──────────────────────────────────────

    // Deliberately a factory-backed property, not a static readonly anonymous
    // object. S&Box hotload preserves initialized static-field values, which
    // made live parity compare a fresh scanner schema against a stale registry
    // object after additive action changes. Rebuilding this rollback literal on
    // access keeps the live proof tied to the currently loaded method body.
    internal static object CockpitVerify => new
    {
        name = "cockpit_verify",
        description = "Run the cockpit beauty verifier as a single orchestrated MCP call. Use action='run' to optionally restart play mode, ask the game-side cockpit diagnostic to select the runtime scene, invoke the cockpit beauty runner, poll FileSystem.Data artifacts, and return packet/rubric/AAA summary fields. Responses include handler_version and contract_version so wrappers can detect stale hotloaded MCP code. This tool is purpose-built to avoid long manual play/stop/log/artifact loops; it does not rebuild Blender assets.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "readiness", "run" } },
                ["label"] = new { type = "string", description = "Run label used for FileSystem.Data/cockpit-beauty/<label>. Default: baseline." },
                ["restart_play"] = new { type = "boolean", description = "Stop and start play before verifying. Default: true." },
                ["timeout_seconds"] = new { type = "integer", description = "Maximum time to wait for readiness and artifacts. Default: 75." },
                ["data_root"] = new { type = "string", description = "Absolute cockpit-beauty data root. Optional; default resolves sibling S&Box-Pub game data." }
            },
            required = new[] { "action" },
            additionalProperties = false
        }
    };
}
