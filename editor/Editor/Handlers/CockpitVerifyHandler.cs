using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

// Migrated to the [McpTool] attribute scan in mcp-dynamic-discovery Phase 3
// Wave 3.1 — async tool (AsyncBeforeMainThread dispatch). Final-wave hardening
// advertises both 'run' and the formerly hidden 'readiness' action so clients
// never need to guess an operation that tools/list omitted.
[McpTool( "cockpit_verify",
	"Run the cockpit beauty verifier as a single orchestrated MCP call. Use action='run' to optionally restart play mode, ask the game-side cockpit diagnostic to select the runtime scene, invoke the cockpit beauty runner, poll FileSystem.Data artifacts, and return packet/rubric/AAA summary fields. Responses include handler_version and contract_version so wrappers can detect stale hotloaded MCP code. This tool is purpose-built to avoid long manual play/stop/log/artifact loops; it does not rebuild Blender assets." )]
internal static class CockpitVerifyHandler
{
    private const string HandlerVersion = "cockpit-verify-handler-2026-04-22-v3";

    /// <summary>
    /// tools/list inputSchema fragment — verbatim copy of the legacy
    /// <c>ToolRegistry.CockpitVerify.inputSchema</c> literal (mcp-dynamic-discovery
    /// Phase 3). The scanner invokes this once at plugin load and caches it.
    /// </summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", description = "The operation to perform.", @enum = new[] { "readiness", "run" } },
            ["label"] = new { type = "string", description = "Run label used for FileSystem.Data/cockpit-beauty/<label>. Default: baseline." },
            ["restart_play"] = new { type = "boolean", description = "Stop and start play before verifying. Default: true." },
            ["timeout_seconds"] = new { type = "integer", description = "Maximum time to wait for readiness and artifacts. Default: 75." },
            ["data_root"] = new { type = "string", description = "Absolute cockpit-beauty data root. Optional; default resolves sibling S&Box-Pub game data." }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    /// <summary>
    /// 'readiness' action — advertised probe of the game-side cockpit diagnostic
    /// without running a full beauty pass.
    /// </summary>
    [McpAction( "readiness", Threading = McpThreading.AsyncBeforeMainThread, ReadOnly = true )]
    internal static async Task<object> Readiness( JsonElement args )
    {
        var timeoutSeconds = Math.Clamp( HandlerBase.GetInt( args, "timeout_seconds", 20 ), 1, 60 );
        var readiness = await WaitForGameDiagnostic( timeoutSeconds );
        return HandlerBase.Success( new
        {
            status = readiness.ready ? "pass" : "fail",
            handler_version = HandlerVersion,
            contract_version = readiness.contract_version,
            readiness = CompactReadiness( readiness )
        } );
    }

    /// <summary>
    /// 'run' action — full orchestrated cockpit beauty verification. Async
    /// (AsyncBeforeMainThread): awaits the main thread internally when it needs
    /// scene access. Lifecycle op — may restart play mode.
    /// </summary>
    [McpAction( "run", Threading = McpThreading.AsyncBeforeMainThread, LifecycleKind = "lifecycle" )]
    internal static async Task<object> Run( JsonElement args )
    {
        var label = SanitizeLabel( HandlerBase.GetString( args, "label", "baseline" ) );
        var restartPlay = HandlerBase.GetBool( args, "restart_play", true );
        var timeoutSeconds = Math.Clamp( HandlerBase.GetInt( args, "timeout_seconds", 75 ), 10, 240 );
        var dataRoot = HandlerBase.GetString( args, "data_root" ) ?? ResolveCockpitDataRoot();
        var runDir = Path.Combine( dataRoot, label );
        var started = DateTimeOffset.Now;

        if ( restartPlay )
        {
            await GameTask.MainThread();
            EditorHandler.Handle( "stop_play", BuildArgs( new { action = "stop_play" } ) );
            await Task.Delay( 2000 );
            await GameTask.MainThread();
            EditorHandler.Handle( "start_play", BuildArgs( new { action = "start_play" } ) );
        }

        var readiness = await WaitForGameDiagnostic( Math.Min( timeoutSeconds, 45 ) );
        if ( !readiness.ready )
        {
            return HandlerBase.Success( new
            {
                status = "fail",
                handler_version = HandlerVersion,
                contract_version = readiness.contract_version,
                label,
                reason = readiness.reason,
                data_root = dataRoot,
                run_dir = runDir,
                readiness = CompactReadiness( readiness )
            } );
        }

        if ( Directory.Exists( runDir ) )
            Directory.Delete( runDir, true );

        await GameTask.MainThread();
        var direct = TryStartBeautyRunnerDirect( readiness.selected_scene_ref, label );
        if ( !direct.started )
        {
            return HandlerBase.Success( new
            {
                status = "fail",
                handler_version = HandlerVersion,
                contract_version = readiness.contract_version,
                label,
                reason = direct.reason,
                data_root = dataRoot,
                run_dir = runDir,
                readiness = CompactReadiness( readiness ),
                console = direct.details
            } );
        }

        var artifacts = await WaitForArtifacts( runDir, timeoutSeconds );
        var summary = ReadSummary( runDir );

        return HandlerBase.Success( new
        {
            status = summary.status == "ok" && summary.fresh_spawn_confirmed && summary.aaa_all_pass && summary.rubric_accept && summary.missing_required_png_count == 0 && summary.suspicious_small_png_count == 0 ? "pass" : "fail",
            handler_version = HandlerVersion,
            contract_version = readiness.contract_version,
            label,
            started = started.ToString( "o" ),
            finished = DateTimeOffset.Now.ToString( "o" ),
            data_root = dataRoot,
            run_dir = runDir,
            readiness = CompactReadiness( readiness ),
            console = direct.details,
            artifacts,
            summary
        } );
    }

    private static object CompactReadiness( CockpitVerifyReadiness readiness )
    {
        if ( readiness == null )
            return new { ready = false, reason = "missing_readiness" };

        var selectedCandidate = readiness.candidates.FirstOrDefault( candidate => candidate.selected );
        return new
        {
            readiness.ready,
            readiness.reason,
            readiness.contract_version,
            readiness.play_state,
            readiness.game_active_scene,
            readiness.selected_scene,
            readiness.selected_reason,
            readiness.player_count,
            readiness.controller_count,
            readiness.local_player_detected,
            readiness.cockpit_chair_count,
            readiness.cockpit_piece_count,
            readiness.ship_piece_count,
            readiness.runner_type,
            readiness.detail,
            candidate_count = readiness.candidates.Count,
            selected_candidate = selectedCandidate is null ? null : new
            {
                selectedCandidate.scene_name,
                selectedCandidate.source_category,
                selectedCandidate.source_name,
                selectedCandidate.selected_reason,
                selectedCandidate.player_count,
                selectedCandidate.controller_count,
                selectedCandidate.local_player_detected,
                selectedCandidate.chair_count,
                selectedCandidate.cockpit_chair_count,
                selectedCandidate.cockpit_piece_count,
                selectedCandidate.ship_piece_count,
                selectedCandidate.ship_composer_count,
                selectedCandidate.camera_valid
            }
        };
    }

    private static async Task<CockpitVerifyReadiness> WaitForGameDiagnostic( int timeoutSeconds )
    {
        var deadline = DateTimeOffset.Now.AddSeconds( timeoutSeconds );
        CockpitVerifyReadiness last = null;
        do
        {
            await GameTask.MainThread();
            last = TryBuildGameReadiness();
            if ( last.ready )
                return last;
            if ( last.reason is "missing_game_contract" or "diagnostic_invocation_failed" )
                return last;
            await Task.Delay( 750 );
        } while ( DateTimeOffset.Now < deadline );

        return last is null
            ? new CockpitVerifyReadiness { ready = false, reason = "Timed out waiting for cockpit diagnostic." }
            : last.WithReason( $"Timed out waiting for game-side cockpit diagnostic readiness. Last: {last.reason}" );
    }

    private static CockpitVerifyReadiness TryBuildGameReadiness()
    {
        var type = FindLoadedType( "CockpitBeautyRunner" );
        if ( type == null )
        {
            return new CockpitVerifyReadiness
            {
                ready = false,
                reason = "missing_game_contract"
            };
        }

        var method = type.GetMethod(
            "BuildDiagnostic",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof( Scene ) },
            modifiers: null );

        if ( method == null )
        {
            return new CockpitVerifyReadiness
            {
                ready = false,
                reason = "missing_game_contract",
                runner_type = type.FullName ?? type.Name
            };
        }

        try
        {
            var diagnostic = method.Invoke( null, new object[] { null } );
            return ConvertDiagnostic( diagnostic );
        }
        catch ( TargetInvocationException ex )
        {
            return new CockpitVerifyReadiness
            {
                ready = false,
                reason = "diagnostic_invocation_failed",
                runner_type = type.FullName ?? type.Name,
                detail = ex.InnerException?.Message ?? ex.Message
            };
        }
        catch ( Exception ex )
        {
            return new CockpitVerifyReadiness
            {
                ready = false,
                reason = "diagnostic_invocation_failed",
                runner_type = type.FullName ?? type.Name,
                detail = ex.Message
            };
        }
    }

    private static CockpitVerifyReadiness ConvertDiagnostic( object diagnostic )
    {
        if ( diagnostic == null )
            return new CockpitVerifyReadiness { ready = false, reason = "diagnostic_returned_null" };

        var selected = GetObjectProperty( diagnostic, "Selected" );
        var selectedScene = GetObjectProperty( selected, "Scene" ) as Scene;
        var captureAllowed = GetBoolProperty( diagnostic, "CaptureAllowed" );
        var failure = GetStringProperty( diagnostic, "FailureReason" );
        var candidates = ConvertCandidates( GetObjectProperty( diagnostic, "Candidates" ) );

        return new CockpitVerifyReadiness
        {
            ready = captureAllowed,
            reason = captureAllowed ? null : (failure ?? "game diagnostic denied capture"),
            contract_version = GetStringProperty( diagnostic, "ContractVersion" ),
            play_state = GetStringProperty( diagnostic, "PlayState" ),
            game_active_scene = GetStringProperty( diagnostic, "GameActiveScene" ),
            selected_scene = GetStringProperty( diagnostic, "SelectedSceneName" ),
            selected_reason = GetStringProperty( diagnostic, "SelectedReason" ),
            player_count = GetIntProperty( selected, "PlayerCount" ),
            controller_count = GetIntProperty( selected, "ControllerCount" ),
            local_player_detected = GetBoolProperty( selected, "LocalPlayerDetected" ),
            cockpit_chair_count = GetIntProperty( selected, "CockpitChairCount" ),
            cockpit_piece_count = GetIntProperty( selected, "CockpitPieceCount" ),
            ship_piece_count = GetIntProperty( selected, "ShipPieceCount" ),
            selected_scene_ref = selectedScene,
            candidates = candidates
        };
    }

    private static List<CockpitVerifySceneCandidate> ConvertCandidates( object value )
    {
        var result = new List<CockpitVerifySceneCandidate>();
        if ( value is not System.Collections.IEnumerable items )
            return result;

        foreach ( var item in items )
        {
            if ( item == null )
                continue;
            result.Add( new CockpitVerifySceneCandidate
            {
                scene_name = GetStringProperty( item, "SceneName" ),
                source_category = GetStringProperty( item, "SourceCategory" ),
                source_name = GetStringProperty( item, "SourceName" ),
                explicit_scene = GetBoolProperty( item, "Explicit" ),
                selected = GetBoolProperty( item, "Selected" ),
                selected_reason = GetStringProperty( item, "SelectedReason" ),
                player_count = GetIntProperty( item, "PlayerCount" ),
                controller_count = GetIntProperty( item, "ControllerCount" ),
                local_player_detected = GetBoolProperty( item, "LocalPlayerDetected" ),
                chair_count = GetIntProperty( item, "ChairCount" ),
                cockpit_chair_count = GetIntProperty( item, "CockpitChairCount" ),
                cockpit_piece_count = GetIntProperty( item, "CockpitPieceCount" ),
                ship_piece_count = GetIntProperty( item, "ShipPieceCount" ),
                ship_composer_count = GetIntProperty( item, "ShipComposerCount" ),
                camera_valid = GetBoolProperty( item, "CameraValid" )
            } );
        }

        return result;
    }

    private static (bool started, string reason, object details) TryStartBeautyRunnerDirect( Scene scene, string label )
    {
        if ( scene == null )
            return (false, "Game-side diagnostic did not select a runtime scene.", new { mode = "direct", status = "fail", reason = "missing_scene" });

        var type = FindLoadedType( "CockpitBeautyRunner" );
        if ( type == null )
            return (false, "CockpitBeautyRunner was not found in loaded game assemblies.", new { mode = "direct", status = "fail", reason = "missing_runner_type", scene = scene.Name });

        var method = type.GetMethod(
            "StartVerifyInScene",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof( Scene ), typeof( string ) },
            modifiers: null );

        if ( method == null )
            return (false, "CockpitBeautyRunner.StartVerifyInScene(Scene,string) was not found.", new { mode = "direct", status = "fail", reason = "missing_direct_entry", scene = scene.Name, runner_type = type.FullName ?? type.Name });

        try
        {
            method.Invoke( null, new object[] { scene, label } );
        }
        catch ( TargetInvocationException ex )
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            return (false, $"CockpitBeautyRunner.StartVerifyInScene threw: {inner}", new { mode = "direct", status = "fail", reason = "runner_exception", scene = scene.Name, runner_type = type.FullName ?? type.Name, error = inner });
        }
        catch ( Exception ex )
        {
            return (false, $"Failed to invoke CockpitBeautyRunner.StartVerifyInScene: {ex.Message}", new { mode = "direct", status = "fail", reason = "invoke_failed", scene = scene.Name, runner_type = type.FullName ?? type.Name, error = ex.Message });
        }

        return (true, null, new { mode = "direct", status = "started", scene = scene.Name, runner_type = type.FullName ?? type.Name });
    }

    private static Type FindLoadedType( string name )
    {
        foreach ( var assembly in AppDomain.CurrentDomain.GetAssemblies() )
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch ( ReflectionTypeLoadException ex ) { types = ex.Types.Where( t => t != null ).ToArray(); }
            catch { continue; }

            var match = types.FirstOrDefault( t => t.Name == name || t.FullName == name );
            if ( match != null )
                return match;
        }

        return null;
    }

    private static object GetObjectProperty( object target, string name )
    {
        try { return target?.GetType().GetProperty( name, BindingFlags.Public | BindingFlags.Instance )?.GetValue( target ); }
        catch { return null; }
    }

    private static string GetStringProperty( object target, string name ) => GetObjectProperty( target, name ) as string;

    private static bool GetBoolProperty( object target, string name )
    {
        var value = GetObjectProperty( target, name );
        return value is bool b && b;
    }

    private static int GetIntProperty( object target, string name )
    {
        var value = GetObjectProperty( target, name );
        return value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
    }

    private static async Task<object> WaitForArtifacts( string runDir, int timeoutSeconds )
    {
        var deadline = DateTimeOffset.Now.AddSeconds( timeoutSeconds );
        do
        {
            if ( File.Exists( Path.Combine( runDir, "packet.json" ) ) &&
                 File.Exists( Path.Combine( runDir, "rubric.json" ) ) &&
                 File.Exists( Path.Combine( runDir, "aaa.txt" ) ) )
                return new { status = "artifacts", run_dir = runDir };

            if ( File.Exists( Path.Combine( runDir, "failure.txt" ) ) )
                return new { status = "failure", run_dir = runDir, failure = Path.Combine( runDir, "failure.txt" ) };

            await Task.Delay( 750 );
        } while ( DateTimeOffset.Now < deadline );

        return new { status = "timeout", run_dir = runDir };
    }

    private static BeautySummary ReadSummary( string runDir )
    {
        var packetPath = Path.Combine( runDir, "packet.json" );
        var rubricPath = Path.Combine( runDir, "rubric.json" );
        var aaaPath = Path.Combine( runDir, "aaa.txt" );
        var failurePath = Path.Combine( runDir, "failure.txt" );

        if ( File.Exists( failurePath ) )
            return new BeautySummary { status = "failure", failure_path = failurePath, failure = File.ReadAllText( failurePath ) };

        if ( !File.Exists( packetPath ) || !File.Exists( rubricPath ) || !File.Exists( aaaPath ) )
            return new BeautySummary { status = "missing", packet_path = packetPath, rubric_path = rubricPath, aaa_path = aaaPath };

        using var packetDoc = JsonDocument.Parse( File.ReadAllText( packetPath ) );
        using var rubricDoc = JsonDocument.Parse( File.ReadAllText( rubricPath ) );
        var packet = packetDoc.RootElement;
        var rubric = rubricDoc.RootElement;
        var pngCount = Directory.GetFiles( runDir, "*.png" ).Length;
        var smallPngCount = Directory.GetFiles( runDir, "*.png" ).Count( f => new FileInfo( f ).Length < 2048 );
        var missingPngs = RequiredPngNames.Where( name => !File.Exists( Path.Combine( runDir, name ) ) ).ToArray();

        if ( !packet.TryGetProperty( "Aaa", out var aaa ) )
            return new BeautySummary { status = "malformed", packet_path = packetPath, rubric_path = rubricPath, aaa_path = aaaPath, failure = "packet.json missing Aaa" };

        return new BeautySummary
        {
            status = "ok",
            fresh_spawn_confirmed = GetBool( packet, "FreshSpawnConfirmed" ),
            aaa_all_pass = GetBool( aaa, "AllPass" ),
            aaa_passed = GetInt( aaa, "PassedCount" ),
            aaa_failed = GetInt( aaa, "FailedCount" ),
            rubric_accept = GetBool( rubric, "Accept" ),
            weighted_score = GetFloat( rubric, "WeightedScore" ),
            critical_min = GetFloat( rubric, "CriticalMin" ),
            chair_critical_min = GetFloat( rubric, "ChairCriticalMin" ),
            stop_reason = rubric.TryGetProperty( "StopReason", out var stop ) ? stop.GetString() : null,
            png_count = pngCount,
            suspicious_small_png_count = smallPngCount,
            missing_required_png_count = missingPngs.Length,
            missing_required_pngs = missingPngs,
            packet_path = packetPath,
            rubric_path = rubricPath,
            aaa_path = aaaPath
        };
    }

    private sealed class CockpitVerifyReadiness
    {
        public bool ready { get; init; }
        public string reason { get; init; }
        public string contract_version { get; init; }
        public string play_state { get; init; }
        public string game_active_scene { get; init; }
        public string selected_scene { get; init; }
        public string selected_reason { get; init; }
        public int player_count { get; init; }
        public int controller_count { get; init; }
        public bool local_player_detected { get; init; }
        public int cockpit_chair_count { get; init; }
        public int cockpit_piece_count { get; init; }
        public int ship_piece_count { get; init; }
        public string runner_type { get; init; }
        public string detail { get; init; }
        public List<CockpitVerifySceneCandidate> candidates { get; init; } = new();

        [JsonIgnore]
        public Scene selected_scene_ref { get; init; }

        public CockpitVerifyReadiness WithReason( string newReason ) => new()
        {
            ready = false,
            reason = newReason,
            contract_version = contract_version,
            play_state = play_state,
            game_active_scene = game_active_scene,
            selected_scene = selected_scene,
            selected_reason = selected_reason,
            player_count = player_count,
            controller_count = controller_count,
            local_player_detected = local_player_detected,
            cockpit_chair_count = cockpit_chair_count,
            cockpit_piece_count = cockpit_piece_count,
            ship_piece_count = ship_piece_count,
            runner_type = runner_type,
            detail = detail,
            selected_scene_ref = selected_scene_ref,
            candidates = candidates
        };
    }

    private sealed class CockpitVerifySceneCandidate
    {
        public string scene_name { get; init; }
        public string source_category { get; init; }
        public string source_name { get; init; }
        public bool explicit_scene { get; init; }
        public bool selected { get; init; }
        public string selected_reason { get; init; }
        public int player_count { get; init; }
        public int controller_count { get; init; }
        public bool local_player_detected { get; init; }
        public int chair_count { get; init; }
        public int cockpit_chair_count { get; init; }
        public int cockpit_piece_count { get; init; }
        public int ship_piece_count { get; init; }
        public int ship_composer_count { get; init; }
        public bool camera_valid { get; init; }
    }

    private sealed class BeautySummary
    {
        public string status { get; init; }
        public bool fresh_spawn_confirmed { get; init; }
        public bool aaa_all_pass { get; init; }
        public int aaa_passed { get; init; }
        public int aaa_failed { get; init; }
        public bool rubric_accept { get; init; }
        public float weighted_score { get; init; }
        public float critical_min { get; init; }
        public float chair_critical_min { get; init; }
        public string stop_reason { get; init; }
        public int png_count { get; init; }
        public int suspicious_small_png_count { get; init; }
        public int missing_required_png_count { get; init; }
        public string[] missing_required_pngs { get; init; }
        public string packet_path { get; init; }
        public string rubric_path { get; init; }
        public string aaa_path { get; init; }
        public string failure_path { get; init; }
        public string failure { get; init; }
    }

    private static JsonElement BuildArgs( object value ) => JsonSerializer.SerializeToElement( value );

    private static readonly string[] RequiredPngNames =
    {
        "forward.png",
        "down_18.png",
        "down_32.png",
        "left_18.png",
        "right_18.png",
        "world_chair_approach.png",
        "forward_center_windshield.png",
        "forward_lower_dash.png",
        "forward_top_brow.png",
        "down_center_instruments.png",
        "seated_chair_context.png",
        "fp_chair_context.png",
        "left_side_bank.png",
        "right_side_bank.png",
        "world_chair_identity.png"
    };

    /// <summary>
    /// Resolve the game's FileSystem.Data cockpit-beauty directory.
    ///
    /// DERIVED, NEVER DECLARED. This method used to hardcode
    ///     &lt;sibling&gt;/S&amp;Box-Pub/game/data/softsplit/sandbox#local/cockpit-beauty
    /// which broke silently when the project Org was renamed softsplit -> general1337:
    /// the game wrote to the new root, this polled the old one, and every
    /// cockpit_verify run ended in `status: "timeout"` while 225 stale pre-rebrand
    /// PNGs sat in the directory it was watching.
    ///
    /// BOTH halves of the old literal were wrong-by-construction: the engine
    /// checkout (the running editor may be any worktree) and the Org segment
    /// (renamable in sandbox.sbproj). So both are now read from the live system:
    ///     editor dir : from the process we are executing inside
    ///     Org/Ident  : parsed from the project's .sbproj
    /// A rename or a build switch is followed automatically from here on.
    /// tools/capture.py performs the identical derivation host-side.
    ///
    /// Every fallback below LOGS why it fired. A path resolver that degrades
    /// silently is precisely the failure this method is being fixed for.
    /// </summary>
    private static string ResolveCockpitDataRoot()
    {
        const string LeafDir = "cockpit-beauty";
        var root = HandlerBase.GetProjectRoot();

        var editorDir = ResolveEditorGameDir();
        if ( !string.IsNullOrEmpty( editorDir ) )
        {
            var (org, ident) = ReadOrgIdent( root );
            if ( !string.IsNullOrEmpty( org ) && !string.IsNullOrEmpty( ident ) )
            {
                // Returned even if it does not exist yet: the game creates it on the
                // first capture, and watching the correct directory from the start is
                // the entire point.
                return Path.Combine( editorDir, "data", org, $"{ident}#local", LeafDir );
            }

            Log.Warning( $"[cockpit_verify] Could not read Org/Ident from a .sbproj under '{root}'. " +
                         "Falling back to a directory search; pass data_root explicitly if this misses." );
        }
        else
        {
            Log.Warning( "[cockpit_verify] Could not resolve the running editor directory. " +
                         "Falling back to a directory search." );
        }

        if ( !string.IsNullOrEmpty( root ) )
        {
            var searchBase = Directory.GetParent( root )?.FullName ?? root;
            try
            {
                var found = Directory.EnumerateDirectories( searchBase, LeafDir, SearchOption.AllDirectories )
                    .OrderByDescending( Directory.GetLastWriteTimeUtc )
                    .FirstOrDefault();
                if ( !string.IsNullOrEmpty( found ) )
                {
                    Log.Warning( $"[cockpit_verify] Using most-recently-written '{LeafDir}' found by search: {found}. " +
                                 "This is a DEGRADED path — it may be a stale pre-rebrand directory." );
                    return found;
                }
            }
            catch ( Exception ex )
            {
                Log.Warning( $"[cockpit_verify] Directory search under '{searchBase}' failed: {ex.Message}" );
            }
        }

        var cwd = Path.Combine( Environment.CurrentDirectory, LeafDir );
        Log.Warning( $"[cockpit_verify] All resolution strategies failed; using CWD fallback '{cwd}'. " +
                     "cockpit_verify will almost certainly time out — pass data_root explicitly." );
        return cwd;
    }

    /// <summary>The `game` directory of the editor process we are running inside.</summary>
    private static string ResolveEditorGameDir()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if ( !string.IsNullOrEmpty( exe ) )
            {
                var dir = Path.GetDirectoryName( exe );
                if ( !string.IsNullOrEmpty( dir ) )
                    return dir;
            }
        }
        catch ( Exception ex )
        {
            Log.Warning( $"[cockpit_verify] MainModule probe failed ({ex.Message}); using AppContext.BaseDirectory." );
        }

        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty( baseDir ) ? null : baseDir.TrimEnd( Path.DirectorySeparatorChar );
    }

    /// <summary>Org/Ident straight out of the project's .sbproj — never assumed.</summary>
    private static (string Org, string Ident) ReadOrgIdent( string projectRoot )
    {
        if ( string.IsNullOrEmpty( projectRoot ) )
            return (null, null);

        try
        {
            var sbproj = Directory.EnumerateFiles( projectRoot, "*.sbproj", SearchOption.TopDirectoryOnly )
                .FirstOrDefault();
            if ( string.IsNullOrEmpty( sbproj ) )
                return (null, null);

            using var doc = JsonDocument.Parse( File.ReadAllText( sbproj ) );
            var rootEl = doc.RootElement;
            var org = rootEl.TryGetProperty( "Org", out var o ) ? o.GetString() : null;
            var ident = rootEl.TryGetProperty( "Ident", out var i ) ? i.GetString() : null;
            return (org, ident);
        }
        catch ( Exception ex )
        {
            Log.Warning( $"[cockpit_verify] Failed to parse .sbproj under '{projectRoot}': {ex.Message}" );
            return (null, null);
        }
    }

    private static string SanitizeLabel( string label )
    {
        label = string.IsNullOrWhiteSpace( label ) ? "baseline" : label.Trim();
        var chars = label.Select( c => char.IsLetterOrDigit( c ) || c == '-' || c == '_' ? c : '-' ).ToArray();
        var safe = new string( chars ).Trim( '-' );
        return string.IsNullOrWhiteSpace( safe ) ? "baseline" : safe;
    }

    private static bool GetBool( JsonElement element, string name ) =>
        element.TryGetProperty( name, out var value ) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static int GetInt( JsonElement element, string name ) =>
        element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static float GetFloat( JsonElement element, string name ) =>
        element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;
}
