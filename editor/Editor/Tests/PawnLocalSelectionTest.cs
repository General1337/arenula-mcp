// Editor/Tests/PawnLocalSelectionTest.cs
//
// Durable regression gate for pawn.* selecting an inert editor/template
// PlayerController instead of the live locally-controlled play-scene pawn.
// [TEST-PARAMS-INTENTIONAL: no production parameters are overridden; the test
// feeds immutable controller facts into the production selector.]

using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula.Tests;

public static class PawnLocalSelectionTest
{
	[ConCmd( "sbep.test_mcp_pawn_local_selection" )]
	public static void Run( string mode = "" )
	{
		const string TestName = "sbep.test_mcp_pawn_local_selection";
		var selfTest = string.Equals( mode, "selftest", StringComparison.OrdinalIgnoreCase );

		var candidates = new List<PawnHandler.ControllerCandidateFacts>
		{
			new()
			{
				Valid = true, Enabled = true, InActiveScene = false,
				Networked = false, IsOwner = false, IsProxy = false
			},
			new()
			{
				Valid = true, Enabled = true, InActiveScene = true,
				Networked = true, IsOwner = true, IsProxy = true
			},
			new()
			{
				Valid = true, Enabled = true, InActiveScene = true,
				Networked = true, IsOwner = !selfTest, IsProxy = selfTest
			}
		};

		var selected = PawnHandler.SelectControllerCandidate(
			candidates, isPlaying: true, out var reason );
		var gates = new List<(string name, bool pass, string detail)>
		{
			("selection_exists", selected >= 0,
				$"selected={selected} reason={reason ?? "<none>"}"),
			("live_candidate_selected", selected == 2,
				$"selected={selected}; candidate[0] is the inert template"),
			("owner_reason_reported", reason == "active_scene_network_owner",
				$"reason={reason ?? "<none>"}"),
			("contradictory_owner_proxy_rejected", selected == 2,
				$"candidate[1] owner=true/proxy=true must be rejected; selected={selected}")
		};

		foreach ( var (name, pass, detail) in gates )
			Log.Info( $"  [{(pass ? "PASS" : "FAIL")}] {name}  — {detail}" );

		if ( selfTest )
		{
			var survivors = gates.Where( g => g.pass ).Select( g => g.name ).ToList();
			if ( survivors.Count == 0 )
				Log.Info( $"{TestName} [SELFTEST]: PASS ({gates.Count} gates bite)" );
			else
				Log.Error( $"{TestName} [SELFTEST]: FAIL — VACUOUS: {string.Join( ", ", survivors )}" );
			return;
		}

		var failures = gates.Count( g => !g.pass );
		if ( failures == 0 ) Log.Info( $"{TestName}: PASS ({gates.Count} gates)" );
		else Log.Error( $"{TestName}: FAIL ({failures} of {gates.Count})" );
	}
}
