using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Arenula;

/// <summary>
/// Orchestrates local generation of the arenula-api schema by shelling out to the
/// sidecar arenula-schemagen.exe (which runs Facepunch.AssemblySchema outside the
/// sbox sandbox — see ../../schemagen/Program.cs). Writes to the api server's
/// existing cache path so the Node loader picks it up with no discovery handshake.
/// </summary>
internal static class SchemaExporter
{
	private const string SchemaFileName = "api-data.json";
	private const string CacheDirName = ".arenula-api";
	private const string SchemagenEnvVar = "ARENULA_SCHEMAGEN";

	// Fallback dev path. Override via ARENULA_SCHEMAGEN env var if your clone lives elsewhere.
	private static readonly string DefaultSchemagenExe = Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),
		"Documents", "Projects", "arenula-mcp", "schemagen", "bin", "Release", "net10.0",
		"arenula-schemagen.exe" );

	private static readonly SemaphoreSlim _writeLock = new( 1, 1 );

	internal static string GetCachePath()
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		return Path.Combine( home, CacheDirName, SchemaFileName );
	}

	internal static string GetSchemagenExePath()
	{
		var fromEnv = Environment.GetEnvironmentVariable( SchemagenEnvVar );
		return string.IsNullOrWhiteSpace( fromEnv ) ? DefaultSchemagenExe : fromEnv;
	}

	private static string GetManagedDir()
	{
		try
		{
			var loc = typeof( Sandbox.GameObject ).Assembly.Location;
			return Path.GetDirectoryName( loc );
		}
		catch
		{
			return null;
		}
	}

	private static string GetEngineDllPath()
	{
		var managed = GetManagedDir();
		if ( managed == null ) return null;
		var engine = Path.Combine( managed, "Sandbox.Engine.dll" );
		return File.Exists( engine ) ? engine : null;
	}

	private static string HashFile( string path )
	{
		if ( !File.Exists( path ) ) return null;
		using var sha = SHA256.Create();
		using var fs = File.OpenRead( path );
		var bytes = sha.ComputeHash( fs );
		return "sha256:" + Convert.ToHexString( bytes ).ToLowerInvariant();
	}

	private static MetaHeader ReadCachedMeta( string path )
	{
		if ( !File.Exists( path ) ) return null;
		try
		{
			using var fs = File.OpenRead( path );
			using var doc = JsonDocument.Parse( fs );
			if ( !doc.RootElement.TryGetProperty( "_meta", out var meta ) ) return null;
			return meta.Deserialize<MetaHeader>();
		}
		catch
		{
			return null;
		}
	}

	internal static bool IsStale()
	{
		var enginePath = GetEngineDllPath();
		if ( enginePath == null ) return false;

		var currentHash = HashFile( enginePath );
		var cached = ReadCachedMeta( GetCachePath() );
		if ( cached == null ) return true;
		return cached.EngineHash != currentHash;
	}

	/// <summary>
	/// Idempotent. Cheap when cache is fresh — just hashes the engine DLL. Regens
	/// otherwise. Catches and logs all exceptions; never throws.
	/// </summary>
	internal static async Task<RegenerateResult> RegenerateIfStale()
	{
		await _writeLock.WaitAsync();
		try
		{
			if ( !IsStale() )
			{
				Log.Info( "[arenula-mcp] schema cache fresh" );
				return new RegenerateResult { Regenerated = false, Reason = "fresh" };
			}

			Log.Info( "[arenula-mcp] regenerating schema: engine hash changed (or first run)" );
			var result = await RegenerateInternal();
			result.Reason = "stale";
			return result;
		}
		catch ( Exception ex )
		{
			Log.Error( $"[arenula-mcp] schema regen failed: {ex.Message}" );
			return new RegenerateResult { Regenerated = false, Reason = $"error: {ex.Message}" };
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// Unconditional regeneration. Throws on any failure (caller surfaces in MCP error).
	/// </summary>
	internal static async Task<RegenerateResult> Regenerate()
	{
		await _writeLock.WaitAsync();
		try
		{
			var result = await RegenerateInternal();
			result.Reason = "manual";
			return result;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private static async Task<RegenerateResult> RegenerateInternal()
	{
		var exe = GetSchemagenExePath();
		if ( !File.Exists( exe ) )
			throw new FileNotFoundException(
				$"arenula-schemagen.exe not found at {exe}. Set {SchemagenEnvVar} env var or build via `dotnet build -c Release` in arenula-mcp/schemagen/.",
				exe );

		var managed = GetManagedDir();
		if ( managed == null )
			throw new InvalidOperationException( "Could not locate sbox bin/managed via Sandbox.GameObject assembly." );

		var cachePath = GetCachePath();

		var psi = new ProcessStartInfo
		{
			FileName = exe,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add( cachePath );
		psi.ArgumentList.Add( managed );

		var sw = Stopwatch.StartNew();
		using var proc = Process.Start( psi )
			?? throw new InvalidOperationException( "Failed to start arenula-schemagen process." );

		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		var stderrTask = proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();
		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		sw.Stop();

		if ( proc.ExitCode != 0 )
		{
			var msg = string.IsNullOrWhiteSpace( stderr ) ? "(no stderr)" : stderr.Trim();
			throw new InvalidOperationException(
				$"arenula-schemagen exited {proc.ExitCode}: {msg}" );
		}

		// Last non-empty line of stdout is the JSON status (preceding lines may be informational).
		var lines = stdout.Split( '\n', StringSplitOptions.RemoveEmptyEntries );
		var jsonLine = lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";

		CliResult cli;
		try
		{
			cli = JsonSerializer.Deserialize<CliResult>( jsonLine )
				?? throw new InvalidOperationException( "stdout JSON parsed to null" );
		}
		catch ( Exception ex )
		{
			throw new InvalidOperationException(
				$"Failed to parse arenula-schemagen stdout: {ex.Message}. Raw: {jsonLine}" );
		}

		Log.Info( $"[arenula-mcp] schema regenerated: {cli.TypeCount} types, {cli.SizeBytes / 1024 / 1024} MB, {sw.Elapsed.TotalMilliseconds:F0}ms (cli reported {cli.DurationMs:F0}ms)" );

		return new RegenerateResult
		{
			Regenerated = true,
			TypeCount = cli.TypeCount,
			SizeBytes = cli.SizeBytes,
			DurationMs = sw.Elapsed.TotalMilliseconds,
			EngineHash = cli.EngineHash,
			GeneratedAt = cli.GeneratedAt,
			Path = cli.Path,
			DllCount = cli.DllCount,
			XmlCount = cli.XmlCount,
		};
	}

	internal sealed class RegenerateResult
	{
		public bool Regenerated { get; set; }
		public string Reason { get; set; }
		public int? TypeCount { get; set; }
		public long? SizeBytes { get; set; }
		public double? DurationMs { get; set; }
		public string EngineHash { get; set; }
		public string GeneratedAt { get; set; }
		public string Path { get; set; }
		public int? DllCount { get; set; }
		public int? XmlCount { get; set; }
	}

	private sealed class CliResult
	{
		[JsonPropertyName( "path" )] public string Path { get; set; }
		[JsonPropertyName( "sizeBytes" )] public long SizeBytes { get; set; }
		[JsonPropertyName( "typeCount" )] public int TypeCount { get; set; }
		[JsonPropertyName( "dllCount" )] public int DllCount { get; set; }
		[JsonPropertyName( "xmlCount" )] public int XmlCount { get; set; }
		[JsonPropertyName( "engineHash" )] public string EngineHash { get; set; }
		[JsonPropertyName( "generatedAt" )] public string GeneratedAt { get; set; }
		[JsonPropertyName( "durationMs" )] public double DurationMs { get; set; }
	}

	internal sealed class MetaHeader
	{
		[JsonPropertyName( "engineHash" )] public string EngineHash { get; set; }
		[JsonPropertyName( "engineDllPath" )] public string EngineDllPath { get; set; }
		[JsonPropertyName( "generatedAt" )] public string GeneratedAt { get; set; }
		[JsonPropertyName( "generator" )] public string Generator { get; set; }
		[JsonPropertyName( "schemaVersion" )] public int SchemaVersion { get; set; }
	}
}
