using System;
using System.Text.Json;
using Sandbox;

namespace Arenula.Tests;

/// <summary>Self-cleaning regression gate for official-MCP borrow wave A.</summary>
public static class McpBorrowWaveATest
{
    private sealed class CompactFixtureComponent : Component
    {
    }

    [ConCmd( "sbep.test_mcp_borrow_wave_a" )]
    public static void Run()
    {
        const string testName = "sbep.test_mcp_borrow_wave_a";
        int pass = 0, fail = 0;
        Log.Info( $"=== {testName} ===" );

        using ( var doc = SerializeEnvelope( HandlerBase.Success( new { ok = true, count = 3 } ) ) )
        {
            var root = doc.RootElement;
            var text = root.GetProperty( "content" )[0].GetProperty( "text" ).GetString();
            var structured = root.GetProperty( "structuredContent" ).GetRawText();
            Gate( testName, "structured_text_twin", text == structured, $"text={text} structured={structured}", ref pass, ref fail );
            Gate( testName, "success_omits_is_error", !root.TryGetProperty( "isError", out _ ), root.GetRawText(), ref pass, ref fail );
        }

        var error = HandlerBase.Error( "expected failure", "test", "none", new { marker = 7 }, McpErrorCode.InvalidArgs );
        using ( var doc = SerializeEnvelope( error ) )
        {
            var root = doc.RootElement;
            var structured = root.GetProperty( "structuredContent" );
            Gate( testName, "typed_error_flag", McpResult.IsErrorResult( error ) && root.GetProperty( "isError" ).GetBoolean(), root.GetRawText(), ref pass, ref fail );
            Gate( testName, "structured_error_payload",
                structured.GetProperty( "error" ).GetString() == "expected failure"
                && structured.GetProperty( "error_code" ).GetString() == McpErrorCode.InvalidArgs,
                structured.GetRawText(), ref pass, ref fail );
        }

        using ( var doc = SerializeEnvelope( HandlerBase.Text( "plain" ) ) )
        {
            Gate( testName, "plain_text_unchanged",
                !doc.RootElement.TryGetProperty( "structuredContent", out _ )
                && doc.RootElement.GetProperty( "content" )[0].GetProperty( "text" ).GetString() == "plain",
                doc.RootElement.GetRawText(), ref pass, ref fail );
        }

        using ( var doc = SerializeEnvelope( HandlerBase.Success( new { payload = new string( 'x', HandlerBase.MaxResponseChars + 100 ) } ) ) )
        {
            Gate( testName, "paged_result_stays_text_only", !doc.RootElement.TryGetProperty( "structuredContent", out _ ),
                "large result did not expose partial structured content", ref pass, ref fail );
        }

        GameObject gameObject = null;
        try
        {
            gameObject = new GameObject( false, "mcp_borrow_wave_a_fixture" );
            Component component = gameObject.Components.Create<CompactFixtureComponent>();
            using var doc = SerializeEnvelope( HandlerBase.Success( new { gameObject, component } ) );
            var structured = doc.RootElement.GetProperty( "structuredContent" );
            var goRef = structured.GetProperty( "gameObject" );
            var componentRef = structured.GetProperty( "component" );
            var compact = goRef.GetProperty( "id" ).GetString() == gameObject.Id.ToString()
                && goRef.GetProperty( "name" ).GetString() == gameObject.Name
                && !goRef.TryGetProperty( "children", out _ )
                && componentRef.GetProperty( "id" ).GetString() == component.Id.ToString()
                && componentRef.GetProperty( "gameObject" ).GetString() == gameObject.Id.ToString();
            Gate( testName, "compact_scene_references", compact, structured.GetRawText(), ref pass, ref fail );
        }
        catch ( Exception exception )
        {
            Gate( testName, "compact_scene_references", false, exception.Message, ref pass, ref fail );
        }
        finally
        {
            gameObject?.Destroy();
        }

        var verdict = fail == 0 ? "PASS" : "FAIL";
        var summary = $"{testName}: {verdict} pass={pass} fail={fail}";
        if ( fail == 0 ) Log.Info( summary ); else Log.Error( summary );
    }

    private static JsonDocument SerializeEnvelope( object value )
        => JsonDocument.Parse( JsonSerializer.Serialize( value, ArenulaMcpServer.JsonOptions ) );

    private static void Gate( string testName, string name, bool ok, string detail, ref int pass, ref int fail )
    {
        if ( ok )
        {
            pass++;
            Log.Info( $"  [PASS] {testName}: {name} — {detail}" );
        }
        else
        {
            fail++;
            Log.Error( $"  [FAIL] {testName}: {name} — {detail}" );
        }
    }
}
