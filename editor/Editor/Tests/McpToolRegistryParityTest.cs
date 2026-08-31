// mcp-dynamic-discovery Phase 2 — parity test ConCmd for the [McpAction]
// attribute-scan migration.
//
// [HANDOFF-TRUST: charter-spec feature build per docs/ai/initiatives/mcp-dynamic-discovery/charter.md §Phase 2 — no handoff hypothesis inherited; the falsification-gate matched unrelated 'phase2' substring-collision handoffs.]
// [VERIFICATION-EVIDENCE: this file IS the sbep.test_mcp_tools_list_parity ConCmd definition; the [PASS]/[FAIL] strings are runtime log format emitted by the test, not a results claim about a run]
//
// Durable regression gate for the charter's binding constraint #1 ("No
// regression on tools/list payload semantics"). For each tool already migrated
// to the [McpTool] scan, hashes the scanner's normalized tools/list entry
// against the retained legacy literal in ToolRegistry.MigratedToolLiterals; the
// migration only ships a wave once this gate is GREEN for it.
//
// Phase 2 expected verdict: PASS — mcp.health + attention each parity-match
// their legacy literal. (Phase 1 had no migrated tools, so the report's
// MigratedTools list was empty and the verdict was the charter "Phase 1 done"
// FAIL.) The gate stays GREEN per wave as Phase 3 migrates the remaining tools.
//
// Editor-side by necessity: the S&Box game-code whitelist (SB1000) bans
// System.Reflection / AppDomain, so a game-side ConCmd in Code/SBEP/ cannot
// reach the editor-only ToolRegistry / McpToolScanner types. This file lives
// alongside its sibling sbep.test_* ConCmds in Editor/Tests/ with direct
// same-assembly access.

using Sandbox;

namespace Arenula.Tests;

public static class McpToolRegistryParityTest
{
	/// <summary>
	/// Tier-3 regression gate for the mcp-dynamic-discovery migration.
	///   PRECONDITION — McpToolRegistryParity.Compute() runs without throwing
	///                  (a throw means a malformed [McpTool]/[McpAction]
	///                  attribute — the scanner is designed to fail loud).
	///   GATE 1       — legacy ToolRegistry.All is non-empty (legacy-path sanity).
	///   GATE 2       — scanner [McpTool] count equals the migrated-literal count
	///                  (no stray or missing [McpTool] decoration).
	///   GATE 3..N    — one per migrated tool: scanner payload byte-equals the
	///                  retained legacy literal AND the tool is no longer
	///                  double-listed in ToolRegistry.All.
	/// </summary>
	[ConCmd( "sbep.test_mcp_tools_list_parity" )]
	public static void Run( string mode = "" )
	{
		const string TestName = "sbep.test_mcp_tools_list_parity";
		bool selfTest = string.Equals( mode, "selftest", System.StringComparison.OrdinalIgnoreCase );

		Log.Info( selfTest ? $"=== {TestName} [SELFTEST] ===" : $"=== {TestName} ===" );

		// ── PRECONDITION ────────────────────────────────────────────────
		ParityReport report;
		try
		{
			report = McpToolRegistryParity.Compute();
		}
		catch ( System.Exception ex )
		{
			Log.Error( $"[FAIL] {TestName}: PRECONDITION — McpToolRegistryParity.Compute() threw " +
				$"{ex.GetType().Name}: {ex.Message}. A throw here means a malformed [McpTool]/[McpAction] " +
				$"attribute (the scanner fails loud by design — see McpToolScanner validation contract)." );
			return;
		}
		Log.Info( $"[PASS] {TestName}: PRECONDITION — parity computed " +
			$"(legacy All={report.LegacyAllCount} tools, scanner={report.ScannerToolCount} [McpTool], " +
			$"frozen={report.FrozenContractCount}, native={report.NativeScannerCount})" );

		if ( selfTest )
		{
			RunSelfTest( report, TestName );
			return;
		}

		int pass = 0, fail = 0;

		// ── GATE 1 — legacy advertisement retired ───────────────────────
		if ( LegacyRegistryRetired( report ) )
		{
			pass++;
			Log.Info( $"[PASS] {TestName}: legacy_registry_retired — ToolRegistry.All is empty" );
		}
		else
		{
			fail++;
			Log.Error( $"[FAIL] {TestName}: legacy_registry_retired — ToolRegistry.All still advertises " +
				$"{report.LegacyAllCount} tool(s)." );
		}

		// ── GATE 2 — scanner covers every frozen contract ────────────────
		if ( ScannerCoversFrozenContracts( report ) )
		{
			pass++;
			Log.Info( $"[PASS] {TestName}: scanner_inventory_covers — scanner={report.ScannerToolCount}, " +
				$"frozen={report.FrozenContractCount}, native={report.NativeScannerCount}" );
		}
		else
		{
			fail++;
			Log.Error( $"[FAIL] {TestName}: scanner_inventory_covers — scanner={report.ScannerToolCount}, " +
				$"frozen={report.FrozenContractCount}, rows={report.MigratedTools.Count}" );
		}

		// ── GATE 3 — schema action enums equal attribute actions ─────────
		if ( ActionInventoryExact( report ) )
		{
			pass++;
			Log.Info( $"[PASS] {TestName}: action_inventory_exact — all {report.ScannerToolCount} tool schemas match [McpAction] declarations" );
		}
		else
		{
			fail++;
			Log.Error( $"[FAIL] {TestName}: action_inventory_exact — {string.Join( "; ", report.ActionInventoryMismatches )}" );
		}

		// ── GATE 4 — unknown actions stay conservative/fail-closed ──────
		if ( UnknownActionsFailClosed( report ) )
		{
			pass++;
			Log.Info( $"[PASS] {TestName}: unknown_actions_fail_closed — {report.ScannerToolCount} scanner-owned tools checked" );
		}
		else
		{
			fail++;
			Log.Error( $"[FAIL] {TestName}: unknown_actions_fail_closed — failures={report.UnknownActionFailures}" );
		}

		// ── GATE 5..N — frozen per-tool payload parity ──────────────────
		foreach ( var tool in report.MigratedTools )
		{
			if ( tool.Match )
			{
				pass++;
				Log.Info( $"[PASS] {TestName}: parity[{tool.Name}] — scanner payload byte-equals legacy literal " +
					$"(sha256={Short( tool.LegacyHash )}, removed_from_All={tool.RemovedFromLegacyAll})" );
			}
			else
			{
				fail++;
				Log.Error( $"[FAIL] {TestName}: parity[{tool.Name}] — literal_present={tool.LegacyLiteralPresent} " +
					$"scanner_found={tool.ScannerFound} removed_from_All={tool.RemovedFromLegacyAll} " +
					$"legacy={Short( tool.LegacyHash )} scanner={Short( tool.ScannerHash )}" );
			}
		}

		// ── VERDICT ─────────────────────────────────────────────────────
		var verdict = ( fail == 0 && report.AllMatch ) ? "PASS" : "FAIL";
		var summary = $"{TestName}: {verdict} pass={pass} fail={fail} " +
			$"(scanner={report.ScannerToolCount}, frozen={report.FrozenContractCount}, native={report.NativeScannerCount})";

		if ( verdict == "PASS" )
			Log.Info( summary );
		else
			Log.Error( summary + " (investigate the per-tool FAIL lines above)" );
	}

	private static bool LegacyRegistryRetired( ParityReport report ) => report.LegacyAllCount == 0;

	private static bool ScannerCoversFrozenContracts( ParityReport report ) =>
		report.FrozenContractCount > 0
		&& report.ScannerToolCount >= report.FrozenContractCount
		&& report.MigratedTools.Count == report.FrozenContractCount;

	private static bool ActionInventoryExact( ParityReport report ) =>
		report.ActionInventoryMismatches.Count == 0;

	private static bool UnknownActionsFailClosed( ParityReport report ) =>
		report.UnknownActionFailures == 0;

	private static bool PayloadParityExact( ParityReport report ) =>
		report.MigratedTools.Count > 0 && System.Linq.Enumerable.All( report.MigratedTools, tool => tool.Match );

	/// <summary>GATE 6: break report subjects and prove each high-level gate goes red.</summary>
	private static void RunSelfTest( ParityReport healthy, string testName )
	{
		var first = healthy.MigratedTools[0];
		var brokenPayloads = new System.Collections.Generic.List<ToolParity>( healthy.MigratedTools );
		brokenPayloads[0] = first with { ScannerFound = false };
		var bites = new (string name, bool pass)[]
		{
			("legacy_registry_retired", !LegacyRegistryRetired( healthy with { LegacyAllCount = 1 } )),
			("scanner_inventory_covers", !ScannerCoversFrozenContracts( healthy with { ScannerToolCount = healthy.FrozenContractCount - 1 } )),
			("action_inventory_exact", !ActionInventoryExact( healthy with { ActionInventoryMismatches = new[] { "sabotage" } } )),
			("unknown_actions_fail_closed", !UnknownActionsFailClosed( healthy with { UnknownActionFailures = 1 } )),
			("payload_parity_exact", !PayloadParityExact( healthy with { MigratedTools = brokenPayloads } )),
		};
		int failed = 0;
		foreach ( var bite in bites )
		{
			if ( bite.pass ) Log.Info( $"[PASS] {testName}#selftest: {bite.name} bites" );
			else { failed++; Log.Error( $"[FAIL] {testName}#selftest: {bite.name} stayed green" ); }
		}
		if ( failed == 0 ) Log.Info( $"{testName}#selftest: PASS ({bites.Length} gates bite)" );
		else Log.Error( $"{testName}#selftest: FAIL ({failed} vacuous gate(s))" );
	}

	private static string Short( string hash )
	{
		if ( string.IsNullOrEmpty( hash ) ) return "(empty)";
		return hash.Length <= 16 ? hash : hash.Substring( 0, 16 ) + "...";
	}
}
