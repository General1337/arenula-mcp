using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// MCP handler for SBEP system verification. Provides structured validation
/// results that AI agents can parse to detect and fix implementation issues.
/// Supports both editor-mode (static) and play-mode (runtime) checks.
/// </summary>
internal static class SbepVerifyHandler
{
    private readonly record struct VerificationIssue( string severity, string message );

    private static readonly Regex GuidHexPattern = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "prefab_check"    => await PrefabCheck( args ),
                "component_check" => await ComponentCheck( args ),
                "smoke_test"      => await SmokeTest( args ),
                "resource_health" => await ResourceHealth( args ),
                "state_snapshot"  => await StateSnapshot( args ),
                "full_verify"     => await FullVerify( args ),
                _ => HandlerBase.Error( $"Unknown sbep_verify action '{action}'", action,
                    "Valid actions: prefab_check, component_check, smoke_test, resource_health, state_snapshot, full_verify" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"sbep_verify.{action} failed: {ex.Message}", action );
        }
    }

    // ── prefab_check ─────────────────────────────────────────────────

    private static async Task<object> PrefabCheck( JsonElement args )
    {
        await GameTask.MainThread();

        var filter = HandlerBase.GetString( args, "filter" );
        var verbose = HandlerBase.GetBool( args, "verbose", false );
        var results = new List<object>();
        int passed = 0, failed = 0, warnings = 0;

        var files = Sandbox.FileSystem.Mounted.FindFile( "sbep/", "*.prefab", true ).ToList();

        foreach ( var relativePath in files )
        {
            var fullPath = $"sbep/{relativePath}";

            if ( !string.IsNullOrEmpty( filter ) &&
                fullPath.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                continue;

            var issues = ValidatePrefab( fullPath );
            int errs = issues.Count( i => i.severity == "error" );
            int warns = issues.Count( i => i.severity == "warning" );

            if ( errs > 0 ) failed++;
            else if ( warns > 0 ) warnings++;
            else passed++;

            if ( errs > 0 || warns > 0 || verbose )
            {
                results.Add( new
                {
                    prefab = fullPath,
                    status = errs > 0 ? "fail" : warns > 0 ? "warn" : "pass",
                    issues
                } );
            }
        }

        return HandlerBase.Success( new
        {
            check = "prefab_check",
            overall = failed > 0 ? "fail" : "pass",
            total = files.Count,
            passed,
            failed,
            warnings,
            results
        } );
    }

    private static List<VerificationIssue> ValidatePrefab( string path )
    {
        var issues = new List<VerificationIssue>();

        string json;
        try { json = Sandbox.FileSystem.Mounted.ReadAllText( path ); }
        catch { issues.Add( new VerificationIssue( "error", "Cannot read file" ) ); return issues; }

        JsonNode root;
        try { root = JsonNode.Parse( json ); }
        catch { issues.Add( new VerificationIssue( "error", "Invalid JSON" ) ); return issues; }

        var rootObj = root?["RootObject"];
        if ( rootObj is null ) { issues.Add( new VerificationIssue( "error", "Missing RootObject" ) ); return issues; }

        // GUID check
        CheckGuids( rootObj, issues );

        // NetworkMode
        var netMode = rootObj["NetworkMode"]?.GetValue<int>() ?? 0;
        if ( netMode != 2 )
            issues.Add( new VerificationIssue( "warning", $"NetworkMode={netMode}, expected 2" ) );

        // Components
        var components = rootObj["Components"]?.AsArray();
        if ( components is null ) { issues.Add( new VerificationIssue( "error", "No components" ) ); return issues; }

        bool hasRb = false, hasCollider = false, hasRenderer = false, hasDevBox = false;
        string modelPath = null;
        bool hasSbep = false;

        foreach ( var comp in components )
        {
            if ( comp is null ) continue;
            var typeName = comp["__type"]?.GetValue<string>();
            if ( typeName == "Sandbox.Rigidbody" ) hasRb = true;
            if ( typeName?.Contains( "Collider" ) == true ) hasCollider = true;
            if ( typeName == "Sandbox.ModelRenderer" ) { hasRenderer = true; modelPath = comp["Model"]?.GetValue<string>(); }
            if ( typeName == "DevBoxModel" ) hasDevBox = true;

            var td = TypeLibrary.GetType( typeName );
            if ( td is null && typeName is not null )
                issues.Add( new VerificationIssue( "warning", $"Type '{typeName}' not found in TypeLibrary" ) );

            // Check if SBEP component
            if ( IsSbepType( typeName ) ) hasSbep = true;
        }

        if ( hasRenderer && IsKnownMissingDevModel( modelPath ) )
            issues.Add( new VerificationIssue( "error", $"Model '{modelPath}' is a known missing dev model path") );

        if ( hasRenderer && string.IsNullOrEmpty( modelPath ) && !hasDevBox )
            issues.Add( new VerificationIssue( "warning", "Null model without DevBoxModel — may be invisible" ) );

        if ( hasSbep && !hasRb )
            issues.Add( new VerificationIssue( "error", "Missing Rigidbody" ) );
        if ( hasSbep && !hasCollider )
            issues.Add( new VerificationIssue( "error", "Missing Collider" ) );

        // .sent check
        var sentPath = path.Replace( ".prefab", ".sent" );
        if ( !Sandbox.FileSystem.Mounted.FileExists( sentPath ) )
            issues.Add( new VerificationIssue( "warning", "No .sent file — won't appear in spawn menu" ) );

        return issues;
    }

    private static bool IsKnownMissingDevModel( string modelPath )
    {
        if ( string.IsNullOrEmpty( modelPath ) ) return false;

        var normalized = modelPath.Replace( '\\', '/' );
        return normalized.Equals( "models/dev/box.vmdl", StringComparison.OrdinalIgnoreCase );
    }

    private static void CheckGuids( JsonNode node, List<VerificationIssue> issues )
    {
        var guid = node["__guid"]?.GetValue<string>();
        if ( !string.IsNullOrEmpty( guid ) && !GuidHexPattern.IsMatch( guid ) )
            issues.Add( new VerificationIssue( "error", $"Non-hex GUID: {guid}" ) );

        var components = node["Components"]?.AsArray();
        if ( components is not null )
            foreach ( var c in components )
            {
                var g = c?["__guid"]?.GetValue<string>();
                if ( !string.IsNullOrEmpty( g ) && !GuidHexPattern.IsMatch( g ) )
                    issues.Add( new VerificationIssue( "error", $"Non-hex component GUID: {g}" ) );
            }

        var children = node["Children"]?.AsArray();
        if ( children is not null )
            foreach ( var child in children )
                if ( child is not null ) CheckGuids( child, issues );
    }

    private static bool IsSbepType( string typeName )
    {
        if ( typeName is null ) return false;
        var td = TypeLibrary.GetType( typeName );
        while ( td is not null )
        {
            if ( td.Name is "SbepDevice" or "ResourceNode" or "HullPiece" ) return true;
            td = td.BaseType;
        }
        return false;
    }

    // ── component_check ──────────────────────────────────────────────

    private static async Task<object> ComponentCheck( JsonElement args )
    {
        await GameTask.MainThread();

        var filter = HandlerBase.GetString( args, "filter" );
        var verbose = HandlerBase.GetBool( args, "verbose", false );
        var results = new List<object>();
        int passed = 0, failed = 0, warnings = 0;

        var types = TypeLibrary.GetTypes<Component>()
            .Where( td => td.Group == "SBEP" && !td.IsAbstract )
            .ToList();

        foreach ( var td in types )
        {
            if ( !string.IsNullOrEmpty( filter ) &&
                td.Name.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                continue;

            var issues = ValidateComponentType( td );
            int errs = issues.Count( i => i.severity == "error" );
            int warns = issues.Count( i => i.severity == "warning" );

            if ( errs > 0 ) failed++;
            else if ( warns > 0 ) warnings++;
            else passed++;

            if ( errs > 0 || warns > 0 || verbose )
            {
                results.Add( new
                {
                    type = td.Name,
                    @base = td.BaseType?.Name,
                    status = errs > 0 ? "fail" : warns > 0 ? "warn" : "pass",
                    issues
                } );
            }
        }

        return HandlerBase.Success( new
        {
            check = "component_check",
            overall = failed > 0 ? "fail" : "pass",
            total = types.Count,
            passed,
            failed,
            warnings,
            results
        } );
    }

    private static List<VerificationIssue> ValidateComponentType( TypeDescription td )
    {
        var issues = new List<VerificationIssue>();

        // Check [Sync] properties
        foreach ( var member in td.Members )
        {
            if ( !member.HasAttribute<SyncAttribute>() ) continue;
            if ( !member.IsProperty )
                issues.Add( new VerificationIssue( "error", $"[Sync] on field '{member.Name}' — must be property" ) );
            else if ( member is PropertyDescription prop && ( !prop.CanRead || !prop.CanWrite ) )
                issues.Add( new VerificationIssue( "error", $"[Sync] on '{member.Name}' needs both getter and setter" ) );
        }

        // Device-specific checks
        bool isDevice = false;
        var check = td;
        while ( check is not null )
        {
            if ( check.Name == "SbepDevice" ) { isDevice = true; break; }
            check = check.BaseType;
        }

        if ( isDevice )
        {
            if ( GetPropertyInHierarchy( td, "SupportedTypes" ) is null )
                issues.Add( new VerificationIssue( "error", "SupportedTypes property not found" ) );
        }

        return issues;
    }

    private static PropertyDescription GetPropertyInHierarchy( TypeDescription td, string name )
    {
        for ( var check = td; check is not null; check = check.BaseType )
        {
            var prop = check.GetProperty( name );
            if ( prop is not null ) return prop;
        }

        return null;
    }

    // ── smoke_test ───────────────────────────────────────────────────

    private static async Task<object> SmokeTest( JsonElement args )
    {
        await GameTask.MainThread();

        // Check play mode
        var session = SceneEditorSession.Active;
        if ( session is null || !session.IsPlaying )
            return HandlerBase.Error( "Smoke test requires play mode. Use editor.start_play first.", "smoke_test" );

        // Execute via ConCmd and capture output
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();

        Sandbox.ConsoleSystem.Run( "sbep.smoke_test" );

        // Result-driven wait: the smoke ConCmd emits a "=== SMOKE TEST: ... ==="
        // terminator on completion (SbepEntitySmokeTest.cs:26,61). Poll for it
        // instead of a blind Task.Delay(3000) so we return as soon as the last
        // entity is tested — the fixed 3s pinned the shared editor in play mode
        // even when the test finished in well under 1s
        // (editor-contention-reduction-plan.md Phase 1.2). Cap at ~3s so a hung or
        // partial test still returns within the old budget.
        const int smokeMaxWaitMs = 3000;
        const int smokePollMs = 50;
        var after = before;
        for ( int waited = 0; waited <= smokeMaxWaitMs; waited += smokePollMs )
        {
            await Task.Delay( smokePollMs );
            after = EditorHandler.LogSnapshotText();
            if ( after.Skip( before.Length ).Any( l => l.Contains( "=== SMOKE TEST:" ) ) )
                break;
        }

        var output = after.Skip( before.Length ).ToArray();

        // Parse results from log output
        int passCount = output.Count( l => l.Contains( "[PASS]" ) );
        int failCount = output.Count( l => l.Contains( "[FAIL]" ) );

        return HandlerBase.Success( new
        {
            check = "smoke_test",
            overall = failCount > 0 ? "fail" : "pass",
            passed = passCount,
            failed = failCount,
            output
        } );
    }

    // ── resource_health ──────────────────────────────────────────────

    private static async Task<object> ResourceHealth( JsonElement args )
    {
        // Delegate to the game assembly's sbep.dump_resources ConCmd
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();

        await GameTask.MainThread();
        Sandbox.ConsoleSystem.Run( "sbep.dump_resources" );

        await Task.Delay( 500 );

        var after = EditorHandler.LogSnapshotText();
        var output = after.Skip( before.Length ).ToArray();

        bool hasWarnings = output.Any( l => l.Contains( "draining" ) || l.Contains( "0 devices" ) );

        return HandlerBase.Success( new
        {
            check = "resource_health",
            overall = hasWarnings ? "warn" : "pass",
            output
        } );
    }

    // ── state_snapshot ───────────────────────────────────────────────

    private static async Task<object> StateSnapshot( JsonElement args )
    {
        await GameTask.MainThread();

        // ConVars
        var convars = new Dictionary<string, object>
        {
            ["sbep.enabled"] = Sandbox.ConsoleSystem.GetValue( "sbep.enabled" ),
            ["sbep.resources_enabled"] = Sandbox.ConsoleSystem.GetValue( "sbep.resources_enabled" ),
            ["sbep.environment_enabled"] = Sandbox.ConsoleSystem.GetValue( "sbep.environment_enabled" ),
            ["sbep.debug"] = Sandbox.ConsoleSystem.GetValue( "sbep.debug" ),
            ["sbep.watchdog"] = Sandbox.ConsoleSystem.GetValue( "sbep.watchdog" )
        };

        // Delegate to sbep.snapshot for detailed counts
        EditorHandler.EnsureInitialized();
        var before = EditorHandler.LogSnapshotText();
        Sandbox.ConsoleSystem.Run( "sbep.snapshot" );
        await Task.Delay( 500 );
        var after = EditorHandler.LogSnapshotText();
        var snapshotOutput = after.Skip( before.Length ).ToArray();

        // Play state
        var session = SceneEditorSession.Active;
        bool isPlaying = session?.IsPlaying == true;

        return HandlerBase.Success( new
        {
            check = "state_snapshot",
            play_mode = isPlaying,
            convars,
            snapshot = snapshotOutput
        } );
    }

    // ── full_verify ──────────────────────────────────────────────────

    private const int MaxFailuresPerCheck = 10;

    private static async Task<object> FullVerify( JsonElement args )
    {
        var steps = new List<object>();
        var failedChecks = new List<string>();
        var failureSummary = new List<object>();

        // 1. Prefab check (always available)
        var prefabResult = await PrefabCheck( args );
        steps.Add( new { step = "prefab_check", result = prefabResult } );
        TallyCheck( "prefab_check", prefabResult, failedChecks, failureSummary );

        // 2. Component check (always available)
        var compResult = await ComponentCheck( args );
        steps.Add( new { step = "component_check", result = compResult } );
        TallyCheck( "component_check", compResult, failedChecks, failureSummary );

        // 3. Resource health + smoke test (if in play mode)
        await GameTask.MainThread();
        var session = SceneEditorSession.Active;
        bool isPlaying = session?.IsPlaying == true;

        if ( isPlaying )
        {
            var resResult = await ResourceHealth( args );
            steps.Add( new { step = "resource_health", result = resResult } );
            TallyCheck( "resource_health", resResult, failedChecks, failureSummary );

            var smokeResult = await SmokeTest( args );
            steps.Add( new { step = "smoke_test", result = smokeResult } );
            TallyCheck( "smoke_test", smokeResult, failedChecks, failureSummary );
        }
        else
        {
            steps.Add( new { step = "smoke_test", result = (object)new { status = "skipped", reason = "not in play mode" } } );
            steps.Add( new { step = "resource_health", result = (object)new { status = "skipped", reason = "not in play mode" } } );
        }

        return HandlerBase.Success( new
        {
            check = "full_verify",
            overall = failedChecks.Count > 0 ? "fail" : "pass",
            play_mode = isPlaying,
            failed_checks = failedChecks,
            failure_summary = failureSummary,
            steps
        } );
    }

    /// <summary>
    /// Extract the <c>overall</c> and the first N failure items from a sub-check result
    /// and append a compact summary. Gives agents a one-glance view of which checks failed
    /// and on which specific items, without needing to re-invoke each sub-check to localize.
    /// </summary>
    private static void TallyCheck( string checkName, object result, List<string> failedChecks, List<object> failureSummary )
    {
        var inner = UnwrapContentJson( result );
        if ( inner is null ) return;

        string overall = null;
        if ( inner.Value.TryGetProperty( "overall", out var overallEl ) )
            overall = overallEl.GetString();

        if ( overall != "fail" )
            return;

        failedChecks.Add( checkName );

        var failures = new List<object>();
        if ( inner.Value.TryGetProperty( "results", out var results ) && results.ValueKind == JsonValueKind.Array )
        {
            foreach ( var item in results.EnumerateArray() )
            {
                if ( !item.TryGetProperty( "status", out var status ) ) continue;
                if ( status.GetString() != "fail" ) continue;

                string identifier = null;
                if ( item.TryGetProperty( "prefab", out var p ) ) identifier = p.GetString();
                else if ( item.TryGetProperty( "type", out var t ) ) identifier = t.GetString();

                var issueMessages = new List<string>();
                if ( item.TryGetProperty( "issues", out var issues ) && issues.ValueKind == JsonValueKind.Array )
                {
                    foreach ( var issue in issues.EnumerateArray() )
                    {
                        if ( issue.TryGetProperty( "Item2", out var m ) )
                            issueMessages.Add( m.GetString() );
                        else if ( issue.TryGetProperty( "message", out var m2 ) )
                            issueMessages.Add( m2.GetString() );
                    }
                }

                failures.Add( new { item = identifier, issues = issueMessages } );
                if ( failures.Count >= MaxFailuresPerCheck ) break;
            }
        }

        int totalFailed = 0;
        if ( inner.Value.TryGetProperty( "failed", out var failedEl ) && failedEl.ValueKind == JsonValueKind.Number )
            totalFailed = failedEl.GetInt32();

        failureSummary.Add( new
        {
            check = checkName,
            failed_count = totalFailed,
            failures,
            truncated = totalFailed > failures.Count
        } );
    }

    /// <summary>
    /// Unwraps the <c>{ content: [{ text: "<json>" }] }</c> envelope produced by
    /// <see cref="HandlerBase.Success"/> and returns the parsed inner element, or null
    /// if the shape doesn't match (e.g. paginated cursor response).
    /// </summary>
    internal static JsonElement? UnwrapContentJson( object result )
    {
        try
        {
            var json = JsonSerializer.Serialize( result );
            using var doc = JsonDocument.Parse( json );
            if ( !doc.RootElement.TryGetProperty( "content", out var content ) )
                return null;
            foreach ( var item in content.EnumerateArray() )
            {
                if ( !item.TryGetProperty( "text", out var textEl ) ) continue;
                var text = textEl.GetString();
                if ( string.IsNullOrEmpty( text ) ) continue;
                using var inner = JsonDocument.Parse( text );
                return inner.RootElement.Clone();
            }
        }
        catch { }
        return null;
    }
}
