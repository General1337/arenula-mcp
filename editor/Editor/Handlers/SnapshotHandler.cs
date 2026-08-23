// Editor/Handlers/SnapshotHandler.cs
//
// [HANDOFF-TRUST: engine-fork-elite-leverage Phase 3.2 — atomic scene-checkpoint
// primitive (library-first). No live hypothesis from any inherited handoff
// applies: this is a greenfield extension of a well-understood library handler
// (packet.md §"Prior Failed Approaches" — none). Per-agent storage mirrors the
// Phase 3.3 ScratchHandler pattern.]
//
// engine-fork-elite-leverage Phase 3.2 — per-agent named scene snapshots.
//
// Three changes over the original flat process-global handler:
//   1. Per-agent two-level storage (agentId → name → snapshot). Concurrent
//      agents never collide on a name and never evict each other's checkpoints.
//   2. Cap raised 10 → 50, evaluated and evicted PER AGENT — a busy agent can
//      no longer starve the rest (the multi-agent bug this phase fixes).
//   3. Material + value-type long-tail capture/restore. The original Restore
//      round-tripped only string/bool/int/float; Color / Vector2-4 / Rotation /
//      Angles / Enum / Material were captured, surfaced in diff, then silently
//      dropped on restore. A MaterialOverride swap is now revertable.
//
// RenderAttributes is NOT snapshotted: it has no key-enumeration API (52 keyed
// Set/Get methods, 0 properties/fields) and its values are transient render
// state "not saved to disk" by design. The Material resource reference carries
// the baked shader parameters via its .vmat — that is the library-achievable
// 80%. A full RenderAttributes bag snapshot is engine-fork Scene.Checkpoint()
// territory (charter §3.2, DEFERRED). See plan.md §"Attributes — scope decision".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// snapshot tool: take, list, diff, restore, delete scene state snapshots.
/// Snapshots are stored per-agent — each calling agent has its own isolated
/// bucket (up to <see cref="MaxSnapshotsPerAgent"/> named snapshots), so
/// concurrent agents never overwrite or evict one another. Enables safe
/// experimentation by comparing before/after states and reverting property
/// changes on existing objects.
/// </summary>
internal static class SnapshotHandler
{
    // agentId → (snapshotName → snapshot). Two-level (not composite-key) so
    // per-agent eviction and per-agent `list` are O(bucket).
    private static readonly Dictionary<string, Dictionary<string, SceneSnapshot>> _snapshotsByAgent = new();

    private const int MaxSnapshotsPerAgent = 50;

    // Fallback bucket for un-identified / legacy clients (no session.identify).
    // They stay fully functional, degraded only in that they share one bucket.
    private const string AnonymousBucket = "_anonymous";

    // Monotonic insertion counter — eviction orders by this, not by TakenAt,
    // so 50+ snapshots taken inside one DateTime tick still evict deterministically.
    private static long _seqCounter;

    internal static object Handle( string action, JsonElement args, McpSession session )
    {
        try
        {
            return action switch
            {
                "take"    => Take( args, session ),
                "list"    => List( args, session ),
                "diff"    => Diff( args, session ),
                "restore" => Restore( args, session ),
                "delete"  => Delete( args, session ),
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
        public string AgentId;
        public long Seq;
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

    // ── Per-agent storage helpers ────────────────────────────────────

    /// <summary>
    /// Resolve the agent bucket id for a call: explicit <c>agent_id</c> arg wins
    /// (lets a supervisor target another agent's bucket), else the live session's
    /// identified <see cref="McpSession.AgentId"/>, else <see cref="AnonymousBucket"/>.
    /// </summary>
    private static string ResolveAgentId( JsonElement args, McpSession session )
    {
        var explicitId = HandlerBase.GetString( args, "agent_id" );
        if ( !string.IsNullOrWhiteSpace( explicitId ) ) return explicitId;
        if ( session != null && !string.IsNullOrWhiteSpace( session.AgentId ) ) return session.AgentId;
        return AnonymousBucket;
    }

    private static Dictionary<string, SceneSnapshot> GetOrCreateBucket( string agentId )
    {
        if ( !_snapshotsByAgent.TryGetValue( agentId, out var bucket ) )
        {
            bucket = new Dictionary<string, SceneSnapshot>();
            _snapshotsByAgent[agentId] = bucket;
        }
        return bucket;
    }

    // ── take ─────────────────────────────────────────────────────────

    private static object Take( JsonElement args, McpSession session )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "take" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "take" );

        var agentId = ResolveAgentId( args, session );
        var bucket = GetOrCreateBucket( agentId );

        // Per-agent eviction: only this agent's oldest is removed when its own
        // bucket is full and the name is new. Never touches another agent's bucket.
        if ( bucket.Count >= MaxSnapshotsPerAgent && !bucket.ContainsKey( name ) )
        {
            var oldest = bucket.OrderBy( kvp => kvp.Value.Seq ).First();
            bucket.Remove( oldest.Key );
        }

        var scopeId = HandlerBase.GetString( args, "scope_id" );
        var includeDisabled = HandlerBase.GetBool( args, "include_disabled", true );

        var snapshot = new SceneSnapshot
        {
            Name = name,
            AgentId = agentId,
            Seq = ++_seqCounter,
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

        bucket[name] = snapshot;

        return HandlerBase.Success( new
        {
            name,
            agentId,
            objectCount = snapshot.Objects.Count,
            takenAt = snapshot.TakenAt.ToString( "yyyy-MM-dd HH:mm:ss" ),
            totalSnapshots = bucket.Count,
            maxSnapshotsPerAgent = MaxSnapshotsPerAgent,
            message = $"Snapshot '{name}' taken with {snapshot.Objects.Count} objects for agent '{agentId}'."
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
                    compState.Properties[prop.Name] = SerializePropertyValue( val );
                }
                catch { }
            }

            state.Components.Add( compState );
        }

        return state;
    }

    /// <summary>
    /// Serialize one component-property value to its restorable string form.
    /// A <see cref="Material"/> is stored by its disk path (<see cref="Material.Name"/>)
    /// so <see cref="Material.Load"/> can re-resolve it; a runtime/anonymous
    /// material (no disk path) stores the non-restorable sentinel
    /// <c>&lt;runtime-material&gt;</c>. Everything else round-trips via
    /// <c>ToString()</c> — Color / Vector2-4 / Rotation / Angles / enum all have
    /// a matching <c>Parse</c> (see <see cref="TryDeserialize"/>).
    /// </summary>
    private static string SerializePropertyValue( object val )
    {
        if ( val == null ) return "null";
        if ( val is Material m )
            return m.IsValid && !string.IsNullOrEmpty( m.Name ) ? m.Name : "<runtime-material>";
        return val.ToString() ?? "null";
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

    private static object List( JsonElement args, McpSession session )
    {
        var allAgents = HandlerBase.GetBool( args, "all_agents", false );
        var agentId = ResolveAgentId( args, session );

        IEnumerable<SceneSnapshot> source;
        if ( allAgents )
            source = _snapshotsByAgent.Values.SelectMany( b => b.Values );
        else
            source = _snapshotsByAgent.TryGetValue( agentId, out var bucket )
                ? bucket.Values
                : Enumerable.Empty<SceneSnapshot>();

        var snapshots = source
            .OrderByDescending( s => s.Seq )
            .Select( s => new
            {
                name = s.Name,
                agentId = s.AgentId,
                objectCount = s.Objects.Count,
                takenAt = s.TakenAt.ToString( "yyyy-MM-dd HH:mm:ss" )
            } )
            .ToList();

        return HandlerBase.Success( new
        {
            count = snapshots.Count,
            agentId = allAgents ? "all" : agentId,
            allAgents,
            maxSnapshotsPerAgent = MaxSnapshotsPerAgent,
            snapshots
        } );
    }

    // ── diff ─────────────────────────────────────────────────────────

    private static object Diff( JsonElement args, McpSession session )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "diff" );

        var nameA = HandlerBase.GetString( args, "name_a" ) ?? HandlerBase.GetString( args, "name" );
        var nameB = HandlerBase.GetString( args, "name_b" );

        if ( string.IsNullOrEmpty( nameA ) )
            return HandlerBase.Error( "Missing required 'name' or 'name_a' parameter.", "diff" );

        var agentId = ResolveAgentId( args, session );
        var bucket = GetOrCreateBucket( agentId );

        if ( !bucket.TryGetValue( nameA, out var snapshotA ) )
            return HandlerBase.Error( $"Snapshot '{nameA}' not found for agent '{agentId}'.", "diff" );

        // If name_b provided, compare two snapshots. Otherwise, compare snapshot vs current scene.
        Dictionary<string, ObjectState> stateB;
        string labelB;

        if ( !string.IsNullOrEmpty( nameB ) )
        {
            if ( !bucket.TryGetValue( nameB, out var snapshotB ) )
                return HandlerBase.Error( $"Snapshot '{nameB}' not found for agent '{agentId}'.", "diff" );
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
            agentId,
            addedCount = added.Count,
            removedCount = removed.Count,
            modifiedCount = modified.Count,
            added,
            removed,
            modified
        } );
    }

    // ── restore ──────────────────────────────────────────────────────

    private static object Restore( JsonElement args, McpSession session )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "restore" );

        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "restore" );

        var agentId = ResolveAgentId( args, session );
        var bucket = GetOrCreateBucket( agentId );

        if ( !bucket.TryGetValue( name, out var snapshot ) )
            return HandlerBase.Error( $"Snapshot '{name}' not found for agent '{agentId}'.", "restore" );

        int restored = 0;
        int skipped = 0;
        int propsRestored = 0;
        int propsSkipped = 0;
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
                    if ( prop == null || !prop.CanWrite || !prop.CanRead ) continue;

                    try
                    {
                        // Compare current value against the snapshot in the SAME
                        // serialized form (SerializePropertyValue) so Material —
                        // captured by .Name, not .ToString() — compares correctly.
                        var currentSerialized = SerializePropertyValue( prop.GetValue( comp ) );
                        if ( currentSerialized == propKvp.Value ) continue; // unchanged

                        if ( TryDeserialize( propKvp.Value, prop.PropertyType, out var restoredValue ) )
                        {
                            prop.SetValue( comp, restoredValue );
                            propsRestored++;
                        }
                        else
                        {
                            propsSkipped++;
                        }
                    }
                    catch
                    {
                        propsSkipped++;
                    }
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
            agentId,
            restored,
            skipped,
            componentPropsRestored = propsRestored,
            componentPropsSkipped = propsSkipped,
            warningCount = warnings.Count,
            warnings,
            message = $"Restored {restored} objects from snapshot '{name}'. {skipped} objects could not be found."
        } );
    }

    /// <summary>
    /// Deserialize one captured property string back to <paramref name="t"/>.
    /// Covers the value-type long tail the original handler dropped on restore:
    /// string / bool / int / long / float / double / Color / Vector2-4 /
    /// Rotation / Angles / enum / Material. Non-restorable sentinels
    /// (<c>null</c>, <c>&lt;runtime-material&gt;</c>) and any unrecognised type
    /// return <c>false</c> so the caller skips cleanly. Each parse stays guarded.
    /// </summary>
    private static bool TryDeserialize( string s, Type t, out object result )
    {
        result = null;
        if ( s == null ) return false;
        // Sentinels: captured but intentionally not restorable.
        if ( s == "null" || s == "<runtime-material>" || s == "<null>" ) return false;

        try
        {
            if ( t == typeof( string ) ) { result = s; return true; }
            if ( t == typeof( bool ) ) { result = bool.Parse( s ); return true; }
            if ( t == typeof( int ) ) { result = int.Parse( s, CultureInfo.InvariantCulture ); return true; }
            if ( t == typeof( long ) ) { result = long.Parse( s, CultureInfo.InvariantCulture ); return true; }
            if ( t == typeof( float ) ) { result = float.Parse( s, CultureInfo.InvariantCulture ); return true; }
            if ( t == typeof( double ) ) { result = double.Parse( s, CultureInfo.InvariantCulture ); return true; }
            if ( t == typeof( Color ) )
            {
                var c = Color.Parse( s );
                if ( c.HasValue ) { result = c.Value; return true; }
                return false;
            }
            if ( t == typeof( Vector2 ) ) { result = Vector2.Parse( s ); return true; }
            if ( t == typeof( Vector3 ) ) { result = Vector3.Parse( s ); return true; }
            if ( t == typeof( Vector4 ) ) { result = Vector4.Parse( s ); return true; }
            if ( t == typeof( Rotation ) ) { result = Rotation.Parse( s ); return true; }
            if ( t == typeof( Angles ) ) { result = Angles.Parse( s ); return true; }
            if ( t == typeof( Material ) )
            {
                var m = Material.Load( s );
                if ( m != null ) { result = m; return true; }
                return false;
            }
            if ( t.IsEnum ) { result = Enum.Parse( t, s ); return true; }
        }
        catch
        {
            return false;
        }
        return false;
    }

    // ── delete ───────────────────────────────────────────────────────

    private static object Delete( JsonElement args, McpSession session )
    {
        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "delete" );

        var agentId = ResolveAgentId( args, session );
        var bucket = GetOrCreateBucket( agentId );

        if ( !bucket.Remove( name ) )
            return HandlerBase.Error( $"Snapshot '{name}' not found for agent '{agentId}'.", "delete" );

        return HandlerBase.Confirm(
            $"Snapshot '{name}' deleted for agent '{agentId}'. {bucket.Count} snapshots remaining." );
    }
}
