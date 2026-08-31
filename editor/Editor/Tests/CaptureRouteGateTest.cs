// sbep.test_capture_route — the gate that would have caught a three-week outage in a day.
//
// [VERIFICATION-EVIDENCE: this file IS the sbep.test_capture_route ConCmd definition; the
//  [PASS]/[FAIL] strings below are the runtime log format it emits, not a claim about a run.]
//
// WHY THIS EXISTS
// ---------------
// docs/ai/contracts/screenshot.md is BINDING and says there is exactly one way to take a
// screenshot: `capture action=shot`. .claude/hooks/one-screenshot-route-gate.sh enforces that by
// blocking six working alternatives.
//
// On 2026-07-25 the handler, the hook, the contract and the CLAUDE.md rule all shipped together —
// and nothing registered the action. `shot` was advertised nowhere, bound nowhere, and answered
// "Unknown action 'shot' for tool 'capture'" for three weeks. Every agent in that window was
// blocked from six routes that worked, in favour of one that could not dispatch. Nobody found it
// by reading; it was found by being blocked.
//
// The general rule this encodes: A HOOK THAT REMOVES EVERY ALTERNATIVE IN FAVOUR OF ONE PATH MUST
// BE ABLE TO FAIL LOUDLY WHEN THAT PATH IS OUT. Three surfaces have to agree for an action to
// exist, and agreeing is exactly the kind of thing nobody re-checks by hand:
//
//   1. ToolRegistry.<Tool>.inputSchema.action.enum  — what the tool ADVERTISES
//   2. .claude/hooks/mcp-action-metadata.snapshot.json — its safety class (host-side; the
//      generator refuses to emit an adapter for an action missing here, so a scanned binding is
//      already proof this row existed at generation time)
//   3. the [McpAction]-decorated method the dispatcher actually reaches — the BINDING
//
// This gate asserts 1 and 3 agree for the capture tool, and that the contract's named route is
// among them. Deleting `shot` from any one surface turns it red on the next run.
//
// Editor-side by necessity: the S&Box game-code whitelist (SB1000) bans System.Reflection, so a
// ConCmd in Code/SBEP/ cannot reach the editor-only ToolRegistry / McpToolScanner types. Same
// placement and convention as its sibling sbep.test_mcp_tools_list_parity.

using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula.Tests;

public static class CaptureRouteGateTest
{
	/// <summary>The route the BINDING screenshot contract names. If this cannot dispatch, the
	/// project has no sanctioned way to take a screenshot at all.</summary>
	private const string ContractTool = "capture";
	private const string ContractAction = "shot";

	/// <summary>
	/// Tier-3 regression gate for the one-screenshot-command contract.
	///   PRECONDITION — the scanner resolves the 'capture' tool.
	///   GATE 1 — `capture action=shot` is BOUND: a dispatchable [McpAction] method exists.
	///   GATE 2 — `shot` is ADVERTISED in the tool's action enum, so a client can discover it.
	///   GATE 3 — advertised and bound sets are identical (no action exists on only one surface).
	///   GATE 4 — shot is dispatched async, which its play-mode backbuffer route requires.
	///   GATE 5 — the one output directory is resolvable and writable.
	/// </summary>
	[ConCmd( "sbep.test_capture_route" )]
	public static void Run()
	{
		const string TestName = "sbep.test_capture_route";
		Log.Info( $"=== {TestName} ===" );

		var failures = new List<string>();

		// ── PRECONDITION ────────────────────────────────────────────────
		if ( !McpToolScanner.Cached.TryGetValue( ContractTool, out var tool ) || tool is null )
		{
			Log.Error( $"[FAIL] {TestName}: PRECONDITION — the scanner has no '{ContractTool}' tool. " +
				"Nothing below can be measured; the plugin assembly did not load or the [McpTool] " +
				"decoration was removed." );
			Log.Error( $"=== {TestName}: FAIL (1 gate) ===" );
			return;
		}

		var bound = tool.Actions.Keys.OrderBy( a => a, System.StringComparer.Ordinal ).ToList();
		var advertised = ReadAdvertisedActions().OrderBy( a => a, System.StringComparer.Ordinal ).ToList();

		// ── GATE 1 — bound ──────────────────────────────────────────────
		var shot = tool.Actions.TryGetValue( ContractAction, out var scanned ) ? scanned : null;
		if ( shot is null )
		{
			failures.Add( $"GATE 1 — '{ContractTool} action={ContractAction}' is NOT BOUND. The " +
				$"dispatcher will answer \"Unknown action '{ContractAction}'\". Bound actions: " +
				$"{string.Join( ", ", bound )}. Fix: add the action to " +
				".claude/hooks/mcp-action-metadata.snapshot.json and re-run " +
				"`python Tools/generate-mcp-discovery-adapters.py`." );
			Log.Error( $"[FAIL] {failures[^1]}" );
		}
		else
		{
			Log.Info( $"[PASS] GATE 1 — '{ContractTool} action={ContractAction}' is bound to " +
				$"{shot.Method?.DeclaringType?.Name}.{shot.Method?.Name}." );
		}

		// ── GATE 2 — advertised ─────────────────────────────────────────
		if ( advertised.Count == 0 )
		{
			failures.Add( "GATE 2 — could not read the advertised action enum from the tool's " +
				"inputSchema. The schema shape changed; this gate is blind until it is updated." );
			Log.Error( $"[FAIL] {failures[^1]}" );
		}
		else if ( !advertised.Contains( ContractAction ) )
		{
			failures.Add( $"GATE 2 — '{ContractAction}' is NOT ADVERTISED in the {ContractTool} action " +
				$"enum, so no client can discover it. Advertised: {string.Join( ", ", advertised )}. " +
				"Fix: add it to the @enum in ToolRegistry.cs." );
			Log.Error( $"[FAIL] {failures[^1]}" );
		}
		else
		{
			Log.Info( $"[PASS] GATE 2 — '{ContractAction}' is advertised ({advertised.Count} actions total)." );
		}

		// ── GATE 3 — the surfaces agree ─────────────────────────────────
		if ( advertised.Count > 0 )
		{
			var advertisedOnly = advertised.Except( bound ).ToList();
			var boundOnly = bound.Except( advertised ).ToList();
			if ( advertisedOnly.Count > 0 || boundOnly.Count > 0 )
			{
				failures.Add( $"GATE 3 — advertised and bound disagree. Advertised but NOT dispatchable " +
					$"(these fail at call time): [{string.Join( ", ", advertisedOnly )}]. Dispatchable but " +
					$"NOT advertised (these are invisible to clients): [{string.Join( ", ", boundOnly )}]." );
				Log.Error( $"[FAIL] {failures[^1]}" );
			}
			else
			{
				Log.Info( $"[PASS] GATE 3 — all {bound.Count} actions are both advertised and bound." );
			}
		}

		// ── GATE 4 — shot's threading ───────────────────────────────────
		if ( shot is not null )
		{
			if ( shot.Threading == McpThreading.MainThread )
			{
				failures.Add( "GATE 4 — shot is dispatched on the MainThread. Its play-mode route waits " +
					"for the game to composite and copy a frame, and blocking the main thread blocks the " +
					"very frames it is waiting on, so that route can only ever time out. Fix: restore the " +
					"per-action async case for capture/shot in Tools/generate-mcp-discovery-adapters.py." );
				Log.Error( $"[FAIL] {failures[^1]}" );
			}
			else
			{
				Log.Info( $"[PASS] GATE 4 — shot dispatches as {shot.Threading}, so it can wait for a frame." );
			}
		}

		// ── GATE 5 — the one output directory ───────────────────────────
		var dir = HandlerBase.ResolveProjectPath( ".screenshots" );
		if ( string.IsNullOrEmpty( dir ) )
		{
			failures.Add( "GATE 5 — could not resolve <repo>/.screenshots. shot has nowhere to write." );
			Log.Error( $"[FAIL] {failures[^1]}" );
		}
		else
		{
			try
			{
				System.IO.Directory.CreateDirectory( dir );
				var probe = System.IO.Path.Combine( dir, ".route-gate-probe" );
				System.IO.File.WriteAllText( probe, "ok" );
				System.IO.File.Delete( probe );
				Log.Info( $"[PASS] GATE 5 — {dir} exists and is writable." );
			}
			catch ( System.Exception ex )
			{
				failures.Add( $"GATE 5 — {dir} is not writable: {ex.Message}" );
				Log.Error( $"[FAIL] {failures[^1]}" );
			}
		}

		// ── Verdict ─────────────────────────────────────────────────────
		if ( failures.Count == 0 )
		{
			Log.Info( $"=== {TestName}: PASS (5/5 gates) — the contract's one screenshot route is " +
				"advertised, bound, correctly threaded and has somewhere to write. ===" );
		}
		else
		{
			Log.Error( $"=== {TestName}: FAIL ({failures.Count} gate(s)) — the screenshot route gate " +
				"is blocking six working alternatives in favour of a route that is not fully wired. " +
				"Contract: docs/ai/contracts/screenshot.md ===" );
		}
	}

	/// <summary>
	/// The action names the capture tool ADVERTISES, read out of its live inputSchema rather
	/// than from a list kept in this file. A hand-kept expectation here would rot exactly the
	/// way the thing it is watching rotted.
	/// </summary>
	private static List<string> ReadAdvertisedActions()
	{
		var found = new List<string>();
		try
		{
			var schema = ToolRegistry.GetInputSchema( ToolRegistry.Capture );
			var properties = schema?.GetType().GetProperty( "properties" )?.GetValue( schema );
			if ( properties is not IDictionary<string, object> dict ) return found;
			if ( !dict.TryGetValue( "action", out var actionProp ) || actionProp is null ) return found;

			var enumValue = actionProp.GetType().GetProperty( "enum" )?.GetValue( actionProp );
			if ( enumValue is IEnumerable<string> names )
				found.AddRange( names );
		}
		catch ( System.Exception ex )
		{
			Log.Warning( $"[sbep.test_capture_route] could not read the advertised action enum: {ex.Message}" );
		}
		return found;
	}
}
