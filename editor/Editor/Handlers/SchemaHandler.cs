using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>
/// Handles schema-cache actions: export (force regen), info (cache state), clear.
/// Schema generation itself is delegated to SchemaExporter (which shells out to
/// the arenula-schemagen CLI sidecar).
/// </summary>
internal static class SchemaHandler
{
	internal static async Task<object> HandleAsync( string action, JsonElement args )
	{
		try
		{
			return action switch
			{
				"export" => await Export(),
				"info"   => Info(),
				"clear"  => Clear(),
				_        => HandlerBase.Error( $"Unknown action '{action}' for tool 'schema'.", action,
				              "Valid actions: export, info, clear" )
			};
		}
		catch ( Exception ex )
		{
			return HandlerBase.Error( ex.Message, action,
				"Check that arenula-schemagen.exe exists (build it via `dotnet build -c Release` in arenula-mcp/schemagen/, or set the ARENULA_SCHEMAGEN env var to its location)." );
		}
	}

	// ── export ────────────────────────────────────────────────────────────

	private static async Task<object> Export()
	{
		var result = await SchemaExporter.Regenerate();
		return HandlerBase.Success( new
		{
			message = "Schema regenerated.",
			path = result.Path,
			sizeBytes = result.SizeBytes,
			typeCount = result.TypeCount,
			dllCount = result.DllCount,
			xmlCount = result.XmlCount,
			engineHash = result.EngineHash,
			generatedAt = result.GeneratedAt,
			durationMs = result.DurationMs,
		} );
	}

	// ── info ──────────────────────────────────────────────────────────────

	private static object Info()
	{
		var cachePath = SchemaExporter.GetCachePath();
		var schemagenExe = SchemaExporter.GetSchemagenExePath();
		var schemagenExists = File.Exists( schemagenExe );

		if ( !File.Exists( cachePath ) )
		{
			return HandlerBase.Success( new
			{
				exists = false,
				path = cachePath,
				schemagenExe,
				schemagenExists,
				suggestion = schemagenExists
					? "Call schema.export to generate."
					: $"arenula-schemagen.exe missing at {schemagenExe}. Build via `dotnet build -c Release` in arenula-mcp/schemagen/, or set ARENULA_SCHEMAGEN.",
			} );
		}

		var fi = new FileInfo( cachePath );
		var stale = SchemaExporter.IsStale();
		return HandlerBase.Success( new
		{
			exists = true,
			path = cachePath,
			sizeBytes = fi.Length,
			mtime = fi.LastWriteTimeUtc.ToString( "o" ),
			stale,
			schemagenExe,
			schemagenExists,
			suggestion = stale ? "Cache is stale — call schema.export to regenerate." : null,
		} );
	}

	// ── clear ─────────────────────────────────────────────────────────────

	private static object Clear()
	{
		var path = SchemaExporter.GetCachePath();
		if ( !File.Exists( path ) )
			return HandlerBase.Confirm( "Already absent." );
		File.Delete( path );
		return HandlerBase.Confirm( "Schema cache cleared." );
	}
}
