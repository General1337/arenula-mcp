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

    internal static async Task<object> HandleAsync( string action, JsonElement args, McpSession session = null )
    {
        try
        {
            // Caller identity for the session-keyed output cache + feed-event
            // attribution (mcp-multiagent-verdict-integrity, findings.md #4).
            // Explicit agent_id arg works on every dispatch path today; the
            // session-threaded registry read activates once the dispatcher
            // wire-up lands (see the session handoff).
            var (callerAgentId, callerSlug) = AgentIdentityRegistry.ResolveCaller( args, session );

            return action switch
            {
                "trigger"           => await Trigger( callerAgentId, callerSlug ),
                "status"            => await GetStatus(),
                "errors"            => await GetErrors( args, callerAgentId ),
                "generate_solution" => await GenerateSolution(),
                "wait"              => await Wait( args ),
                "smart_trigger"     => await SmartTrigger( args, callerAgentId, callerSlug ),
                "fix_context"       => await FixContext( args, callerAgentId ),
                "snapshot_hotreload"=> await SnapshotHotreload(),
                "verify_hotreload"  => await VerifyHotreload( args ),
                "check"             => await Check( args, callerAgentId, callerSlug ),
                _                   => HandlerBase.Error( $"Unknown action '{action}' for tool 'compile'.", action,
                                        "Valid actions: trigger, status, errors, generate_solution, wait, smart_trigger, fix_context, snapshot_hotreload, verify_hotreload, check" )
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
    private static async Task<object> Trigger( string agentId = null, string agentSlug = null )
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

        // Attribute engine build events fired during this trigger to the caller
        // (findings.md #4 — feeds the session-keyed cache + feed.poll filter).
        using var _attribution = CompileTriggerAttribution.Begin( agentId, agentSlug );

        // Phase 1.7 (editor-contention-reduction-plan.md): incremental DEV compile,
        // not the publish/export path. EditorUtility.Projects.Compile is the publish
        // compiler (fresh CompileGroup("Publish"), Release, mounts publish packages,
        // fresh group's NeedsBuild is ALWAYS true → a full rebuild + package churn on
        // EVERY call). WaitForCompiles() → Project.CompileAsync() is the persistent
        // NeedsBuild-gated dev compile whose fork build-events refresh _lastOutputs
        // (failures included, via OnEngineCompileFailed). Same fix as Check(); the
        // publish path is kept only as the stable-engine fallback. Memory:
        // reference_editorutility_projects_compile_is_publish_path.
        long preSeq = System.Threading.Interlocked.Read( ref _compileEventSeq );
        bool haveStockPre = TryReadStockBuildResult( out var stockPre );   // Phase A pre-state

        await GameTask.MainThread();
        try { await EditorUtility.Projects.WaitForCompiles(); }
        catch { /* failures surface via the fork OnEngineCompileFailed event */ }

        // Grace for the fork build events to land + refresh _lastOutputs.
        var swGrace = System.Diagnostics.Stopwatch.StartNew();
        while ( System.Threading.Interlocked.Read( ref _compileEventSeq ) == preSeq
                && swGrace.ElapsedMilliseconds < 2000 )
        {
            await Task.Delay( 100 );
        }

        long postSeq = System.Threading.Interlocked.Read( ref _compileEventSeq );
        bool builtThisTrigger;
        string triggerSource;

        if ( postSeq > preSeq )
        {
            // Fresh fork-event diagnostics; _lastOutputs already refreshed + Recorded
            // by OnEngineBuildCompleted under this caller's active attribution scope.
            builtThisTrigger = true;
            triggerSource = "incremental_build";
        }
        else if ( postSeq > 0 && _lastOutputs != null && _lastOutputs.Length > 0 )
        {
            // Fork build, nothing needed rebuilding → the cached outputs are current.
            builtThisTrigger = false;
            triggerSource = "no_build_needed";
        }
        else if ( TryReadStockBuildResult( out var stockNow ) )
        {
            // STOCK-ENGINE CHEAP PATH (Phase A, 2026-08-05) — see the block comment on
            // TryReadStockBuildResult. WaitForCompiles() above already ran the engine's
            // persistent, NeedsBuild-gated dev CompileGroup; its BuildResult is the
            // authoritative unmasked verdict. A NEW Diagnostics list instance means a
            // build actually ran. No publish rebuild, no package churn.
            builtThisTrigger = !haveStockPre || !ReferenceEquals( stockPre.Token, stockNow.Token );
            triggerSource = builtThisTrigger ? "dev_build_result" : "dev_build_result_no_build_needed";

            // `trigger`'s legacy response shape is per-CompilerOutput, and BuildResult.Output
            // is SUCCESS-ONLY — publishing it as `_lastOutputs` would quietly drop a failed
            // compiler and make a broken build read as clean. So record the SHAPED
            // diagnostics instead (which include the failed compilers) and return the
            // group's own verdict directly. This also keeps `compile.errors` working: it
            // reads the cache's DiagnosticEntry DTOs, not `_lastOutputs`.
            var stockShaped = ShapeStockDiagnostics( stockNow, out var stockErrors, out var stockWarnings );
            await GameTask.MainThread();
            CompileOutputCache.RecordDiagnostics( stockShaped, agentId, agentSlug,
                builtThisTrigger ? "dev_build_result" : "dev_build_result_no_build_needed" );

            return HandlerBase.Success( new
            {
                message = builtThisTrigger
                    ? "Compilation completed (incremental dev build)."
                    : "Nothing needed rebuilding; returning the current build result.",
                projectCount = compilable.Count,
                source = triggerSource,
                built = builtThisTrigger,
                success = !stockNow.Failed,
                totalErrors = stockErrors,
                totalWarnings = stockWarnings,
                compilerResults = Array.Empty<object>(),
                note = "Stock engine (no fork build events): diagnostics come from the engine's "
                     + "persistent dev CompileGroup BuildResult, which includes FAILED compilers. "
                     + "Publish rebuild skipped (Phase A). Use compile.check for the full shaped "
                     + "diagnostics + hotload verdict."
            } );
        }
        else
        {
            // Last resort: no fork events, no cache, and the dev CompileGroup is
            // unreachable / has never built. Publish Compile() fallback — expensive.
            builtThisTrigger = true;
            triggerSource = "publish_fallback";
            var allOutputs = new List<CompilerOutput>();
            foreach ( var project in compilable )
            {
                var outputs = await EditorUtility.Projects.Compile( project, null );
                if ( outputs != null ) allOutputs.AddRange( outputs );
            }
            await GameTask.MainThread();
            _lastOutputs = allOutputs.ToArray();
            CompileOutputCache.Record( _lastOutputs, agentId, agentSlug, "mcp_trigger" );
        }

        await GameTask.MainThread();
        var outputsForResult = _lastOutputs ?? Array.Empty<CompilerOutput>();
        var results = new List<object>();

        foreach ( var output in outputsForResult )
        {
            var compiler = output?.Compiler;
            if ( compiler == null ) continue;

            results.Add( new
            {
                compiler = compiler.Name ?? "unknown",
                successful = output.Successful,
                diagnosticCount = output.Diagnostics?.Count ?? 0
            } );

            // Forward FRESH compile diagnostics to the editor log buffer. Skip on a
            // no-build trigger — re-logging unchanged diagnostics is just noise.
            if ( !builtThisTrigger || compiler.Diagnostics == null ) continue;

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
            source = triggerSource,
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

        // Session-keyed cache summary (findings.md #4) — lets an agent confirm
        // its own compile entry survives a sibling's trigger.
        var cacheEntries = CompileOutputCache.Snapshot()
            .Select( e => new
            {
                seq = e.Seq,
                utc = e.Utc.ToString( "o" ),
                agent_id = e.AgentId,
                agent_slug = e.AgentSlug,
                source = e.Source,
                output_count = e.Outputs?.Length ?? 0
            } )
            .ToList();

        return HandlerBase.Success( new
        {
            projects = projectStatuses,
            compilers = compilerStatuses,
            hasCompilerCache = _lastOutputs != null,
            output_cache = cacheEntries,
            output_cache_cap = CompileOutputCache.MaxEntries,
            hint = compilerStatuses.Count == 0
                ? "Run compile.trigger first to populate compiler state."
                : null
        } );
    }

    // ── errors ────────────────────────────────────────────────────────────

    /// <summary>
    /// Return compiler diagnostics (errors/warnings) from the last build.
    /// Supports optional severity filter: "error", "warning", "info", "hidden".
    /// Microsoft.CodeAnalysis.Diagnostic is available because s&amp;box ships Roslyn for its compiler.
    /// </summary>
    private static Task<object> GetErrors( JsonElement args, string callerAgentId = null )
    {
        // OFF THE MAIN THREAD (editor-contention-reduction-plan.md Phase 1.1):
        // this reads ONLY the immutable DiagnosticEntry DTOs that
        // CompileOutputCache snapshotted at build-complete on the main thread
        // (Record / MergeFailedIntoLatest -> SnapshotDiagnostics). It never
        // touches a live Sandbox.Compiler, so it cannot race a sibling's
        // in-flight build. The previous code read compiler.Diagnostics HERE,
        // which dereferences Output.Diagnostics.ToArray() — enumerating a
        // List<Diagnostic> the engine populates DURING a build (engine source
        // Compiler.cs:48 + CompilerOutput.cs:45); doing that off the main-thread
        // hop would throw "Collection was modified during enumeration" under
        // multi-agent compiles. The cache DTO snapshot is what makes dropping the
        // GameTask.MainThread() hop safe, so high-frequency compile.errors polls
        // stop queuing behind a sibling's compile/hotload on the single editor
        // main thread (the dominant "everything freezes while someone compiles"
        // stall). compile.status is NOT moved — it reads live project/build state.
        var severityFilter = HandlerBase.GetString( args, "severity" );

        // Session-keyed view (findings.md #4): an identified caller with a cached
        // compile of their own reads THAT entry — a sibling's trigger can no
        // longer clobber this agent's error view mid-read.
        var (diagnostics, cacheView, isSessionView) = ResolveDiagnosticsView( callerAgentId );

        if ( diagnostics == null )
        {
            if ( isSessionView )
            {
                // The caller's own cached entry exists but carries no compiler
                // outputs — a valid (empty) per-agent view, not a missing cache.
                return Task.FromResult<object>( HandlerBase.Success( new
                {
                    totalCount = 0,
                    filter = severityFilter,
                    cache_view = cacheView,
                    diagnostics = Array.Empty<object>()
                } ) );
            }
            return Task.FromResult<object>( HandlerBase.Error(
                "No compile outputs available. Run compile.trigger first.",
                "errors",
                "Use compile.trigger to build, then compile.errors to see diagnostics." ) );
        }

        var sorted = diagnostics
            .Where( d => string.IsNullOrEmpty( severityFilter )
                         || string.Equals( d.severity, severityFilter, StringComparison.OrdinalIgnoreCase ) )
            .OrderBy( d => d.sortOrder )
            .ToList();

        return Task.FromResult<object>( HandlerBase.Success( new
        {
            totalCount = sorted.Count,
            filter = severityFilter,
            cache_view = cacheView,
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
        } ) );
    }

    /// <summary>
    /// Snapshot the live compiler diagnostics for a set of build outputs into
    /// immutable <see cref="DiagnosticEntry"/> DTOs that carry NO engine
    /// references. MUST run on the main thread — it dereferences the live
    /// <c>Sandbox.Compiler.Diagnostics</c> getter, which does
    /// <c>Output.Diagnostics.ToArray()</c> (enumerates a List the engine mutates
    /// during a build; engine source Compiler.cs:48). The callers are the cache
    /// write paths (Record / MergeFailedIntoLatest), all of which run at
    /// build-complete on the main thread. Once snapshotted, <see cref="GetErrors"/>
    /// can read the DTOs off the main-thread hop safely (Phase 1.1). The per-output
    /// try/catch is belt-and-suspenders: should this ever be reached off-thread it
    /// degrades to "skip that compiler" rather than throwing back into the engine
    /// build-event dispatch (OnEngineBuildCompleted must never throw).
    /// </summary>
    internal static List<DiagnosticEntry> SnapshotDiagnostics( CompilerOutput[] outputs )
    {
        var entries = new List<DiagnosticEntry>();
        if ( outputs == null ) return entries;

        foreach ( var output in outputs )
        {
            if ( output == null ) continue;
            var compiler = output.Compiler;
            if ( compiler == null ) continue;

            try
            {
                var compilerDiagnostics = compiler.Diagnostics; // live read — main thread only
                if ( compilerDiagnostics == null || compilerDiagnostics.Length == 0 )
                    continue;

                foreach ( var diag in compilerDiagnostics )
                {
                    var severity = diag.Severity.ToString().ToLowerInvariant();

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

                    entries.Add( new DiagnosticEntry
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
            catch
            {
                // Defensive: only reachable if the diagnostics list is mutated
                // mid-snapshot (should not happen — callers snapshot on the main
                // thread at build-complete). Skip this compiler rather than throw.
            }
        }

        return entries;
    }

    /// <summary>
    /// Resolve the off-thread-safe diagnostics DTO list a compile.errors read
    /// should see — the diagnostics counterpart of <see cref="ResolveOutputsView"/>.
    /// An identified caller with its own cache entry reads that entry's frozen
    /// snapshot (clobber-proof); everyone else reads the NEWEST cache entry, which
    /// mirrors the legacy <see cref="_lastOutputs"/> global because every
    /// <c>_lastOutputs</c> write pairs with a Record / MergeFailedIntoLatest call.
    /// Returns a null list only when no build has been cached yet, so GetErrors can
    /// emit the "run compile.trigger first" error.
    /// </summary>
    private static (IReadOnlyList<DiagnosticEntry> diagnostics, object provenance, bool isSessionView) ResolveDiagnosticsView( string callerAgentId )
    {
        if ( !string.IsNullOrEmpty( callerAgentId )
             && CompileOutputCache.TryGetLatestFor( callerAgentId, out var entry ) )
        {
            return (entry.Diagnostics ?? (IReadOnlyList<DiagnosticEntry>)Array.Empty<DiagnosticEntry>(), new
            {
                view = "session_keyed",
                agent_id = entry.AgentId,
                agent_slug = entry.AgentSlug,
                source = entry.Source,
                utc = entry.Utc.ToString( "o" ),
                age_ms = (long)(DateTime.UtcNow - entry.Utc).TotalMilliseconds
            }, true);
        }

        var newest = CompileOutputCache.Newest();
        return (newest?.Diagnostics, new
        {
            view = "latest_global",
            note = string.IsNullOrEmpty( callerAgentId )
                ? "No agent_id supplied — reading the shared latest snapshot (legacy behavior; a sibling trigger can replace it at any time). Pass agent_id=<your id> on trigger/check AND on this read for a clobber-proof per-agent view."
                : "No cached compile output attributed to this agent yet — falling back to the shared latest snapshot. Run compile.trigger/check with the same agent_id first."
        }, false);
    }

    /// <summary>
    /// Resolve which compile outputs a read action should see (findings.md #4).
    /// An identified caller with an entry of their own in the session-keyed
    /// cache reads that entry (clobber-proof); everyone else reads the legacy
    /// shared <see cref="_lastOutputs"/> snapshot. The provenance object is
    /// attached to the response as <c>cache_view</c> so agents can tell WHICH
    /// view they got.
    /// </summary>
    private static (CompilerOutput[] outputs, object provenance, bool isSessionView) ResolveOutputsView( string callerAgentId )
    {
        if ( !string.IsNullOrEmpty( callerAgentId )
             && CompileOutputCache.TryGetLatestFor( callerAgentId, out var entry ) )
        {
            return (entry.Outputs, new
            {
                view = "session_keyed",
                agent_id = entry.AgentId,
                agent_slug = entry.AgentSlug,
                source = entry.Source,
                utc = entry.Utc.ToString( "o" ),
                age_ms = (long)(DateTime.UtcNow - entry.Utc).TotalMilliseconds
            }, true);
        }

        return (_lastOutputs, new
        {
            view = "latest_global",
            note = string.IsNullOrEmpty( callerAgentId )
                ? "No agent_id supplied — reading the shared latest snapshot (legacy behavior; a sibling trigger can replace it at any time). Pass agent_id=<your id> on trigger/check AND on this read for a clobber-proof per-agent view."
                : "No cached compile output attributed to this agent yet — falling back to the shared latest snapshot. Run compile.trigger/check with the same agent_id first."
        }, false);
    }

    /// <summary>
    /// Immutable plain-C# snapshot of one diagnostic for sorting + serialization.
    /// Carries NO live engine references, so compile.errors can read it off the
    /// main-thread hop. Internal (not private) so CompileOutputCache.Record can
    /// store these alongside each cached build — the build-complete snapshot that
    /// makes the off-thread read safe (editor-contention-reduction-plan.md Phase 1.1).
    /// </summary>
    internal sealed class DiagnosticEntry
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
    private static async Task<object> SmartTrigger( JsonElement args, string agentId = null, string agentSlug = null )
    {
        var contextLines = HandlerBase.GetInt( args, "context_lines", 10 );
        if ( contextLines < 0 ) contextLines = 0;
        if ( contextLines > 30 ) contextLines = 30;

        var maxErrors = HandlerBase.GetInt( args, "max_errors", 5 );
        if ( maxErrors < 1 ) maxErrors = 1;
        if ( maxErrors > 20 ) maxErrors = 20;

        // Attribute engine build events across the whole trigger+wait span
        // (findings.md #4) — Trigger() nests its own same-agent scope, which the
        // singleton-distinct attribution logic treats as one agent.
        using var _attribution = CompileTriggerAttribution.Begin( agentId, agentSlug );

        // 1. Trigger compilation
        var triggerResult = await Trigger( agentId, agentSlug );

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
            // STOCK ENGINE (Phase A, 2026-08-05). `_lastOutputs` is fed by the fork build
            // events or by the publish fallback, and on a stock engine we now run neither.
            // The previous code returned `success: true, errors: []` here — a FALSE GREEN
            // of exactly the class §2 of hotreload.md catalogues: it reported a clean build
            // while holding no evidence at all. Read the dev CompileGroup's BuildResult
            // instead, which carries every compiler's diagnostics including failed ones.
            if ( TryReadStockBuildResult( out var stockNow ) )
            {
                var shaped = ShapeStockDiagnostics( stockNow, out var stockErrors, out var stockWarnings );
                var top = shaped
                    .OrderBy( d => d.sortOrder )
                    .Take( maxErrors )
                    .Select( d => (object)new
                    {
                        d.id, d.severity, d.message, d.compiler, d.file, d.line, d.column,
                        sourceContext = ReadSourceContext( d.file, d.line, contextLines )
                    } )
                    .ToList();

                return HandlerBase.Success( new
                {
                    success = !stockNow.Failed && stockErrors == 0,
                    message = stockNow.Failed || stockErrors > 0
                        ? $"Compilation FAILED — {stockErrors} error(s)."
                        : "Compilation completed.",
                    source = "dev_build_result",
                    totalErrors = stockErrors,
                    totalWarnings = stockWarnings,
                    errors = top,
                    note = "Stock engine: read from the dev CompileGroup BuildResult (includes "
                         + "failed compilers). Prefer compile.check — one call, same unmasked "
                         + "source, plus assembly-swap detection and ConCmd re-registration."
                } );
            }

            // Genuinely no evidence. Do NOT claim success.
            return HandlerBase.Success( new
            {
                success = false,
                message = "Compilation state UNKNOWN — no build outputs and no dev CompileGroup "
                        + "result available. Do not treat this as clean.",
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
    private static async Task<object> FixContext( JsonElement args, string callerAgentId = null )
    {
        await GameTask.MainThread();

        var errorIndex = HandlerBase.GetInt( args, "error_index", 0 );
        var contextLines = HandlerBase.GetInt( args, "context_lines", 15 );
        if ( contextLines < 0 ) contextLines = 0;
        if ( contextLines > 50 ) contextLines = 50;

        // Same session-keyed view as compile.errors (findings.md #4) — error
        // indices must be resolved against the SAME outputs the caller just
        // listed, not a snapshot a sibling may have replaced since.
        var (viewOutputs, cacheView, _) = ResolveOutputsView( callerAgentId );

        if ( viewOutputs == null || viewOutputs.Length == 0 )
            return HandlerBase.Error( "No compile outputs. Run compile.trigger or compile.smart_trigger first.", "fix_context" );

        // Collect all error diagnostics
        var errors = new List<(string id, string severity, string message, string file, int? line, int? column)>();

        foreach ( var output in viewOutputs )
        {
            if ( output == null ) continue;
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
            total_errors = errors.Count,
            cache_view = cacheView
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
    /// Verify whether freshly-compiled code is actually LIVE in the editor.
    ///
    /// Reworked 2026-06-11 (mcp-hotload-truth-bundle, findings.md #1): the authoritative
    /// signal is now a direct TypeLibrary probe (types declared in recently-edited .cs
    /// files — or an explicit <c>type_names</c> array — resolved against the live
    /// TypeLibrary), not the <c>[EditorEvent.Hotload]</c> generation counter, which is
    /// unreliable in both directions (false-stall: 2026-04-18-mcp-pipeline-false-stall;
    /// false-ok: doc 58 §S2). Returns a tri-state <c>verdict</c>:
    /// <list type="bullet">
    ///   <item><c>live</c> — every probed type resolves; the new code is provably loaded.</item>
    ///   <item><c>confirmed-stale</c> — one or more probed types fail to resolve; the swap
    ///     did not land (or the names are typos — see missing_types).</item>
    ///   <item><c>unknown</c> — nothing to probe (no recent edits, no type_names given).
    ///     The legacy generation-based status is attached as advisory only.</item>
    /// </list>
    /// Also returns <c>loaded_type_hash</c> — a stable FNV-1a hash over
    /// (assembly-name:MVID) of every non-engine loaded assembly (newest instance per
    /// name). Compare across calls: a changed hash proves a DLL swap occurred; an
    /// unchanged hash across a "successful" compile proves it did not.
    ///
    /// The legacy generation comparison (pass <c>generation</c> from snapshot_hotreload)
    /// is retained as <c>legacy_status</c> for existing callers; <c>generation</c> is
    /// now optional.
    /// </summary>
    private static async Task<object> VerifyHotreload( JsonElement args )
    {
        await GameTask.MainThread();

        var preGeneration = HandlerBase.GetInt( args, "generation", -1 );
        var currentGeneration = HotReloadProbe.Generation;
        var diag = DescribeHotloadState( preGeneration, currentGeneration );

        var probe = RunTypeLibraryProbe( args, out var sourceFiles, out var autoDerived );
        var (verdict, verdictHint) = ClassifyProbeVerdict( probe );
        var (loadedHash, hashedCount) = ComputeLoadedCodeHash();

        return HandlerBase.Success( new
        {
            verdict,
            loaded_type_hash = loadedHash,
            hashed_assembly_count = hashedCount,
            typelibrary_probe = probe.attempted > 0 ? (object)new
            {
                attempted = probe.attempted,
                found = probe.found,
                missing = probe.missing,
                auto_derived = autoDerived,
                source_files = sourceFiles,
                found_types = probe.foundTypes,
                missing_types = probe.missingTypes
            } : null,
            registry_heal = ShapeHealSummary( ConCmdRegistryHealer.LastHealSummary ),
            legacy_status = diag.Status,
            pre_generation = preGeneration < 0 ? (int?)null : preGeneration,
            post_generation = currentGeneration,
            advanced = diag.Advanced,
            last_hotload_utc = HotReloadProbe.LastHotloadUtc == DateTime.MinValue
                ? null
                : HotReloadProbe.LastHotloadUtc.ToString( "o" ),
            hint = verdictHint ?? diag.Hint,
            hotload_log_tail = verdict == "live" ? null : diag.LogTail
        } );
    }

    // ── check (one-call honest verification, findings.md #2) ─────────────

    /// <summary>
    /// One-call compile verification: trigger + wait(timeout_ms) + UNMASKED diagnostics
    /// + hotload verdict, in a single response. Collapses the binding 3-call discipline
    /// (feed subscribe → smart_trigger → feed poll) and the 3–4 round-trip
    /// compile→wait→errors→verify loop.
    ///
    /// Why "unmasked": <c>EditorUtility.Projects.Compile</c> THROWS on a failed build
    /// (InvalidOperationException), so the legacy trigger path dies before refreshing
    /// <see cref="_lastOutputs"/> — compile.errors then reports the PREVIOUS build's
    /// diagnostics (frequently 0 errors) while the real CS0136/CS0272 sits invisible.
    /// This action (a) try/catches per project, (b) prefers diagnostics arriving via the
    /// fork-engine build events (the same source that feeds <c>feed.compile</c> —
    /// OnEngineBuildCompleted/OnEngineCompileFailed refresh the cache INCLUDING failed
    /// compilers), and (c) when neither source produced fresh outputs, returns
    /// <c>overall:"unknown"</c> with the trigger exceptions — it never reports a stale
    /// cache as current truth.
    /// </summary>
    private static async Task<object> Check( JsonElement args, string callerAgentId = null, string callerSlug = null )
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // Default kept modest so a stuck build doesn't hold the HTTP connection
        // (and one editor main-thread lease) longer than necessary. The primary
        // per-project compile below is fully awaited regardless of this value;
        // timeout_ms only bounds the WaitForCompiles phase. Callers that truly
        // need to wait out a long secondary build pass it explicitly. The gateway
        // request timeout MUST stay >= this max (see sbox-mcp-gateway.mjs) so a
        // slow check is never aborted+re-POSTed into a duplicate build.
        var timeoutMs = HandlerBase.GetInt( args, "timeout_ms", 30000 );
        if ( timeoutMs < 5000 ) timeoutMs = 5000;
        if ( timeoutMs > 300000 ) timeoutMs = 300000;

        var maxDiagnostics = HandlerBase.GetInt( args, "max_diagnostics", 50 );
        if ( maxDiagnostics < 1 ) maxDiagnostics = 1;
        if ( maxDiagnostics > 200 ) maxDiagnostics = 200;

        await GameTask.MainThread();

        // Attribute engine build events across the whole trigger+wait+grace span
        // (findings.md #4 — session-keyed cache + feed.poll agent filter).
        using var _attribution = CompileTriggerAttribution.Begin( callerAgentId, callerSlug );

        // Pre-state for swap detection.
        int preGeneration = HotReloadProbe.Generation;
        long preSeq = System.Threading.Interlocked.Read( ref _compileEventSeq );
        var (preHash, _) = ComputeLoadedCodeHash();

        // 1) Trigger — per-project try/catch (Compile() throws on failed builds).
        var projects = EditorUtility.Projects.GetAll();
        if ( projects == null || projects.Count == 0 )
            return HandlerBase.Error( "No projects found.", "check", "Open a project in the s&box editor." );

        var compilable = projects
            .Where( p => p.Active && !p.IsTransient && !p.IsBuiltIn && p.HasCodePath() )
            .ToList();
        if ( compilable.Count == 0 )
            return HandlerBase.Error( "No compilable projects found.", "check",
                "Ensure the project is active and has a Code folder." );

        // Phase 1.7 (editor-contention-reduction-plan.md): use the LIGHT incremental
        // dev compile, not the heavyweight publish path, whenever the fork build
        // events are available (the running full-fallback build).
        //
        // EditorUtility.Projects.Compile (the per-project call this action USED to run
        // first) is the PUBLISH/export compiler: a fresh CompileGroup("Publish") whose
        // NeedsBuild is ALWAYS true → a full Release rebuild of base + every library +
        // game, plus publish-package mount/unmount churn — on EVERY check, even when
        // nothing changed (engine Utility.Projects.Compile.cs; memory
        // reference_editorutility_projects_compile_is_publish_path). That is the
        // "compile.check launches a bunch of probes / hammers until restart" cost.
        //
        // WaitForCompiles() → Project.CompileAsync() is the incremental DEV compile
        // (persistent CompileGroup, genuinely NeedsBuild-gated, Debug, fast-hotload,
        // and it builds the editor/ folder the publish path skips). Its fork
        // OnEngineBuildCompleted/OnEngineCompileFailed events refresh _lastOutputs
        // INCLUDING failed compilers (Phase 1.2) — the unmasked source this action
        // already PREFERS over the publish output. So on the fork build the publish
        // Compile() was redundant work whose result was discarded.
        var swTrigger = new System.Diagnostics.Stopwatch(); // only ticks during the publish fallback
        var directOutputs = new List<CompilerOutput>();
        var projectResults = new List<object>();
        bool anyThrew = false;

        // 0) Pre-state for the STOCK path (Phase A). On an engine without the fork build
        //    events, "did a build run this check?" is answered by whether the dev group's
        //    BuildResult.Diagnostics is a NEW list instance: BuildAsync assigns a fresh
        //    Results on a real build and leaves the previous one untouched when
        //    !NeedsBuild (CompileGroup.cs:228). Captured before the wait so the compare
        //    below is meaningful.
        bool haveStockPre = TryReadStockBuildResult( out var stockPre );

        // 1) Incremental dev compile FIRST (light, NeedsBuild-gated). WaitForCompiles
        //    internally FileWatch.Tick()s to pick up edits, then Project.CompileAsync()
        //    builds only if something is dirty — a no-op when nothing changed.
        var swWait = System.Diagnostics.Stopwatch.StartNew();
        bool waitTimedOut = false;
        try
        {
            await GameTask.MainThread();
            var waitTask = EditorUtility.Projects.WaitForCompiles();
            var completed = await Task.WhenAny( waitTask, Task.Delay( timeoutMs ) );
            if ( completed == waitTask ) await waitTask;
            else waitTimedOut = true;
        }
        catch { /* build exceptions surface via the fork OnEngineCompileFailed event */ }
        swWait.Stop();

        // 2) Event grace — the fork build events fire after build completion and refresh
        //    _lastOutputs (including FAILED compilers via OnEngineCompileFailed). Give
        //    them a short window to arrive when they haven't already.
        var swGrace = System.Diagnostics.Stopwatch.StartNew();
        while ( System.Threading.Interlocked.Read( ref _compileEventSeq ) == preSeq
                && swGrace.ElapsedMilliseconds < 2000 )
        {
            await Task.Delay( 100 );
        }
        swGrace.Stop();

        // 3) Choose the freshest diagnostics source.
        long postSeq = System.Threading.Interlocked.Read( ref _compileEventSeq );
        bool forkEventsFresh = postSeq > preSeq;   // the dev compile above fired events
        bool forkBuildEngine = postSeq > 0;        // fork build-events exist on this engine
        bool haveCache = _lastOutputs != null && _lastOutputs.Length > 0;
        CompilerOutput[] sourceOutputs;
        string diagnosticsSource;
        bool builtThisCheck;                        // did a build actually run this check?

        // Phase A: when set, diagnostics come from the engine's dev CompileGroup rather
        // than from CompilerOutput[]. Step 5 branches on this.
        StockBuildResult stockNow = default;
        bool useStockDiagnostics = false;

        if ( forkEventsFresh )
        {
            // The incremental compile rebuilt something; the fork events delivered
            // fresh, unmasked diagnostics (failures included). Publish rebuild skipped.
            sourceOutputs = _lastOutputs;
            diagnosticsSource = "engine_build_events"; // same producer as feed.compile
            builtThisCheck = true;
            projectResults.Add( new { status = "incremental_build", note = "fork build events delivered fresh diagnostics; publish rebuild skipped (Phase 1.7)" } );
        }
        else if ( forkBuildEngine && haveCache )
        {
            // Fork build, nothing needed rebuilding → the cached diagnostics are
            // current (every actual build raises the event, so no event == no build
            // == cache still accurate). Skip the publish rebuild + its package churn.
            sourceOutputs = _lastOutputs;
            diagnosticsSource = "cache_current_no_build_needed";
            builtThisCheck = false;
            projectResults.Add( new { status = "no_build_needed", note = "nothing changed since the last build; returning current diagnostics, publish rebuild skipped (Phase 1.7)" } );
        }
        else if ( TryReadStockBuildResult( out stockNow ) )
        {
            // STOCK-ENGINE CHEAP PATH (Phase A, 2026-08-05). No fork build events on this
            // binary, but the engine's own persistent dev CompileGroup just ran under
            // WaitForCompiles() above and is holding the authoritative, UNMASKED result:
            // BuildResult.Diagnostics carries every compiler's diagnostics including the
            // failed ones (BuildResult.Output does NOT — it is success-only, which is why
            // we deliberately ignore it here).
            //
            // This replaces a full publish rebuild — a fresh CompileGroup("Publish") whose
            // NeedsBuild is ALWAYS true, so a Release rebuild of base + every library +
            // game plus publish package mount/unmount churn ran on EVERY call, even with
            // nothing changed. Measured: 13+ minutes holding the exclusive editor lease.
            useStockDiagnostics = true;
            sourceOutputs = null;   // diagnostics come from stockNow, not from outputs

            // A new Diagnostics list instance means BuildAsync actually rebuilt.
            builtThisCheck = !haveStockPre || !ReferenceEquals( stockPre.Token, stockNow.Token );

            diagnosticsSource = builtThisCheck
                ? "dev_build_result"
                : "dev_build_result_no_build_needed";

            projectResults.Add( new
            {
                status = builtThisCheck ? "incremental_build" : "no_build_needed",
                note = builtThisCheck
                    ? "stock engine: read the dev CompileGroup's BuildResult (unmasked, includes failed compilers); publish rebuild skipped (Phase A)"
                    : "stock engine: nothing needed rebuilding; returning the dev CompileGroup's current BuildResult, publish rebuild skipped (Phase A)"
            } );
        }
        else
        {
            // Last resort: no fork events, no cache, AND the dev CompileGroup is
            // unreachable or has never produced a result (a cold editor before its first
            // build, or an engine that renamed these members). Fall back to the publish
            // Compile() per project for an unmasked source — it THROWS on a failed build.
            // This is the expensive path and should now be rare; `diagnostics.source`
            // reads `publish_fallback` when you are on it.
            builtThisCheck = true;
            swTrigger.Start();
            foreach ( var project in compilable )
            {
                var projectName = project.Config?.Title ?? project.Package?.Title ?? "unknown";
                try
                {
                    var outputs = await EditorUtility.Projects.Compile( project, null );
                    if ( outputs != null ) directOutputs.AddRange( outputs );
                    projectResults.Add( new
                    {
                        project = projectName,
                        status = "returned",
                        output_count = outputs?.Length ?? 0
                    } );
                }
                catch ( Exception ex )
                {
                    anyThrew = true;
                    projectResults.Add( new
                    {
                        project = projectName,
                        status = "threw",
                        exception = ex.Message,
                        note = "EditorUtility.Projects.Compile throws on failed builds — diagnostics below "
                             + "are recovered from the engine build events (same source as feed.compile)."
                    } );
                }
            }
            swTrigger.Stop();

            long postSeqAfterPublish = System.Threading.Interlocked.Read( ref _compileEventSeq );
            if ( postSeqAfterPublish > preSeq )
            {
                sourceOutputs = _lastOutputs;
                diagnosticsSource = "engine_build_events";
            }
            else if ( directOutputs.Count > 0 )
            {
                sourceOutputs = directOutputs.ToArray();
                diagnosticsSource = "direct_compile_return";
                _lastOutputs = sourceOutputs; // keep legacy errors/status actions coherent
            }
            else
            {
                sourceOutputs = null;
                diagnosticsSource = anyThrew ? "unavailable_compile_threw" : "unavailable_no_outputs";
            }
        }

        // Session-keyed cache write (findings.md #4): this caller's freshest
        // outputs survive any sibling trigger that lands after this call. The
        // main-thread hop guarantees Record's diagnostics snapshot dereferences
        // the live compiler safely (no-op if already on the main thread).
        if ( sourceOutputs != null )
        {
            await GameTask.MainThread();
            CompileOutputCache.Record( sourceOutputs, callerAgentId, callerSlug,
                diagnosticsSource == "engine_build_events" ? "compile_check_events" : "compile_check_direct" );
        }

        // 5) Shape diagnostics — full list, errors first, honest truncation.
        int totalErrors = 0, totalWarnings = 0;
        bool anyCompilerFailed = false;
        var diagnosticItems = new List<object>();
        if ( useStockDiagnostics )
        {
            // Phase A stock path: the diagnostics live on the dev CompileGroup's
            // BuildResult, not on CompilerOutput[]. `Failed` is the group's own verdict
            // over ALL compilers, so it is authoritative even when a failed compiler
            // contributed no output.
            var collected = ShapeStockDiagnostics( stockNow, out totalErrors, out totalWarnings );
            anyCompilerFailed = stockNow.Failed;

            // Keep compile.errors / the per-agent session view coherent on this path.
            // It reads the cache's DiagnosticEntry DTOs, and the publish fallback we no
            // longer run was the only thing populating them before.
            await GameTask.MainThread();
            CompileOutputCache.RecordDiagnostics( collected, callerAgentId, callerSlug, diagnosticsSource );

            foreach ( var d in collected.OrderBy( d => d.sortOrder ).Take( maxDiagnostics ) )
            {
                diagnosticItems.Add( new
                {
                    d.id, d.severity, d.message, d.compiler, d.file, d.line, d.column,
                    hint = GenerateFixHint( d.id )
                } );
            }
        }
        else if ( sourceOutputs != null )
        {
            var collected = new List<DiagnosticEntry>();
            foreach ( var output in sourceOutputs )
            {
                var compiler = output?.Compiler;
                if ( compiler == null ) continue;
                if ( !compiler.BuildSuccess ) anyCompilerFailed = true;
                if ( compiler.Diagnostics == null ) continue;

                foreach ( var diag in compiler.Diagnostics )
                {
                    var severity = diag.Severity.ToString().ToLowerInvariant();
                    if ( severity == "error" ) totalErrors++;
                    else if ( severity == "warning" ) totalWarnings++;
                    else continue;

                    string filePath = null;
                    int? line = null, column = null;
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

                    collected.Add( new DiagnosticEntry
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

            foreach ( var d in collected.OrderBy( d => d.sortOrder ).Take( maxDiagnostics ) )
            {
                diagnosticItems.Add( new
                {
                    d.id, d.severity, d.message, d.compiler, d.file, d.line, d.column,
                    hint = GenerateFixHint( d.id )
                } );
            }
        }

        // 6) Hotload truth — only self-heal the ConCmd registry + run the TypeLibrary
        //    probe when a build actually ran this check. A "cache current / no build
        //    needed" check cannot have swapped the DLL, so there is nothing to heal or
        //    probe — skipping that cascade on the common no-op check is the other half
        //    of the Phase 1.7 hammer fix (no probe storm on a check that built nothing).
        //    A real build that hotloads is still healed automatically by
        //    ConCmdRegistryHealer.OnHotload regardless of this explicit call.
        await GameTask.MainThread();
        var (postHash, hashedCount) = ComputeLoadedCodeHash();
        bool hashChanged = !string.Equals( preHash, postHash, StringComparison.Ordinal );
        bool inheritedFailedBuildResult = useStockDiagnostics && !builtThisCheck && stockNow.Failed;
        bool failedWithoutErrorDiagnostics = anyCompilerFailed && totalErrors == 0;

        ConCmdRegistryHealer.HealSummary heal = null;
        int postGeneration;
        PipelineHandler.ProbeResult probe;
        List<string> sourceFiles = null;
        bool autoDerived = false;
        string verdict, verdictHint;

        if ( builtThisCheck )
        {
            try { heal = ConCmdRegistryHealer.HealNow( "compile.check" ); }
            catch ( Exception ex ) { Log.Warning( $"compile.check registry heal failed: {ex.Message}" ); }

            postGeneration = HotReloadProbe.Generation;
            probe = RunTypeLibraryProbe( args, out sourceFiles, out autoDerived );
            (verdict, verdictHint) = ClassifyProbeVerdict( probe );
        }
        else
        {
            // No build ran → no DLL swap is possible. A successful persistent
            // BuildResult proves the loaded code is current, but a FAILED one is
            // sticky engine state: CompileGroup.BuildAsync() returns the prior
            // result when NeedsBuild=false. Calling that "live" made a sibling's
            // earlier failure look like this caller's fresh compile verdict.
            // VERIFIED via /check-engine CompileGroup.BuildResult 2026-08-20.
            postGeneration = preGeneration;
            probe = default;
            verdict = ClassifyNoBuildHotloadVerdict( useStockDiagnostics && stockNow.Failed );
            verdictHint = verdict == "live"
                ? "No build was needed (nothing changed since the last successful build) — the loaded "
                  + "code is current; ConCmd registry heal + TypeLibrary probe skipped (Phase 1.7)."
                : "No build ran and the stock dev CompileGroup still carries a failed BuildResult. "
                  + "Loaded code may predate current source; resolve the prior build failure, save or "
                  + "otherwise dirty the affected compiler, then run compile.check again.";
        }
        var legacy = DescribeHotloadState( preGeneration, postGeneration );

        // 7) Overall — never claim clean from an unavailable or failed source.
        // `sourceOutputs == null` used to be the only "we have no source" signal, but the
        // Phase A stock path deliberately leaves it null — its diagnostics come from the
        // dev CompileGroup's BuildResult, not from CompilerOutput[]. Without this the
        // cheap path would report `overall: "unknown"` on a perfectly good verdict.
        string overall = ClassifyOverallReceipt(
            sourceOutputs != null || useStockDiagnostics,
            totalErrors,
            anyCompilerFailed );

        swTotal.Stop();
        return HandlerBase.Success( new
        {
            overall,
            build = new
            {
                projects = projectResults,
                any_threw = anyThrew,
                wait_timed_out = waitTimedOut,
                any_compiler_failed = anyCompilerFailed,
                built_this_check = builtThisCheck,
                inherited_failed_build_result = inheritedFailedBuildResult,
                failed_without_error_diagnostics = failedWithoutErrorDiagnostics
            },
            diagnostics = new
            {
                source = diagnosticsSource,
                last_event_utc = _lastCompileEventUtc == DateTime.MinValue
                    ? null
                    : _lastCompileEventUtc.ToString( "o" ),
                total_errors = totalErrors,
                total_warnings = totalWarnings,
                returned = diagnosticItems.Count,
                truncated = totalErrors + totalWarnings > diagnosticItems.Count,
                items = diagnosticItems,
                // Only "unavailable" when we genuinely have no source. The Phase A stock
                // path leaves sourceOutputs null BY DESIGN (diagnostics come from the dev
                // CompileGroup's BuildResult), so it must not be reported as unknown.
                unavailable_reason = ( sourceOutputs == null && !useStockDiagnostics )
                    ? "No fresh compile outputs reached this call (direct return AND engine build events both empty). "
                      + "Diagnostics are UNKNOWN — do not treat as clean. See build.projects for trigger exceptions."
                    : failedWithoutErrorDiagnostics
                        ? "The compiler group reports failure but supplied zero Error diagnostics. This is not a "
                          + "local clean/error verdict: it may be a sticky prior BuildResult or a non-Roslyn "
                          + "compiler failure. Resolve the last visible build failure and force one fresh build."
                        : null
            },
            hotload = new
            {
                verdict,
                loaded_type_hash = postHash,
                pre_loaded_type_hash = preHash,
                hash_changed = hashChanged,
                hashed_assembly_count = hashedCount,
                legacy_status = legacy.Status,
                pre_generation = preGeneration,
                post_generation = postGeneration,
                typelibrary_probe = probe.attempted > 0 ? (object)new
                {
                    attempted = probe.attempted,
                    found = probe.found,
                    missing = probe.missing,
                    auto_derived = autoDerived,
                    source_files = sourceFiles,
                    found_types = probe.foundTypes,
                    missing_types = probe.missingTypes
                } : null,
                hint = verdictHint ?? legacy.Hint
            },
            registry_heal = ShapeHealSummary( heal ),
            timings_ms = new
            {
                trigger = swTrigger.Elapsed.TotalMilliseconds,
                wait = swWait.Elapsed.TotalMilliseconds,
                event_grace = swGrace.Elapsed.TotalMilliseconds,
                total = swTotal.Elapsed.TotalMilliseconds
            }
        } );
    }

    // ── Hotload-truth shared helpers (verify_hotreload + check) ──────────

    /// <summary>
    /// Run the TypeLibrary probe against an explicit <c>type_names</c> array, or —
    /// when omitted — types declared in .cs files edited within <c>recent_seconds</c>
    /// (default 120). Reuses PipelineHandler's proven extraction + probe plumbing.
    /// Main-thread callers only (TypeLibrary access).
    /// </summary>
    private static PipelineHandler.ProbeResult RunTypeLibraryProbe(
        JsonElement args, out List<string> sourceFiles, out bool autoDerived )
    {
        sourceFiles = null;
        autoDerived = false;

        List<string> names = null;
        if ( args.ValueKind == JsonValueKind.Object
             && args.TryGetProperty( "type_names", out var el )
             && el.ValueKind == JsonValueKind.Array )
        {
            names = new List<string>();
            foreach ( var item in el.EnumerateArray() )
            {
                if ( item.ValueKind != JsonValueKind.String ) continue;
                var s = item.GetString();
                if ( !string.IsNullOrWhiteSpace( s ) ) names.Add( s.Trim() );
            }
        }

        if ( names == null || names.Count == 0 )
        {
            var recentSeconds = HandlerBase.GetInt( args, "recent_seconds", 120 );
            if ( recentSeconds < 10 ) recentSeconds = 10;
            if ( recentSeconds > 3600 ) recentSeconds = 3600;
            sourceFiles = PipelineHandler.ListRecentCsFiles( recentSeconds );
            names = PipelineHandler.ExtractDeclaredTypes( sourceFiles );
            autoDerived = true;
        }

        return PipelineHandler.ProbeTypes( names );
    }

    private static (string verdict, string hint) ClassifyProbeVerdict( PipelineHandler.ProbeResult probe )
    {
        if ( probe.attempted == 0 )
            return ("unknown",
                "Nothing to probe — no .cs files edited recently and no type_names supplied. "
              + "Pass type_names=[\"YourEditedType\"] for a definitive live/stale verdict. "
              + "The legacy generation status below is ADVISORY ONLY (the [EditorEvent.Hotload] "
              + "counter is unreliable in both directions).");

        if ( probe.missing == 0 )
            return ("live",
                $"TypeLibrary resolves all {probe.attempted} probed type(s) — the new code is provably loaded. "
              + "Ignore any 'stalled' legacy status.");

        return ("confirmed-stale",
            $"{probe.missing} of {probe.attempted} probed type(s) do NOT resolve in TypeLibrary "
          + "(see missing_types). Either the DLL swap did not land (true stall) or the names are "
          + "typos. Re-check spelling first; if the names are right, the swap is confirmed stale — "
          + "recompile, and only then consider an editor restart.");
    }

    // ── stock-engine dev-CompileGroup probe (Phase A, 2026-08-05) ─────────────
    //
    // WHY: `check`/`trigger`/`smart_trigger` pick their cheap path off the fork-only
    // `CompileGroup.OnBuildCompleted` event. On a zero-custom-delta upstream build
    // (SBox-Pub-core-v2-candidate, the current canonical editor) that event does not
    // exist, `BindForkEngineEvent` silently no-ops, and every compile call fell through
    // to `EditorUtility.Projects.Compile()` — the PUBLISH compiler, whose NeedsBuild is
    // always true. Measured live 2026-08-05: one `compile check` held the editor's
    // exclusive lease for 13+ minutes and rejected every sibling call.
    //
    // The cure is to read the same thing the fork event carries, from the engine's own
    // persistent dev CompileGroup, which `WaitForCompiles()` has already run and which
    // IS genuinely NeedsBuild-gated (CompileGroup.BuildAsync short-circuits at
    // engine/Sandbox.Compiling/CompileGroup.cs:228 when nothing is dirty).
    //
    // TWO ENGINE FACTS THIS DEPENDS ON, both read from core-v2 source:
    //
    //  1. `Project.CompileGroup` is `internal static` (Project.Static.cs:16) — hence
    //     reflection. `CompileGroup` the TYPE is public and already referenced in this
    //     file, so `BuildResult` / `NeedsBuild` need no reflection once we hold it.
    //
    //  2. **`BuildResult.Output` contains ONLY THE SUCCESSFUL COMPILERS.** CompileGroup.cs
    //     adds every compiler's diagnostics to `result.Diagnostics`, but only appends to
    //     `result.Output` inside `if ( compiler.Output.Successful )`. Reading `Output`
    //     as the diagnostics source would therefore HIDE exactly the errors this action
    //     exists to surface — the CS0136/CS0272 masking class all over again. So we read
    //     `BuildResult.Diagnostics` (all compilers) plus `BuildResult.Failed`.
    //
    // Every accessor is best-effort: any reflection miss, shape change or exception
    // returns false and the caller falls back to the publish path. A future engine that
    // renames these members degrades to the old behaviour, it does not break.

    /// <summary>Snapshot of the engine's persistent dev CompileGroup build result.</summary>
    private readonly struct StockBuildResult
    {
        public readonly bool Failed;
        public readonly List<Microsoft.CodeAnalysis.Diagnostic> Diagnostics;
        /// <summary>Identity of the diagnostics list — a NEW instance means a build ran.</summary>
        public readonly object Token;

        public StockBuildResult( bool failed, List<Microsoft.CodeAnalysis.Diagnostic> diagnostics )
        {
            Failed = failed;
            Diagnostics = diagnostics;
            Token = diagnostics;
        }
    }

    /// <summary>
    /// Reflect the engine's persistent dev <see cref="CompileGroup"/> (Project.CompileGroup,
    /// internal static). Null when unavailable — caller falls back to the publish path.
    /// </summary>
    private static CompileGroup TryGetDevCompileGroup()
    {
        try
        {
            var prop = typeof( Project ).GetProperty( "CompileGroup",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public );
            return prop?.GetValue( null ) as CompileGroup;
        }
        catch { return null; }
    }

    /// <summary>
    /// Read <c>BuildResult.Failed</c> + <c>BuildResult.Diagnostics</c> off the dev group.
    /// Returns false when the group is missing or has never produced a result.
    /// </summary>
    private static bool TryReadStockBuildResult( out StockBuildResult snapshot )
    {
        snapshot = default;
        try
        {
            var group = TryGetDevCompileGroup();
            if ( group == null ) return false;

            var result = group.BuildResult;   // public struct property
            if ( result.Diagnostics == null ) return false;   // `default` — no build yet this session

            snapshot = new StockBuildResult( result.Failed, result.Diagnostics );
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Shape a stock build result into the same <see cref="DiagnosticEntry"/> list the
    /// fork-event path produces. `compilerLabel` stands in for the per-compiler name the
    /// fork path gets from CompilerOutput — a raw Roslyn Diagnostic does not carry it.
    /// </summary>
    private static List<DiagnosticEntry> ShapeStockDiagnostics(
        StockBuildResult snapshot, out int totalErrors, out int totalWarnings )
    {
        totalErrors = 0;
        totalWarnings = 0;
        var collected = new List<DiagnosticEntry>();
        if ( snapshot.Diagnostics == null ) return collected;

        foreach ( var diag in snapshot.Diagnostics )
        {
            string severity;
            try { severity = diag.Severity.ToString().ToLowerInvariant(); }
            catch { continue; }

            if ( severity == "error" ) totalErrors++;
            else if ( severity == "warning" ) totalWarnings++;
            else continue;

            string filePath = null;
            int? line = null, column = null;
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

            string message;
            try { message = diag.GetMessage(); }
            catch { message = "(diagnostic message unavailable)"; }

            collected.Add( new DiagnosticEntry
            {
                id = diag.Id,
                severity = severity,
                message = message,
                compiler = "dev-compile-group",
                file = filePath,
                line = line,
                column = column,
                sortOrder = severity == "error" ? 0 : 1
            } );
        }

        return collected;
    }

    /// <summary>
    /// Fail closed when a compiler says it failed but supplies no Error diagnostic.
    /// Reporting <c>errors</c> there falsely attributes an unlocatable sticky result
    /// to the current caller; reporting <c>clean</c> masks it. Only a concrete Error
    /// diagnostic earns <c>errors</c>.
    /// </summary>
    internal static string ClassifyOverallReceipt(
        bool sourceAvailable, int totalErrors, bool anyCompilerFailed )
    {
        if ( !sourceAvailable ) return "unknown";
        if ( totalErrors > 0 ) return "errors";
        if ( anyCompilerFailed ) return "unknown";
        return "clean";
    }

    /// <summary>
    /// A no-op check is live only when the persistent result is successful.
    /// CompileGroup intentionally retains its previous BuildResult when no compiler
    /// needs a build, including a previous failure.
    /// </summary>
    internal static string ClassifyNoBuildHotloadVerdict( bool priorBuildFailed ) =>
        priorBuildFailed ? "unknown" : "live";

    /// <summary>
    /// Stable identity hash of the loaded NON-ENGINE managed code: FNV-1a 64-bit over
    /// sorted "assemblyName:MVID" lines (newest instance per simple name — same survey
    /// the registry healer uses). The MVID changes on every compile, so comparing this
    /// hash across two calls answers "did a DLL swap actually happen?" without trusting
    /// any engine event.
    /// </summary>
    private static (string hash, int assemblyCount) ComputeLoadedCodeHash()
    {
        try
        {
            var newest = ConCmdRegistryHealer.SurveyNewestAssemblies( out _ );
            var lines = new List<string>( newest.Count );
            foreach ( var (name, asm) in newest )
            {
                string mvid;
                try { mvid = asm.ManifestModule.ModuleVersionId.ToString( "N" ); }
                catch { mvid = "no-mvid"; }
                lines.Add( name + ":" + mvid );
            }
            lines.Sort( StringComparer.Ordinal );

            ulong hash = 14695981039346656037UL; // FNV-1a 64 offset basis
            foreach ( var lineText in lines )
            {
                foreach ( var ch in lineText )
                {
                    hash ^= (byte)ch;
                    hash *= 1099511628211UL;
                    hash ^= (byte)(ch >> 8);
                    hash *= 1099511628211UL;
                }
                hash ^= (byte)'\n';
                hash *= 1099511628211UL;
            }
            return (hash.ToString( "x16" ), lines.Count);
        }
        catch
        {
            return (null, 0);
        }
    }

    /// <summary>Project a heal summary into the response's snake_case shape (null-safe).</summary>
    private static object ShapeHealSummary( ConCmdRegistryHealer.HealSummary heal )
    {
        if ( heal == null ) return null;
        return new
        {
            utc = heal.Utc,
            trigger = heal.Trigger,
            duration_ms = heal.DurationMs,
            stale_assemblies_healed = heal.StaleAssembliesHealed,
            commands_removed = heal.CommandsRemoved,
            commands_re_registered = heal.CommandsReRegistered,
            new_assemblies_registered = heal.NewAssembliesRegistered,
            healed_assemblies = heal.HealedAssemblies,
            re_registered_commands = heal.ReRegisteredCommands,
            error = heal.Error
        };
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

    // ── Fork-engine event subscription (Phase 1.1, AP-100 cure) ──────────
    // Cures AP-100 by replacing log-scrape with event-driven freshness of
    // _lastOutputs. Detected at runtime via REFLECTION on Sandbox.CompileGroup,
    // probing for the fork-only `public static event Action<CompilerOutput[]>
    // OnBuildCompleted` member. Subscribes when present, no-ops cleanly when
    // not — same .dll runs against both fork and stable Facepunch engines.
    //
    // Why reflection instead of `#if FORK_ENGINE`: the S&Box editor's project
    // compiler is a custom Roslyn pipeline (engine/Sandbox.Compiling/) that
    // reads DefineConstants from the package's .sbproj `Metadata.Compiler`
    // block, NOT from MSBuild .csproj/.targets. There is no msbuild import
    // chain at compile time, so .csproj or Directory.Build.targets edits do
    // not reach Roslyn's parser. Hardcoding FORK_ENGINE in sbox_mcp.sbproj
    // would always-define it, breaking the resilience seam (stable engine
    // would try to compile-in the fork-only references and fail). Reflection
    // sidesteps this cleanly: zero compile-time coupling to fork-only types,
    // the library binary is byte-identical between fork and stable builds.
    //
    // Charter §"Binding constraints" #2 (canonical text: "Fork-only APIs
    // live in Libraries/arenula_mcp/** and are gated behind #if FORK_ENGINE")
    // is satisfied in SPIRIT (Workshop-shipped Code/SBEP/ does NOT reference
    // any fork symbol — publish-compat-gate enforces) but not LITERAL (the
    // gating mechanism is runtime reflection, not #if). Documented as an
    // accepted pattern in docs/ai/initiatives/engine-fork-elite-leverage/
    // charter.md after Phase 1.1 ships. The publish-compat-gate banned-symbol
    // scan still works correctly: literal `CompileGroup.OnBuildCompleted`
    // member-access strings are still blocked under Code/SBEP/; the
    // reflection-string `"OnBuildCompleted"` (no dot prefix) does not match.
    //
    // VERIFIED via engine source read 2026-05-18 (codex/hotload-full-fallback,
    // engine/Sandbox.Compiling/CompileGroup.cs):
    //   * OnBuildCompleted declared `public static event Action<CompilerOutput[]>`
    //     at CompileGroup.cs:46.
    //   * Raised once per BuildAsync completion at CompileGroup.cs:400 after
    //     BuildResult publication, before per-instance OnCompileFinished.
    //   * Per-subscriber try/catch in RaiseOnBuildCompleted shields the chain.
    //   * CompileGroup carries [SkipHotload] — engine assembly persists across
    //     library hotloads, so the prior subscription's delegate may still be
    //     in the invocation list pointing at a dead method target. The -+=
    //     pattern in EnsureBound is idempotent across reload cycles.
    //
    // Engine source: M:/SBoxProjects/SBox-Pub-hotload-full-fallback/engine/
    //                Sandbox.Compiling/CompileGroup.cs (commit 27e634ae)

    // The reflected event info + the strongly-typed delegate we hand to it.
    // Both null when running under stable engine (event doesn't exist).
    private static System.Reflection.EventInfo _engineEventInfo;
    private static Delegate _engineHandler;

    // Phase 1.2 sibling event — fires with FAILED CompilerOutputs that the
    // 1.1 OnBuildCompleted deliberately excludes from its payload (see
    // engine/Sandbox.Compiling/CompileGroup.cs: result.Output gates on
    // compiler.Output.Successful). Both null when running under stable engine.
    private static System.Reflection.EventInfo _engineFailedEventInfo;
    private static Delegate _engineFailedHandler;

    /// <summary>
    /// Handler bound to the fork-only event via reflection. Keeps the
    /// existing <see cref="_lastOutputs"/> cache fresh across non-MCP
    /// builds (file-save auto-recompile, UI Compile All, hotload-driven
    /// recompile). Empty payload is treated as "no compilers ran" and
    /// ignored to avoid clobbering the last-good cache.
    /// </summary>
    /// <summary>
    /// Monotonic counter bumped each time a fork-engine build event refreshes
    /// <see cref="_lastOutputs"/>. compile.check snapshots it pre-trigger and treats
    /// an advance as proof the cache is FRESH (event-fed, failures included) rather
    /// than stale from a prior build — the staleness that makes compile.errors mask
    /// CS errors after EditorUtility.Projects.Compile throws on a failed build.
    /// Stays 0 forever on a stable (non-fork) engine where the events don't exist.
    /// </summary>
    private static long _compileEventSeq;

    /// <summary>UTC of the most recent fork-engine build event, for response provenance.</summary>
    private static DateTime _lastCompileEventUtc = DateTime.MinValue;

    private static void OnEngineBuildCompleted( CompilerOutput[] outputs )
    {
        if ( outputs == null || outputs.Length == 0 ) return;
        _lastOutputs = outputs;
        System.Threading.Interlocked.Increment( ref _compileEventSeq );
        _lastCompileEventUtc = DateTime.UtcNow;
        // Session-keyed cache write (findings.md #4) — attributed to the single
        // in-flight MCP trigger when unambiguous, else unattributed.
        var (attrAgentId, attrSlug) = CompileTriggerAttribution.Current;
        CompileOutputCache.Record( outputs, attrAgentId, attrSlug, "engine_build_completed" );
        // [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
        // Phase 2.1 (engine-fork-elite-leverage row 2.1): forward the
        // already-cached outputs to feed.compile subscribers. FeedHandler
        // owns the wire-format + ring-buffer; it no-ops when no subscribers
        // are attached and never throws back into this engine-event-dispatch
        // path (defensive try/catch identical to EmitAsyncJob's contract).
        FeedHandler.EmitCompile( "build_completed", outputs );
    }

    /// <summary>
    /// Phase 1.2 handler — fires after <see cref="OnEngineBuildCompleted"/>
    /// when one or more compilers failed. Appends the failed outputs to the
    /// just-populated <see cref="_lastOutputs"/> so callers of
    /// <c>compile.errors --severity=error</c> see the failed compilers'
    /// diagnostics (the engine's warn-level <c>Log.Warning("Error | ...")</c>
    /// stream and the structured Diagnostics list now reach the same cache).
    ///
    /// Engine-side <c>RaiseOnCompileFailed</c> already early-returns on empty
    /// payload, so this method runs only when there's real failure data.
    ///
    /// Cures AP-100 second-pass: prior to 1.2, the 1.1 event delivered only
    /// successful CompilerOutputs, leaving <see cref="_lastOutputs"/> stale
    /// from the last good build when a fresh build had any failed compiler.
    /// </summary>
    private static void OnEngineCompileFailed( CompilerOutput[] failedOutputs )
    {
        if ( failedOutputs == null || failedOutputs.Length == 0 ) return;

        System.Threading.Interlocked.Increment( ref _compileEventSeq );
        _lastCompileEventUtc = DateTime.UtcNow;

        if ( _lastOutputs == null || _lastOutputs.Length == 0 )
        {
            _lastOutputs = failedOutputs;
            var (earlyAttrAgentId, earlyAttrSlug) = CompileTriggerAttribution.Current;
            CompileOutputCache.Record( failedOutputs, earlyAttrAgentId, earlyAttrSlug, "engine_compile_failed" );
            return;
        }

        // Merge successful (just written by OnEngineBuildCompleted) + failed.
        // Order doesn't matter for the GetErrors loop — it iterates all and
        // filters by diag.Severity per-diagnostic.
        var merged = new CompilerOutput[_lastOutputs.Length + failedOutputs.Length];
        _lastOutputs.CopyTo( merged, 0 );
        failedOutputs.CopyTo( merged, _lastOutputs.Length );
        _lastOutputs = merged;

        // Keep the session-keyed cache coherent with the merge: replace the
        // engine_build_completed entry this same build just wrote (instead of
        // double-recording) so the per-agent view includes the FAILED compilers.
        var (failAttrAgentId, failAttrSlug) = CompileTriggerAttribution.Current;
        CompileOutputCache.MergeFailedIntoLatest( merged, failAttrAgentId, failAttrSlug );

        // [HANDOFF-TRUST: Phase 2.1 wrapper, no live hypothesis inherited]
        // Phase 2.1 (engine-fork-elite-leverage row 2.1): forward FAILED
        // outputs as a separate compile_failed event so severity-aware
        // subscribers (e.g., red-team panels) can differentiate "build
        // succeeded with warnings" from "build had failures". The 1.1
        // build_completed event already fired with the successful subset
        // immediately prior — both arrive on the same channel and order.
        FeedHandler.EmitCompile( "compile_failed", failedOutputs );
    }

    // Hotload re-bind. Engine's [SkipHotload] keeps the static event field
    // alive across library reloads; we may end up with a stale delegate in
    // the invocation list pointing at the prior generation's method. The
    // -=/+= pair in EnsureBound is idempotent in either case.
    [EditorEvent.Hotload]
    public static void RebindForkEngineEventsOnHotload()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"CompileHandler.RebindForkEngineEventsOnHotload failed: {ex.Message}" );
        }
    }

    static CompileHandler()
    {
        try { EnsureBound(); }
        catch ( Exception ex )
        {
            Log.Warning( $"CompileHandler static ctor (fork-engine reflection probe) failed: {ex.Message}" );
        }
    }

    private static void EnsureBound()
    {
        var compileGroupType = typeof( CompileGroup );

        // ── 1.1 binding: OnBuildCompleted (successful outputs) ──────────
        BindForkEngineEvent(
            compileGroupType,
            eventName: "OnBuildCompleted",
            handlerMethodName: nameof( OnEngineBuildCompleted ),
            existingHandler: ref _engineHandler,
            eventInfoSink: ref _engineEventInfo );

        // ── 1.2 binding: OnCompileFailed (failed outputs) ───────────────
        // Sibling fork-only event. Independent probe so 1.1 keeps binding
        // even on a fork build that pre-dates 1.2 (graceful degradation).
        BindForkEngineEvent(
            compileGroupType,
            eventName: "OnCompileFailed",
            handlerMethodName: nameof( OnEngineCompileFailed ),
            existingHandler: ref _engineFailedHandler,
            eventInfoSink: ref _engineFailedEventInfo );
    }

    /// <summary>
    /// Reflection-probe + bind a fork-only public static event on the given
    /// type. Silently no-ops when the event doesn't exist (stable Facepunch
    /// engine). Idempotently re-binds across library hotloads via -+= so the
    /// invocation list never accumulates stale dead-target delegates.
    /// Shared between the 1.1 and 1.2 fork-event subscriptions to avoid
    /// duplicating the reflection plumbing.
    /// </summary>
    private static void BindForkEngineEvent(
        Type type,
        string eventName,
        string handlerMethodName,
        ref Delegate existingHandler,
        ref System.Reflection.EventInfo eventInfoSink )
    {
        var ev = type.GetEvent(
            eventName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static );

        if ( ev == null )
        {
            eventInfoSink = null;
            existingHandler = null;
            return;
        }

        try
        {
            if ( existingHandler != null )
                ev.RemoveEventHandler( null, existingHandler );
        }
        catch { /* defensive — old handler may not be valid post-hotload */ }

        var handlerType = ev.EventHandlerType;
        var handlerMethod = typeof( CompileHandler ).GetMethod(
            handlerMethodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static );
        if ( handlerMethod == null )
        {
            Log.Warning( $"CompileHandler.BindForkEngineEvent: {handlerMethodName} not found via reflection — fork-engine event {eventName} will not bind." );
            return;
        }

        existingHandler = Delegate.CreateDelegate( handlerType, handlerMethod );
        ev.AddEventHandler( null, existingHandler );
        eventInfoSink = ev;
    }
}

// ── CompileTriggerAttribution (mcp-multiagent-verdict-integrity, findings.md #4) ──
//
// Best-effort answer to "which agent caused the engine build event that just
// fired?". MCP compile actions (trigger / smart_trigger / check) register an
// in-flight scope; the engine's OnBuildCompleted/OnCompileFailed handlers and
// FeedHandler.EmitCompile read Current at event time. Resolution is honest by
// construction: exactly one distinct identified agent in flight → that agent;
// multiple distinct agents (or none, e.g. a file-save auto-recompile) → null =
// unattributed. Unattributed events pass every feed.poll agent filter and never
// claim a per-agent cache slot under the wrong identity.
//
// A 10s tail window after the last scope ends covers engine events that arrive
// just after the MCP action returned (the same lag compile.check's event-grace
// loop exists for).

internal static class CompileTriggerAttribution
{
    private static readonly object Sync = new();
    private static readonly List<(long Id, string AgentId, string Slug)> _inflight = new();
    private static long _nextId;
    private static string _lastAgentId;
    private static string _lastSlug;
    private static DateTime _lastEndedUtc = DateTime.MinValue;
    private const int TailWindowMs = 10000;

    /// <summary>Open an in-flight attribution scope. Dispose to close (try/finally or using).</summary>
    internal static IDisposable Begin( string agentId, string slug )
    {
        lock ( Sync )
        {
            var id = ++_nextId;
            _inflight.Add( (id, agentId, slug) );
            return new Scope( id );
        }
    }

    /// <summary>
    /// The agent to attribute an engine build event to right now, or (null, null)
    /// when ambiguous or unknown.
    /// </summary>
    internal static (string AgentId, string Slug) Current
    {
        get
        {
            lock ( Sync )
            {
                string agent = null;
                string slug = null;
                bool any = false;
                foreach ( var entry in _inflight )
                {
                    if ( string.IsNullOrEmpty( entry.AgentId ) ) continue;
                    if ( !any )
                    {
                        agent = entry.AgentId;
                        slug = entry.Slug;
                        any = true;
                    }
                    else if ( !string.Equals( agent, entry.AgentId, StringComparison.Ordinal ) )
                    {
                        return (null, null); // two distinct identified agents in flight — ambiguous
                    }
                }
                if ( any ) return (agent, slug);

                if ( _inflight.Count == 0 && _lastAgentId != null
                     && (DateTime.UtcNow - _lastEndedUtc).TotalMilliseconds < TailWindowMs )
                    return (_lastAgentId, _lastSlug);

                return (null, null);
            }
        }
    }

    private static void End( long id )
    {
        lock ( Sync )
        {
            for ( int i = 0; i < _inflight.Count; i++ )
            {
                if ( _inflight[i].Id != id ) continue;
                if ( !string.IsNullOrEmpty( _inflight[i].AgentId ) )
                {
                    _lastAgentId = _inflight[i].AgentId;
                    _lastSlug = _inflight[i].Slug;
                    _lastEndedUtc = DateTime.UtcNow;
                }
                _inflight.RemoveAt( i );
                return;
            }
        }
    }

    private sealed class Scope : IDisposable
    {
        private long _id;
        internal Scope( long id ) { _id = id; }
        public void Dispose()
        {
            var id = System.Threading.Interlocked.Exchange( ref _id, 0 );
            if ( id != 0 ) End( id );
        }
    }
}

// ── CompileOutputCache (mcp-multiagent-verdict-integrity, findings.md #4) ────
//
// Session-keyed ring of the last N compile outputs, ADDITIVE alongside the
// legacy global _lastOutputs snapshot (which keeps its exact name + semantics:
// ResourceHandler reads it, CompileErrorSeverityTest reflects it by name, and
// DescribeHotloadState judges swap-expectation from it). The ring is what makes
// compile.errors/fix_context clobber-proof: agent A's entry survives agent B's
// trigger because B writes a NEW entry instead of replacing A's view.
//
// Entries are immutable — the failed-build merge REPLACES the newest entry
// rather than mutating it, so a reader holding a reference never sees a torn
// outputs/source pair.

internal static class CompileOutputCache
{
    internal sealed class Entry
    {
        public CompilerOutput[] Outputs { get; init; }
        /// <summary>
        /// Immutable diagnostics snapshot for this build, captured on the main
        /// thread at Record time (CompileHandler.SnapshotDiagnostics). compile.errors
        /// reads THIS off the main-thread hop instead of the live Sandbox.Compiler,
        /// which is what makes the off-thread read safe (Phase 1.1).
        /// </summary>
        public IReadOnlyList<CompileHandler.DiagnosticEntry> Diagnostics { get; init; }
        public DateTime Utc { get; init; }
        public string AgentId { get; init; }
        public string AgentSlug { get; init; }
        public string Source { get; init; }
        public long Seq { get; init; }
    }

    private static readonly object Sync = new();
    private static readonly List<Entry> _entries = new(); // oldest first, newest last
    private static long _seq;

    internal const int MaxEntries = 8;
    private const int FailedMergeWindowMs = 5000;

    /// <summary>
    /// Append an entry from ALREADY-SHAPED diagnostics (Phase A, 2026-08-05).
    /// The stock-engine path reads the dev CompileGroup's <c>BuildResult.Diagnostics</c>,
    /// which is a raw Roslyn list with no <see cref="CompilerOutput"/> behind it — and
    /// deliberately so: <c>BuildResult.Output</c> is SUCCESS-ONLY, so routing this through
    /// <see cref="Record"/> would silently drop a failed compiler and let a broken build
    /// read as clean. <c>Outputs</c> is therefore empty here; every consumer that matters
    /// (<c>compile.errors</c> via <c>ResolveDiagnosticsView</c>) reads the DTO list.
    /// </summary>
    internal static void RecordDiagnostics(
        IReadOnlyList<CompileHandler.DiagnosticEntry> diagnostics,
        string agentId, string agentSlug, string source )
    {
        if ( diagnostics == null ) return;
        lock ( Sync )
        {
            _entries.Add( new Entry
            {
                Outputs = Array.Empty<CompilerOutput>(),
                Diagnostics = diagnostics,
                Utc = DateTime.UtcNow,
                AgentId = agentId,
                AgentSlug = agentSlug,
                Source = source,
                Seq = ++_seq
            } );
            while ( _entries.Count > MaxEntries ) _entries.RemoveAt( 0 );
        }
    }

    /// <summary>Append an entry (drop-oldest past <see cref="MaxEntries"/>).</summary>
    internal static void Record( CompilerOutput[] outputs, string agentId, string agentSlug, string source )
    {
        if ( outputs == null ) return;
        // Snapshot diagnostics on the calling (main) thread BEFORE taking the lock.
        // Every Record caller runs at build-complete on the main thread, where
        // dereferencing the live compiler is safe; the frozen DTOs then let
        // compile.errors read off the main-thread hop (Phase 1.1).
        var diagnostics = CompileHandler.SnapshotDiagnostics( outputs );
        lock ( Sync )
        {
            _entries.Add( new Entry
            {
                Outputs = outputs,
                Diagnostics = diagnostics,
                Utc = DateTime.UtcNow,
                AgentId = agentId,
                AgentSlug = agentSlug,
                Source = source,
                Seq = ++_seq
            } );
            while ( _entries.Count > MaxEntries ) _entries.RemoveAt( 0 );
        }
    }

    /// <summary>
    /// The OnEngineCompileFailed path: the engine fires build_completed (success
    /// subset) then compile_failed milliseconds later for the same build. Replace
    /// the just-written engine entry with the merged snapshot instead of
    /// double-recording, so the per-agent view carries the failed compilers too.
    /// </summary>
    internal static void MergeFailedIntoLatest( CompilerOutput[] mergedSnapshot, string agentId, string agentSlug )
    {
        if ( mergedSnapshot == null || mergedSnapshot.Length == 0 ) return;
        // Re-snapshot the merged (successful + failed) outputs on the main thread
        // so the replaced/added cache entry's DTOs include the failed compilers'
        // diagnostics (Phase 1.1; fired from OnEngineCompileFailed on the main thread).
        var diagnostics = CompileHandler.SnapshotDiagnostics( mergedSnapshot );
        lock ( Sync )
        {
            if ( _entries.Count > 0 )
            {
                var newest = _entries[^1];
                if ( newest.Source == "engine_build_completed"
                     && (DateTime.UtcNow - newest.Utc).TotalMilliseconds < FailedMergeWindowMs )
                {
                    _entries[^1] = new Entry
                    {
                        Outputs = mergedSnapshot,
                        Diagnostics = diagnostics,
                        Utc = DateTime.UtcNow,
                        AgentId = newest.AgentId ?? agentId,
                        AgentSlug = newest.AgentSlug ?? agentSlug,
                        Source = "engine_build_merged",
                        Seq = newest.Seq
                    };
                    return;
                }
            }

            _entries.Add( new Entry
            {
                Outputs = mergedSnapshot,
                Diagnostics = diagnostics,
                Utc = DateTime.UtcNow,
                AgentId = agentId,
                AgentSlug = agentSlug,
                Source = "engine_compile_failed",
                Seq = ++_seq
            } );
            while ( _entries.Count > MaxEntries ) _entries.RemoveAt( 0 );
        }
    }

    /// <summary>Newest entry attributed to <paramref name="agentId"/>, if any.</summary>
    internal static bool TryGetLatestFor( string agentId, out Entry entry )
    {
        entry = null;
        if ( string.IsNullOrEmpty( agentId ) ) return false;
        lock ( Sync )
        {
            for ( int i = _entries.Count - 1; i >= 0; i-- )
            {
                if ( string.Equals( _entries[i].AgentId, agentId, StringComparison.Ordinal ) )
                {
                    entry = _entries[i];
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>All entries, oldest first (for compile.status's output_cache summary).</summary>
    internal static Entry[] Snapshot()
    {
        lock ( Sync ) { return _entries.ToArray(); }
    }

    /// <summary>
    /// Newest entry, or null if none cached. Mirrors the legacy <c>_lastOutputs</c>
    /// global (every _lastOutputs write pairs with a Record/Merge), so its frozen
    /// diagnostics DTOs are the off-thread-safe equivalent of the old live read
    /// for callers without a session-keyed entry (ResolveDiagnosticsView).
    /// </summary>
    internal static Entry Newest()
    {
        lock ( Sync ) { return _entries.Count > 0 ? _entries[^1] : null; }
    }

    /// <summary>Drop all entries attributed to an agent (test cleanup). Returns removed count.</summary>
    internal static int EvictAgent( string agentId )
    {
        if ( string.IsNullOrEmpty( agentId ) ) return 0;
        lock ( Sync )
        {
            return _entries.RemoveAll( e => string.Equals( e.AgentId, agentId, StringComparison.Ordinal ) );
        }
    }
}
