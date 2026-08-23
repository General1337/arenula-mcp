// Editor/Handlers/AssetQueryHandler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// asset_query tool: browse, search, open, get_dependencies, get_model_info,
/// get_material_properties, get_mesh_info, get_bounds, get_unsaved,
/// get_status, get_json, get_references.
/// All actions are read-only.
/// Ported from Ozmium OzmiumAssetHandlers, UtilityToolHandlers, OzmiumEditorHandlers.
/// New actions: get_status, get_json, get_references.
/// </summary>
internal static class AssetQueryHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        // Lazy-subscribe to the engine logger on first call. We do this here rather than
        // in a static ctor because static ctors run during plugin load when EditorUtility
        // wiring isn't necessarily safe yet — same defensive pattern as
        // EditorHandler.EnsureInitialized.
        EnsureCompileErrorSubscription();
        try
        {
            return action switch
            {
                "browse"                  => Browse( args ),
                "search"                  => Search( args ),
                "open"                    => Open( args ),
                "get_dependencies"        => GetDependencies( args ),
                "get_model_info"          => GetModelInfo( args ),
                "get_material_properties" => GetMaterialProperties( args ),
                "get_mesh_info"           => GetMeshInfo( args ),
                "get_bounds"              => GetBounds( args ),
                "get_unsaved"             => GetUnsaved(),
                "get_status"              => GetStatus( args ),
                "get_json"                => GetJson( args ),
                "get_references"          => GetReferences( args ),
                "get_compile_errors"      => GetCompileErrors( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: browse, search, open, get_dependencies, get_model_info, get_material_properties, get_mesh_info, get_bounds, get_unsaved, get_status, get_json, get_references, get_compile_errors" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── get_compile_errors backing store ──────────────────────────────────
    //
    // Per-asset resource-compile failures, captured by subscribing to the engine logger
    // and parsing the path out of "Failed to compile <path>" / "Resource compile error"
    // / "Compile error in" lines. The C# compiler error path is already covered by the
    // 'compile' tool (CompileHandler._lastOutputs); this is the *resource* compiler
    // (.vmat / .vmdl / .shader / .png), which has no public event surface
    // (verified 2026-05-09 via mcp__sbox-editor__reflection — Sandbox.Resources.*
    // compilers are abstract, no AllGroups static accessor, no per-asset event).
    //
    // LRU eviction: append-at-tail, drop-from-head. _byPath preserves O(1) lookup +
    // O(1) move-to-back on duplicate path; _order owns iteration order.

    internal sealed record CompileError(
        string Path,        // forward-slashed, lowercase for stable filtering
        string Message,
        DateTime Utc,
        string Kind );      // "vmat" | "vmdl" | "shader" | "tmat" | "texture" | "fbx" | "other"

    private const int _compileErrorsCap = 100;
    internal const int CompileErrorsCap = _compileErrorsCap;

    private static readonly Dictionary<string, LinkedListNode<CompileError>> _byPath
        = new( StringComparer.OrdinalIgnoreCase );
    private static readonly LinkedList<CompileError> _order = new();
    private static readonly object _compileErrorsLock = new();
    private static DateTime _compileErrorsCapturedAt = DateTime.MinValue;

    private static bool _compileErrorSubscribed;
    private static readonly object _compileErrorSubscribeLock = new();

    // Path-extraction regex — matches a typical asset filename anywhere in the message.
    // Supports forward + backslash separators (engine emits both depending on platform).
    private static readonly Regex _assetPathRegex = new(
        @"\b([\w\-/\\.]+\.(vmat|vmdl|shader|tmat|png|jpg|tga|fbx|sound|sbproj))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static void EnsureCompileErrorSubscription()
    {
        if ( _compileErrorSubscribed ) return;
        lock ( _compileErrorSubscribeLock )
        {
            if ( _compileErrorSubscribed ) return;
            try
            {
                // Defensive RemoveLogger first — idempotent across hot-reload cycles
                // that re-load this static class (mirror of EditorHandler pattern).
                try { EditorUtility.RemoveLogger( OnEngineLogForCompileErrors ); } catch { }
                EditorUtility.AddLogger( OnEngineLogForCompileErrors );
                _compileErrorSubscribed = true;
            }
            catch ( Exception ex )
            {
                try { Log.Error( $"AssetQueryHandler.EnsureCompileErrorSubscription faulted: {ex.Message}" ); } catch { }
            }
        }
    }

    private static void OnEngineLogForCompileErrors( LogEvent ev )
    {
        try
        {
            // Only resource-compile-class lines. Two filter axes:
            //   1. Logger name — engine resource compilers use a stable "engine/<XCompiler>"
            //      logger family. This is the strongest signal because the engine emits a
            //      compiler diagnostic THROUGH the named logger regardless of message text.
            //      Wave-2 finding 2026-05-09: the synthetic ghost_invalid.vmat test fired
            //      "Feature combo not found" via engine/MaterialCompiler — a pure message-text
            //      filter would have missed every "soft" material/shader compile failure.
            //   2. Message-text token — backstop for compilers that don't tag their logger
            //      consistently (some legacy paths log via Sandbox.Log directly).
            var msg = ev.Message ?? string.Empty;
            if ( msg.Length == 0 ) return;
            var logger = ev.Logger ?? string.Empty;

            bool loggerMatch =
                logger.IndexOf( "MaterialCompiler", StringComparison.OrdinalIgnoreCase ) >= 0 ||
                logger.IndexOf( "ShaderCompiler",   StringComparison.OrdinalIgnoreCase ) >= 0 ||
                logger.IndexOf( "ResourceCompiler", StringComparison.OrdinalIgnoreCase ) >= 0 ||
                logger.IndexOf( "TextureCompiler",  StringComparison.OrdinalIgnoreCase ) >= 0 ||
                logger.IndexOf( "ModelCompiler",    StringComparison.OrdinalIgnoreCase ) >= 0;

            bool messageMatch =
                msg.IndexOf( "failed to compile",        StringComparison.OrdinalIgnoreCase ) >= 0 ||
                msg.IndexOf( "resource compile error",   StringComparison.OrdinalIgnoreCase ) >= 0 ||
                msg.IndexOf( "compile error in",         StringComparison.OrdinalIgnoreCase ) >= 0 ||
                msg.IndexOf( "shader compile error",     StringComparison.OrdinalIgnoreCase ) >= 0;

            if ( !loggerMatch && !messageMatch ) return;

            // Asset-path extraction — first match wins. If nothing parses, store with
            // path="<unparsed>" so the agent can still see the noise (better to surface
            // signal we can't classify than to drop it silently).
            var m = _assetPathRegex.Match( msg );
            string path, kind;
            if ( m.Success )
            {
                path = m.Groups[1].Value.Replace( '\\', '/' ).ToLowerInvariant();
                kind = m.Groups[2].Value.ToLowerInvariant() switch
                {
                    "vmat"   => "vmat",
                    "vmdl"   => "vmdl",
                    "shader" => "shader",
                    "tmat"   => "tmat",
                    "png" or "jpg" or "tga" => "texture",
                    "fbx"    => "fbx",
                    "sound"  => "sound",
                    _        => "other"
                };
            }
            else
            {
                // No recognizable asset path in the message — common for compiler-logger
                // matches like "Feature combo not found" from engine/MaterialCompiler.
                // Embed a ms-precision timestamp so each unparseable entry gets a unique
                // dict key (otherwise N back-to-back compiler diagnostics would all
                // collapse onto a single "<unparsed>" entry under the LRU dedup logic).
                var when = ev.Time == default ? DateTime.UtcNow : ev.Time.ToUniversalTime();
                path = $"<unparsed:{when:HHmmssfff}>";
                kind = "other";
            }

            var entry = new CompileError(
                Path:    path,
                Message: msg,
                Utc:     ev.Time == default ? DateTime.UtcNow : ev.Time.ToUniversalTime(),
                Kind:    kind );

            lock ( _compileErrorsLock )
            {
                _compileErrorsCapturedAt = entry.Utc;
                if ( _byPath.TryGetValue( path, out var existing ) )
                {
                    _order.Remove( existing );
                    _byPath.Remove( path );
                }
                var node = _order.AddLast( entry );
                _byPath[path] = node;
                while ( _order.Count > _compileErrorsCap )
                {
                    var head = _order.First;
                    if ( head == null ) break;
                    _byPath.Remove( head.Value.Path );
                    _order.RemoveFirst();
                }
            }
        }
        catch
        {
            // Best-effort capture — never let a logger callback fault.
        }
    }

    // ── get_compile_errors ────────────────────────────────────────────────
    // Returns the LRU dict of recent per-asset compile failures, newest first.
    // Optional path prefix + kind filters with directory-boundary safety.
    //
    // Params:
    //   path  — case-insensitive prefix filter ("materials/sbep/" narrows to that dir)
    //   kind  — single extension class filter ("vmat" / "shader" / "texture" / etc.)
    //   count — trim to last N after filtering (default = all, capped by _compileErrorsCap)

    private static object GetCompileErrors( JsonElement args )
    {
        var pathFilter = HandlerBase.GetString( args, "path"  );
        var kindFilter = HandlerBase.GetString( args, "kind"  );
        var count      = HandlerBase.GetInt(    args, "count", 0 );

        CompileError[] all;
        DateTime capturedAt;
        int total;
        lock ( _compileErrorsLock )
        {
            all = new CompileError[_order.Count];
            int i = 0;
            for ( var node = _order.Last; node != null; node = node.Previous, i++ )
                all[i] = node.Value;
            capturedAt = _compileErrorsCapturedAt;
            total = _order.Count;
        }

        IEnumerable<CompileError> filtered = all;

        if ( !string.IsNullOrWhiteSpace( pathFilter ) )
        {
            // Directory boundary check — same trick as Browse: prevent "foo" matching
            // "foobar/asset.vmat" by ensuring the prefix ends at a path separator.
            var dir = pathFilter.Trim().Trim( '/', '\\' ).Replace( '\\', '/' ).ToLowerInvariant();
            var dirWithSlash = dir.Length > 0 ? dir + "/" : "";
            filtered = filtered.Where( e =>
            {
                if ( dir.Length == 0 ) return true;
                return e.Path.Equals( dir, StringComparison.OrdinalIgnoreCase )
                    || e.Path.StartsWith( dirWithSlash, StringComparison.OrdinalIgnoreCase );
            } );
        }

        if ( !string.IsNullOrWhiteSpace( kindFilter ) )
        {
            var k = kindFilter.Trim().ToLowerInvariant();
            filtered = filtered.Where( e => string.Equals( e.Kind, k, StringComparison.OrdinalIgnoreCase ) );
        }

        var page = filtered.ToArray();
        if ( count > 0 && count < page.Length )
            page = page[..count];

        var items = page.Select( e => new
        {
            path    = e.Path,
            message = e.Message,
            utc     = e.Utc.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
            kind    = e.Kind
        } ).ToList();

        return HandlerBase.Success( new
        {
            ok            = true,
            total         = total,
            cap           = _compileErrorsCap,
            capturedAtUtc = capturedAt == DateTime.MinValue
                              ? null
                              : capturedAt.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ" ),
            count         = items.Count,
            errors        = items
        } );
    }

    // ── browse ────────────────────────────────────────────────────────────
    // Ported from OzmiumAssetHandlers.BrowseAssets

    private static object Browse( JsonElement args )
    {
        var directory = HandlerBase.GetString( args, "directory" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 50 );

        var all = new List<Asset>();
        // 2026-04-21 — normalize once (trim leading/trailing slashes) and
        // check with an explicit path boundary so `foo` doesn't match
        // `foobar/asset.vmat`. Prior impl used raw StartsWith(dir) which
        // silently widened directory searches across sibling prefixes.
        var dirFilter = ( directory ?? "" ).Trim().Trim( '/' );
        var dirFilterWithSlash = dirFilter.Length > 0 ? dirFilter + "/" : "";
        foreach ( var asset in AssetSystem.All )
        {
            if ( dirFilter.Length > 0 )
            {
                var aPath = asset.Path ?? "";
                if ( !aPath.Equals( dirFilter, StringComparison.OrdinalIgnoreCase )
                     && !aPath.StartsWith( dirFilterWithSlash, StringComparison.OrdinalIgnoreCase ) )
                    continue;
            }
            all.Add( asset );
        }

        return HandlerBase.Paginate( all, offset, limit, asset => new
        {
            path = asset.Path ?? "",
            relativePath = asset.RelativePath ?? asset.Path ?? "",
            name = asset.Name ?? "",
            type = asset.AssetType?.FriendlyName ?? asset.AssetType?.FileExtension ?? "",
            extension = asset.AssetType?.FileExtension ?? ""
        } );
    }

    // ── search ────────────────────────────────────────────────────────────
    // Ported from OzmiumAssetHandlers.SearchAssets

    private static object Search( JsonElement args )
    {
        var query = HandlerBase.GetString( args, "query" );
        var typeFilter = HandlerBase.GetString( args, "type" );
        var format = HandlerBase.GetString( args, "format", "concise" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 50 );

        if ( string.IsNullOrEmpty( query ) )
            return HandlerBase.Error( "Missing required 'query' parameter.", "search" );

        var matches = new List<Asset>();
        foreach ( var asset in AssetSystem.All )
        {
            var aName = asset.Name ?? "";
            var aPath = asset.Path ?? "";
            var ext = asset.AssetType?.FileExtension ?? "";
            var friendly = asset.AssetType?.FriendlyName ?? ext;

            bool nameMatch = aName.IndexOf( query, StringComparison.OrdinalIgnoreCase ) >= 0
                          || aPath.IndexOf( query, StringComparison.OrdinalIgnoreCase ) >= 0;
            if ( !nameMatch ) continue;

            if ( !string.IsNullOrEmpty( typeFilter ) )
            {
                bool typeMatch = ext.IndexOf( typeFilter, StringComparison.OrdinalIgnoreCase ) >= 0
                              || friendly.IndexOf( typeFilter, StringComparison.OrdinalIgnoreCase ) >= 0;
                if ( !typeMatch ) continue;
            }

            matches.Add( asset );
        }

        if ( format == "detailed" )
        {
            return HandlerBase.Paginate( matches, offset, limit, asset => new
            {
                path = asset.Path ?? "",
                relativePath = asset.RelativePath ?? asset.Path ?? "",
                name = asset.Name ?? "",
                type = asset.AssetType?.FriendlyName ?? "",
                extension = asset.AssetType?.FileExtension ?? "",
                absolutePath = asset.AbsolutePath ?? ""
            } );
        }

        return HandlerBase.Paginate( matches, offset, limit, asset => new
        {
            path = asset.Path ?? "",
            name = asset.Name ?? "",
            type = asset.AssetType?.FriendlyName ?? asset.AssetType?.FileExtension ?? ""
        } );
    }

    // ── open ──────────────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.OpenAsset

    private static object Open( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "open" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "open" );

        asset.OpenInEditor();
        return HandlerBase.Confirm( $"Opened '{path}' in editor." );
    }

    // ── get_dependencies ──────────────────────────────────────────────────
    // Ported from UtilityToolHandlers.GetAssetDependencies

    private static object GetDependencies( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_dependencies" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "get_dependencies" );

        var deps = new List<string>();
        var visited = new HashSet<string>();
        CollectDependencies( asset, deps, visited );

        return HandlerBase.Success( new
        {
            asset = path,
            dependencyCount = deps.Count,
            dependencies = deps
        } );
    }

    private static void CollectDependencies( Asset asset, List<string> result, HashSet<string> visited )
    {
        if ( !visited.Add( asset.Path ) ) return;
        try
        {
            foreach ( var dep in asset.GetReferences( false ) )
            {
                var depPath = dep?.Path ?? "?";
                result.Add( depPath );
                var depAsset = AssetSystem.FindByPath( depPath );
                if ( depAsset != null )
                    CollectDependencies( depAsset, result, visited );
            }
        }
        catch { }
    }

    // ── get_model_info ────────────────────────────────────────────────────
    // Ported from OzmiumAssetHandlers.GetModelInfo

    private static object GetModelInfo( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        var format = HandlerBase.GetString( args, "format", "concise" );

        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_model_info" );

        var model = Model.Load( path );
        if ( model == null )
            return HandlerBase.Error( $"Model not found: '{path}'.", "get_model_info" );

        var attachments = new List<object>();
        try
        {
            var attObj = model.Attachments;
            if ( attObj != null )
            {
                foreach ( var att in attObj.All )
                    attachments.Add( new { name = att.Name, index = att.Index } );
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            path,
            boneCount = model.BoneCount,
            attachmentCount = attachments.Count,
            attachments = format == "detailed" ? attachments : null,
            bounds = new { mins = HandlerBase.V3( model.Bounds.Mins ), maxs = HandlerBase.V3( model.Bounds.Maxs ) }
        } );
    }

    // ── get_material_properties ───────────────────────────────────────────
    // Ported from OzmiumAssetHandlers.GetMaterialProperties

    private static object GetMaterialProperties( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_material_properties" );

        var mat = Material.Load( path );
        if ( mat == null )
            return HandlerBase.Error( $"Material not found: '{path}'.", "get_material_properties" );

        return HandlerBase.Success( new
        {
            path,
            name = mat.Name,
            shader = mat.ShaderName
        } );
    }

    // ── get_mesh_info ─────────────────────────────────────────────────────
    // Partially ported from MeshEditHandlers.GetMeshInfo
    // Uses reflection to read EditorMeshComponent's PolygonMesh data

    private static object GetMeshInfo( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null ) return HandlerBase.Error( "No active scene.", "get_mesh_info" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "get_mesh_info" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "get_mesh_info" );

        // Look for EditorMeshComponent or MeshComponent which has a PolygonMesh/Mesh property
        var meshComp = go.Components.GetAll()
            .FirstOrDefault( c => c.GetType().Name.Contains( "EditorMeshComponent" )
                               || c.GetType().Name.Contains( "PolygonMesh" )
                               || c.GetType().Name == "MeshComponent" );

        if ( meshComp == null )
        {
            // Fallback: check for ModelRenderer with a model
            var modelRenderer = go.Components.GetAll()
                .FirstOrDefault( c => c.GetType().Name.Contains( "ModelRenderer" ) );
            if ( modelRenderer != null )
            {
                var modelProp = modelRenderer.GetType().GetProperty( "Model" );
                var model = modelProp?.GetValue( modelRenderer ) as Model;
                if ( model != null && !model.IsError )
                {
                    return HandlerBase.Success( new
                    {
                        id = go.Id.ToString(),
                        name = go.Name,
                        type = "ModelRenderer",
                        boneCount = model.BoneCount,
                        bounds = new { mins = HandlerBase.V3( model.Bounds.Mins ), maxs = HandlerBase.V3( model.Bounds.Maxs ) }
                    } );
                }
            }

            return HandlerBase.Error( $"No mesh component found on '{go.Name}'.", "get_mesh_info" );
        }

        // Try to read vertex/face counts from PolygonMesh via reflection
        var meshProp = meshComp.GetType().GetProperty( "PolygonMesh" )
                    ?? meshComp.GetType().GetProperty( "Mesh" );
        if ( meshProp == null )
            return HandlerBase.Success( new
            {
                id = go.Id.ToString(),
                name = go.Name,
                type = meshComp.GetType().Name,
                note = "PolygonMesh property not found — mesh details unavailable."
            } );

        var mesh = meshProp.GetValue( meshComp );
        if ( mesh == null )
            return HandlerBase.Success( new
            {
                id = go.Id.ToString(),
                name = go.Name,
                type = meshComp.GetType().Name,
                note = "Mesh is null."
            } );

        // Read vertex and face counts via reflection
        var vertexCountProp = mesh.GetType().GetProperty( "VertexCount" )
                           ?? mesh.GetType().GetProperty( "Vertices" );
        var faceCountProp = mesh.GetType().GetProperty( "FaceCount" )
                         ?? mesh.GetType().GetProperty( "Faces" );

        int? vertexCount = null;
        int? faceCount = null;

        if ( vertexCountProp != null )
        {
            var val = vertexCountProp.GetValue( mesh );
            if ( val is int vc ) vertexCount = vc;
            else if ( val is System.Collections.ICollection coll ) vertexCount = coll.Count;
        }
        if ( faceCountProp != null )
        {
            var val = faceCountProp.GetValue( mesh );
            if ( val is int fc ) faceCount = fc;
            else if ( val is System.Collections.ICollection coll ) faceCount = coll.Count;
        }

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            type = meshComp.GetType().Name,
            vertexCount,
            faceCount
        } );
    }

    // ── get_bounds ────────────────────────────────────────────────────────
    // Ported from UtilityToolHandlers.GetObjectBounds

    private static object GetBounds( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null ) return HandlerBase.Error( "No active scene.", "get_bounds" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "get_bounds" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "get_bounds" );
        var bounds = SceneHelpers.GetGameObjectBounds( go );

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            mins = HandlerBase.V3( bounds.Mins ),
            maxs = HandlerBase.V3( bounds.Maxs ),
            center = HandlerBase.V3( bounds.Center ),
            size = HandlerBase.V3( bounds.Size )
        } );
    }

    // ── get_unsaved ───────────────────────────────────────────────────────
    // Ported from OzmiumEditorHandlers.GetSceneUnsaved (extended to all sessions)

    private static object GetUnsaved()
    {
        var results = new List<object>();
        try
        {
            foreach ( var s in SceneEditorSession.All )
            {
                if ( s == null ) continue;
                if ( s.HasUnsavedChanges )
                {
                    results.Add( new
                    {
                        name = s.Scene?.Name ?? "(unnamed)",
                        isActive = s == SceneEditorSession.Active,
                        isPrefabSession = s.IsPrefabSession
                    } );
                }
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            unsavedCount = results.Count,
            message = results.Count > 0 ? $"{results.Count} session(s) have unsaved changes." : "All sessions are saved.",
            unsaved = results
        } );
    }

    // ── get_status ────────────────────────────────────────────────────────
    // NEW action

    private static object GetStatus( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_status" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "get_status" );

        long fileSize = 0;
        try
        {
            if ( !string.IsNullOrEmpty( asset.AbsolutePath ) && System.IO.File.Exists( asset.AbsolutePath ) )
                fileSize = new System.IO.FileInfo( asset.AbsolutePath ).Length;
        }
        catch { }

        return HandlerBase.Success( new
        {
            path,
            name = asset.Name,
            type = asset.AssetType?.FriendlyName ?? "",
            extension = asset.AssetType?.FileExtension ?? "",
            absolutePath = asset.AbsolutePath ?? "",
            fileSize,
            fileSizeFormatted = fileSize > 1024 * 1024
                ? $"{fileSize / (1024.0 * 1024.0):F1} MB"
                : fileSize > 1024
                    ? $"{fileSize / 1024.0:F1} KB"
                    : $"{fileSize} bytes"
        } );
    }

    // ── get_json ──────────────────────────────────────────────────────────
    // NEW action

    private static object GetJson( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_json" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "get_json" );

        if ( string.IsNullOrEmpty( asset.AbsolutePath ) || !System.IO.File.Exists( asset.AbsolutePath ) )
            return HandlerBase.Error( $"Asset file not found on disk: '{path}'.", "get_json" );

        var raw = System.IO.File.ReadAllText( asset.AbsolutePath );

        if ( raw.Length > HandlerBase.MaxResponseChars - 200 )
            raw = raw[..(HandlerBase.MaxResponseChars - 200)] + "\n... [truncated]";

        return HandlerBase.Text( raw );
    }

    // ── get_references ────────────────────────────────────────────────────
    // NEW action — shared implementation also used by asset_manage.get_references

    internal static object GetReferences( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        var deep = HandlerBase.GetBool( args, "deep", false );

        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "get_references" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "get_references" );

        // Assets this path references (outgoing)
        var outgoing = new List<string>();
        try
        {
            var visited = new HashSet<string>();
            if ( deep )
                CollectDependencies( asset, outgoing, visited );
            else
                foreach ( var dep in asset.GetReferences( false ) )
                    outgoing.Add( dep?.Path ?? "?" );
        }
        catch { }

        // Assets that reference this path (incoming) — scan all assets
        var incoming = new List<string>();
        try
        {
            foreach ( var other in AssetSystem.All )
            {
                if ( other.Path == path ) continue;
                try
                {
                    foreach ( var dep in other.GetReferences( false ) )
                    {
                        if ( dep?.Path == path )
                        {
                            incoming.Add( other.Path );
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return HandlerBase.Success( new
        {
            asset = path,
            referencedBy = incoming,
            referencedByCount = incoming.Count,
            references = outgoing,
            referencesCount = outgoing.Count
        } );
    }
}
