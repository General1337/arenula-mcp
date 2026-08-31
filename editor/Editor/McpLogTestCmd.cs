using System;
using Sandbox;

namespace Arenula;

/// <summary>
/// Verification-only ConCmd that exercises every Log.* severity plus a caught
/// exception. Used by Phase-2 exit-criteria checks for MCP structured log capture.
/// Kept in the MCP plugin so it ships with the log-capture change and can be
/// removed without touching game code.
/// </summary>
internal static class McpLogTestCmd
{
	[ConCmd( "mcp.logtest", Help = "Emit Info/Warning/Error + caught exception to verify MCP log capture." )]
	public static void Run()
	{
		Log.Info( "MCP-CAPTURE-TEST-INFO" );
		Log.Warning( "MCP-CAPTURE-TEST-WARN" );
		Log.Error( "MCP-CAPTURE-TEST-ERROR" );
		try
		{
			throw new InvalidOperationException( "MCP-CAPTURE-TEST-EX" );
		}
		catch ( Exception ex )
		{
			Log.Error( ex, "MCP-CAPTURE-TEST-EX-WRAPPED" );
		}
	}
}
