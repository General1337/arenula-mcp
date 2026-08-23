// Editor/Core/SceneIntegrityGuard.cs
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// Disk-vs-memory scene integrity accounting shared by editor.reload_scene,
/// editor.save_scene, and asset_manage.reload.
///
/// WHY THIS EXISTS: SceneEditorSession.Reload() is Scene.Load( Scene.Source ) —
/// Scene.Source is the CACHED SceneFile resource, NOT a fresh disk read. A reload
/// issued while the asset watcher hasn't reimported a fresh disk edit (typically
/// play mode) silently resurrects stale state while reporting success; a subsequent
/// save_scene then serializes that stale state over the newer disk content. This
/// destroyed ~8K lines for two independent agents in one week (2026-05-02).
/// Engine source: SBox-Pub/engine/Sandbox.Tools/Scene/Session/SceneEditorSession.cs:299-308
/// (Reload), :310-340 (Save).
///
/// The guard has two layers:
///  1. A stateless count assertion — parse the scene JSON on disk, count GameObjects
///     (and components), compare against a full uncapped walk of the live scene.
///     The data-loss direction is always MEMORY &lt; DISK; memory &gt; disk is normal
///     (prefab expansion, MCP-created objects) and never blocks.
///  2. A per-scene-path hold registry — reload_scene places a hold when its
///     post-reload verdict is partial_load / disk_overwritten; save_scene refuses
///     while a hold exists (force:true overrides). NOTE: statics are wiped by an
///     editor hotload of arenula_mcp, so a hold does not survive a plugin recompile —
///     layer 1 is stateless and still protects after a wipe.
/// </summary>
internal static class SceneIntegrityGuard
{
    /// <summary>Parsed-from-disk truth for a scene file at a moment in time.</summary>
    internal sealed class DiskSnapshot
    {
        public string AbsolutePath;
        public bool   Exists;
        public bool   Parsed;          // false = file exists but JSON parse failed
        public string ParseError;
        public int    GameObjectCount; // recursive: "GameObjects" roots + nested "Children"
        public int    ComponentCount;  // entries of all "Components" arrays
        public long   FileLength;
        public string ContentHash;     // SHA-256 hex of raw bytes
    }

    /// <summary>A refuse-to-save tripwire placed by a bad reload verdict.</summary>
    internal sealed class Hold
    {
        public string   Reason;        // "partial_load" | "disk_overwritten"
        public DateTime PlacedUtc;
        public int      DiskGameObjects;
        public int      MemoryGameObjects;
        public string   Detail;
    }

    // Keyed by normalized scene resource path. ConcurrentDictionary because handler
    // dispatch and feed callbacks can touch this from different threads.
    private static readonly ConcurrentDictionary<string, Hold> _holds =
        new( StringComparer.OrdinalIgnoreCase );

    // ── Hold registry ─────────────────────────────────────────────────────

    internal static string NormalizeKey( string scenePath )
    {
        if ( string.IsNullOrEmpty( scenePath ) ) return null;
        return SceneHelpers.NormalizePath( scenePath )?.Replace( '\\', '/' ).TrimStart( '/' );
    }

    internal static void PlaceHold( string scenePath, string reason, int diskGos, int memGos, string detail )
    {
        var key = NormalizeKey( scenePath );
        if ( key == null ) return;
        _holds[key] = new Hold
        {
            Reason = reason,
            PlacedUtc = DateTime.UtcNow,
            DiskGameObjects = diskGos,
            MemoryGameObjects = memGos,
            Detail = detail
        };
    }

    internal static bool TryGetHold( string scenePath, out Hold hold )
    {
        hold = null;
        var key = NormalizeKey( scenePath );
        return key != null && _holds.TryGetValue( key, out hold );
    }

    internal static void ClearHold( string scenePath )
    {
        var key = NormalizeKey( scenePath );
        if ( key != null ) _holds.TryRemove( key, out _ );
    }

    // ── Disk side ─────────────────────────────────────────────────────────

    /// <summary>Resolve a scene resource path ("scenes/x.scene") to an absolute file path.</summary>
    internal static string ResolveAbsolutePath( string resourcePath )
    {
        if ( string.IsNullOrEmpty( resourcePath ) ) return null;
        try
        {
            var asset = AssetSystem.FindByPath( SceneHelpers.NormalizePath( resourcePath ) );
            if ( !string.IsNullOrEmpty( asset?.AbsolutePath ) && System.IO.File.Exists( asset.AbsolutePath ) )
                return asset.AbsolutePath;
        }
        catch { }
        try { return HandlerBase.ResolveProjectPath( resourcePath ); }
        catch { return null; }
    }

    /// <summary>
    /// Read + parse the scene JSON on disk and count GameObjects/Components.
    /// Never throws — parse failures are reported via Parsed/ParseError so callers
    /// can degrade to "can't verify" rather than blocking on malformed files.
    /// </summary>
    internal static DiskSnapshot SnapshotDisk( string absolutePath )
    {
        var snap = new DiskSnapshot { AbsolutePath = absolutePath };
        if ( string.IsNullOrEmpty( absolutePath ) ) return snap;

        byte[] bytes;
        try
        {
            if ( !System.IO.File.Exists( absolutePath ) ) return snap;
            bytes = System.IO.File.ReadAllBytes( absolutePath );
        }
        catch ( Exception ex )
        {
            snap.ParseError = $"read failed: {ex.Message}";
            return snap;
        }

        snap.Exists = true;
        snap.FileLength = bytes.LongLength;
        try
        {
            snap.ContentHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData( bytes ) );
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse( bytes );
            int gos = 0, comps = 0;
            if ( doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty( "GameObjects", out var roots ) &&
                 roots.ValueKind == JsonValueKind.Array )
            {
                foreach ( var go in roots.EnumerateArray() )
                    CountGameObjectJson( go, ref gos, ref comps );
            }
            snap.GameObjectCount = gos;
            snap.ComponentCount = comps;
            snap.Parsed = true;
        }
        catch ( Exception ex )
        {
            snap.ParseError = ex.Message;
        }
        return snap;
    }

    private static void CountGameObjectJson( JsonElement go, ref int gos, ref int comps )
    {
        if ( go.ValueKind != JsonValueKind.Object ) return;
        gos++;
        if ( go.TryGetProperty( "Components", out var components ) &&
             components.ValueKind == JsonValueKind.Array )
            comps += components.GetArrayLength();
        if ( go.TryGetProperty( "Children", out var children ) &&
             children.ValueKind == JsonValueKind.Array )
        {
            foreach ( var child in children.EnumerateArray() )
                CountGameObjectJson( child, ref gos, ref comps );
        }
    }

    // ── Memory side ───────────────────────────────────────────────────────

    /// <summary>
    /// Full uncapped walk of the live scene. Deliberately NOT SceneHelpers.WalkAll —
    /// that helper skips IgnoreMarker/IgnoreTag objects and bails on subtrees with
    /// many children (MaxAutoWalkChildren), which would undercount large scenes and
    /// false-trip the guard. Integrity comparison needs every object counted.
    /// </summary>
    internal static (int GameObjects, int Components) CountLive( Scene scene )
    {
        int gos = 0, comps = 0;
        if ( scene == null ) return (0, 0);
        try
        {
            foreach ( var root in scene.Children )
                CountLiveSubtree( root, ref gos, ref comps );
        }
        catch { }
        return (gos, comps);
    }

    private static void CountLiveSubtree( GameObject go, ref int gos, ref int comps )
    {
        if ( go == null ) return;
        gos++;
        try { comps += go.Components.Count; } catch { }
        foreach ( var child in go.Children )
            CountLiveSubtree( child, ref gos, ref comps );
    }

    // ── Verdict helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Save-blocking deficit threshold: small scenes tolerate up to 3 missing objects
    /// (legit single-object deletions stay friction-free), large scenes tolerate 10%.
    /// The catastrophic class this guards against is dozens-of-objects / thousands-of-lines.
    /// </summary>
    internal static int SaveDeficitThreshold( int diskCount )
        => Math.Max( 3, diskCount / 10 );
}
