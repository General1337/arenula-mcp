using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// snapshot tool: take, list, diff, restore, delete scene state snapshots.
/// Enables safe experimentation by comparing before/after states
/// and reverting property changes on existing objects.
/// </summary>
internal static class SnapshotHandler
{
    private static readonly Dictionary<string, SceneSnapshot> _snapshots = new();
    private const int MaxSnapshots = 10;

    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "take"    => Take( args ),
                "list"    => List(),
                "diff"    => Diff( args ),
                "restore" => Restore( args ),
                "delete"  => Delete( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'snapshot'.", action,
                    "Valid actions: take, list, diff, restore, delete" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── Data structures ──────────────────────────────────────────────

    private sealed class SceneSnapshot
    {
        public string Name;
        public DateTime TakenAt;
        public Dictionary<string, ObjectState> Objects; // keyed by GUID
    }

    private sealed class ObjectState
    {
        public string Id;
        public string Name;
        public string ParentId;
        public bool Enabled;
        public List<string> Tags;
        public string WorldPosition;
        public string WorldRotation;
        public string WorldScale;
        public List<ComponentState> Components;
    }

    private sealed class ComponentState
    {
        public string Id;
        public string TypeName;
        public bool Enabled;
        public Dictionary<string, string> Properties; // serialized as strings
    }

    // ── take ─────────────────────────────────────────────────────────

    private static object Take( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "take" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "take" );

        if ( _snapshots.Count >= MaxSnapshots && !_snapshots.ContainsKey( name ) )
        {
            // Remove oldest snapshot
            var oldest = _snapshots.OrderBy( kvp => kvp.Value.TakenAt ).First();
            _snapshots.Remove( oldest.Key );
        }

        var scopeId = HandlerBase.GetString( args, "scope_id" );
        var includeDisabled = HandlerBase.GetBool( args, "include_disabled", true );

        var snapshot = new SceneSnapshot
        {
            Name = name,
            TakenAt = DateTime.UtcNow,
            Objects = new Dictionary<string, ObjectState>()
        };

        IEnumerable<GameObject> objects;
        if ( !string.IsNullOrEmpty( scopeId ) )
        {
            var root = SceneHelpers.FindByIdOrThrow( scene, scopeId, "take" );
            objects = WalkSubtree( root, includeDisabled );
        }
        else
        {
            objects = SceneHelpers.WalkAll( scene, includeDisabled );
        }

        foreach ( var go in objects )
        {
            var state = CaptureObjectState( go );
            snapshot.Objects[state.Id] = state;
        }

        _snapshots[name] = snapshot;

        return HandlerBase.Success( new
        {
            name,
            objectCount = snapshot.Objects.Count,
            takenAt = snapshot.TakenAt.ToString( "yyyy-MM-dd HH:mm:ss" ),
            totalSnapshots = _snapshots.Count,
            message = $"Snapshot '{name}' taken with {snapshot.Objects.Count} objects."
        } );
    }

    private static ObjectState CaptureObjectState( GameObject go )
    {
        var state = new ObjectState
        {
            Id = go.Id.ToString(),
            Name = go.Name,
            ParentId = go.Parent?.Id.ToString(),
            Enabled = go.Enabled,
            Tags = go.Tags.TryGetAll().ToList(),
            WorldPosition = $"{go.WorldPosition.x},{go.WorldPosition.y},{go.WorldPosition.z}",
            WorldRotation = $"{go.WorldRotation.Angles().pitch},{go.WorldRotation.Angles().yaw},{go.WorldRotation.Angles().roll}",
            WorldScale = $"{go.WorldScale.x},{go.WorldScale.y},{go.WorldScale.z}",
            Components = new List<ComponentState>()
        };

        foreach ( var comp in go.Components.GetAll() )
        {
            var compState = new ComponentState
            {
                Id = comp.Id.ToString(),
                TypeName = comp.GetType().Name,
                Enabled = comp.Enabled,
                Properties = new Dictionary<string, string>()
            };

            // Capture writable properties
            foreach ( var prop in comp.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance ) )
            {
                if ( !prop.CanRead || !prop.CanWrite ) continue;

                try
                {
                    var val = prop.GetValue( comp );
                    compState.Properties[prop.Name] = val?.ToString() ?? "null";
                }
                catch { }
            }

            state.Components.Add( compState );
        }

        return state;
    }

    private static IEnumerable<GameObject> WalkSubtree( GameObject root, bool includeDisabled )
    {
        if ( !includeDisabled && !root.Enabled ) yield break;
        yield return root;
        foreach ( var child in root.Children )
        {
            foreach ( var go in WalkSubtree( child, includeDisabled ) )
                yield return go;
        }
    }

    // ── list ─────────────────────────────────────────────────────────

    private static object List()
    {
        var snapshots = _snapshots.Values
            .OrderByDescending( s => s.TakenAt )
            .Select( s => new
            {
                name = s.Name,
                objectCount = s.Objects.Count,
                takenAt = s.TakenAt.ToString( "yyyy-MM-dd HH:mm:ss" )
            } )
            .ToList();

        return HandlerBase.Success( new
        {
            count = snapshots.Count,
            maxSnapshots = MaxSnapshots,
            snapshots
        } );
    }

    // ── diff ─────────────────────────────────────────────────────────

    private static object Diff( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "diff" );

        var nameA = HandlerBase.GetString( args, "name_a" ) ?? HandlerBase.GetString( args, "name" );
        var nameB = HandlerBase.GetString( args, "name_b" );

        if ( string.IsNullOrEmpty( nameA ) )
            return HandlerBase.Error( "Missing required 'name' or 'name_a' parameter.", "diff" );

        if ( !_snapshots.TryGetValue( nameA, out var snapshotA ) )
            return HandlerBase.Error( $"Snapshot '{nameA}' not found.", "diff" );

        // If name_b provided, compare two snapshots. Otherwise, compare snapshot vs current scene.
        Dictionary<string, ObjectState> stateB;
        string labelB;

        if ( !string.IsNullOrEmpty( nameB ) )
        {
            if ( !_snapshots.TryGetValue( nameB, out var snapshotB ) )
                return HandlerBase.Error( $"Snapshot '{nameB}' not found.", "diff" );
            stateB = snapshotB.Objects;
            labelB = nameB;
        }
        else
        {
            // Capture current scene state
            stateB = new Dictionary<string, ObjectState>();
            foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
            {
                var state = CaptureObjectState( go );
                stateB[state.Id] = state;
            }
            labelB = "current_scene";
        }

        var added = new List<object>();
        var removed = new List<object>();
        var modified = new List<object>();

        // Objects in B but not in A = added
        foreach ( var kvp in stateB )
        {
            if ( !snapshotA.Objects.ContainsKey( kvp.Key ) )
            {
                added.Add( new { id = kvp.Value.Id, name = kvp.Value.Name } );
            }
        }

        // Objects in A but not in B = removed
        foreach ( var kvp in snapshotA.Objects )
        {
            if ( !stateB.ContainsKey( kvp.Key ) )
            {
                removed.Add( new { id = kvp.Value.Id, name = kvp.Value.Name } );
            }
        }

        // Objects in both = check for modifications
        foreach ( var kvp in snapshotA.Objects )
        {
            if ( !stateB.TryGetValue( kvp.Key, out var stateObjB ) ) continue;

            var stateObjA = kvp.Value;
            var changes = new List<object>();

            if ( stateObjA.Name != stateObjB.Name )
                changes.Add( new { field = "Name", from_val = stateObjA.Name, to_val = stateObjB.Name } );
            if ( stateObjA.Enabled != stateObjB.Enabled )
                changes.Add( new { field = "Enabled", from_val = stateObjA.Enabled.ToString(), to_val = stateObjB.Enabled.ToString() } );
            if ( stateObjA.WorldPosition != stateObjB.WorldPosition )
                changes.Add( new { field = "WorldPosition", from_val = stateObjA.WorldPosition, to_val = stateObjB.WorldPosition } );
            if ( stateObjA.WorldRotation != stateObjB.WorldRotation )
                changes.Add( new { field = "WorldRotation", from_val = stateObjA.WorldRotation, to_val = stateObjB.WorldRotation } );
            if ( stateObjA.WorldScale != stateObjB.WorldScale )
                changes.Add( new { field = "WorldScale", from_val = stateObjA.WorldScale, to_val = stateObjB.WorldScale } );

            // Compare component properties
            var compMapA = stateObjA.Components.ToDictionary( c => c.Id );
            var compMapB = stateObjB.Components.ToDictionary( c => c.Id );

            // Added components
            foreach ( var cb in stateObjB.Components )
            {
                if ( !compMapA.ContainsKey( cb.Id ) )
                    changes.Add( new { field = $"component_added", from_val = (string)null, to_val = cb.TypeName } );
            }

            // Removed components
            foreach ( var ca in stateObjA.Components )
            {
                if ( !compMapB.ContainsKey( ca.Id ) )
                    changes.Add( new { field = $"component_removed", from_val = ca.TypeName, to_val = (string)null } );
            }

            // Modified component properties
            foreach ( var ca in stateObjA.Components )
            {
                if ( !compMapB.TryGetValue( ca.Id, out var cb ) ) continue;

                if ( ca.Enabled != cb.Enabled )
                    changes.Add( new { field = $"{ca.TypeName}.Enabled", from_val = ca.Enabled.ToString(), to_val = cb.Enabled.ToString() } );

                foreach ( var propA in ca.Properties )
                {
                    if ( cb.Properties.TryGetValue( propA.Key, out var valB ) )
                    {
                        if ( propA.Value != valB )
                            changes.Add( new { field = $"{ca.TypeName}.{propA.Key}", from_val = propA.Value, to_val = valB } );
                    }
                }
            }

            if ( changes.Count > 0 )
            {
                modified.Add( new
                {
                    id = stateObjA.Id,
                    name = stateObjB.Name ?? stateObjA.Name,
                    changeCount = changes.Count,
                    changes
                } );
            }
        }

        return HandlerBase.Success( new
        {
            compare = $"{nameA} vs {labelB}",
            addedCount = added.Count,
            removedCount = removed.Count,
            modifiedCount = modified.Count,
            added,
            removed,
            modified
        } );
    }

    // ── restore ──────────────────────────────────────────────────────

    private static object Restore( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "restore" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "restore" );

        if ( !_snapshots.TryGetValue( name, out var snapshot ) )
            return HandlerBase.Error( $"Snapshot '{name}' not found.", "restore" );

        int restored = 0;
        int skipped = 0;
        var warnings = new List<string>();

        foreach ( var kvp in snapshot.Objects )
        {
            var objState = kvp.Value;
            var go = SceneHelpers.FindById( scene, objState.Id );

            if ( go == null )
            {
                skipped++;
                warnings.Add( $"Object '{objState.Name}' ({objState.Id}) no longer exists — cannot restore." );
                continue;
            }

            // Restore basic properties
            go.Name = objState.Name;
            go.Enabled = objState.Enabled;

            // Restore transform (stored as "x,y,z" format)
            try
            {
                if ( !string.IsNullOrEmpty( objState.WorldPosition ) )
                    go.WorldPosition = HandlerBase.ParseVector3( objState.WorldPosition );
            }
            catch { }

            try
            {
                if ( !string.IsNullOrEmpty( objState.WorldRotation ) )
                {
                    var r = HandlerBase.ParseVector3( objState.WorldRotation );
                    go.WorldRotation = Rotation.From( r.x, r.y, r.z );
                }
            }
            catch { }

            try
            {
                if ( !string.IsNullOrEmpty( objState.WorldScale ) )
                    go.WorldScale = HandlerBase.ParseVector3( objState.WorldScale );
            }
            catch { }

            // Restore component properties
            foreach ( var compState in objState.Components )
            {
                if ( !Guid.TryParse( compState.Id, out var compGuid ) ) continue;
                var comp = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
                if ( comp == null ) continue;

                comp.Enabled = compState.Enabled;

                foreach ( var propKvp in compState.Properties )
                {
                    var prop = comp.GetType().GetProperty( propKvp.Key,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );
                    if ( prop == null || !prop.CanWrite ) continue;

                    try
                    {
                        // Read current value to compare
                        var currentVal = prop.GetValue( comp )?.ToString() ?? "null";
                        if ( currentVal == propKvp.Value ) continue; // unchanged, skip

                        // Attempt to restore — this is best-effort since we stored as strings
                        var targetType = prop.PropertyType;
                        if ( targetType == typeof( string ) )
                            prop.SetValue( comp, propKvp.Value == "null" ? null : propKvp.Value );
                        else if ( targetType == typeof( bool ) )
                            prop.SetValue( comp, bool.Parse( propKvp.Value ) );
                        else if ( targetType == typeof( int ) )
                            prop.SetValue( comp, int.Parse( propKvp.Value ) );
                        else if ( targetType == typeof( float ) )
                            prop.SetValue( comp, float.Parse( propKvp.Value, System.Globalization.CultureInfo.InvariantCulture ) );
                    }
                    catch { }
                }
            }

            restored++;
        }

        // Check for objects in scene that weren't in snapshot (added since snapshot)
        var currentIds = new HashSet<string>();
        foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
            currentIds.Add( go.Id.ToString() );

        int addedSinceSnapshot = currentIds.Count( id => !snapshot.Objects.ContainsKey( id ) );
        if ( addedSinceSnapshot > 0 )
            warnings.Add( $"{addedSinceSnapshot} objects were added since snapshot and were NOT removed (restore does not delete)." );

        return HandlerBase.Success( new
        {
            snapshot = name,
            restored,
            skipped,
            warningCount = warnings.Count,
            warnings,
            message = $"Restored {restored} objects from snapshot '{name}'. {skipped} objects could not be found."
        } );
    }

    // ── delete ───────────────────────────────────────────────────────

    private static object Delete( JsonElement args )
    {
        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "delete" );

        if ( !_snapshots.Remove( name ) )
            return HandlerBase.Error( $"Snapshot '{name}' not found.", "delete" );

        return HandlerBase.Confirm( $"Snapshot '{name}' deleted. {_snapshots.Count} snapshots remaining." );
    }
}
