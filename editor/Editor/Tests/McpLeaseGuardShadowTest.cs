// Phase 0a promotion gate for the resource-side lease shadow.
// Injects all four CANNOT_EVALUATE causes through a pure decision seam; it does
// not touch the live coordinator, holder file, scene, or editor state.
//
// Verdict note: this ConCmd lives in arenula_mcp.editor.dll, which cannot take a
// compile-time dependency on the game assembly's SBEP.Tests.TestVerdictRegistry
// without the type ambiguity documented by the sibling editor-library tests.
// It therefore emits named gates plus one machine-readable summary line.
// [TEST-PARAMS-INTENTIONAL: no parameter overrides -- this is a pure decision table]

using System;
using System.IO;
using Sandbox;

namespace Arenula.Tests;

public static class McpLeaseGuardShadowTest
{
	private const string TestName = "sbep.test_mcp_lease_shadow";
	private const string IlStamp = "2026-08-20-project-root-v3";

	[ConCmd( "sbep.test_mcp_lease_shadow" )]
	public static void Run( string mode = "" )
	{
		var selfTest = string.Equals( mode, "selftest", StringComparison.OrdinalIgnoreCase );
		int pass = 0, fail = 0;
		Log.Info( $"=== {TestName}{(selfTest ? " [SELFTEST]" : "")} il={IlStamp} ===" );

		CheckCannot( "missing-holder", McpLeaseGuard.ShadowCause.MissingHolderState,
			McpLeaseGuard.ShadowCause.MissingHolderState, selfTest, ref pass, ref fail );
		CheckCannot( "torn-holder", McpLeaseGuard.ShadowCause.TornHolderState,
			McpLeaseGuard.ShadowCause.TornHolderState, selfTest, ref pass, ref fail );
		CheckCannot( "coordinator-unreachable", McpLeaseGuard.ShadowCause.CoordinatorUnreachable,
			McpLeaseGuard.ShadowCause.CoordinatorUnreachable, selfTest, ref pass, ref fail );

		var anonymous = McpLeaseGuard.EvaluateShadowSnapshot(
			selfTest ? "agent-a" : null, false, selfTest ? "agent-a" : "holder-a",
			McpLeaseGuard.ShadowCause.None );
		Check( "anonymous-caller",
			anonymous.Decision == McpLeaseGuard.ShadowDecision.CannotEvaluate
			&& anonymous.Cause == McpLeaseGuard.ShadowCause.AnonymousCaller,
			selfTest, ref pass, ref fail );

		var deny = McpLeaseGuard.EvaluateShadowSnapshot(
			selfTest ? "agent-a" : "agent-b", false, "agent-a", McpLeaseGuard.ShadowCause.None );
		Check( "known-other-denies",
			deny.Decision == McpLeaseGuard.ShadowDecision.Deny
			&& deny.Cause == McpLeaseGuard.ShadowCause.None,
			selfTest, ref pass, ref fail );

		var allow = McpLeaseGuard.EvaluateShadowSnapshot(
			selfTest ? "agent-b" : "agent-a", false, "agent-a", McpLeaseGuard.ShadowCause.None );
		Check( "known-self-allows",
			allow.Decision == McpLeaseGuard.ShadowDecision.Allow,
			selfTest, ref pass, ref fail );

		var activeRoot = HandlerBase.GetProjectRoot();
		Check( "live-project-root",
			!selfTest && !string.IsNullOrWhiteSpace( activeRoot )
			&& Path.GetFullPath( McpLeaseGuard.CurrentProjectRoot )
				== Path.GetFullPath( activeRoot ),
			selfTest, ref pass, ref fail );

		var syntheticRoot = Path.Combine( Path.GetTempPath(), "sbpp-active-project" );
		var unrelatedCwd = Path.Combine( Path.GetTempPath(), "sbpp-engine-game" );
		Check( "active-project-root-wins-over-cwd",
			!selfTest && McpLeaseGuard.ResolveProjectRoot( syntheticRoot, unrelatedCwd ) == syntheticRoot,
			selfTest, ref pass, ref fail );

		var requestId = Guid.NewGuid().ToString();
		var direct = McpLeaseGuard.BindOuterLeaseSnapshot(
			"agent-a", "scene.save", requestId, "agent-a", "scene_edit",
			"scene.save", "boot-a", "", 0, 0, 1234 );
		Check( "terminal-binding-direct-exact",
			!selfTest && direct.IsValid,
			selfTest, ref pass, ref fail );

		var parent = McpLeaseGuard.BindOuterLeaseSnapshot(
			"agent-a", "scene.save", requestId, "agent-a", "session",
			"skill.sandboxpp-playtest", "boot-a", "", 0, 0, 1234 );
		Check( "terminal-binding-session-parent-rejected",
			!selfTest && !parent.IsValid,
			selfTest, ref pass, ref fail );

		var mismatched = McpLeaseGuard.BindOuterLeaseSnapshot(
			"agent-a", "scene.save", requestId, "agent-a", "scene_edit",
			"scene.load", "boot-a", "", 0, 0, 1234 );
		Check( "terminal-binding-operation-mismatch-rejected",
			!selfTest && !mismatched.IsValid,
			selfTest, ref pass, ref fail );

		if ( selfTest )
		{
			var ok = pass == 0 && fail == 11;
			var summary = $"{TestName}#selftest: {(ok ? "PASS" : "FAIL")} gates_bitten={fail} survivors={pass}";
			if ( ok ) Log.Info( summary ); else Log.Error( summary );
			return;
		}

		var normalSummary = $"{TestName}: {(fail == 0 ? "PASS" : "FAIL")} pass={pass} fail={fail}";
		if ( fail == 0 ) Log.Info( normalSummary ); else Log.Error( normalSummary );
	}

	private static void CheckCannot( string name, McpLeaseGuard.ShadowCause injected,
		McpLeaseGuard.ShadowCause expected, bool selfTest, ref int pass, ref int fail )
	{
		var result = McpLeaseGuard.EvaluateShadowSnapshot(
			"agent-a", false, selfTest ? "agent-a" : null,
			selfTest ? McpLeaseGuard.ShadowCause.None : injected );
		Check( name,
			result.Decision == McpLeaseGuard.ShadowDecision.CannotEvaluate
			&& result.Cause == expected,
			selfTest, ref pass, ref fail );
	}

	private static void Check( string name, bool condition, bool selfTest, ref int pass, ref int fail )
	{
		if ( condition )
		{
			pass++;
			if ( selfTest ) Log.Error( $"[SURVIVOR] {TestName}: {name}" );
			else Log.Info( $"[PASS] {TestName}: {name}" );
		}
		else
		{
			fail++;
			if ( selfTest ) Log.Info( $"[BITE] {TestName}: {name}" );
			else Log.Error( $"[FAIL] {TestName}: {name}" );
		}
	}
}
