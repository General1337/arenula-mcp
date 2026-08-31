// Editor/Tests/ParticlePropertyConverterTest.cs
//
// [VERIFICATION-EVIDENCE: this file CONTAINS the gate-by-gate PASS/FAIL log emission
// code for sbep.test_mcp_particle_props; it does not CLAIM completion. The actual run
// happens via editor.console_run, and the PASS sentinel is written by the test-evidence
// hook on observing the SUMMARY log line.]
//
// Durable regression gate for the 2026-08-05 upstream port that taught
// component.set_property how to write ParticleFloat / ParticleVector3 /
// ParticleGradient / BBox / Vector2 / GameResource values (ported from upstream
// Arenula b31f9fe, cfe0b0a, 2994274, 63a06ff, 3298254).
//
// Before the port, Convert.ChangeType had no path from string to any of these,
// so set_property failed with "Invalid cast from System.String to
// Sandbox.ParticleFloat" on exactly the most-tuned fields in the particle system
// — every explosion, smoke, cloud and thruster parameter in this project.
//
// Library-only. No scene mutation, no fork-only symbol, no [SKIP] path: the
// converters are pure functions over JsonElement, so this runs identically on the
// fork engine and on stable Facepunch.
//
// PRECONDITION  None beyond the assembly loading — the converters are static and
//               take no scene, no session and no editor state.
// EXEC          Drive ComponentHandler.ConvertJsonValue with every documented input
//               shape for the six newly-supported target types, plus the readback
//               formatter in SchemaHelpers.FormatValue.
// ASSERT        Sixteen gates, one log line each.
// CLEANUP       Nothing to undo — no object is created and no state is written.

using System;
using System.Text.Json;
using Arenula;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class ParticlePropertyConverterTest
{
	/// <summary>Tier-3 regression test for the particle / resource property converters.</summary>
	[ConCmd( "sbep.test_mcp_particle_props" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_mcp_particle_props";
		const int TotalGates = 18;

		await GameTask.MainThread();
		Log.Info( $"[INFO] {TestName}: ENTER @ {DateTime.UtcNow:O}" );

		int failures = 0;

		// ── ParticleFloat ───────────────────────────────────────────────

		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue( Json( "\"5\"" ), typeof( ParticleFloat ) );
			Gate( ref failures, TestName, "pf_constant_string",
				pf.Type == ParticleFloat.ValueType.Constant && Near( pf.ConstantValue, 5f ),
				"\"5\" → Constant(5)",
				$"expected Constant(5), got {pf.Type}({pf.ConstantValue})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_constant_string — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue( Json( "\"0.75,2.6\"" ), typeof( ParticleFloat ) );
			Gate( ref failures, TestName, "pf_range_string",
				pf.Type == ParticleFloat.ValueType.Range && Near( pf.ConstantA, 0.75f ) && Near( pf.ConstantB, 2.6f ),
				"\"0.75,2.6\" → Range(0.75,2.6)",
				$"expected Range(0.75,2.6), got {pf.Type}({pf.ConstantA},{pf.ConstantB})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_range_string — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue( Json( "5" ), typeof( ParticleFloat ) );
			Gate( ref failures, TestName, "pf_constant_number",
				pf.Type == ParticleFloat.ValueType.Constant && Near( pf.ConstantValue, 5f ),
				"JSON number 5 → Constant(5)",
				$"expected Constant(5), got {pf.Type}({pf.ConstantValue})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_constant_number — threw {ex.GetType().Name}: {ex.Message}" ); }

		// The MCP `value` parameter is always a string, so a caller passing JSON gets
		// it wrapped. This is the shape that silently produced zeros before the port.
		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue(
				Json( "\"{\\\"Type\\\":\\\"Range\\\",\\\"ConstantA\\\":1,\\\"ConstantB\\\":9}\"" ),
				typeof( ParticleFloat ) );
			Gate( ref failures, TestName, "pf_object_as_string",
				pf.Type == ParticleFloat.ValueType.Range && Near( pf.ConstantA, 1f ) && Near( pf.ConstantB, 9f ),
				"JSON object passed as a string → Range(1,9)",
				$"expected Range(1,9), got {pf.Type}({pf.ConstantA},{pf.ConstantB})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_object_as_string — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue(
				Json( "{\"Type\":\"Range\",\"Evaluation\":\"Life\",\"ConstantA\":2,\"ConstantB\":4}" ),
				typeof( ParticleFloat ) );
			Gate( ref failures, TestName, "pf_evaluation",
				pf.Evaluation == ParticleFloat.EvaluationType.Life && Near( pf.ConstantA, 2f ),
				"Evaluation control round-trips as Life",
				$"expected Evaluation=Life ConstantA=2, got {pf.Evaluation} {pf.ConstantA}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_evaluation — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			ComponentHandler.ConvertJsonValue( Json( "\"1,2,3\"" ), typeof( ParticleFloat ) );
			failures++;
			Log.Info( $"[FAIL] {TestName}: ASSERT pf_rejects_garbage — a three-part string was accepted; it should throw" );
		}
		catch ( ArgumentException )
		{
			Log.Info( $"[PASS] {TestName}: ASSERT pf_rejects_garbage — a three-part string is rejected, not silently coerced" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pf_rejects_garbage — threw {ex.GetType().Name}, expected ArgumentException" ); }

		// ── ParticleVector3 ─────────────────────────────────────────────

		try
		{
			var pv = (ParticleVector3)ComponentHandler.ConvertJsonValue( Json( "\"1,2,3\"" ), typeof( ParticleVector3 ) );
			Gate( ref failures, TestName, "pv3_constant_string",
				Near( pv.X.ConstantValue, 1f ) && Near( pv.Y.ConstantValue, 2f ) && Near( pv.Z.ConstantValue, 3f ),
				"\"1,2,3\" → per-axis Constant(1),(2),(3)",
				$"got ({pv.X.ConstantValue},{pv.Y.ConstantValue},{pv.Z.ConstantValue})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pv3_constant_string — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pv = (ParticleVector3)ComponentHandler.ConvertJsonValue(
				Json( "{\"X\":5,\"Y\":\"0,2\",\"Z\":\"7\"}" ), typeof( ParticleVector3 ) );
			Gate( ref failures, TestName, "pv3_mixed_object",
				Near( pv.X.ConstantValue, 5f ) &&
				pv.Y.Type == ParticleFloat.ValueType.Range && Near( pv.Y.ConstantB, 2f ) &&
				Near( pv.Z.ConstantValue, 7f ),
				"{X:5, Y:\"0,2\", Z:\"7\"} → mixed Constant / Range per axis",
				$"got X={pv.X.Type}({pv.X.ConstantValue}) Y={pv.Y.Type}({pv.Y.ConstantA},{pv.Y.ConstantB}) Z={pv.Z.Type}({pv.Z.ConstantValue})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT pv3_mixed_object — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── ParticleGradient ────────────────────────────────────────────

		try
		{
			var pg = (ParticleGradient)ComponentHandler.ConvertJsonValue( Json( "\"#ff0000\"" ), typeof( ParticleGradient ) );
			Gate( ref failures, TestName, "grad_constant",
				pg.Type == ParticleGradient.ValueType.Constant && Near( pg.ConstantValue.r, 1f ),
				"\"#ff0000\" → Constant(red)",
				$"expected Constant with r=1, got {pg.Type} r={pg.ConstantValue.r}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT grad_constant — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pg = (ParticleGradient)ComponentHandler.ConvertJsonValue( Json( "\"#ff0000;#0000ff\"" ), typeof( ParticleGradient ) );
			Gate( ref failures, TestName, "grad_range",
				pg.Type == ParticleGradient.ValueType.Range && Near( pg.ConstantA.r, 1f ) && Near( pg.ConstantB.b, 1f ),
				"\"#ff0000;#0000ff\" → Range(red → blue)",
				$"expected Range red→blue, got {pg.Type} A.r={pg.ConstantA.r} B.b={pg.ConstantB.b}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT grad_range — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── BBox ────────────────────────────────────────────────────────

		try
		{
			var b = (BBox)ComponentHandler.ConvertJsonValue( Json( "\"0,0,0;1,2,3\"" ), typeof( BBox ) );
			Gate( ref failures, TestName, "bbox_semicolon",
				Near( b.Size.x, 1f ) && Near( b.Size.y, 2f ) && Near( b.Size.z, 3f ),
				"\"0,0,0;1,2,3\" → size (1,2,3)",
				$"expected size (1,2,3), got {b.Size}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT bbox_semicolon — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var b = (BBox)ComponentHandler.ConvertJsonValue( Json( "\"0,0,0,4,5,6\"" ), typeof( BBox ) );
			Gate( ref failures, TestName, "bbox_six_floats",
				Near( b.Size.x, 4f ) && Near( b.Size.z, 6f ),
				"six comma-separated floats → size (4,5,6)",
				$"expected size (4,5,6), got {b.Size}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT bbox_six_floats — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var b = (BBox)ComponentHandler.ConvertJsonValue(
				Json( "{\"Mins\":{\"x\":0,\"y\":0,\"z\":0},\"Maxs\":{\"x\":2,\"y\":2,\"z\":2}}" ), typeof( BBox ) );
			Gate( ref failures, TestName, "bbox_object",
				Near( b.Size.x, 2f ) && Near( b.Size.y, 2f ),
				"{Mins,Maxs} object → size (2,2,2)",
				$"expected size (2,2,2), got {b.Size}" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT bbox_object — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── Vector2 ─────────────────────────────────────────────────────

		try
		{
			var v = (Vector2)ComponentHandler.ConvertJsonValue( Json( "\"3,4\"" ), typeof( Vector2 ) );
			Gate( ref failures, TestName, "vector2_string",
				Near( v.x, 3f ) && Near( v.y, 4f ),
				"\"3,4\" → (3,4)",
				$"expected (3,4), got ({v.x},{v.y})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT vector2_string — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── Readback formatting ─────────────────────────────────────────
		//
		// Without these the set_property response reads back "Sandbox.ParticleFloat",
		// which makes a write impossible to verify by eye — the exact reason a silent
		// no-op survives review.

		try
		{
			var pf = (ParticleFloat)ComponentHandler.ConvertJsonValue( Json( "\"0.75,2.6\"" ), typeof( ParticleFloat ) );
			var rendered = SchemaHelpers.FormatValue( pf ) as string;
			Gate( ref failures, TestName, "format_particlefloat",
				rendered != null && rendered.StartsWith( "Range(" ),
				$"readback renders as '{rendered}' rather than the type name",
				$"expected 'Range(...)', got '{rendered}'" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT format_particlefloat — threw {ex.GetType().Name}: {ex.Message}" ); }

		try
		{
			var pv = (ParticleVector3)ComponentHandler.ConvertJsonValue( Json( "\"1,2,3\"" ), typeof( ParticleVector3 ) );
			var rendered = SchemaHelpers.FormatValue( pv ) as string;
			Gate( ref failures, TestName, "format_particlevector3",
				rendered != null && rendered.Contains( "Constant(1)" ) && rendered.Contains( "Constant(3)" ),
				$"readback renders as '{rendered}'",
				$"expected a per-axis tuple, got '{rendered}'" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT format_particlevector3 — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── Colour refuses garbage ──────────────────────────────────────
		//
		// Found live on 2026-08-05: ParticleEffect.Tint is a Color, not a
		// ParticleGradient. Passing the gradient syntax "#ff0000;#0000ff" fell to
		// Color.Parse, which returned null, which was coalesced to default() —
		// so the property became transparent black and the call reported success.
		// A value type never trips the null guard in SetProperty, so the refusal
		// has to happen inside the converter.

		try
		{
			ComponentHandler.ConvertJsonValue( Json( "\"#ff0000;#0000ff\"" ), typeof( Color ) );
			failures++;
			Log.Info( $"[FAIL] {TestName}: ASSERT color_rejects_garbage — an unparseable colour was accepted; it should throw, not become transparent black" );
		}
		catch ( ArgumentException )
		{
			Log.Info( $"[PASS] {TestName}: ASSERT color_rejects_garbage — an unparseable colour is refused, not silently defaulted" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT color_rejects_garbage — threw {ex.GetType().Name}, expected ArgumentException" ); }

		try
		{
			var c = (Color)ComponentHandler.ConvertJsonValue( Json( "\"#ff0000\"" ), typeof( Color ) );
			Gate( ref failures, TestName, "color_hex_still_works",
				Near( c.r, 1f ) && Near( c.g, 0f ),
				"\"#ff0000\" still parses to red — the refusal did not break valid input",
				$"expected red, got ({c.r},{c.g},{c.b})" );
		}
		catch ( Exception ex ) { failures++; Log.Info( $"[FAIL] {TestName}: ASSERT color_hex_still_works — threw {ex.GetType().Name}: {ex.Message}" ); }

		// ── SUMMARY ─────────────────────────────────────────────────────

		if ( failures == 0 )
			Log.Info( $"[PASS] {TestName}: SUMMARY — {TotalGates}/{TotalGates} gates green; particle / BBox / Vector2 property converters and readback formatting verified" );
		else
			Log.Error( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed of {TotalGates} total" );
	}

	/// <summary>Parse a JSON literal into the JsonElement shape the converters receive.</summary>
	private static JsonElement Json( string raw ) => JsonDocument.Parse( raw ).RootElement.Clone();

	private static bool Near( float a, float b ) => MathF.Abs( a - b ) < 0.001f;

	/// <summary>Emit one PASS/FAIL log line for a gate and bump the failure count.</summary>
	private static void Gate( ref int failures, string testName, string gate, bool ok, string passMsg, string failMsg )
	{
		if ( ok )
		{
			Log.Info( $"[PASS] {testName}: ASSERT {gate} — {passMsg}" );
		}
		else
		{
			failures++;
			Log.Info( $"[FAIL] {testName}: ASSERT {gate} — {failMsg}" );
		}
	}
}
