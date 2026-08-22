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

	// Fallback dev paths, probed in order. ARENULA_SCHEMAGEN always wins and is the only
	// supported answer for anyone who is not the author — these guesses exist so a local
	// clone in the usual spot works without configuration, nothing more.
	//
	// The single hardcoded path this replaced silently stopped resolving when the clone
	// moved under Projects/sbox/, and the failure surfaced only as "arenula-schemagen.exe
	// not found" with one path in it. Probing several and naming all of them in the error
	// is the difference between a two-second fix and a hunt.
	private static readonly string[] SchemagenProbePaths = BuildProbePaths();

	private static string[] BuildProbePaths()
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		string Candidate( params string[] parts )
		{
			var all = new string[parts.Length + 5];
			all[0] = home;
			all[1] = "Documents";
			all[2] = "Projects";
			Array.Copy( parts, 0, all, 3, parts.Length );
			all[parts.Length + 3] = "schemagen";
			all[parts.Length + 4] = Path.Combine( "bin", "Release", "net10.0", "arenula-schemagen.exe" );
			return Path.Combine( all );
		}
		return new[]
		{
			Candidate( "sbox", "arenula-mcp" ),
			Candidate( "arenula-mcp" ),
		};
	}

	private static readonly SemaphoreSlim _writeLock = new( 1, 1 );

	internal static string GetCachePath()
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		return Path.Combine( home, CacheDirName, SchemaFileName );
	}

	internal static string GetSchemagenExePath()
	{
		var fromEnv = Environment.GetEnvironmentVariable( SchemagenEnvVar );
		if ( !string.IsNullOrWhiteSpace( fromEnv ) )
			return fromEnv;

		foreach ( var candidate in SchemagenProbePaths )
		{
			if ( File.Exists( candidate ) )
				return candidate;
		}

		// Nothing found — return the first candidate so callers have a concrete path to
		// report. GetSchemagenProbeDescription() gives the full list for the error text.
		return SchemagenProbePaths[0];
	}

	/// <summary>Human-readable list of everywhere we looked, for error messages.</summary>
	internal static string GetSchemagenProbeDescription()
		=> $"{SchemagenEnvVar} env var (unset), then: {string.Join( ", ", SchemagenProbePaths )}";

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

	/// <summary>
	/// Stage every assembly schemagen must see into a scratch directory, and return it.
	///
	/// bin/managed alone is NOT the engine surface. The shipped addon packages compile
	/// separately to addons/*/.bin/ and addons/*/.sbox/bin/, and that is where the
	/// Sandbox.UI controls, CitizenAnimationHelper and NetworkHelper live — roughly 60
	/// types. Passing bin/managed straight to schemagen (which is what this did until
	/// 2026-08-15) produced a schema in which all of them read as "does not exist", and a
	/// missing assembly is worse than a stale schema because the absence looks
	/// authoritative. sbox-context carried a whole separate script to work around it.
	///
	/// schemagen globs Sandbox.*.dll and Facepunch.*.dll, so addon packages are staged
	/// under conforming names. Renaming the file does not change assembly identity.
	/// </summary>
	private static string StageAssemblies( string managed )
	{
		var staged = Path.Combine( Path.GetTempPath(), "arenula-schema-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( staged );

		foreach ( var pattern in new[] { "Sandbox.*.dll", "Sandbox.*.xml", "Facepunch.*.dll", "Facepunch.*.xml" } )
		{
			foreach ( var f in Directory.GetFiles( managed, pattern ) )
				File.Copy( f, Path.Combine( staged, Path.GetFileName( f ) ), true );
		}

		// <sbox>/bin/managed -> <sbox>
		var sboxRoot = Path.GetDirectoryName( Path.GetDirectoryName( managed ) );
		var addonsDir = sboxRoot == null ? null : Path.Combine( sboxRoot, "addons" );
		var addonCount = 0;

		if ( addonsDir != null && Directory.Exists( addonsDir ) )
		{
			// Pick the NEWEST copy of each assembly, never the first one enumerated. Both
			// locations can hold the same package and either may be the fresher one: an
			// engine update refreshes .bin/ while opening the editor rebuilds .sbox/bin/.
			// Positional precedence would silently stage whichever we happened to hit.
			var newest = new System.Collections.Generic.Dictionary<string, FileInfo>( StringComparer.OrdinalIgnoreCase );

			foreach ( var addon in Directory.GetDirectories( addonsDir ) )
			{
				foreach ( var rel in new[] { ".bin", Path.Combine( ".sbox", "bin" ) } )
				{
					var dir = Path.Combine( addon, rel );
					if ( !Directory.Exists( dir ) ) continue;
					foreach ( var dll in Directory.GetFiles( dir, "*.dll" ) )
					{
						var fi = new FileInfo( dll );
						var stem = Path.GetFileNameWithoutExtension( dll );
						if ( !newest.TryGetValue( stem, out var prev ) || fi.LastWriteTimeUtc > prev.LastWriteTimeUtc )
							newest[stem] = fi;
					}
				}
			}

			foreach ( var kv in newest )
			{
				var safe = kv.Key.Replace( " ", "" ).Replace( ".", "-" );
				File.Copy( kv.Value.FullName, Path.Combine( staged, $"Sandbox.addon-{safe}.dll" ), true );
				addonCount++;
			}
		}

		if ( addonCount == 0 )
		{
			Log.Warning( "[arenula-mcp] no addon packages found under " +
				$"{addonsDir ?? "(unknown)"} — the schema will cover the engine only, and " +
				"base-addon types (Sandbox.UI controls, CitizenAnimationHelper, NetworkHelper) " +
				"will read as absent." );
		}

		return staged;
	}

	private static void TryDeleteDirectory( string dir )
	{
		try
		{
			if ( dir != null && Directory.Exists( dir ) )
				Directory.Delete( dir, true );
		}
		catch ( Exception ex )
		{
			// A leaked temp dir is not worth failing a successful generation over.
			Log.Info( $"[arenula-mcp] could not clean up staging dir {dir}: {ex.Message}" );
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
				$"arenula-schemagen.exe not found. Looked in: {GetSchemagenProbeDescription()}. " +
				$"Set {SchemagenEnvVar} to its full path, or build it via `dotnet build -c Release` in arenula-mcp/schemagen/.",
				exe );

		var managed = GetManagedDir();
		if ( managed == null )
			throw new InvalidOperationException( "Could not locate sbox bin/managed via Sandbox.GameObject assembly." );

		var cachePath = GetCachePath();

		// Engine assemblies alone would omit the shipped addon packages; see StageAssemblies.
		var staged = StageAssemblies( managed );

		string stdout, stderr;
		Stopwatch sw;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = exe,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			psi.ArgumentList.Add( cachePath );
			psi.ArgumentList.Add( staged );

			sw = Stopwatch.StartNew();
			using var proc = Process.Start( psi )
				?? throw new InvalidOperationException( "Failed to start arenula-schemagen process." );

			var stdoutTask = proc.StandardOutput.ReadToEndAsync();
			var stderrTask = proc.StandardError.ReadToEndAsync();
			await proc.WaitForExitAsync();
			stdout = await stdoutTask;
			stderr = await stderrTask;
			sw.Stop();

			if ( proc.ExitCode != 0 )
			{
				var failMsg = string.IsNullOrWhiteSpace( stderr ) ? "(no stderr)" : stderr.Trim();
				throw new InvalidOperationException(
					$"arenula-schemagen exited {proc.ExitCode}: {failMsg}" );
			}
		}
		finally
		{
			TryDeleteDirectory( staged );
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
