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

internal static class CockpitVerifyHandler
{
    private const string HandlerVersion = "cockpit-verify-handler-2026-04-22-v3";

    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        return action switch
        {
            "readiness" => await Readiness( args ),
            "run" => await Run( args ),
            _ => HandlerBase.Error( $"Unknown cockpit_verify action '{action}'.", action, "Use action='readiness' or action='run'." )
        };
    }

    private static async Task<object> Readiness( JsonElement args )
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

    private static async Task<object> Run( JsonElement args )
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

    private static string ResolveCockpitDataRoot()
    {
        var root = HandlerBase.GetProjectRoot();
        if ( string.IsNullOrEmpty( root ) )
            return Path.Combine( Environment.CurrentDirectory, "cockpit-beauty" );
        var sibling = Directory.GetParent( root )?.FullName ?? root;
        return Path.Combine( sibling, "S&Box-Pub", "game", "data", "softsplit", "sandbox#local", "cockpit-beauty" );
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
