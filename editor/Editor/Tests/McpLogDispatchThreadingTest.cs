// Editor/Tests/McpLogDispatchThreadingTest.cs
//
// Durable regression gate for the 2026-07-09 off-thread-Alloc console-spam bug
// (docs/ai/sessions/2026-07-09-mcp-request-offthread-alloc-spam/packet.md).
//
// THE BUG: ArenulaMcpServer raised OnLogMessage directly on the HTTP worker thread
// (LogSessionLifecycleInfo / LogConnectionError), and SafeRunOnMainThread — despite its
// name — dispatched via GameTask.RunInThreadAsync, which posts to SyncContext.WorkerThread.
// McpServerWindow subscribes to that event and builds Qt Labels in its handler; every Qt
// widget ctor calls InteropSystem.Alloc, which calls ThreadSafe.AssertIsMainThread and throws
// "Alloc must be called on the main thread!". The throw escaped through a finally block, and
// the catch that handled it wrote to an already-closed HttpListenerResponse, producing an
// ObjectDisposedException that nobody observed.
//
// THE INVARIANT THIS GATES: OnLogMessage subscribers are invoked on the main thread, no matter
// which thread raised the message. That is the single property whose violation caused all of it.
//
// PRECONDITION  ConCmd runs on the main editor thread (captures its id as the reference).
// EXEC          Raise ArenulaMcpServer.RaiseLogMessage from a genuine worker thread via
//               GameTask.RunInThreadAsync — the same shape as an HTTP handler logging an
//               SSE session open/close — then pump the main thread until the dispatch lands.
// ASSERT        Three gates, one log line each:
//                 raised_off_main_thread — the raising thread really was not the main thread
//                                          (guards against the test vacuously passing)
//                 subscriber_invoked     — the probe actually received the message
//                 invoked_on_main_thread — the probe ran on the main thread
// CLEANUP       Unsubscribe the probe. Idempotent; no fixtures.
//
// Run: sbep.test_mcp_log_offthread_dispatch

using System;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula.Tests;

public static class McpLogDispatchThreadingTest
{
    private const string Tag = "[sbep.test_mcp_log_offthread_dispatch]";

    /// <summary>Tier-3 regression gate: OnLogMessage must reach subscribers on the main thread.</summary>
    [ConCmd( "sbep.test_mcp_log_offthread_dispatch" )]
    public static async void Run()
    {
        Log.Info( $"{Tag} START" );

        var mainThreadId = Environment.CurrentManagedThreadId;
        var marker       = $"[TEST] offthread-dispatch-probe-{Guid.NewGuid():N}";

        var workerThreadId   = 0;
        var observedThreadId = 0;
        var observedCount    = 0;

        void Probe( string msg )
        {
            if ( msg is null || !msg.Contains( marker, StringComparison.Ordinal ) ) return;
            Volatile.Write( ref observedThreadId, Environment.CurrentManagedThreadId );
            Interlocked.Increment( ref observedCount );
        }

        var pass = false;

        try
        {
            ArenulaMcpServer.OnLogMessage += Probe;

            // EXEC — raise from a real worker thread, exactly as a request handler does.
            await GameTask.RunInThreadAsync( () =>
            {
                Volatile.Write( ref workerThreadId, Environment.CurrentManagedThreadId );
                ArenulaMcpServer.RaiseLogMessage( marker );
            } );

            // RaiseLogMessage is fire-and-forget; yield until the main-thread continuation lands.
            for ( var i = 0; i < 40 && Volatile.Read( ref observedCount ) == 0; i++ )
            {
                await Task.Delay( 25 );
                await GameTask.MainThread();
            }

            var worker   = Volatile.Read( ref workerThreadId );
            var observed = Volatile.Read( ref observedThreadId );
            var count    = Volatile.Read( ref observedCount );

            // Without this gate the test would pass vacuously if RunInThreadAsync ever ran inline.
            var gRaisedOffMain = worker != 0 && worker != mainThreadId;
            var gInvoked       = count > 0;
            var gOnMain        = observed == mainThreadId;

            Log.Info( $"{Tag} GATE raised_off_main_thread {( gRaisedOffMain ? "PASS" : "FAIL" )} (worker={worker} main={mainThreadId})" );
            Log.Info( $"{Tag} GATE subscriber_invoked     {( gInvoked ? "PASS" : "FAIL" )} (count={count})" );
            Log.Info( $"{Tag} GATE invoked_on_main_thread {( gOnMain ? "PASS" : "FAIL" )} (observed={observed} main={mainThreadId})" );

            pass = gRaisedOffMain && gInvoked && gOnMain;
        }
        catch ( Exception ex )
        {
            Log.Error( $"{Tag} THREW {ex}" );
            pass = false;
        }

        // CLEANUP — no await in a finally (SB1000); the unsubscribe is synchronous anyway.
        ArenulaMcpServer.OnLogMessage -= Probe;

        Log.Info( $"{Tag} SUMMARY {( pass ? "PASS" : "FAIL" )}" );
    }
}
