using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Editor;

namespace Arenula;

/// <summary>
/// Automated build-and-verify pipeline. Orchestrates save → compile → check errors → smoke test
/// in a single MCP call. Returns structured pass/fail for AI agent consumption.
/// </summary>
internal static class PipelineHandler
{
    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "compile_and_verify"  => await CompileAndVerify( args ),
                "probe_typelibrary"   => await ProbeTypeLibrary( args ),
                _ => HandlerBase.Error( $"Unknown pipeline action '{action}'", action,
                    "Valid actions: compile_and_verify, probe_typelibrary" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"pipeline.{action} failed: {ex.Message}", action );
        }
    }

    /// <summary>
    /// Pre-compile snapshot of the active scene used to detect the `.vmdl`-recompile
    /// scene-wipe bug. We record the top-level child count and a stable hash of the
    /// named top-level children. Post-compile we recompute and classify the state as
    /// intact / blank / degraded — "blank" means everything is gone; "degraded" means
    /// some anchors survived but named objects we tracked disappeared.
    /// </summary>
    private sealed class SceneIntegritySnapshot
    {
        public int ChildCount;
        public int NamedObjectHash;
        public bool Captured;
    }

    private static async Task<object> CompileAndVerify( JsonElement args )
    {
        var timeoutMs = HandlerBase.GetInt( args, "timeout_ms", 120000 );
        var skipVerify = HandlerBase.GetBool( args, "skip_verify", false );
        // Pre-compile reload defaults ON but is opt-out: callers iterating on shader /
        // asset files with no C# changes can skip the extra scene reload if they want.
        var preReload = HandlerBase.GetBool( args, "pre_reload", true );
        var steps = new List<object>();

        // Snapshot scene state + hotload generation before anything runs.
        var preSnapshot = CaptureSceneSnapshot();
        int preGeneration = HotReloadProbe.Generation;

        // Step 1: Save scene
        try
        {
            await GameTask.MainThread();
            var session = SceneEditorSession.Active;
            if ( session == null )
            {
                steps.Add( new { step = "save", status = "skipped", reason = "no active session" } );
            }
            else if ( session.IsPlaying
                      || string.IsNullOrEmpty( session.Scene?.Source?.ResourcePath ) )
            {
                // Play-mode clones and untitled scenes have no backing file path.
                // SceneEditorSession.Save(saveAs:false) opens a Save As dialog in
                // that state and blocks the pipeline on user input. Compile does
                // not require a disk save, so skip is safe. Sandbox.Resource.ResourcePath
                // is the authoritative path property (verified via sbox-api 2026-04-22).
                steps.Add( new
                {
                    step = "save",
                    status = "skipped",
                    reason = session.IsPlaying
                        ? "play-mode clone (no file path)"
                        : "untitled scene (no file path)"
                } );
            }
            else
            {
                session.Save( false );
                steps.Add( new { step = "save", status = "ok" } );
            }
        }
        catch ( Exception ex )
        {
            steps.Add( new { step = "save", status = "error", message = ex.Message } );

            // Recovery: save throwing "Assert: IsValid" is the signature of the
            // post-asset-recompile scene-blanking bug — a component ref became
            // invalid and the serializer tripped. Reload from disk before we
            // burn the compile step on a corrupted session.
            var msg = ex.Message ?? string.Empty;
            bool isBlankingBug = msg.Contains( "Assert", StringComparison.OrdinalIgnoreCase )
                              || msg.Contains( "IsValid", StringComparison.OrdinalIgnoreCase );
            if ( isBlankingBug )
            {
                try
                {
                    var recoveryResult = EditorHandler.ReloadScene( BuildArgs( new { action = "reload_scene" } ) );
                    steps.Add( new { step = "save_recovery", status = "reloaded", trigger = "assert_on_save", result = recoveryResult } );
                }
                catch ( Exception rex )
                {
                    steps.Add( new { step = "save_recovery", status = "failed", message = rex.Message } );
                }
            }
        }

        // Step 1.5: Pre-compile scene reload (opt-out via pre_reload=false)
        // Implements task-packet hypothesis 2 — live Component instances can block
        // the hotload upgrader, which silently rejects the DLL swap. Reloading the
        // scene from disk before the compile clears those instances, giving the
        // swap a clean upgrade target. Gated narrowly: only fires when we're in
        // play mode AND a .cs file declaring a Component / BasePostProcess subclass
        // was modified in the last 60s, so it never disturbs shader/asset-only edits.
        // Ref: docs/ai/task-packets/2026-04-14-typelibrary-hotreload-stall.md
        try
        {
            await GameTask.MainThread();
            var preSession = SceneEditorSession.Active;
            bool isPlayingNow = preSession?.IsPlaying == true;

            if ( preReload && isPlayingNow )
            {
                var recentComponentFiles = ListRecentComponentFiles( 60 );
                if ( recentComponentFiles.Count > 0 )
                {
                    // EditorHandler.ReloadScene already stops play internally via
                    // EditorScene.Stop before cloning the scene from disk — we don't
                    // need a separate Stop call here. After reload, re-enter play so
                    // the rest of the pipeline (SBEP verify step 5) observes the
                    // same play state the caller started in.
                    var reloadResult = EditorHandler.ReloadScene( BuildArgs( new { action = "reload_scene" } ) );
                    EditorScene.Play( SceneEditorSession.Active );

                    steps.Add( new
                    {
                        step = "pre_compile_reload",
                        status = "ok",
                        reason = "playing + recent Component/BasePostProcess edit — clearing live instances before DLL swap",
                        touched_files = recentComponentFiles,
                        result = reloadResult
                    } );
                }
                else
                {
                    steps.Add( new { step = "pre_compile_reload", status = "skipped", reason = "no recent Component/BasePostProcess edits" } );
                }
            }
            else if ( !preReload )
            {
                steps.Add( new { step = "pre_compile_reload", status = "skipped", reason = "pre_reload=false" } );
            }
            // else: not playing → nothing live to clear, skip silently.
        }
        catch ( Exception ex )
        {
            // Pre-reload is best-effort: if it fails, proceed to compile anyway so we
            // don't make a bad situation worse. The subsequent hotreload_verify step
            // will still detect whether the swap succeeded.
            steps.Add( new { step = "pre_compile_reload", status = "error", message = ex.Message } );
        }

        // Step 2: Trigger compilation
        object triggerResult;
        try
        {
            var triggerArgs = BuildArgs( new { action = "trigger" } );
            triggerResult = await CompileHandler.HandleAsync( "trigger", triggerArgs );
            steps.Add( new { step = "compile_trigger", status = "ok" } );
        }
        catch ( Exception ex )
        {
            steps.Add( new { step = "compile_trigger", status = "error", message = ex.Message } );
            return MakeResult( "fail", "compile_trigger", steps, "Compilation trigger failed." );
        }

        // Step 3: Wait for compilation
        try
        {
            var waitArgs = BuildArgs( new { action = "wait", timeout_ms = timeoutMs } );
            await CompileHandler.HandleAsync( "wait", waitArgs );
            steps.Add( new { step = "compile_wait", status = "ok" } );
        }
        catch ( Exception ex )
        {
            steps.Add( new { step = "compile_wait", status = "error", message = ex.Message } );
            return MakeResult( "fail", "compile_wait", steps, "Compilation timed out or failed." );
        }

        // Step 4: Check for errors
        object errorsResult;
        try
        {
            var errorsArgs = BuildArgs( new { action = "errors", severity = "error" } );
            errorsResult = await CompileHandler.HandleAsync( "errors", errorsArgs );
            steps.Add( new { step = "compile_errors", status = "ok", result = errorsResult } );
        }
        catch ( Exception ex )
        {
            steps.Add( new { step = "compile_errors", status = "error", message = ex.Message } );
            return MakeResult( "fail", "compile_errors", steps, "Could not read compile errors." );
        }

        // Check if there are actual compile errors
        bool hasErrors = HasCompileErrors( errorsResult );
        if ( hasErrors )
        {
            return MakeResult( "fail", "compilation", steps,
                "Compilation produced errors. Fix them and retry." );
        }

        // Invalidate ConCmd cache after successful recompile
        ConsoleHandler.InvalidateCache();

        // Hot-reload verification — did the DLL actually swap, AND did the
        // InstanceUpgrader succeed without warnings? Shared helper in CompileHandler
        // classifies the state and attaches an engine-log tail filtered for hotload /
        // TypeLibrary signal. The "upgrade_failed" status (generation advanced but the
        // engine emitted "couldn't upgrade") is treated as a hard failure below —
        // starting Play after this state produces scene corruption with no recovery
        // short of a cold editor restart.
        //
        // New 2026-04-18: the TypeLibrary probe is the authoritative signal. The
        // [EditorEvent.Hotload] counter is unreliable in both directions (false-stall
        // documented in docs/ai/sessions/2026-04-18-mcp-pipeline-false-stall/packet.md,
        // false-ok documented in docs/sbep-reference/58-typelibrary-hotreload-deep-investigation.md
        // §S2). We extract the user-authored type names from the just-edited .cs files
        // and ask TypeLibrary to resolve each. All-resolve → DLL is provably live. Any
        // miss → stall is real. Probe result overrides the event-counter verdict.
        bool upgradeFailed = false;
        try
        {
            int postGeneration = HotReloadProbe.Generation;
            var diag = CompileHandler.DescribeHotloadState( preGeneration, postGeneration );

            // TypeLibrary probe — authoritative "is the new code live?" signal.
            var recentCsFiles = ListRecentCsFiles( 60 );
            var declaredTypes = ExtractDeclaredTypes( recentCsFiles );
            var probe = ProbeTypes( declaredTypes );

            // Override: if we have a probe verdict, trust it over the generation counter.
            string finalStatus = diag.Status;
            string finalHint = diag.Hint;
            if ( probe.attempted > 0 )
            {
                if ( probe.missing == 0 )
                {
                    // Every user-authored type from the freshly-edited files resolves in
                    // TypeLibrary → DLL is live. Downgrade stall-family statuses to ok.
                    if ( finalStatus == "stalled" || finalStatus == "stalled_event_missing" )
                    {
                        finalStatus = "ok";
                        finalHint = "TypeLibrary probe: all " + probe.attempted + " recently-edited type(s) resolve. "
                                  + "Code is live even though [EditorEvent.Hotload] did not advance the counter.";
                    }
                }
                else if ( finalStatus == "ok" )
                {
                    // Counter said ok but probe found missing types → escalate. This is the
                    // false-negative path from doc 58 §S2 where generation advanced but
                    // TypeLibrary is actually corrupted.
                    finalStatus = "stalled";
                    finalHint = "TypeLibrary probe: " + probe.missing + " of " + probe.attempted
                              + " recently-edited type(s) failed to resolve. The hotload generation "
                              + "counter advanced but TypeLibrary does not see the new types. "
                              + "See steps.hotreload_verify.typelibrary_probe.missing_types.";
                }
            }

            upgradeFailed = finalStatus == "upgrade_failed";

            steps.Add( new
            {
                step = "hotreload_verify",
                status = finalStatus,
                pre_generation = preGeneration,
                post_generation = postGeneration,
                advanced = diag.Advanced,
                hint = finalHint,
                typelibrary_probe = probe.attempted > 0 ? (object)new
                {
                    attempted = probe.attempted,
                    found = probe.found,
                    missing = probe.missing,
                    source_files = recentCsFiles,
                    found_types = probe.foundTypes,
                    missing_types = probe.missingTypes
                } : null,
                hotload_log_tail = diag.LogTail
            } );
        }
        catch ( Exception hex )
        {
            steps.Add( new { step = "hotreload_verify", status = "error", message = hex.Message } );
        }

        if ( upgradeFailed )
        {
            return MakeResult( "fail", "hotreload_verify", steps,
                "TypeLibrary corruption detected — the InstanceUpgrader emitted 'couldn't upgrade' " +
                "during the hotload swap. Live Component instances hold stale Type refs and " +
                "Components.Get<T>() / scene.GetAllComponents<T>() will return null. " +
                "Do NOT call editor.start_play. Ask the user to cold-restart S&Box (close the " +
                "editor, relaunch, reload the scene) and retry the edit afterwards. " +
                "See steps.hotreload_verify.hotload_log_tail for the engine's own signal." );
        }

        // Belt-and-braces: recompile can invalidate scene state even when the
        // save step succeeded. Compare pre/post snapshots to classify integrity
        // as intact / blank / degraded, and reload from disk on anything worse
        // than intact so the caller doesn't hit a silent scene wipe.
        await GameTask.MainThread();
        try
        {
            var postScene = SceneEditorSession.Active?.Scene;
            var postSnapshot = CaptureSceneSnapshot();
            string integrity = ClassifyIntegrity( preSnapshot, postSnapshot );

            steps.Add( new
            {
                step = "scene_integrity",
                status = integrity,
                pre_child_count = preSnapshot.Captured ? preSnapshot.ChildCount : (int?)null,
                post_child_count = postSnapshot.Captured ? postSnapshot.ChildCount : (int?)null,
                pre_named_hash = preSnapshot.Captured ? preSnapshot.NamedObjectHash : (int?)null,
                post_named_hash = postSnapshot.Captured ? postSnapshot.NamedObjectHash : (int?)null
            } );

            if ( postScene != null && integrity != "intact" && integrity != "unknown" )
            {
                var recoveryResult = EditorHandler.ReloadScene( BuildArgs( new { action = "reload_scene" } ) );
                steps.Add( new
                {
                    step = "post_compile_recovery",
                    status = "reloaded",
                    trigger = integrity,
                    result = recoveryResult
                } );
            }
        }
        catch ( Exception pex )
        {
            steps.Add( new { step = "post_compile_recovery", status = "error", message = pex.Message } );
        }

        // Step 5: SBEP verification (if in play mode and not skipped)
        await GameTask.MainThread();
        var activeSession = SceneEditorSession.Active;
        bool isPlaying = activeSession?.IsPlaying == true;

        if ( isPlaying && !skipVerify )
        {
            try
            {
                var verifyArgs = BuildArgs( new { action = "full_verify" } );
                var verifyResult = await SbepVerifyHandler.HandleAsync( "full_verify", verifyArgs );

                // Structurally extract overall + failure summary instead of string-matching.
                var inner = SbepVerifyHandler.UnwrapContentJson( verifyResult );
                string verifyOverall = "unknown";
                object failedChecks = null;
                object failureSummary = null;

                if ( inner.HasValue )
                {
                    if ( inner.Value.TryGetProperty( "overall", out var overallEl ) )
                        verifyOverall = overallEl.GetString();
                    if ( inner.Value.TryGetProperty( "failed_checks", out var fcEl ) )
                        failedChecks = JsonSerializer.Deserialize<List<string>>( fcEl.GetRawText() );
                    if ( inner.Value.TryGetProperty( "failure_summary", out var fsEl ) )
                        failureSummary = JsonSerializer.Deserialize<List<object>>( fsEl.GetRawText() );
                }

                steps.Add( new
                {
                    step = "sbep_verify",
                    status = verifyOverall == "fail" ? "fail" : "ok",
                    overall = verifyOverall,
                    failed_checks = failedChecks,
                    failure_summary = failureSummary,
                    result = verifyResult
                } );

                if ( verifyOverall == "fail" )
                {
                    return MakeResult( "fail", "sbep_verify", steps,
                        "SBEP verification found failures. Inspect steps.sbep_verify.failure_summary for the specific failed items — no re-invocation needed." );
                }
            }
            catch ( Exception ex )
            {
                steps.Add( new { step = "sbep_verify", status = "error", message = ex.Message } );
                return MakeResult( "fail", "sbep_verify", steps, $"SBEP verify error: {ex.Message}" );
            }
        }
        else
        {
            string reason = skipVerify ? "skip_verify=true" : "not in play mode";
            steps.Add( new { step = "sbep_verify", status = "skipped", reason } );
        }

        // All passed
        return MakeResult( "pass", (string)null, steps, null );
    }

    private static object MakeResult( string overall, string failedStep, List<object> steps, string actionable )
    {
        return HandlerBase.Success( new
        {
            overall,
            failed_step = failedStep,
            step_count = steps.Count,
            steps,
            actionable
        } );
    }

    private static bool HasCompileErrors( object result )
    {
        try
        {
            var json = JsonSerializer.Serialize( result );
            // Look for error diagnostics in the serialized output
            // The compile.errors handler returns { diagnostics: [...], count: N }
            return json.Contains( "\"severity\":\"Error\"" ) || json.Contains( "\"severity\":\"error\"" );
        }
        catch { return false; }
    }

    private static JsonElement BuildArgs( object obj )
    {
        var json = JsonSerializer.Serialize( obj );
        using var doc = JsonDocument.Parse( json );
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Capture a stable snapshot of the active scene's top-level GameObjects.
    /// Any exception yields <c>Captured=false</c> so the caller can degrade to "unknown"
    /// rather than false-classifying a scene as blank.
    /// </summary>
    private static SceneIntegritySnapshot CaptureSceneSnapshot()
    {
        var snap = new SceneIntegritySnapshot();
        try
        {
            var scene = SceneEditorSession.Active?.Scene;
            if ( scene == null ) return snap;

            int count = 0;
            int hash = 17;
            foreach ( var child in scene.Children )
            {
                if ( child == null ) continue;
                count++;
                var name = child.Name ?? string.Empty;
                // Unchecked mul/add — we want rollover, not overflow exceptions.
                unchecked { hash = hash * 31 + name.GetHashCode( StringComparison.Ordinal ); }
            }

            snap.ChildCount = count;
            snap.NamedObjectHash = hash;
            snap.Captured = true;
        }
        catch
        {
            snap.Captured = false;
        }
        return snap;
    }

    /// <summary>
    /// Enumerate recently-modified <c>.cs</c> files under <c>Code/</c> that declare a
    /// <c>Component</c>/<c>BasePostProcess</c> subclass OR contain a <c>[ConCmd]</c> /
    /// <c>[ConVar]</c> attribute. Used by the pre-compile reload heuristic.
    ///
    /// <b>Broadened 2026-04-18:</b> previously matched only <c>: Component</c> /
    /// <c>: BasePostProcess</c>. Per <c>docs/sbep-reference/58-typelibrary-hotreload-deep-investigation.md</c>
    /// §"Known-triggering edits", adding a <c>[ConCmd]</c> method to an existing static
    /// class (e.g. <c>ShipBudgetConCmds.cs</c>) also reliably corrupts TypeLibrary — yet
    /// those files contain no Component declaration, so the narrow filter missed them
    /// and pre-reload never fired for exactly the edit that breaks things. Adding the
    /// attribute tokens catches that case. Regular shader/asset-only iterations still
    /// don't match.
    /// </summary>
    private static List<string> ListRecentComponentFiles( int secondsBack )
    {
        var results = new List<string>();
        try
        {
            var root = WorkspacePaths.ProjectRoot;
            if ( string.IsNullOrEmpty( root ) ) return results;
            var codeDir = System.IO.Path.Combine( root, "Code" );
            if ( !System.IO.Directory.Exists( codeDir ) ) return results;

            var cutoff = DateTime.Now - TimeSpan.FromSeconds( secondsBack );
            foreach ( var path in System.IO.Directory.EnumerateFiles( codeDir, "*.cs", System.IO.SearchOption.AllDirectories ) )
            {
                try
                {
                    var info = new System.IO.FileInfo( path );
                    if ( info.LastWriteTime < cutoff ) continue;

                    var text = System.IO.File.ReadAllText( path );
                    // Component-subclass match is the original condition. ConCmd/ConVar
                    // match captures the attribute-in-static-class case from doc 58.
                    bool isHotloadRisky =
                           text.Contains( ": Component", StringComparison.Ordinal )
                        || text.Contains( ": BasePostProcess", StringComparison.Ordinal )
                        || text.Contains( "[ConCmd", StringComparison.Ordinal )
                        || text.Contains( "[ConVar", StringComparison.Ordinal );

                    if ( isHotloadRisky )
                    {
                        var rel = path.Length > root.Length + 1
                            ? path.Substring( root.Length + 1 ).Replace( '\\', '/' )
                            : path;
                        results.Add( rel );
                    }
                }
                catch { /* unreadable file — skip */ }
            }
        }
        catch { /* best-effort — never fail the pipeline over this */ }
        return results;
    }

    /// <summary>
    /// Enumerate all recently-modified <c>.cs</c> files under <c>Code/</c> regardless of
    /// content. Used by the TypeLibrary probe to decide which types to resolve — we need
    /// to check every edited file, not just the hotload-risky ones, because a "safe"
    /// method-body edit still produces new IL we want to confirm is loaded.
    /// </summary>
    private static List<string> ListRecentCsFiles( int secondsBack )
    {
        var results = new List<string>();
        try
        {
            var root = WorkspacePaths.ProjectRoot;
            if ( string.IsNullOrEmpty( root ) ) return results;
            var codeDir = System.IO.Path.Combine( root, "Code" );
            if ( !System.IO.Directory.Exists( codeDir ) ) return results;

            var cutoff = DateTime.Now - TimeSpan.FromSeconds( secondsBack );
            foreach ( var path in System.IO.Directory.EnumerateFiles( codeDir, "*.cs", System.IO.SearchOption.AllDirectories ) )
            {
                try
                {
                    var info = new System.IO.FileInfo( path );
                    if ( info.LastWriteTime < cutoff ) continue;
                    var rel = path.Length > root.Length + 1
                        ? path.Substring( root.Length + 1 ).Replace( '\\', '/' )
                        : path;
                    results.Add( rel );
                }
                catch { /* unreadable file — skip */ }
            }
        }
        catch { /* best-effort — never fail the pipeline over this */ }
        return results;
    }

    /// <summary>
    /// Parse top-level type declarations from a set of <c>.cs</c> source files.
    /// Matches <c>class</c>, <c>struct</c>, <c>record</c>, <c>interface</c>, <c>enum</c>.
    /// This is the set of type names we hand to <see cref="ProbeTypes"/> to verify
    /// TypeLibrary resolves them after compile.
    ///
    /// Intentionally simple regex — we don't need a full C# parser. False positives
    /// (e.g. a type commented-out inside a block comment) resolve harmlessly when
    /// TypeLibrary returns null for a name that doesn't actually exist; we treat that
    /// as "probe found fewer types than expected" which is a normal steady state after
    /// heavy edits and not a failure mode on its own.
    /// </summary>
    private static List<string> ExtractDeclaredTypes( List<string> relPaths )
    {
        var types = new HashSet<string>( StringComparer.Ordinal );
        if ( relPaths == null || relPaths.Count == 0 ) return new List<string>();

        var root = WorkspacePaths.ProjectRoot;
        if ( string.IsNullOrEmpty( root ) ) return new List<string>();

        var rx = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:public\s+|internal\s+|private\s+|protected\s+|static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*\s*(?:class|struct|record|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled );

        foreach ( var rel in relPaths )
        {
            try
            {
                var abs = System.IO.Path.Combine( root, rel );
                if ( !System.IO.File.Exists( abs ) ) continue;
                var text = System.IO.File.ReadAllText( abs );
                foreach ( System.Text.RegularExpressions.Match m in rx.Matches( text ) )
                {
                    var name = m.Groups[1].Value;
                    if ( !string.IsNullOrEmpty( name ) ) types.Add( name );
                }
            }
            catch { /* unreadable — skip */ }
        }
        return types.ToList();
    }

    /// <summary>
    /// Probe result for <see cref="ProbeTypes"/> — aggregate counts + per-type breakdown
    /// so the response can show exactly which types failed to resolve.
    /// </summary>
    private readonly record struct ProbeResult(
        int attempted, int found, int missing,
        List<object> foundTypes, List<string> missingTypes );

    /// <summary>
    /// Ask <c>Sandbox.TypeLibrary</c> to resolve each type name; report the declaring
    /// assembly for each hit so the caller can see that the fresh DLL is loaded.
    ///
    /// This is the authoritative "is the new code live?" signal — more reliable than
    /// <see cref="HotReloadProbe.Generation"/>, which depends on an engine event that
    /// doesn't reliably fire for MCP-triggered compiles. The event counter was the
    /// original source of the false-stall false-positive documented in
    /// <c>docs/ai/sessions/2026-04-18-mcp-pipeline-false-stall/packet.md</c>.
    /// </summary>
    private static ProbeResult ProbeTypes( List<string> typeNames )
    {
        var found = new List<object>();
        var missing = new List<string>();
        if ( typeNames == null || typeNames.Count == 0 )
            return new ProbeResult( 0, 0, 0, found, missing );

        foreach ( var name in typeNames )
        {
            try
            {
                var td = TypeLibrary.GetType( name );
                var clr = td?.TargetType;
                if ( clr != null )
                {
                    string asmName = null;
                    string asmLocation = null;
                    try { asmName = clr.Assembly.GetName().Name; } catch { }
                    try { asmLocation = clr.Assembly.Location; } catch { }
                    found.Add( new
                    {
                        name,
                        full_name = clr.FullName,
                        assembly = asmName,
                        assembly_location = asmLocation
                    } );
                }
                else
                {
                    missing.Add( name );
                }
            }
            catch
            {
                missing.Add( name );
            }
        }
        return new ProbeResult( typeNames.Count, found.Count, missing.Count, found, missing );
    }

    /// <summary>
    /// Standalone TypeLibrary probe exposed as <c>pipeline.probe_typelibrary</c>.
    /// Takes an explicit <c>type_names</c> array, or — if omitted — auto-derives the
    /// list from types declared in <c>.cs</c> files edited in the last 60 seconds.
    /// Returns the same <c>typelibrary_probe</c> shape embedded in
    /// <c>compile_and_verify</c> so agents can reuse the classification logic.
    ///
    /// Intended usage: after a compile returns <c>stalled</c> / <c>stalled_event_missing</c>,
    /// call this to cheaply confirm whether the new types are actually live before
    /// escalating to the user for a cold restart. Runs in milliseconds — no scene work,
    /// no play-mode dependency.
    /// </summary>
    private static async Task<object> ProbeTypeLibrary( JsonElement args )
    {
        await GameTask.MainThread();

        List<string> names = null;
        if ( args.ValueKind == JsonValueKind.Object && args.TryGetProperty( "type_names", out var el ) && el.ValueKind == JsonValueKind.Array )
        {
            names = new List<string>();
            foreach ( var item in el.EnumerateArray() )
            {
                if ( item.ValueKind == JsonValueKind.String )
                {
                    var s = item.GetString();
                    if ( !string.IsNullOrWhiteSpace( s ) ) names.Add( s.Trim() );
                }
            }
        }

        List<string> sourceFiles = null;
        bool autoDerived = false;
        if ( names == null || names.Count == 0 )
        {
            sourceFiles = ListRecentCsFiles( 60 );
            names = ExtractDeclaredTypes( sourceFiles );
            autoDerived = true;
        }

        var probe = ProbeTypes( names );
        string verdict =
            probe.attempted == 0 ? "no_types_to_probe"
            : probe.missing == 0 ? "all_found"
            : "missing";

        return HandlerBase.Success( new
        {
            verdict,
            attempted = probe.attempted,
            found = probe.found,
            missing = probe.missing,
            auto_derived = autoDerived,
            source_files = sourceFiles,
            found_types = probe.foundTypes,
            missing_types = probe.missingTypes,
            hint = verdict switch
            {
                "all_found" => "Every requested type resolves in TypeLibrary. Code is live.",
                "missing" => "One or more types did not resolve. Either (a) the compile did not swap the DLL (stall), or (b) the names were misspelled. Inspect missing_types.",
                _ => "No type names supplied and no .cs files edited in the last 60s — nothing to probe. Pass type_names=[\"YourType\"] explicitly."
            }
        } );
    }

    /// <summary>
    /// Classify scene integrity from pre/post snapshots:
    /// <list type="bullet">
    ///   <item><c>intact</c> — child count and named-object hash match.</item>
    ///   <item><c>blank</c> — post-scene has zero children (full wipe).</item>
    ///   <item><c>degraded</c> — count or hash changed but scene still has content
    ///     (partial corruption: named anchors disappeared). Recovery is worth trying.</item>
    ///   <item><c>unknown</c> — snapshot capture failed; don't trigger recovery blindly.</item>
    /// </list>
    /// </summary>
    private static string ClassifyIntegrity( SceneIntegritySnapshot pre, SceneIntegritySnapshot post )
    {
        if ( !post.Captured ) return "unknown";
        if ( post.ChildCount == 0 ) return "blank";
        if ( !pre.Captured ) return "intact"; // no baseline to compare against
        if ( pre.ChildCount == post.ChildCount && pre.NamedObjectHash == post.NamedObjectHash )
            return "intact";
        // Anchors changed but scene has content — suspect silent corruption.
        return "degraded";
    }
}
