using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// gameobject tool: create, destroy, duplicate, reparent, rename, enable, set_tags, set_transform, batch_transform.
/// Ported from Ozmium OzmiumWriteHandlers + UtilityToolHandlers.BatchTransform.
/// </summary>
internal static class GameObjectHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "create"          => Create( args ),
                "destroy"         => Destroy( args ),
                "destroy_by_name" => DestroyByName( args ),
                "destroy_children" => DestroyChildren( args ),
                "duplicate"       => Duplicate( args ),
                "reparent"        => Reparent( args ),
                "rename"          => Rename( args ),
                "enable"          => Enable( args ),
                "set_tags"        => SetTags( args ),
                "set_transform"   => SetTransform( args ),
                "batch_transform" => BatchTransform( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'gameobject'.", action,
                    "Valid actions: create, destroy, destroy_by_name, destroy_children, duplicate, reparent, rename, enable, set_tags, set_transform, batch_transform" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── create ────────────────────────────────────────────────────────

    private static object Create( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "create" );

        var name = HandlerBase.GetString( args, "name", "New GameObject" );
        var parentId = HandlerBase.GetString( args, "parent_id" );
        var posStr = HandlerBase.GetString( args, "position" );
        var rotStr = HandlerBase.GetString( args, "rotation" );
        var scaleStr = HandlerBase.GetString( args, "scale" );

        var go = scene.CreateObject();
        go.Name = name;

        if ( !string.IsNullOrEmpty( parentId ) )
        {
            var parent = SceneHelpers.FindByIdOrThrow( scene, parentId, "create" );
            go.SetParent( parent );
        }

        if ( !string.IsNullOrEmpty( posStr ) )
            go.WorldPosition = HandlerBase.ParseVector3( posStr );

        if ( !string.IsNullOrEmpty( rotStr ) )
        {
            var r = HandlerBase.ParseVector3( rotStr );
            go.WorldRotation = Rotation.From( r.x, r.y, r.z );
        }

        if ( !string.IsNullOrEmpty( scaleStr ) )
            go.WorldScale = HandlerBase.ParseVector3( scaleStr );

        return HandlerBase.WriteConfirm( go, $"Created '{go.Name}'." );
    }

    // ── destroy ───────────────────────────────────────────────────────

    private static object Destroy( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "destroy" );

        // Support bulk destroy by name (e.g. destroy all "info_player_start" under a parent)
        var name = HandlerBase.GetString( args, "name" );
        var parentId = HandlerBase.GetString( args, "parent_id" );
        if ( !string.IsNullOrEmpty( name ) && !string.IsNullOrEmpty( parentId ) )
        {
            // Use deep search to find parent (handles map-spawned objects)
            var parent = SceneHelpers.FindById( scene, parentId );
            if ( parent == null && Guid.TryParse( parentId, out var pguid ) )
                parent = FindGameObjectDeep( scene, pguid );

            if ( parent == null )
                return HandlerBase.Error( $"Parent '{parentId}' not found.", "destroy" );

            var toDestroy = parent.Children.Where( c => c.Name == name ).ToList();
            foreach ( var go in toDestroy )
                go.Destroy();

            return HandlerBase.Confirm( $"Destroyed {toDestroy.Count} objects named '{name}' under '{parent.Name}'." );
        }

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "destroy" );

        // Try FindById first (now uses GetAllObjects + deep walk), then fall back to local deep search
        var target = SceneHelpers.FindById( scene, id );
        if ( target == null && Guid.TryParse( id, out var guid ) )
        {
            target = FindGameObjectDeep( scene, guid );
        }

        if ( target == null )
            return HandlerBase.Error( $"GameObject '{id}' not found in scene", "destroy" );

        var displayName = target.Name;
        target.Destroy();

        return HandlerBase.Confirm( $"Destroyed '{displayName}' ({id})." );
    }

    // ── destroy_by_name ──────────────────────────────────────────────

    private static object DestroyByName( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "destroy_by_name" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "destroy_by_name" );

        var parentId = HandlerBase.GetString( args, "id" );

        // If parent specified, only destroy direct children of that parent matching the name
        string parentName = "scene";

        if ( !string.IsNullOrEmpty( parentId ) && Guid.TryParse( parentId, out var pguid ) )
        {
            // Find parent using deep unfiltered search (handles map-spawned objects)
            GameObject parent = FindGameObjectDeep( scene, pguid );
            if ( parent == null )
                return HandlerBase.Error( $"Parent '{parentId}' not found.", "destroy_by_name" );

            parentName = parent.Name;
            var toDestroy = parent.Children.Where( go => go.Name == name ).ToList();
            foreach ( var go in toDestroy )
                go.Destroy();

            return HandlerBase.Confirm( $"Destroyed {toDestroy.Count} objects named '{name}' under '{parentName}'." );
        }
        else
        {
            // Search entire scene tree recursively
            var toDestroy = new List<GameObject>();
            CollectByName( scene.Children, name, toDestroy );
            foreach ( var go in toDestroy )
                go.Destroy();

            return HandlerBase.Confirm( $"Destroyed {toDestroy.Count} objects named '{name}' in scene." );
        }
    }

    // ── destroy_children ────────────────────────────────────────────

    private static object DestroyChildren( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "destroy_children" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "destroy_children" );

        if ( !Guid.TryParse( id, out var guid ) )
            return HandlerBase.Error( $"Invalid GUID: '{id}'.", "destroy_children" );

        // Find parent using deep unfiltered search (handles map-spawned objects)
        GameObject parent = FindGameObjectDeep( scene, guid );
        if ( parent == null )
            return HandlerBase.Error( $"GameObject '{id}' not found in scene.", "destroy_children" );

        var children = parent.Children.ToList();
        int count = children.Count;
        foreach ( var child in children )
            child.Destroy();

        return HandlerBase.Confirm( $"Destroyed {count} children of '{parent.Name}'." );
    }

    // ── helpers for deep search without FindById ─────────────────────

    /// <summary>
    /// Deep recursive search through go.Children directly, bypassing FindById.
    /// Works for map-spawned objects that may not be found by other methods.
    /// </summary>
    private static GameObject FindGameObjectDeep( Scene scene, Guid guid )
    {
        // Strategy 1: Try scene.GetAllObjects which includes map-spawned objects
        try
        {
            foreach ( var go in scene.GetAllObjects( true ) )
                if ( go.Id == guid ) return go;
        }
        catch { }

        // Strategy 2: Recursive walk through Children
        foreach ( var root in scene.Children )
        {
            var result = FindInSubtreeDeep( root, guid );
            if ( result != null ) return result;
        }
        return null;
    }

    private static GameObject FindInSubtreeDeep( GameObject go, Guid guid )
    {
        if ( go.Id == guid ) return go;
        foreach ( var child in go.Children )
        {
            var result = FindInSubtreeDeep( child, guid );
            if ( result != null ) return result;
        }
        return null;
    }

    /// <summary>
    /// Recursively collects all objects with the given name from the scene tree.
    /// </summary>
    private static void CollectByName( IEnumerable<GameObject> objects, string name, List<GameObject> results )
    {
        foreach ( var go in objects )
        {
            if ( go.Name == name )
                results.Add( go );
            CollectByName( go.Children, name, results );
        }
    }

    // ── duplicate ─────────────────────────────────────────────────────

    private static object Duplicate( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "duplicate" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "duplicate" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "duplicate" );

        // Serialize + deserialize into the correct scene for a safe deep copy.
        // Clone() may target Game.ActiveScene which can differ from the editor session scene.
        var json = go.Serialize();
        var clone = scene.CreateObject( false );
        clone.Deserialize( json );
        clone.Name = go.Name;
        clone.MakeNameUnique();
        clone.Enabled = true;

        return HandlerBase.WriteConfirm( clone, $"Duplicated '{go.Name}' as '{clone.Name}'." );
    }

    // ── reparent ──────────────────────────────────────────────────────

    private static object Reparent( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "reparent" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "reparent" );

        var parentId = HandlerBase.GetString( args, "parent_id" );
        if ( string.IsNullOrEmpty( parentId ) )
            return HandlerBase.Error( "Missing required 'parent_id' parameter. Pass 'null' to move to scene root.", "reparent" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "reparent" );

        if ( parentId == "null" || parentId == "root" )
        {
            go.SetParent( null );
            return HandlerBase.WriteConfirm( go, $"Moved '{go.Name}' to scene root." );
        }

        var parent = SceneHelpers.FindByIdOrThrow( scene, parentId, "reparent" );
        go.SetParent( parent );

        return HandlerBase.WriteConfirm( go, $"Moved '{go.Name}' under '{parent.Name}'." );
    }

    // ── rename ────────────────────────────────────────────────────────

    private static object Rename( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "rename" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "rename" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "rename" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "rename" );
        var oldName = go.Name;
        go.Name = name;

        return HandlerBase.WriteConfirm( go, $"Renamed '{oldName}' to '{go.Name}'." );
    }

    // ── enable ────────────────────────────────────────────────────────

    private static object Enable( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "enable" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "enable" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "enable" );

        if ( args.TryGetProperty( "enabled", out var enabledEl ) )
            go.Enabled = enabledEl.GetBoolean();
        else
            return HandlerBase.Error( "Missing required 'enabled' parameter.", "enable" );

        return HandlerBase.WriteConfirm( go, $"'{go.Name}' is now {(go.Enabled ? "enabled" : "disabled")}." );
    }

    // ── set_tags ──────────────────────────────────────────────────────

    private static object SetTags( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "set_tags" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "set_tags" );

        var tagsStr = HandlerBase.GetString( args, "tags" );
        if ( string.IsNullOrEmpty( tagsStr ) )
            return HandlerBase.Error( "Missing required 'tags' parameter (comma-separated tag list).", "set_tags" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "set_tags" );

        // Replace all tags
        go.Tags.RemoveAll();
        foreach ( var tag in tagsStr.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
            go.Tags.Add( tag );

        return HandlerBase.WriteConfirm( go, $"Tags on '{go.Name}': {string.Join( ", ", go.Tags.TryGetAll() )}" );
    }

    // ── set_transform ─────────────────────────────────────────────────

    private static object SetTransform( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "set_transform" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "set_transform" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "set_transform" );
        var space = HandlerBase.GetString( args, "space", "world" );
        bool isLocal = space.Equals( "local", StringComparison.OrdinalIgnoreCase );

        var posStr = HandlerBase.GetString( args, "position" );
        var rotStr = HandlerBase.GetString( args, "rotation" );
        var scaleStr = HandlerBase.GetString( args, "scale" );

        if ( !string.IsNullOrEmpty( posStr ) )
        {
            var pos = HandlerBase.ParseVector3( posStr );
            if ( isLocal ) go.LocalPosition = pos;
            else go.WorldPosition = pos;
        }

        if ( !string.IsNullOrEmpty( rotStr ) )
        {
            var r = HandlerBase.ParseVector3( rotStr );
            var rot = Rotation.From( r.x, r.y, r.z );
            if ( isLocal ) go.LocalRotation = rot;
            else go.WorldRotation = rot;
        }

        if ( !string.IsNullOrEmpty( scaleStr ) )
        {
            var s = HandlerBase.ParseVector3( scaleStr );
            if ( isLocal ) go.LocalScale = s;
            else go.WorldScale = s;
        }

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            message = $"Updated {space} transform for '{go.Name}'.",
            space,
            position = isLocal ? HandlerBase.V3( go.LocalPosition ) : HandlerBase.V3( go.WorldPosition ),
            rotation = isLocal ? HandlerBase.Rot( go.LocalRotation ) : HandlerBase.Rot( go.WorldRotation ),
            scale = isLocal ? HandlerBase.V3( go.LocalScale ) : HandlerBase.V3( go.WorldScale )
        } );
    }

    // ── batch_transform ───────────────────────────────────────────────

    private static object BatchTransform( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "batch_transform" );

        var idsStr = HandlerBase.GetString( args, "ids" );
        if ( string.IsNullOrEmpty( idsStr ) )
            return HandlerBase.Error( "Missing required 'ids' parameter (comma-separated GUIDs).", "batch_transform" );

        var posStr = HandlerBase.GetString( args, "position" );
        var rotStr = HandlerBase.GetString( args, "rotation" );
        var scaleStr = HandlerBase.GetString( args, "scale" );

        if ( string.IsNullOrEmpty( posStr ) && string.IsNullOrEmpty( rotStr ) && string.IsNullOrEmpty( scaleStr ) )
            return HandlerBase.Error( "Provide at least one of: position, rotation, scale.", "batch_transform" );

        Vector3? posOffset = !string.IsNullOrEmpty( posStr ) ? HandlerBase.ParseVector3( posStr ) : null;
        Vector3? rotOffset = !string.IsNullOrEmpty( rotStr ) ? HandlerBase.ParseVector3( rotStr ) : null;
        Vector3? scaleOffset = !string.IsNullOrEmpty( scaleStr ) ? HandlerBase.ParseVector3( scaleStr ) : null;

        var ids = idsStr.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        int count = 0;
        var errors = new List<string>();

        foreach ( var id in ids )
        {
            var go = SceneHelpers.FindById( scene, id );
            if ( go == null )
            {
                errors.Add( $"Not found: {id}" );
                continue;
            }

            if ( posOffset.HasValue )
                go.WorldPosition += posOffset.Value;

            if ( rotOffset.HasValue )
            {
                var r = rotOffset.Value;
                go.WorldRotation *= Rotation.From( r.x, r.y, r.z );
            }

            if ( scaleOffset.HasValue )
                go.WorldScale *= scaleOffset.Value;

            count++;
        }

        var message = $"Transformed {count} object(s).";
        if ( errors.Count > 0 )
            message += $" Errors: {string.Join( "; ", errors )}";

        return HandlerBase.Confirm( message );
    }
}
