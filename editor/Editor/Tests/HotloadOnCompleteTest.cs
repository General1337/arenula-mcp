// [HANDOFF-TRUST: closeout refactor to reflection-bridge — independent of H1 raise-site hypothesis, which the predecessor already verified. Engine fork commits ed7b424d / 764a599b are not touched.]
//
// engine-fork-elite-leverage Phase 1.3 closeout — test ConCmd for
// Sandbox.Hotload.OnComplete.
//
// Durable regression gate for the AP-069 cure. Probes the fork-only event
// via the reflection bridge in Libraries/arenula_mcp/Editor/Reflection/
// HotloadResultBridge.cs so this file compiles identically against both
// fork and stable Facepunch engines.
//
// Under fork engine: subscribes via the bridge, creates a synthetic Hotload
// instance via reflection on HotloadResultBridge.HotloadType, calls
// UpdateReferences() with no swaps queued (which returns
// HotloadResult.NoActionSingleton per engine source UpdateReferences.cs:64-65,
// and which Phase 1.3 also raises OnComplete for — the NoAction inclusion
// is deliberate per AP-069 disambiguation). Asserts the event fired with a
// non-null result whose NoAction == true.
//
// Under stable engine: the bridge.IsAvailable returns false and the test
// emits one [SKIP] log line. Stable runs do not falsely report failure.
//
// Why a synthetic Hotload instance + reflection-invoked UpdateReferences
// (not e.g. EditorUtility.Projects.Compile or a real hotload trigger):
// - Deterministic. NoAction is the single-deterministic outcome of "fresh
//   Hotload + UpdateReferences with no pending swaps." A real editor-driven
//   hotload would depend on file-watcher timing, build state, and what
//   other agents are doing concurrently in a multi-agent session.
// - Fast (<10ms). No build wait.
// - Tests the actual raise path (UpdateReferences calls RaiseOnComplete).
//   If someone removes the RaiseOnComplete call from UpdateReferences in
//   the future, this test breaks — which is exactly what we want.
// - Zero side effects on the editor's live hotload pipeline. The synthetic
//   instance has its own Swaps dictionary, doesn't touch the editor's
//   HotloadManager.Hotload.
//
// Why reflection bridge (not direct typeof + Action<HotloadResult>):
// engine/Sandbox.Compiling/Compiler/Compiler.References.cs:8-16 hardcodes
// the library compile reference set. "Sandbox.Hotload" is NOT in the list.
// Direct `typeof(Hotload)` / `HotloadResult` parameter / `HotloadEntryType`
// references fail to compile under both fork AND stable engines. See
// HotloadResultBridge.cs for the full rationale.
//
// VERIFIED via engine source read 2026-05-18 (codex/hotload-full-fallback,
// engine/Sandbox.Hotload/Hotload.OnComplete.cs commit ed7b424d +
// engine/Sandbox.Hotload/UpdateReferences.cs):
//   * OnComplete declared `public static event Action<HotloadResult>` on
//     Sandbox.Hotload partial class (Hotload.OnComplete.cs:64).
//   * Raised at UpdateReferences.cs:65 (NoAction early-out) and :172 (normal
//     completion) via internal static RaiseOnComplete(HotloadResult) helper.
//   * Per-subscriber try/catch in RaiseOnComplete shields the chain.
//   * Hotload ctor signature: public Hotload(bool addDefaultUpgraders=true,
//     Sandbox.Diagnostics.Logger logger=null) at Hotload.cs:73.
// Engine source: M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
//                Sandbox.Hotload/Hotload.OnComplete.cs

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Arenula.Reflection;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class HotloadOnCompleteTest
{
	/// <summary>
	/// Tier-3 regression test for the AP-069 cure.
	///   PRECONDITION — bridge.IsAvailable check. Under stable engine the
	///                  test [SKIP]s here.
	///   EXEC         — subscribe via bridge, create a fresh Hotload via
	///                  reflection on bridge.HotloadType, call
	///                  UpdateReferences() with no swaps queued (returns
	///                  NoActionSingleton). 100ms grace.
	///   ASSERT       — eventFires in [1, 5] (tolerance for concurrent real
	///                  hotload), capturedResult non-null, NoAction == true.
	///                  (Created timestamp NOT checked — NoActionSingleton
	///                  is a class-level singleton populated at engine load.)
	///   CLEANUP      — unsubscribes regardless of pass/fail.
	/// </summary>
	[ConCmd( "sbep.test_hotload_on_complete" )]
	public static async void Run()
	{
		const string TestName = "sbep.test_hotload_on_complete";

		// ── PRECONDITION ────────────────────────────────────────────────
		await GameTask.MainThread();

		if ( !HotloadResultBridge.IsAvailable )
		{
			Log.Info( $"[SKIP] {TestName}: fork-only event Sandbox.Hotload.OnComplete not available — {HotloadResultBridge.InitFailureReason ?? "unknown reason"}. This test is fork-only by design." );
			return;
		}

		Log.Info( $"[PASS] {TestName}: PRECONDITION — fork event present (HotloadResultBridge.IsAvailable=true; Sandbox.Hotload.OnComplete bound via reflection)" );

		// ── EXEC ────────────────────────────────────────────────────────
		int eventFires = 0;
		object capturedResult = null;

		void Handler( object result )
		{
			Interlocked.Increment( ref eventFires );
			capturedResult = result;
		}

		Delegate subscription = HotloadResultBridge.Subscribe( Handler );

		try
		{
			var hotloadType = HotloadResultBridge.HotloadType;

			// Synthetic Hotload instance via reflection on the (bool, Logger)
			// constructor. Pass (false, null) to skip default upgrader
			// registration (faster + side-effect-free).
			var ctor = hotloadType.GetConstructor(
				BindingFlags.Public | BindingFlags.Instance,
				null,
				new[] { typeof( bool ), typeof( Sandbox.Diagnostics.Logger ) },
				null );
			if ( ctor == null )
			{
				Log.Info( $"[FAIL] {TestName}: EXEC — could not find Hotload(bool, Logger) constructor via reflection. Engine API may have shifted." );
				return;
			}

			var hotloadInstance = ctor.Invoke( new object[] { false, null } );

			var updateReferencesMethod = hotloadType.GetMethod(
				"UpdateReferences",
				BindingFlags.Public | BindingFlags.Instance );
			if ( updateReferencesMethod == null )
			{
				Log.Info( $"[FAIL] {TestName}: EXEC — could not find Hotload.UpdateReferences method via reflection. Engine API may have shifted." );
				return;
			}

			// No Swaps queued → UpdateReferences returns NoActionSingleton at
			// engine UpdateReferences.cs:65. Phase 1.3 raises OnComplete here.
			updateReferencesMethod.Invoke( hotloadInstance, null );

			// Engine raises the event synchronously inside UpdateReferences
			// BEFORE the Invoke return. By the time we resume it should
			// already be fired. Allow a small grace window only for the rare
			// case where the call is dispatched onto a different sync context.
			await Task.Delay( 100 );

			Log.Info( $"[PASS] {TestName}: EXEC — UpdateReferences() returned, event handler invoked {eventFires}× during window" );

			// ── ASSERT ──────────────────────────────────────────────────
			if ( eventFires == 0 )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — event did NOT fire after UpdateReferences (expected ≥1)" );
				return;
			}
			if ( eventFires > 5 )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — event fired {eventFires}× (expected 1-5; >5 indicates duplicate subscription or runaway hotload activity)" );
				return;
			}
			if ( capturedResult == null )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — captured result is null" );
				return;
			}
			if ( !HotloadResultBridge.NoAction( capturedResult ) )
			{
				Log.Info( $"[FAIL] {TestName}: ASSERT — expected captured.NoAction==true (synthetic Hotload had no swaps queued, expected NoActionSingleton path) but got false. This indicates the event fired from a CONCURRENT real hotload rather than our synthetic trigger — re-run when the editor is idle." );
				return;
			}

			Log.Info( $"[PASS] {TestName}: ASSERT — event fired {eventFires}× with NoAction=true HotloadResult; AP-069 cure verified end-to-end (UpdateReferences→RaiseOnComplete→reflection-bridge-bound subscriber→typed HotloadResult payload)" );
		}
		finally
		{
			// ── CLEANUP ─────────────────────────────────────────────────
			HotloadResultBridge.Unsubscribe( subscription );
			Log.Info( $"[PASS] {TestName}: CLEANUP — handler unsubscribed" );
		}
	}
}
