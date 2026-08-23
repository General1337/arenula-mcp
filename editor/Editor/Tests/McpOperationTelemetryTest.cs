// Editor/Tests/McpOperationTelemetryTest.cs
//
// Durable regression gate for passive MCP operation telemetry. The test invokes
// the telemetry seam directly: it never mutates the live scene, starts play
// mode, or dispatches a synthetic MCP request through the shared editor.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula.Tests;

public static class McpOperationTelemetryTest
{
    [ConCmd( "sbep.test_mcp_operation_telemetry" )]
    public static void Run()
    {
        const string TestName = "sbep.test_mcp_operation_telemetry";
        int failures = 0;
        var root = HandlerBase.GetProjectRoot();
        var tempRoot = string.IsNullOrWhiteSpace( root )
            ? null
            : Path.Combine( root, ".mcp-telemetry", "tests", Guid.NewGuid().ToString( "N" ) );

        try
        {
            Log.Info( $"[INFO] {TestName}: ENTER v1 @ {DateTime.UtcNow:O}" );
            if ( string.IsNullOrWhiteSpace( root ) || string.IsNullOrWhiteSpace( McpOperationTelemetry.ArtifactPath ) )
                throw new InvalidOperationException( "Project root or telemetry artifact path was unavailable." );

            var markerThatMustNeverAppear = "SECRET-ARG-" + Guid.NewGuid().ToString( "N" );
            var session = new McpSession
            {
                SessionId = "telemetry-test-session",
                AgentId = "telemetry-test-agent",
                AgentSlug = "telemetry-test-slug"
            };
            var metadata = ToolRegistry.GetOperationMetadata( "mcp.health", null );
            var call = McpOperationTelemetry.Begin( session, "mcp.health", null, metadata, 123 );
            McpOperationTelemetry.BindOperation( call, "op_telemetrytest" );
            McpOperationTelemetry.Complete( call, "ok", 456 );

            var errorCall = McpOperationTelemetry.Begin(
                session, "schema", "missing", metadata, markerThatMustNeverAppear.Length );
            McpOperationTelemetry.Complete( errorCall, "tool_error", 42 );
            McpOperationTelemetry.FlushForTests();

            var rows = ReadTelemetryRows();
            var callRows = rows.Where( row => GetString( row, "call_id" ) == call.CallId ).ToArray();
            var start = callRows.FirstOrDefault( row => GetString( row, "phase" ) == "start" );
            var finish = callRows.FirstOrDefault( row => GetString( row, "phase" ) == "finish" );

            bool paired = start.ValueKind == JsonValueKind.Object
                && finish.ValueKind == JsonValueKind.Object;
            bool attributed = paired
                && GetString( finish, "session_id" ) == session.SessionId
                && GetString( finish, "agent_id" ) == session.AgentId
                && GetString( finish, "agent_slug" ) == session.AgentSlug
                && GetString( finish, "operation_id" ) == "op_telemetrytest";
            Gate(
                TestName,
                "paired_attribution",
                paired && attributed,
                "start/finish paired by call_id with session, agent, slug, and coordinator operation_id",
                ref failures );

            var allRaw = string.Join( "\n", ReadTelemetryText() );
            bool privateShape = !allRaw.Contains( markerThatMustNeverAppear )
                && !allRaw.Contains( "\"arguments\"" )
                && !allRaw.Contains( "\"params\"" )
                && !allRaw.Contains( "\"content\"" );
            Gate(
                TestName,
                "privacy_fixed_shape",
                privateShape,
                "artifact contains fixed metadata only; no arguments, params, response content, or secret marker",
                ref failures );

            var errorFinish = rows.FirstOrDefault( row =>
                GetString( row, "call_id" ) == errorCall.CallId
                && GetString( row, "phase" ) == "finish" );
            Gate(
                TestName,
                "typed_outcome",
                errorFinish.ValueKind == JsonValueKind.Object
                    && GetString( errorFinish, "outcome" ) == "tool_error",
                "tool failure outcome is machine-readable",
                ref failures );

            Directory.CreateDirectory( tempRoot );
            var rotationPath = Path.Combine( tempRoot, "rotation.jsonl" );
            bool rotationWrite = McpOperationTelemetry.TryAppendLinesForTest(
                rotationPath,
                new[] { new string( 'a', 700 ), new string( 'b', 700 ) },
                1024 );
            bool rotated = rotationWrite
                && File.Exists( rotationPath )
                && File.Exists( rotationPath + ".1" )
                && new FileInfo( rotationPath ).Length <= 1024
                && new FileInfo( rotationPath + ".1" ).Length <= 1024;
            Gate(
                TestName,
                "bounded_rotation",
                rotated,
                "active log and one backup remain within the configured bound",
                ref failures );

            var blocker = Path.Combine( tempRoot, "not-a-directory" );
            File.WriteAllText( blocker, "fixture" );
            bool isolated = !McpOperationTelemetry.TryAppendLinesForTest(
                Path.Combine( blocker, "operations.jsonl" ),
                new[] { "{}" },
                1024 );
            Gate(
                TestName,
                "write_failure_isolated",
                isolated,
                "unwritable artifact path returns false without throwing into the MCP call",
                ref failures );
        }
        catch ( Exception ex )
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: unexpected exception — {ex}" );
        }
        finally
        {
            try
            {
                if ( !string.IsNullOrWhiteSpace( tempRoot ) && Directory.Exists( tempRoot ) )
                    Directory.Delete( tempRoot, true );
            }
            catch
            {
                failures++;
                Log.Info( $"[FAIL] {TestName}: cleanup could not remove '{tempRoot}'" );
            }
        }

        var verdictPath = WriteDurableVerdict( root, TestName, failures );
        if ( verdictPath == null )
        {
            failures++;
            Log.Info( $"[FAIL] {TestName}: durable verdict could not be written" );
        }

        if ( failures == 0 )
            Log.Info( $"[PASS] {TestName}: SUMMARY — 5/5 gates passed; artifact={McpOperationTelemetry.ArtifactPath}; verdict={verdictPath}" );
        else
            Log.Info( $"[FAIL] {TestName}: SUMMARY — {failures} gate(s) failed; verdict={verdictPath ?? "unavailable"}" );
    }

    private static List<JsonElement> ReadTelemetryRows()
    {
        var rows = new List<JsonElement>();
        foreach ( var line in ReadTelemetryText() )
        {
            try
            {
                using var document = JsonDocument.Parse( line );
                rows.Add( document.RootElement.Clone() );
            }
            catch ( JsonException )
            {
                // A torn row is ignored by the report surface as well.
            }
        }
        return rows;
    }

    private static IEnumerable<string> ReadTelemetryText()
    {
        var path = McpOperationTelemetry.ArtifactPath;
        foreach ( var candidate in new[] { path + ".1", path } )
        {
            if ( !File.Exists( candidate ) ) continue;
            foreach ( var line in File.ReadLines( candidate ) )
            {
                if ( !string.IsNullOrWhiteSpace( line ) )
                    yield return line;
            }
        }
    }

    private static string GetString( JsonElement element, string property )
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty( property, out var value )
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string WriteDurableVerdict( string root, string testName, int failures )
    {
        try
        {
            if ( string.IsNullOrWhiteSpace( root ) ) return null;
            var directory = Path.Combine( root, ".mcp-telemetry", "test-verdicts" );
            Directory.CreateDirectory( directory );
            var path = Path.Combine( directory, testName + ".json" );
            var verdict = new
            {
                schema_version = 1,
                test = testName,
                outcome = failures == 0 ? "Pass" : "Fail",
                gates_total = 5,
                gates_passed = Math.Max( 0, 5 - failures ),
                failures,
                completed_utc = DateTime.UtcNow,
                telemetry_artifact = McpOperationTelemetry.ArtifactPath
            };
            File.WriteAllText( path, JsonSerializer.Serialize( verdict ) );
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void Gate(
        string testName,
        string gate,
        bool passed,
        string detail,
        ref int failures )
    {
        if ( !passed ) failures++;
        Log.Info( $"[{(passed ? "PASS" : "FAIL")}] {testName}: ASSERT {gate} — {detail}" );
    }
}
