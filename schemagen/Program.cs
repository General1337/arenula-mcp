using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Facepunch.AssemblySchema;

namespace ArenulaSchemagen;

internal static class Program
{
	private const int CurrentSchemaVersion = 1;
	private const string GeneratorName = "arenula-schemagen v1";

	private static int Main(string[] args)
	{
		if (args.Length < 1 || args.Contains("--help") || args.Contains("-h"))
		{
			PrintUsage();
			return args.Contains("--help") || args.Contains("-h") ? 0 : 2;
		}

		var outputPath = args[0];
		var managedDir = args.Length >= 2
			? args[1]
			: DiscoverManagedDir();

		if (managedDir is null)
		{
			WriteError("Could not locate s&box bin/managed directory. Pass it as the second argument.");
			return 1;
		}

		if (!Directory.Exists(managedDir))
		{
			WriteError($"Managed directory does not exist: {managedDir}");
			return 1;
		}

		try
		{
			var result = Generate(outputPath, managedDir);
			Console.Out.WriteLine(JsonSerializer.Serialize(result));
			return 0;
		}
		catch (Exception ex)
		{
			WriteError($"Schema generation failed: {ex.Message}");
			WriteError(ex.StackTrace ?? "");
			return 1;
		}
	}

	private static GenerateResult Generate(string outputPath, string managedDir)
	{
		var sw = Stopwatch.StartNew();
		var startedAt = DateTime.UtcNow;

		var dlls = Directory.GetFiles(managedDir, "Sandbox.*.dll");
		if (dlls.Length == 0)
			throw new InvalidOperationException($"No Sandbox.*.dll files found in {managedDir}");

		var enginePath = dlls.FirstOrDefault(p =>
			string.Equals(Path.GetFileName(p), "Sandbox.Engine.dll", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException("Sandbox.Engine.dll not found in managed dir");

		using var builder = new Builder();
		int xmlCount = 0;
		foreach (var dll in dlls)
		{
			var pdbPath = Path.ChangeExtension(dll, ".pdb");
			var pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
			builder.AddAssembly(File.ReadAllBytes(dll), pdbBytes);
			var xml = Path.ChangeExtension(dll, ".xml");
			if (File.Exists(xml))
			{
				builder.AddDocumentation(File.ReadAllBytes(xml));
				xmlCount++;
			}
		}

		var schema = builder.Build();
		schema.StripNonPublic();
		var typeCount = schema.Types?.Count ?? 0;

		var engineHash = HashFile(enginePath);
		var wrapped = new
		{
			_meta = new MetaHeader
			{
				EngineHash = engineHash,
				EngineDllPath = enginePath.Replace('\\', '/'),
				GeneratedAt = startedAt.ToString("o"),
				Generator = GeneratorName,
				SchemaVersion = CurrentSchemaVersion,
			},
			schema.Types,
		};

		var json = JsonSerializer.Serialize(wrapped);

		var outDir = Path.GetDirectoryName(outputPath);
		if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
			Directory.CreateDirectory(outDir);

		var tmpPath = outputPath + ".tmp";
		File.WriteAllText(tmpPath, json);
		if (File.Exists(outputPath)) File.Delete(outputPath);
		File.Move(tmpPath, outputPath);

		sw.Stop();
		var sizeBytes = new FileInfo(outputPath).Length;

		return new GenerateResult
		{
			Path = outputPath,
			SizeBytes = sizeBytes,
			TypeCount = typeCount,
			DllCount = dlls.Length,
			XmlCount = xmlCount,
			EngineHash = engineHash,
			GeneratedAt = startedAt.ToString("o"),
			DurationMs = sw.Elapsed.TotalMilliseconds,
		};
	}

	private static string? DiscoverManagedDir()
	{
		var candidates = new[]
		{
			@"C:\Program Files (x86)\Steam\steamapps\common\sbox\bin\managed",
			@"C:\Program Files\Steam\steamapps\common\sbox\bin\managed",
			@"D:\SteamLibrary\steamapps\common\sbox\bin\managed",
			@"D:\Steam\steamapps\common\sbox\bin\managed",
		};
		return candidates.FirstOrDefault(Directory.Exists);
	}

	private static string HashFile(string path)
	{
		using var sha = SHA256.Create();
		using var fs = File.OpenRead(path);
		var bytes = sha.ComputeHash(fs);
		return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
	}

	private static void WriteError(string msg) =>
		Console.Error.WriteLine($"[arenula-schemagen] {msg}");

	private static void PrintUsage()
	{
		Console.Out.WriteLine("Usage: arenula-schemagen <output-path> [managed-dir]");
		Console.Out.WriteLine();
		Console.Out.WriteLine("  output-path  Absolute path to write the schema JSON (e.g. ~/.arenula-api/api-data.json)");
		Console.Out.WriteLine("  managed-dir  Optional. Path to s&box bin/managed/. Auto-discovered if omitted.");
		Console.Out.WriteLine();
		Console.Out.WriteLine("On success, prints a one-line JSON status to stdout, exits 0.");
		Console.Out.WriteLine("On failure, writes error to stderr, exits non-zero.");
	}
}

internal sealed class GenerateResult
{
	[JsonPropertyName("path")] public string Path { get; set; } = "";
	[JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
	[JsonPropertyName("typeCount")] public int TypeCount { get; set; }
	[JsonPropertyName("dllCount")] public int DllCount { get; set; }
	[JsonPropertyName("xmlCount")] public int XmlCount { get; set; }
	[JsonPropertyName("engineHash")] public string EngineHash { get; set; } = "";
	[JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = "";
	[JsonPropertyName("durationMs")] public double DurationMs { get; set; }
}

internal sealed class MetaHeader
{
	[JsonPropertyName("engineHash")] public string EngineHash { get; set; } = "";
	[JsonPropertyName("engineDllPath")] public string EngineDllPath { get; set; } = "";
	[JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = "";
	[JsonPropertyName("generator")] public string Generator { get; set; } = "";
	[JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
}
