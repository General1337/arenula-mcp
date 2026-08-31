// Durable, scene-free regression gate for authoritative MCP action metadata.
// The test exercises the resolver seam directly; no tool handler is invoked and
// no coordinator/editor state is mutated.

using System;
using System.Linq;
using Sandbox;

namespace Arenula.Tests;

public static class McpActionMetadataTruthTest
{
    private const string TestName = "sbep.test_mcp_action_metadata_truth";

    [ConCmd( TestName )]
    public static void Run( string mode = "" )
    {
        if ( string.Equals( mode, "selftest", StringComparison.OrdinalIgnoreCase ) )
        {
            RunSabotageSelftest();
            return;
        }

        int failures = 0;
        Log.Info( $"=== {TestName} ===" );

        var batch = ToolRegistry.ResolveOperationMetadata( "batch", "execute" );
        Gate( "scanned_batch_lifecycle",
            IsExplicitScanned( batch )
            && !batch.Metadata.ReadOnly
            && batch.Metadata.WriteKind == "none"
            && batch.Metadata.LifecycleKind == "lifecycle",
            Describe( batch ), ref failures );

        var health = ToolRegistry.ResolveOperationMetadata( "mcp.health", null );
        Gate( "zero_action_scanned_readonly",
            IsExplicitScanned( health ) && health.Metadata.ReadOnly,
            Describe( health ), ref failures );

        var attention = ToolRegistry.ResolveOperationMetadata( "attention", "peek" );
        Gate( "attention_explicit_readonly",
            IsExplicitScanned( attention ) && attention.Metadata.ReadOnly,
            Describe( attention ), ref failures );

        var snapshot = ToolRegistry.ResolveOperationMetadata( "step_debugger", "snapshot" );
        var watch = ToolRegistry.ResolveOperationMetadata( "step_debugger", "watch" );
        Gate( "step_debugger_mutations",
            IsExplicitScanned( snapshot ) && snapshot.Metadata.WriteKind == "scene_write"
            && IsExplicitScanned( watch ) && watch.Metadata.WriteKind == "scene_write",
            $"snapshot={Describe( snapshot )}; watch={Describe( watch )}", ref failures );

        var workflowCreate = ToolRegistry.ResolveOperationMetadata( "workflow", "create_prefab_pair" );
        var workflowVerify = ToolRegistry.ResolveOperationMetadata( "workflow", "verify_networked_component" );
        var workflowList = ToolRegistry.ResolveOperationMetadata( "workflow", "list" );
        Gate( "legacy_workflow_per_action",
            IsExplicitLegacy( workflowCreate ) && workflowCreate.Metadata.WriteKind == "file_write"
            && IsExplicitLegacy( workflowVerify ) && workflowVerify.Metadata.LifecycleKind == "lifecycle"
            && IsExplicitLegacy( workflowList ) && workflowList.Metadata.ReadOnly,
            $"create={Describe( workflowCreate )}; verify={Describe( workflowVerify )}; list={Describe( workflowList )}",
            ref failures );

        var workflowUnknown = ToolRegistry.ResolveOperationMetadata( "workflow", "not_a_real_action" );
        var crossIndexUnknown = ToolRegistry.ResolveOperationMetadata( "cross_index", "not_a_real_action" );
        Gate( "mixed_tools_unknown_fail_closed",
            IsConservativeUnknown( workflowUnknown ) && IsConservativeUnknown( crossIndexUnknown ),
            $"workflow={Describe( workflowUnknown )}; cross_index={Describe( crossIndexUnknown )}",
            ref failures );

        var legacyKeyed = ToolRegistry.ResolveOperationMetadata( "gameobject", "set_transform" );
        Gate( "legacy_exact_lock_key_retained",
            IsExplicitLegacy( legacyKeyed )
            && legacyKeyed.Metadata.WriteKind == "scene_write_per_go"
            && legacyKeyed.Metadata.LockKey == LockKeyStrategy.GameObjectId,
            Describe( legacyKeyed ), ref failures );

        var toolDefault = ToolRegistry.ResolveOperationMetadata( "scene", "summary" );
        Gate( "readonly_tool_default_provenance",
            toolDefault.IsFallback
            && toolDefault.Provenance == "fallback"
            && toolDefault.Source == "tool_default"
            && toolDefault.Metadata.ReadOnly,
            Describe( toolDefault ), ref failures );

        var unknown = ToolRegistry.ResolveOperationMetadata( "__metadata_truth_unknown__", "mutate" );
        Gate( "unknown_fail_closed",
            IsConservativeUnknown( unknown ), Describe( unknown ), ref failures );

        var fallbackDescription = ToolRegistry.DescribeUnknownActionFallback();
        Gate( "unknown_fallback_contract_described",
            GetProperty( fallbackDescription, "read_only" ) is false
            && GetProperty( fallbackDescription, "write_kind" ) as string == "scene_write"
            && GetProperty( fallbackDescription, "lifecycle_kind" ) as string == "none"
            && GetProperty( fallbackDescription, "lock_key" ) as string == "none"
            && GetProperty( fallbackDescription, "provenance" ) as string == "fallback"
            && GetProperty( fallbackDescription, "source" ) as string == "unknown_default"
            && GetProperty( fallbackDescription, "is_fallback" ) is true,
            "top-level contract is conservative SceneWrite with fallback/unknown_default provenance",
            ref failures );

        var scannedUnknown = ToolRegistry.ResolveOperationMetadata( "batch", "not_a_real_action" );
        Gate( "scanned_unknown_does_not_fall_through",
            IsConservativeUnknown( scannedUnknown ), Describe( scannedUnknown ), ref failures );

        var bareDescription = ToolRegistry.DescribeActions( ToolRegistry.McpHealth ).Single();
        Gate( "zero_action_bare_route_described",
            GetProperty( bareDescription, "action" ) == null
            && GetProperty( bareDescription, "tool" ) as string == "mcp.health"
            && GetProperty( bareDescription, "qualified_action" ) as string == "mcp.health"
            && GetProperty( bareDescription, "provenance" ) as string == "explicit"
            && GetProperty( bareDescription, "source" ) as string == "mcp_action"
            && GetProperty( bareDescription, "is_fallback" ) is false,
            "bare action metadata includes tool, qualified_action, explicit scanner provenance, and is_fallback=false",
            ref failures );

        var describedStepActions = ToolRegistry.DescribeActions( ToolRegistry.StepDebugger )
            .Select( entry => GetProperty( entry, "action" ) as string )
            .ToArray();
        var sortedStepActions = describedStepActions
            .OrderBy( action => action, StringComparer.OrdinalIgnoreCase )
            .ThenBy( action => action, StringComparer.Ordinal )
            .ToArray();
        Gate( "action_metadata_deterministic_order",
            describedStepActions.SequenceEqual( sortedStepActions, StringComparer.Ordinal ),
            string.Join( ",", describedStepActions ), ref failures );

        try
        {
            var readOnly = new ToolRegistry.ToolActionMetadata { ReadOnly = true };
            ToolRegistry.EnsureCompatibleDuplicate( "test.same", readOnly, readOnly );
            Gate( "matching_duplicate_allowed", true,
                "identical duplicate metadata passed compatibility guard", ref failures );
        }
        catch ( Exception ex )
        {
            Gate( "matching_duplicate_allowed", false, ex.Message, ref failures );
        }

        var outcome = failures == 0 ? "PASS" : "FAIL";
        Log.Info( $"[{outcome}] {TestName}: SUMMARY — failures={failures}; gates=14" );
    }

    private static void RunSabotageSelftest()
    {
        // Perturb the subject value that the normal unknown_fail_closed gate
        // consumes: preserve its fallback provenance but replace conservative
        // SceneWrite with the historical permissive ReadOnly default.
        var healthy = ToolRegistry.ResolveOperationMetadata( "__metadata_truth_unknown__", "mutate" );
        var sabotaged = new ToolRegistry.ResolvedToolActionMetadata
        {
            Metadata = new ToolRegistry.ToolActionMetadata { ReadOnly = true },
            Provenance = healthy.Provenance,
            Source = healthy.Source,
            IsFallback = healthy.IsFallback,
        };

        bool normalGatePassed = IsConservativeUnknown( sabotaged );
        Log.Info( $"[{(normalGatePassed ? "PASS" : "FAIL")}] {TestName}: ASSERT unknown_fail_closed " +
            $"[SELFTEST SABOTAGE] — {Describe( sabotaged )}" );

        bool observedExpectedRed = !normalGatePassed;
        Log.Info( observedExpectedRed
            ? $"[PASS] {TestName} selftest: permissive unknown fallback made the normal gate go red"
            : $"[FAIL] {TestName} selftest: normal gate stayed green under permissive unknown fallback sabotage" );
    }

    private static bool IsExplicitScanned( ToolRegistry.ResolvedToolActionMetadata value )
        => value != null && !value.IsFallback
            && value.Provenance == "explicit" && value.Source == "mcp_action";

    private static bool IsExplicitLegacy( ToolRegistry.ResolvedToolActionMetadata value )
        => value != null && !value.IsFallback
            && value.Provenance == "explicit" && value.Source == "legacy_action";

    private static bool IsConservativeUnknown( ToolRegistry.ResolvedToolActionMetadata value )
        => value != null && value.IsFallback
            && value.Provenance == "fallback" && value.Source == "unknown_default"
            && !value.Metadata.ReadOnly
            && value.Metadata.WriteKind == "scene_write"
            && value.Metadata.LifecycleKind == "none"
            && value.Metadata.LockKey == LockKeyStrategy.None;

    private static object GetProperty( object value, string name )
        => value?.GetType().GetProperty( name )?.GetValue( value );

    private static string Describe( ToolRegistry.ResolvedToolActionMetadata value )
        => value == null
            ? "<null>"
            : $"source={value.Source},provenance={value.Provenance},fallback={value.IsFallback}," +
              $"read_only={value.Metadata.ReadOnly},write={value.Metadata.WriteKind}," +
              $"lifecycle={value.Metadata.LifecycleKind},lock={value.Metadata.LockKey}";

    private static void Gate( string name, bool passed, string detail, ref int failures )
    {
        if ( !passed ) failures++;
        Log.Info( $"[{(passed ? "PASS" : "FAIL")}] {TestName}: ASSERT {name} — {detail}" );
    }
}
