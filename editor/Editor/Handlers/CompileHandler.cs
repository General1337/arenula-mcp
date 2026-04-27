using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Handles compilation-related actions: trigger, status, errors, generate_solution, wait.
/// Async handler — dispatched on background thread, switches to main thread for editor API calls.
/// </summary>
internal static class CompileHandler
{
    /// <summary>
    /// Cache of the most recent compile outputs, populated by <see cref="Trigger"/>.
    /// Used by <see cref="GetStatus"/> and <see cref="GetErrors"/> to read compiler state
    /// without triggering a new build.
    /// </summary>
    internal static CompilerOutput[] _lastOutputs;

    internal static async Task<object> HandleAsync( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "trigger"           => await Trigger(),
                "status"            => await GetStatus(),
                "errors"            => await GetErrors( args ),
                "generate_solution" => await GenerateSolution(),
                "wait"              => await Wait( args ),
                "smart_trigger"     => await SmartTrigger( args ),
                "fix_context"       => await FixContext( args ),
                "snapshot_hotreload"=> await SnapshotHotreload(),
                "verify_hotreload"  => await VerifyHotreload( args ),
                _                   => HandlerBase.Error( $"Unknown action '{action}' for tool 'compile'.", action,
                                        "Valid actions: trigger, status, errors, generate_solution, wait, smart_trigger, fix_context, snapshot_hotreload, verify_hotreload" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action, "Check that the editor is running and a project is loaded." );
        }
    }

    // ── trigger ───────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger compilation of all active projects.
    /// Uses EditorUtility.Projects.Compile() which returns CompilerOutput[] per project.
    /// </summary>
    private static async Task<object> Trigger()
    {
        await GameTask.MainThread();

        var projects = EditorUtility.Projects.GetAll();
        if ( projects == null || projects.Count == 0 )
            return HandlerBase.Error( "No projects found.", "trigger", "Open a project in the s&box editor." );

        // Filter to active, non-transient projects that have code
        var compilable = projects
            .Where( p => p.Active && !p.IsTransient && !p.IsBuiltIn && p.HasCodePath() )
            .ToList();

        if ( compilable.Count == 0 )
            return HandlerBase.Error( "No compilable projects found.", "trigger",
                "Ensure the project is active and has a Code folder." );

        var allOutputs = new List<CompilerOutput>();
        var results = new List<object>();

        foreach ( var project in compilable )
        {
            var outputs = await EditorUtility.Projects.Compile( project, null );
            if ( outputs != null )
            {
                allOutputs.AddRange( outputs );
                foreach ( var output in outputs )
                {
                    results.Add( new
                    {
                        compiler = output.Compiler?.Name ?? "unknown",
                        successful = output.Successful,
                        diagnosticCount = output.Diagnostics?.Count ?? 0
                    } );
                }
            }
        }

        _lastOutputs = allOutputs.ToArray();

        // Forward compile diagnostics to the editor log buffer
        foreach ( var output in allOutputs )
        {
            var compiler = output.Compiler;
            if ( compiler?.Diagnostics == null ) continue;

            foreach ( var diag in compiler.Diagnostics )
            {
                var severity = diag.Severity.ToString().ToUpperInvariant();
                var msg = diag.GetMessage();
                string location = "";
                try
                {
                    var lineSpan = diag.Location?.GetMappedLineSpan();
                    if ( lineSpan.HasValue && lineSpan.Value.IsValid )
                        location = $" ({lineSpan.Value.Path}:{lineSpan.Value.StartLinePosition.Line + 1})";
                }
                catch { }

                EditorHandler.AppendLog( $"[{severity}] {diag.Id}: {msg}{location}" );
            }

            if ( !compiler.BuildSuccess )
                EditorHandler.AppendLog( $"[COMPILE] {compiler.Name}: BUILD FAILED" );
            else
                EditorHandler.AppendLog( $"[COMPILE] {compiler.Name}: Build succeeded" );
        }

        return HandlerBase.Success( new
        {
            message = "Compilation triggered.",
            projectCount = compilable.Count,
            compilerResults = results
        } );
    }

    // ── status ────────────────────────────────────────────────────────────

    /// <summary>
    /// Report compilation status for all known compilers.
    /// Uses cached outputs from last Trigger, plus checks live compiler state.
    /// </summary>
    private static async Task<object> GetStatus()
    {
        await GameTask.MainThread();

        var projects = EditorUtility.Projects.GetAll();
        if ( projects == null || projects.Count == 0 )
            return HandlerBase.Error( "No projects found.", "status", "Open a project in the s&box editor." );

        var projectStatuses = new List<object>();

        foreach ( var project in projects.Where( p => p.Active && !p.IsTransient && !p.IsBuiltIn ) )
        {
            projectStatuses.Add( new
            {
                name = project.Config?.Title ?? project.Package?.Title ?? "unknown",
                ident = project.Config?.FullIdent,
                hasCompiler = project.HasCompiler,
                hasCode = project.HasCodePath(),
                active = project.Active
            } );
        }

        // If we have cached compiler outputs, report their state
        var compilerStatuses = new List<object>();

        if ( _lastOutputs != null )
        {
            foreach ( var output in _lastOutputs )
            {
                var compiler = output.Compiler;
                if ( compiler == null ) continue;

                compilerStatuses.Add( new
                {
                    name = compiler.Name,
                    isBuilding = compiler.IsBuilding,
                    needsBuild = compiler.NeedsBuild,
                    buildSuccess = compiler.BuildSuccess,
                    diagnosticCount = compiler.Diagnostics?.Length ?? 0,
                    groupName = compiler.Group?.Name,
                    groupIsBuilding = compiler.Group?.IsBuilding ?? false,
                    groupNeedsBuild = compiler.Group?.NeedsBuild ?? false
                } );
            }
        }

        return HandlerBase.Success( new
        {
            projects = projectStatuses,
            compilers = compilerStatuses,
            hasCompilerCache = _lastOutputs != null,
            hint = compilerStatuses.Count == 0
                ? "Run compile.trigger first to populate compiler state."
                : null
        } );
    }

    // ── errors ────────────────────────────────────────────────────────────

    /// <summary>
    /// Return compiler diagnostics (errors/warnings) from the last build.
    /// Supports optional severity filter: "error", "warning", "info", "hidden".
    /// Microsoft.CodeAnalysis.Diagnostic is available because s&box ships Roslyn for its compiler.
    /// </summary>
    private static async Task<object> GetErrors( JsonElement args )
    {
        await GameTask.MainThread();

        var severityFilter = HandlerBase.GetString( args, "severity" );

        if ( _lastOutputs == null || _lastOutputs.Length == 0 )
        {
            return HandlerBase.Error(
                "No compile outputs available. Run compile.trigger first.",
                "errors",
                "Use compile.trigger to build, then compile.errors to see diagnostics." );
        }

        var diagnostics = new List<DiagnosticEntry>();

        foreach ( var output in _lastOutputs )
        {
            var compiler = output.Compiler;
            var compilerDiagnostics = compiler?.Diagnostics;

            if ( compilerDiagnostics == null || compilerDiagnostics.Length == 0 )
                continue;

            foreach ( var diag in compilerDiagnostics )
            {
                var severity = diag.Severity.ToString().ToLowerInvariant();

                // Apply severity filter if specified
                if ( !string.IsNullOrEmpty( severityFilter ) &&
                     !string.Equals( severity, severityFilter, StringComparison.OrdinalIgnoreCase ) )
                    continue;

                string filePath = null;
                int? line = null;
                int? column = null;

                // Location and GetMappedLineSpan() are Roslyn APIs on Microsoft.CodeAnalysis.Diagnostic
                try
                {
                    var lineSpan = diag.Location?.GetMappedLineSpan();
                    if ( lineSpan.HasValue && lineSpan.Value.IsValid )
                    {
                        filePath = lineSpan.Value.Path;
                        line = lineSpan.Value.StartLinePosition.Line + 1;
                        column = lineSpan.Value.StartLinePosition.Character + 1;
                    }
                }
                catch
                {
                    // Location APIs may not be available in all sandbox contexts
                }

                diagnostics.Add( new DiagnosticEntry
                {
                    id = diag.Id,
                    severity = severity,
                    message = diag.GetMessage(),
                    compiler = compiler.Name,
                    file = filePath,
                    line = line,
                    column = column,
                    sortOrder = severity switch
                    {
                        "error" => 0,
                        "warning" => 1,
                        "info" => 2,
                        _ => 3
                    }
                } );
            }
        }

        var sorted = diagnostics.OrderBy( d => d.sortOrder ).ToList();

        return HandlerBase.Success( new
        {
            totalCount = sorted.Count,
            filter = severityFilter,
            diagnostics = sorted.Select( d => new
            {
                d.id,
                d.severity,
                d.message,
                d.compiler,
                d.file,
                d.line,
                d.column
            } )
        } );
    }

    /// <summary>Internal record for sorting diagnostics before serialization.</summary>
    private sealed class DiagnosticEntry
    {
        public string id;
        public string severity;
        public string message;
        public string compiler;
        public string file;
        public int? line;
        public int? column;
        public int sortOrder;
    }

    // ── generate_solution ─────────────────────────────────────────────────

    /// <summary>
    /// Regenerate the .sln file for all projects.
    /// </summary>
    private static async Task<object> GenerateSolution()
    {
        await GameTask.MainThread();
        await EditorUtility.Projects.GenerateSolution();
        return HandlerBase.Confirm( "Solution file regenerated successfully." );
    }

    // ── wait ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Block until all compilations complete, with configurable timeout.
    /// EditorUtility.Projects.WaitForCompiles() is a static Task that resolves when
    /// all local project compiles finish.
    /// </summary>
    private static async Task<object> Wait( JsonElement args )
    {
        var timeoutMs = HandlerBase.GetInt( args, "timeout_ms", 60000 );

        if ( timeoutMs < 1000 )
            timeoutMs = 1000;
        if ( timeoutMs > 300000 )
            timeoutMs = 300000;

        try
        {
            // WaitForCompiles is a static editor API — kick it off from main thread
            await GameTask.MainThread();
            var waitTask = EditorUtility.Projects.WaitForCompiles();

            // Race against timeout on background thread
            var completed = await Task.WhenAny( waitTask, Task.Delay( timeoutMs ) );

            if ( completed != waitTask )
            {
                return HandlerBase.Error(
                    $"Compilation did not complete within {timeoutMs}ms.",
                    "wait",
                    "Increase timeout_ms or check for compile errors." );
            }

            // Await the actual task to propagate any exceptions
            await waitTask;

            // Read final state on main thread
            await GameTask.MainThread();

            if ( _lastOutputs != null )
            {
                var anyFailed = _lastOutputs.Any( o => o.Compiler != null && !o.Compiler.BuildSuccess );
                var totalDiagnostics = _lastOutputs
                    .Where( o => o.Compiler?.Diagnostics != null )
                    .Sum( o => o.Compiler.Diagnostics.Length );

                return HandlerBase.Success( new
                {
                    message = "Compilation finished.",
                    allSucceeded = !anyFailed,
                    totalDiagnostics,
                    timeoutMs
                } );
            }

            return HandlerBase.Confirm( "Compilation finished." );
        }
        catch ( OperationCanceledException )
        {
            return HandlerBase.Error(
                $"Compilation wait cancelled after {timeoutMs}ms.",
                "wait",
                "Increase timeout_ms or check for compile errors." );
        }
    }

    // ── smart_trigger ────────────────────────────────────────────────────

    /// <summary>
    /// Composite action: trigger + wait + return errors with source file context.
    /// One call gives the AI everything it needs to fix compilation errors.
    /// </summary>
    private static async Task<object> SmartTrigger( JsonElement args )
    {
        var contextLines = HandlerBase.GetInt( args, "context_lines", 10 );
        if ( contextLines < 0 ) contextLines = 0;
        if ( contextLines > 30 ) contextLines = 30;

        var maxErrors = HandlerBase.GetInt( args, "max_errors", 5 );
        if ( maxErrors < 1 ) maxErrors = 1;
        if ( maxErrors > 20 ) maxErrors = 20;

        // 1. Trigger compilation
        var triggerResult = await Trigger();

        // 2. Wait for completion (60s timeout)
        await GameTask.MainThread();
        try
        {
            var waitTask = EditorUtility.Projects.WaitForCompiles();
            var completed = await Task.WhenAny( waitTask, Task.Delay( 60000 ) );
            if ( completed == waitTask ) await waitTask;
        }
        catch { /* timeout or cancellation, continue to read state */ }

        // 3. Read diagnostics
        await GameTask.MainThread();

        if ( _lastOutputs == null || _lastOutputs.Length == 0 )
        {
            return HandlerBase.Success( new
            {
                success = true,
                message = "Compilation completed with no cached outputs.",
                errors = Array.Empty<object>()
            } );
        }

        var anyFailed = _lastOutputs.Any( o => o.Compiler != null && !o.Compiler.BuildSuccess );
        var allDiagnostics = new List<DiagnosticEntry>();

        foreach ( var output in _lastOutputs )
        {
            var compiler = output.Compiler;
            if ( compiler?.Diagnostics == null ) continue;

            foreach ( var diag in compiler.Diagnostics )
            {
                var severity = diag.Severity.ToString().ToLowerInvariant();
                if ( severity != "error" && severity != "warning" ) continue;

                string filePath = null;
                int? line = null;
                int? column = null;

                try
                {
                    var lineSpan = diag.Location?.GetMappedLineSpan();
                    if ( lineSpan.HasValue && lineSpan.Value.IsValid )
                    {
                        filePath = lineSpan.Value.Path;
                        line = lineSpan.Value.StartLinePosition.Line + 1;
                        column = lineSpan.Value.StartLinePosition.Character + 1;
                    }
                }
                catch { }

                allDiagnostics.Add( new DiagnosticEntry
                {
                    id = diag.Id,
                    severity = severity,
                    message = diag.GetMessage(),
                    compiler = compiler.Name,
                    file = filePath,
                    line = line,
                    column = column,
                    sortOrder = severity == "error" ? 0 : 1
                } );
            }
        }

        var orderedAll = allDiagnostics.OrderBy( d => d.sortOrder ).ToList();
        var sorted = orderedAll.Take( maxErrors ).ToList();

        // 4. Enrich each error with source context
        var enrichedErrors = new List<object>();
        foreach ( var diag in sorted )
        {
            var sourceContext = ReadSourceContext( diag.file, diag.line, contextLines );
            enrichedErrors.Add( new
            {
                diag.id,
                diag.severity,
                diag.message,
                diag.file,
                diag.line,
                diag.column,
                hint = GenerateFixHint( diag.id ),
                source_context = sourceContext
            } );
        }

        int totalErrorCount = allDiagnostics.Count( d => d.severity == "error" );
        int totalWarningCount = allDiagnostics.Count( d => d.severity == "warning" );
        bool truncated = orderedAll.Count > enrichedErrors.Count;

        return HandlerBase.Success( new
        {
            success = !anyFailed,
            totalErrors = totalErrorCount,
            totalWarnings = totalWarningCount,
            returned = enrichedErrors.Count,
            truncated,
            shown_errors = enrichedErrors.Count,
            hidden_errors = orderedAll.Count - enrichedErrors.Count,
            truncation_hint = truncated
                ? $"Showing {enrichedErrors.Count} of {orderedAll.Count} diagnostics. Call compile.fix_context with error_index=0..{orderedAll.Count - 1} for full context, or compile.errors for the complete unenriched list."
                : null,
            errors = enrichedErrors
        } );
    }

    // ── fix_context ──────────────────────────────────────────────────────

    /// <summary>
    /// For a specific compilation error (by index), return the full source file
    /// and surrounding context to help the AI fix it in one shot.
    /// </summary>
    private static async Task<object> FixContext( JsonElement args )
    {
        await GameTask.MainThread();

        var errorIndex = HandlerBase.GetInt( args, "error_index", 0 );
        var contextLines = HandlerBase.GetInt( args, "context_lines", 15 );
        if ( contextLines < 0 ) contextLines = 0;
        if ( contextLines > 50 ) contextLines = 50;

        if ( _lastOutputs == null || _lastOutputs.Length == 0 )
            return HandlerBase.Error( "No compile outputs. Run compile.trigger or compile.smart_trigger first.", "fix_context" );

        // Collect all error diagnostics
        var errors = new List<(string id, string severity, string message, string file, int? line, int? column)>();

        foreach ( var output in _lastOutputs )
        {
            var compiler = output.Compiler;
            if ( compiler?.Diagnostics == null ) continue;

            foreach ( var diag in compiler.Diagnostics )
            {
                var severity = diag.Severity.ToString().ToLowerInvariant();
                if ( severity != "error" && severity != "warning" ) continue;

                string filePath = null;
                int? line = null;
                int? column = null;

                try
                {
                    var lineSpan = diag.Location?.GetMappedLineSpan();
                    if ( lineSpan.HasValue && lineSpan.Value.IsValid )
                    {
                        filePath = lineSpan.Value.Path;
                        line = lineSpan.Value.StartLinePosition.Line + 1;
                        column = lineSpan.Value.StartLinePosition.Character + 1;
                    }
                }
                catch { }

                errors.Add( (diag.Id, severity, diag.GetMessage(), filePath, line, column) );
            }
        }

        // Sort: errors first, then warnings
        errors = errors.OrderBy( e => e.severity == "error" ? 0 : 1 ).ToList();

        if ( errorIndex < 0 || errorIndex >= errors.Count )
            return HandlerBase.Error( $"Error index {errorIndex} out of range. Available: 0-{errors.Count - 1}.", "fix_context" );

        var err = errors[errorIndex];
        var sourceContext = ReadSourceContext( err.file, err.line, contextLines );

        // Also try to read the full file (truncated to fit response budget)
        string fullFile = null;
        if ( !string.IsNullOrEmpty( err.file ) && System.IO.File.Exists( err.file ) )
        {
            try
            {
                fullFile = System.IO.File.ReadAllText( err.file );
                if ( fullFile.Length > 12000 )
                    fullFile = fullFile.Substring( 0, 12000 ) + "\n... [file truncated at 12000 chars]";
            }
            catch { }
        }

        return HandlerBase.Success( new
        {
            error = new
            {
                index = errorIndex,
                id = err.id,
                severity = err.severity,
                message = err.message,
                file = err.file,
                line = err.line,
                column = err.column,
                hint = GenerateFixHint( err.id )
            },
            source_context = sourceContext,
            full_file = fullFile,
            total_errors = errors.Count
        } );
    }

    // ── Hot-reload diagnostics ────────────────────────────────────────────
    // Shared by compile.verify_hotreload (low-level, explicit pre-snapshot) and
    // pipeline.compile_and_verify (orchestrated). Centralized so both entry points
    // return the same status/hint shape and the same evidence payload.

    /// <summary>
    /// Structured hotload diagnostic for both MCP entry points. Carries the classified
    /// status, a one-line hint derived from the engine log tail, and the raw filtered
    /// log entries for the agent to inspect when the hint isn't specific enough.
    /// </summary>
    internal readonly record struct HotloadDiagnostic(
        string Status,
        string Hint,
        bool Advanced,
        bool AnyBuildReported,
        bool AnySuccess,
        bool AnyFailed,
        List<object> LogTail );

    /// <summary>
    /// Describe the hot-reload state after a compile. Inspects <see cref="_lastOutputs"/>
    /// to judge whether a swap was *expected*, compares pre/post <see cref="HotReloadProbe.Generation"/>
    /// to judge whether one *occurred*, and attaches an engine-log tail filtered for
    /// hotload / TypeLibrary / reload lines so the agent has the engine's own rejection
    /// reason on hand instead of guessing.
    ///
    /// Status values:
    /// <list type="bullet">
    ///   <item><c>ok</c> — generation advanced; swap happened cleanly.</item>
    ///   <item><c>upgrade_failed</c> — generation advanced, but the engine emitted
    ///     "couldn't upgrade" / "TypeLibrary could not find" inside the swap window.
    ///     Live Component instances are holding stale Type refs; the editor's
    ///     TypeLibrary is corrupted. <b>Cold restart is the only recovery</b> — do not
    ///     start Play until the user restarts the editor. See
    ///     <c>docs/sbep-reference/58-typelibrary-hotreload-deep-investigation.md</c>.</item>
    ///   <item><c>stalled</c> — build succeeded, gen did not advance, AND the engine logged
    ///     hotload activity inside the swap window (true stall: engine tried to swap but the
    ///     swap was rejected). Recovery: cold restart usually required.</item>
    ///   <item><c>stalled_event_missing</c> — build succeeded, gen did not advance, and the
    ///     engine logged NO hotload activity at all. This is the false-positive path documented
    ///     in <c>docs/ai/sessions/2026-04-18-mcp-pipeline-false-stall/packet.md</c>:
    ///     <c>[EditorEvent.Hotload]</c> does not reliably fire for MCP-triggered compiles that
    ///     don't change assembly topology, OR a disk-watcher hotload already completed before
    ///     the pipeline captured its pre-snapshot. The new DLL IS on disk and will load on the
    ///     next Play; existing live instances may or may not have been swapped in-place.
    ///     Treat as advisory — verify with a runtime probe (run the new ConCmd or call
    ///     <c>sbep.verify_m1_5</c>) before requesting a cold restart.</item>
    ///   <item><c>no_swap_build_failed</c> — compile errors blocked the swap correctly.</item>
    ///   <item><c>no_op</c> — no compile ran, nothing to swap.</item>
    ///   <item><c>unknown</c> — missing inputs (no pre-snapshot, no cached outputs).</item>
    /// </list>
    /// </summary>
    internal static HotloadDiagnostic DescribeHotloadState( int preGeneration, int currentGeneration )
    {
        bool advanced = preGeneration >= 0 && currentGeneration > preGeneration;
        bool anyBuildReported = false;
        bool anySuccess = false;
        bool anyFailed = false;

        if ( _lastOutputs != null )
        {
            foreach ( var output in _lastOutputs )
            {
                var compiler = output.Compiler;
                if ( compiler == null ) continue;
                anyBuildReported = true;
                if ( compiler.BuildSuccess ) anySuccess = true;
                else anyFailed = true;
            }
        }

        string status;
        string hint;
        List<object> logTail = null;
        string logTailText = null;

        if ( preGeneration < 0 )
        {
            status = "unknown";
            hint = "Call compile.snapshot_hotreload before triggering compile, then pass its generation back.";
        }
        else if ( advanced )
        {
            // Widen the log scan to catch "TypeLibrary could not find" lines that can
            // arrive milliseconds after the [EditorEvent.Hotload] handler ran. This
            // is the false-positive path documented in session
            // 2026-04-18-typelibrary-hotreload-investigation/findings.md §4.
            HotReloadProbe.RescanForWarnings( HotReloadProbe.LastHotloadUtc - TimeSpan.FromSeconds( 5 ) );

            if ( HotReloadProbe.LastHotloadHadWarnings( currentGeneration ) )
            {
                status = "upgrade_failed";
                (logTail, logTailText) = CollectHotloadLogTail( 40 );
                var tok = HotReloadProbe.LastWarningToken ?? "upgrade warnings";
                hint = $"Hotload completed but the engine emitted '{tok}' during the swap window. " +
                       "TypeLibrary is corrupted: live Component instances hold stale Type refs, so " +
                       "Components.Get<T>() and scene.GetAllComponents<T>() will return null. " +
                       "COLD RESTART REQUIRED — ask the user to close and relaunch S&Box before any new Play. " +
                       "See hotload_log_tail and docs/sbep-reference/58-typelibrary-hotreload-deep-investigation.md.";
            }
            else
            {
                status = "ok";
                hint = null;
            }
        }
        else if ( anyFailed )
        {
            status = "no_swap_build_failed";
            hint = "Build reported errors, so no DLL swap occurred. Fix compile errors first.";
        }
        else if ( !anyBuildReported )
        {
            status = "unknown";
            hint = "No cached compile outputs — cannot judge whether a swap was expected.";
        }
        else if ( anySuccess )
        {
            (logTail, logTailText) = CollectHotloadLogTail( 20 );
            // Distinguish a true stall (engine tried to swap and something rejected the swap
            // mid-flight — log tail carries *engine-swap* activity) from the much more common
            // case where [EditorEvent.Hotload] simply didn't fire for this compile. The event
            // is not a reliable advance-detector for MCP-triggered compiles: empirical obs
            // is that generation frequently never advances even when the new DLL is live.
            //
            // The discriminator must key on *engine swap phrases* only — NOT the broad
            // CollectHotloadLogTail filter (hotload|hotreload|typelibrary|reload|replac),
            // because benign MCP-side "TypeLibrary could not find X" noise (AP-061, from
            // ConCmd handlers calling Game.ActiveScene.GetAllComponents<T>() in stale
            // context) also matches that filter. Previous iterations of this code used the
            // broad filter as the discriminator and re-triggered the same false positive.
            //
            // Engine-emitted swap phrases observed: "Hotload took", "HotloadObjectIndex",
            // "replacing assembly", "Hotload swap". These are distinct from the downstream
            // TypeLibrary-lookup noise.
            // Ref: docs/ai/sessions/2026-04-18-mcp-pipeline-false-stall/packet.md
            bool tailHasEngineSwapActivity =
                !string.IsNullOrEmpty( logTailText )
                && ( logTailText.Contains( "Hotload took", StringComparison.OrdinalIgnoreCase )
                  || logTailText.Contains( "HotloadObjectIndex", StringComparison.OrdinalIgnoreCase )
                  || logTailText.Contains( "replacing assembly", StringComparison.OrdinalIgnoreCase )
                  || logTailText.Contains( "Hotload swap", StringComparison.OrdinalIgnoreCase )
                  || logTailText.Contains( "couldn't upgrade", StringComparison.OrdinalIgnoreCase ) );

            if ( tailHasEngineSwapActivity )
            {
                status = "stalled";
                hint = BuildStalledHint( logTailText );
            }
            else
            {
                status = "stalled_event_missing";
                hint = "Build succeeded but the [EditorEvent.Hotload] counter did not advance AND "
                     + "the engine emitted no hotload-swap activity in the log tail. The new DLL "
                     + "is on disk and will load on the next Play — the event subscription likely "
                     + "missed this swap (common for MCP-triggered compiles that don't change "
                     + "assembly topology). ADVISORY — do not request a cold restart from this "
                     + "signal alone. Verify via pipeline.probe_typelibrary (cheap direct check) "
                     + "before escalating.";
            }
        }
        else
        {
            status = "no_op";
            hint = "No build activity — nothing to swap.";
        }

        // Always surface the tail for the non-trivial cases — cheap, high signal.
        if ( logTail == null && status != "ok" && status != "no_swap_build_failed" && status != "no_op" )
            (logTail, _) = CollectHotloadLogTail( 20 );

        return new HotloadDiagnostic(
            status, hint, advanced, anyBuildReported, anySuccess, anyFailed, logTail );
    }

    /// <summary>
    /// Filter the shared engine-log ring buffer (<see cref="EditorHandler.LogSnapshot"/>)
    /// for hot-reload / TypeLibrary signal. Returns most-recent-first, up to
    /// <paramref name="max"/> entries, plus a concatenated searchable text blob the
    /// hint builder matches against without re-walking the list.
    /// </summary>
    private static (List<object> entries, string searchableText) CollectHotloadLogTail( int max )
    {
        var results = new List<object>();
        var sb = new System.Text.StringBuilder();
        try
        {
            var snap = EditorHandler.LogSnapshot();
            if ( snap == null || snap.Length == 0 ) return (results, string.Empty);

            static bool IsHotloadLine( McpLogEntry e )
            {
                var m = e.Message ?? string.Empty;
                return m.Contains( "hotload", StringComparison.OrdinalIgnoreCase )
                    || m.Contains( "hotreload", StringComparison.OrdinalIgnoreCase )
                    || m.Contains( "typelibrary", StringComparison.OrdinalIgnoreCase )
                    || m.Contains( "reload", StringComparison.OrdinalIgnoreCase )
                    || m.Contains( "replac", StringComparison.OrdinalIgnoreCase );
            }

            for ( int i = snap.Length - 1; i >= 0 && results.Count < max; i-- )
            {
                var e = snap[i];
                if ( !IsHotloadLine( e ) ) continue;
                results.Add( new
                {
                    time    = e.Time.ToString( "o" ),
                    level   = e.Level,
                    logger  = e.Logger,
                    message = e.Message,
                    source  = e.Source
                } );
                sb.AppendLine( e.Message ?? string.Empty );
                if ( !string.IsNullOrEmpty( e.Stack ) ) sb.AppendLine( e.Stack );
            }
        }
        catch { /* best-effort — never fail the pipeline over diagnostics */ }
        return (results, sb.ToString());
    }

    /// <summary>
    /// Craft a stalled-state hint from the engine log tail. Known-token matches point
    /// at the most likely failure mode; fallback is the generic hint. Tokens chosen
    /// from engine-source survey of <c>Sandbox.Hotload.HotloadResult</c> and
    /// <c>Sandbox.TypeLibrary</c> warning text — keep them in sync with engine updates.
    /// </summary>
    private static string BuildStalledHint( string searchable )
    {
        if ( string.IsNullOrEmpty( searchable ) )
            return "Build succeeded but no hotload fired. Editor likely still running pre-compile DLL. Engine emitted no hotload-adjacent log lines — run editor.get_log filter=\"(?i)hotload|typelibrary\" manually to inspect.";

        if ( searchable.Contains( "could not find", StringComparison.OrdinalIgnoreCase )
          || searchable.Contains( "not registered", StringComparison.OrdinalIgnoreCase ) )
        {
            return "Hotload stalled; engine reports TypeLibrary lookups failing (see hotload_log_tail). Most likely cause: a Component instance was live when a structural change landed. Recovery: stop play, recompile, start play. If it recurs, restart the editor.";
        }
        if ( searchable.Contains( "couldn't upgrade", StringComparison.OrdinalIgnoreCase )
          || searchable.Contains( "InstanceUpgrader", StringComparison.OrdinalIgnoreCase ) )
        {
            return "Hotload rejected the instance upgrade (see hotload_log_tail). Reload the scene to clear live instances, then recompile.";
        }
        if ( searchable.Contains( "NoAction", StringComparison.OrdinalIgnoreCase ) )
        {
            return "Hotload recorded NoAction — no assembly replacement was queued. Most likely the file was not saved to disk or the compile did not produce a new DLL. Verify the edit is on disk and retry.";
        }

        return "Build succeeded but no hotload fired. Editor likely still running pre-compile DLL. See hotload_log_tail for the engine's own signal. Generic recovery: stop play, rebuild, start play; if that fails, restart the editor.";
    }

    // ── snapshot_hotreload / verify_hotreload ────────────────────────────

    /// <summary>
    /// Capture the hotload generation counter before a compile. The agent is expected
    /// to pass the returned <c>generation</c> back into <see cref="VerifyHotreload"/> after
    /// waiting for compile, so the handler can detect a silent swap failure.
    /// </summary>
    private static async Task<object> SnapshotHotreload()
    {
        await GameTask.MainThread();
        return HandlerBase.Success( new
        {
            generation = HotReloadProbe.Generation,
            last_hotload_utc = HotReloadProbe.LastHotloadUtc == DateTime.MinValue
                ? null
                : HotReloadProbe.LastHotloadUtc.ToString( "o" )
        } );
    }

    /// <summary>
    /// Compare the current hotload generation against a snapshot taken before compile.
    /// If build succeeded but the generation did not advance, the DLL swap stalled —
    /// the editor is still running the pre-compile assembly. Caller passes the pre-compile
    /// <c>generation</c> from <see cref="SnapshotHotreload"/>.
    ///
    /// On a stall, the response includes <c>hotload_log_tail</c> (filtered engine-log
    /// lines matching hotload / TypeLibrary / reload) so the caller can see the engine's
    /// own rejection reason without a second round-trip.
    /// </summary>
    private static async Task<object> VerifyHotreload( JsonElement args )
    {
        await GameTask.MainThread();

        var preGeneration = HandlerBase.GetInt( args, "generation", -1 );
        var currentGeneration = HotReloadProbe.Generation;
        var diag = DescribeHotloadState( preGeneration, currentGeneration );

        return HandlerBase.Success( new
        {
            status = diag.Status,
            pre_generation = preGeneration < 0 ? (int?)null : preGeneration,
            post_generation = currentGeneration,
            advanced = diag.Advanced,
            last_hotload_utc = HotReloadProbe.LastHotloadUtc == DateTime.MinValue
                ? null
                : HotReloadProbe.LastHotloadUtc.ToString( "o" ),
            hint = diag.Hint,
            hotload_log_tail = diag.LogTail
        } );
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Read lines from a source file around a specific line number.
    /// Returns a structured object with line numbers and an error-line marker.
    /// </summary>
    private static object ReadSourceContext( string filePath, int? errorLine, int contextLines )
    {
        if ( string.IsNullOrEmpty( filePath ) || !errorLine.HasValue )
            return null;

        if ( !System.IO.File.Exists( filePath ) )
            return new { file = filePath, error = "File not found" };

        string[] lines;
        try { lines = System.IO.File.ReadAllLines( filePath ); }
        catch { return new { file = filePath, error = "Could not read file" }; }

        var start = Math.Max( 0, errorLine.Value - 1 - contextLines );
        var end = Math.Min( lines.Length, errorLine.Value - 1 + contextLines + 1 );

        var contextEntries = new List<object>();
        for ( int i = start; i < end; i++ )
        {
            contextEntries.Add( new
            {
                line = i + 1,
                text = lines[i],
                is_error_line = ( i + 1 == errorLine.Value )
            } );
        }

        return new { file = filePath, lines = contextEntries };
    }

    /// <summary>
    /// Generate a quick fix hint based on common Roslyn error codes.
    /// </summary>
    private static string GenerateFixHint( string errorCode )
    {
        return errorCode switch
        {
            "CS0246" => "Type or namespace not found. Add a 'using' directive or check the type name spelling.",
            "CS1061" => "Member not found. Check the API reference for the correct method/property name.",
            "CS0103" => "Name not in scope. Declare the variable or check its scope.",
            "CS0117" => "Type does not contain a static member with that name.",
            "CS0029" => "Cannot implicitly convert type. Add an explicit cast or use the correct type.",
            "CS0266" => "Cannot implicitly convert type. An explicit cast is required.",
            "CS0019" => "Operator cannot be applied. Check operand types.",
            "CS1503" => "Argument type mismatch. Check the expected parameter type.",
            "CS0234" => "Namespace member not found. Check the using directives and referenced assemblies.",
            "CS0428" => "Cannot convert method group to non-delegate type. Add () to call the method.",
            "CS0162" => "Unreachable code detected. Remove or restructure the dead code path.",
            "CS0168" => "Variable declared but never used. Remove or use the variable.",
            "CS0649" => "Field never assigned and will have default value. Initialize or assign it.",
            "CS8600" => "Converting null literal or possible null value to non-nullable type.",
            "CS8602" => "Dereference of a possibly null reference. Add a null check.",
            _ => null
        };
    }
}
