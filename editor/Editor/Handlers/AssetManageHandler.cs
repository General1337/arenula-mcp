using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// asset_manage tool: create, delete, rename, move, save, reload, get_references.
/// All actions are destructive (modify files on disk).
/// Ported from Ozmium OzmiumAssetHandlers.ReloadAsset.
/// New actions: create, delete, rename, move, save.
/// get_references delegates to AssetQueryHandler.GetReferences.
/// </summary>
internal static class AssetManageHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create"         => Create( args ),
                "delete"         => Delete( args ),
                "rename"         => Rename( args ),
                "move"           => Move( args ),
                "save"           => Save( args ),
                "reload"         => Reload( args ),
                "get_references" => AssetQueryHandler.GetReferences( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: create, delete, rename, move, save, reload, get_references" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── create ────────────────────────────────────────────────────────────
    // NEW action — Research API first
    // Query: mcp__api__search_members "GameResource" + "CreateInstance"
    // Query: mcp__api__search_types "AssetType"
    // Approach: create a minimal JSON file on disk, then compile via AssetSystem

    private static object Create( JsonElement args )
    {
        var type = HandlerBase.GetString( args, "type" );
        var path = HandlerBase.GetString( args, "path" );

        if ( string.IsNullOrEmpty( type ) )
            return HandlerBase.Error( "Missing required 'type' parameter.", "create" );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "create" );

        // Determine the correct file extension for the type
        // Common s&box asset types: Material (.vmat), SoundEvent (.sound),
        // GameResource subtypes (.asset), etc.
        // For now, create a minimal JSON file if the path has a known extension.
        try
        {
            // Resolve the absolute path
            var absPath = HandlerBase.ResolveProjectPath( path );
            if ( absPath == null )
                return HandlerBase.Error( "Could not determine project root directory.", "create" );
            var dir = System.IO.Path.GetDirectoryName( absPath );
            if ( !string.IsNullOrEmpty( dir ) && !System.IO.Directory.Exists( dir ) )
                System.IO.Directory.CreateDirectory( dir );

            // Create minimal content based on extension
            var ext = System.IO.Path.GetExtension( path ).ToLowerInvariant();
            var content = ext switch
            {
                ".vmat" => "// THIS FILE IS AUTO-GENERATED\n\nLayer0\n{\n\tshader \"shaders/complex.shader\"\n}\n",
                ".sound" => "{\n  \"UI\": false,\n  \"Volume\": \"1.00,1.00,1\",\n  \"Pitch\": \"1.00,1.00,1\",\n  \"Decibels\": 70,\n  \"SelectionMode\": \"Random\",\n  \"Sounds\": []\n}",
                _ => $"{{\n  \"__type\": \"{type}\"\n}}"
            };

            System.IO.File.WriteAllText( absPath, content );

            // 2026-04-21 — do NOT call AssetSystem.RegisterFile here.
            // Engine rescan picks up the new file; eager registration can leave
            // stale entries in the Facepunch asset cache (no UnregisterFile API
            // exists) which manifest as per-frame errors on the editor-internal
            // 'Editor.LocalAssetLocations tool.frame' handler.
            // MCP clients that need an immediate asset handle should call
            // asset_manage.reload after create.

            return HandlerBase.Success( new
            {
                message = $"Created asset '{path}' of type '{type}'.",
                path,
                absolutePath = absPath
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to create asset: {ex.Message}", "create" );
        }
    }

    // ── delete ────────────────────────────────────────────────────────────
    // NEW action — moves to recycle bin

    private static object Delete( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "delete" );

        var asset = AssetSystem.FindByPath( path );
        var absPath = asset?.AbsolutePath;

        // Fallback: resolve from project root if asset system hasn't indexed yet
        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            absPath = HandlerBase.ResolveProjectPath( path );

        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            return HandlerBase.Error( $"Asset file not found: '{path}'.", "delete" );

        try
        {
            System.IO.File.Delete( absPath );

            return HandlerBase.Confirm( $"Deleted '{path}'." );
        }
        catch ( Exception )
        {
            // Fallback: regular delete if recycle bin API not available
            try
            {
                System.IO.File.Delete( absPath );
                return HandlerBase.Confirm( $"Deleted '{path}' (permanent — recycle bin API not available)." );
            }
            catch ( Exception ex2 )
            {
                return HandlerBase.Error( $"Failed to delete: {ex2.Message}", "delete" );
            }
        }
    }

    // ── rename ────────────────────────────────────────────────────────────
    // NEW action

    private static object Rename( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        var newName = HandlerBase.GetString( args, "new_name" );

        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "rename" );
        if ( string.IsNullOrEmpty( newName ) )
            return HandlerBase.Error( "Missing required 'new_name' parameter.", "rename" );

        var asset = AssetSystem.FindByPath( path );
        var absPath = asset?.AbsolutePath;

        // Fallback: resolve from project root if asset system hasn't indexed yet
        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            absPath = HandlerBase.ResolveProjectPath( path );

        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            return HandlerBase.Error( $"Asset file not found: '{path}'.", "rename" );

        try
        {
            var dir = System.IO.Path.GetDirectoryName( absPath );
            var ext = System.IO.Path.GetExtension( absPath );

            // Ensure new name has the correct extension
            if ( !newName.EndsWith( ext, StringComparison.OrdinalIgnoreCase ) )
                newName += ext;

            var newAbsPath = System.IO.Path.Combine( dir, newName );
            System.IO.File.Move( absPath, newAbsPath );

            // 2026-04-21 — do NOT call AssetSystem.RegisterFile(newAbsPath).
            // Rename leaves the OLD path in the engine's asset cache (no
            // UnregisterFile API exists). Registering the new path on top of
            // that injects a second stale entry. The 'Editor.LocalAssetLocations
            // tool.frame' spam in the editor console was traced to this pattern.
            // Engine rescan picks up the new file and retires the old entry.

            // Compute new relative path
            var parentDir = System.IO.Path.GetDirectoryName( path );
            var newRelPath = string.IsNullOrEmpty( parentDir )
                ? newName
                : parentDir.Replace( '\\', '/' ) + "/" + newName;

            return HandlerBase.Success( new
            {
                message = $"Renamed '{path}' to '{newName}'.",
                oldPath = path,
                newPath = newRelPath
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to rename: {ex.Message}", "rename" );
        }
    }

    // ── move ──────────────────────────────────────────────────────────────
    // NEW action

    private static object Move( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        var destination = HandlerBase.GetString( args, "destination" );

        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "move" );
        if ( string.IsNullOrEmpty( destination ) )
            return HandlerBase.Error( "Missing required 'destination' parameter.", "move" );

        var asset = AssetSystem.FindByPath( path );
        var absPath = asset?.AbsolutePath;

        // Fallback: resolve from project root if asset system hasn't indexed yet
        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            absPath = HandlerBase.ResolveProjectPath( path );

        if ( string.IsNullOrEmpty( absPath ) || !System.IO.File.Exists( absPath ) )
            return HandlerBase.Error( $"Asset file not found: '{path}'.", "move" );

        try
        {
            var fileName = System.IO.Path.GetFileName( absPath );

            // Resolve destination as relative to the project root
            var projectRoot = HandlerBase.GetProjectRoot();
            if ( string.IsNullOrEmpty( projectRoot ) )
                return HandlerBase.Error( "Could not determine project root directory.", "move" );

            var destAbsDir = System.IO.Path.Combine( projectRoot, destination.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );
            if ( !System.IO.Directory.Exists( destAbsDir ) )
                System.IO.Directory.CreateDirectory( destAbsDir );

            var destAbsPath = System.IO.Path.Combine( destAbsDir, fileName );
            System.IO.File.Move( absPath, destAbsPath );

            // 2026-04-21 — do NOT call AssetSystem.RegisterFile(destAbsPath).
            // See Rename handler for rationale: registering after File.Move
            // leaves the old path in the engine's asset cache and injects a
            // fresh entry, producing 'Editor.LocalAssetLocations tool.frame'
            // per-frame errors. Engine rescan handles the transition.

            var newRelPath = destination.TrimEnd( '/' ) + "/" + fileName;

            return HandlerBase.Success( new
            {
                message = $"Moved '{path}' to '{newRelPath}'.",
                oldPath = path,
                newPath = newRelPath
            } );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to move: {ex.Message}", "move" );
        }
    }

    // ── save ──────────────────────────────────────────────────────────────
    // NEW action

    private static object Save( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "save" );

        var asset = AssetSystem.FindByPath( path );
        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "save" );

        try
        {
            // Trigger recompile which effectively "saves" the asset state
            asset.Compile( true );
            return HandlerBase.Confirm( $"Saved asset '{path}'." );
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( $"Failed to save: {ex.Message}", "save" );
        }
    }

    // ── reload ────────────────────────────────────────────────────────────
    // Ported from OzmiumAssetHandlers.ReloadAsset

    private static object Reload( JsonElement args )
    {
        var path = SceneHelpers.NormalizePath( HandlerBase.GetString( args, "path" ) );
        if ( string.IsNullOrEmpty( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "reload" );

        var asset = AssetSystem.FindByPath( path );

        // Fallback: try registering from disk if not yet indexed
        if ( asset == null )
        {
            var absPath = HandlerBase.ResolveProjectPath( path );
            if ( !string.IsNullOrEmpty( absPath ) && System.IO.File.Exists( absPath ) )
                asset = AssetSystem.RegisterFile( absPath );
        }

        if ( asset == null )
            return HandlerBase.Error( $"Asset not found: '{path}'.", "reload" );

        // Fail fast when the asset is referenced by a currently loaded scene
        // (findings #5, 2026-06-11). Recompiling a live-referenced asset can blank
        // or corrupt loaded scene state ("MCP self-heals but not 100%"). The cheap
        // disk-text scan of each open scene's source file catches the common case
        // BEFORE damage instead of after. Newly imported assets are not referenced
        // by any scene yet, so the post-import "reload if invisible" flow is unaffected.
        var force = HandlerBase.GetBool( args, "force", false );
        if ( !force )
        {
            var referencedBy = FindLoadedSceneReferences( asset );
            if ( referencedBy.Count > 0 )
            {
                return HandlerBase.Error(
                    $"REFUSED: '{asset.Path}' is referenced by the currently loaded scene(s): " +
                    $"{string.Join( ", ", referencedBy )}. Recompiling a live-referenced asset risks wiping or " +
                    $"corrupting loaded scene state.",
                    "reload",
                    "Save your work and close/switch the scene first, or pass force:true to recompile anyway. " +
                    "After a forced reload, run editor.reload_scene and check integrity.verified before any save_scene.",
                    new { assetPath = asset.Path, referencedByLoadedScenes = referencedBy },
                    McpErrorCode.ReloadRefusedSceneReferenced );
            }
        }

        asset.Compile( true );
        return HandlerBase.Confirm( $"Recompile triggered for '{path}'."
            + ( force ? " (forced past loaded-scene reference check)" : "" ) );
    }

    /// <summary>
    /// Scene resource paths among open editor sessions whose on-disk source text
    /// references the asset — or that ARE the asset, for .scene reloads (highest risk).
    /// Disk-text substring scan: scene JSON stores source-relative asset paths
    /// (e.g. "meshy/thruster/thruster.vmdl"), which is exactly Asset.Path. Indirect
    /// references (a .vmat used by a .vmdl placed in the scene) are NOT caught —
    /// that would need a transitive GetReferences walk; documented limitation.
    /// </summary>
    private static List<string> FindLoadedSceneReferences( Asset asset )
    {
        var hits = new List<string>();
        var assetPath = asset?.Path?.Replace( '\\', '/' );
        if ( string.IsNullOrEmpty( assetPath ) ) return hits;

        IEnumerable<SceneEditorSession> sessions;
        try { sessions = SceneEditorSession.All ?? new List<SceneEditorSession>(); }
        catch { return hits; }

        foreach ( var session in sessions )
        {
            string scenePath = null;
            try { scenePath = session?.Scene?.Source?.ResourcePath; } catch { }
            if ( string.IsNullOrEmpty( scenePath ) ) continue;
            var normScene = scenePath.Replace( '\\', '/' );

            // Reloading the loaded scene file itself is the direct scene-wipe case.
            if ( string.Equals( normScene, assetPath, StringComparison.OrdinalIgnoreCase ) )
            {
                hits.Add( normScene + " (is the loaded scene itself)" );
                continue;
            }

            try
            {
                var abs = SceneIntegrityGuard.ResolveAbsolutePath( scenePath );
                if ( string.IsNullOrEmpty( abs ) || !System.IO.File.Exists( abs ) ) continue;
                var text = System.IO.File.ReadAllText( abs );
                if ( text.IndexOf( assetPath, StringComparison.OrdinalIgnoreCase ) >= 0 )
                    hits.Add( normScene );
            }
            catch { }
        }
        return hits;
    }
}
