using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Editor;
using Sandbox;

namespace Arenula;

/// <summary>
/// component tool: add, remove, set_property, set_enabled, get_properties, get_types, copy.
/// Ported from Ozmium OzmiumWriteHandlers, OzmiumAssetHandlers, UtilityToolHandlers.
/// </summary>
internal static class ComponentHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "add"            => Add( args ),
                "remove"         => Remove( args ),
                "set_property"   => SetProperty( args ),
                "set_enabled"    => SetEnabled( args ),
                "get_properties" => GetProperties( args ),
                "get_types"      => GetTypes( args ),
                "copy"           => Copy( args ),
                "bulk_set"       => BulkSet( args, "bulk_set" ),
                "apply_properties" => BulkSet( args, "apply_properties" ),
                _ => HandlerBase.Error( $"Unknown action '{action}' for tool 'component'.", action,
                    "Valid actions: add, remove, set_property, set_enabled, get_properties, get_types, copy, bulk_set, apply_properties" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────

    /// <summary>
    /// Find a component on a GO by its GUID string.
    /// Returns null if not found.
    /// </summary>
    private static Component FindComponentById( GameObject go, string componentId )
    {
        if ( string.IsNullOrEmpty( componentId ) ) return null;
        if ( !Guid.TryParse( componentId, out var guid ) ) return null;
        return go.Components.GetAll().FirstOrDefault( c => c.Id == guid );
    }

    /// <summary>
    /// Find a component on a GO by GUID, or throw with a helpful message.
    /// </summary>
    private static Component FindComponentByIdOrThrow( GameObject go, string componentId, string action )
    {
        var comp = FindComponentById( go, componentId );
        if ( comp == null )
        {
            var available = go.Components.GetAll()
                .Select( c => $"{c.GetType().Name} ({c.Id})" )
                .ToList();
            var hint = available.Count > 0
                ? $"Available components: {string.Join( ", ", available )}"
                : "This object has no components.";
            throw new ArgumentException( $"Component '{componentId}' not found on '{go.Name}'. {hint}" );
        }
        return comp;
    }

    /// <summary>
    /// Fast component type lookup using TypeLibrary (indexed).
    /// Prefers game-assembly types over Sandbox built-ins when names collide.
    /// Ported from OzmiumWriteHandlers.FindComponentTypeDescription.
    /// </summary>
    internal static TypeDescription FindComponentTypeDescription( string typeName )
    {
        TypeDescription fallback = null;
        foreach ( var candidate in TypeLibrary.GetTypes<Component>() )
        {
            if ( !candidate.Name.Equals( typeName, StringComparison.OrdinalIgnoreCase ) )
                continue;

            // Prefer game types (not in Sandbox namespace) over engine built-ins
            var ns = candidate.TargetType.Namespace ?? "";
            if ( !ns.StartsWith( "Sandbox", StringComparison.OrdinalIgnoreCase ) )
                return candidate;

            fallback ??= candidate;
        }

        if ( fallback != null ) return fallback;

        // Last resort: try exact match by full type name
        var td = TypeLibrary.GetType( typeName );
        if ( td != null && td.TargetType.IsClass && !td.TargetType.IsAbstract
            && typeof( Component ).IsAssignableFrom( td.TargetType ) )
            return td;

        return null;
    }

    // ── add ──────────────────────────────────────────────────────────

    private static object Add( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "add" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "add" );

        var type = HandlerBase.GetString( args, "type" );
        if ( string.IsNullOrEmpty( type ) )
            return HandlerBase.Error( "Missing required 'type' parameter.", "add" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "add" );

        var td = FindComponentTypeDescription( type );
        if ( td == null )
        {
            // Suggest similar type names
            var suggestions = TypeLibrary.GetTypes<Component>()
                .Where( t => !t.IsAbstract && t.Name.IndexOf( type, StringComparison.OrdinalIgnoreCase ) >= 0 )
                .Select( t => t.Name )
                .Take( 10 )
                .ToList();

            string hint = suggestions.Count > 0
                ? $"Similar types: {string.Join( ", ", suggestions )}"
                : "No similar types found. Use component.get_types to search.";

            return HandlerBase.Error( $"Component type '{type}' not found. {hint}", "add" );
        }

        var comp = go.Components.Create( td );

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            handle = SceneHelpers.BuildHandle( go ),
            component_id = comp.Id.ToString(),
            component_type = comp.GetType().Name,
            component_handle = SceneHelpers.BuildHandle( comp ),
            message = $"Added '{td.Name}' to '{go.Name}'."
        } );
    }

    // ── remove ───────────────────────────────────────────────────────

    private static object Remove( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "remove" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "remove" );

        var componentId = HandlerBase.GetString( args, "component_id" );
        if ( string.IsNullOrEmpty( componentId ) )
            return HandlerBase.Error( "Missing required 'component_id' parameter.", "remove" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "remove" );
        var comp = FindComponentByIdOrThrow( go, componentId, "remove" );

        var typeName = comp.GetType().Name;
        comp.Destroy();

        return HandlerBase.Confirm( $"Removed '{typeName}' from '{go.Name}'." );
    }

    // ── set_property ─────────────────────────────────────────────────

    private static object SetProperty( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "set_property" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "set_property" );

        var componentId = HandlerBase.GetString( args, "component_id" );
        if ( string.IsNullOrEmpty( componentId ) )
            return HandlerBase.Error( "Missing required 'component_id' parameter.", "set_property" );

        var propName = HandlerBase.GetString( args, "property" );
        if ( string.IsNullOrEmpty( propName ) )
            return HandlerBase.Error( "Missing required 'property' parameter.", "set_property" );

        if ( !args.TryGetProperty( "value", out var valEl ) )
            return HandlerBase.Error( "Missing required 'value' parameter.", "set_property" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "set_property" );
        var comp = FindComponentByIdOrThrow( go, componentId, "set_property" );

        var prop = comp.GetType().GetProperty( propName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );
        if ( prop == null )
            return HandlerBase.Error( $"Property '{propName}' not found on '{comp.GetType().Name}'.", "set_property" );
        if ( !prop.CanWrite )
            return HandlerBase.Error( $"Property '{propName}' is read-only.", "set_property" );

        object converted = ConvertJsonValue( valEl, prop.PropertyType );

        // Post-condition guard (upstream 8fe43bd, extended). A reference-valued
        // conversion that did not actually resolve must not be written and then
        // reported as success — that is the silent-no-op class, where the caller
        // believes a write landed that never did.
        //
        // Two distinct failure shapes, both covered by UnresolvedReason:
        //   - null: GameResource lookups and Component / GameObject GUID lookups.
        //   - a placeholder: Model.Load hands back the error model for a bad path
        //     rather than null, so a null check alone misses every bad .vmdl.
        var unresolved = UnresolvedReason( converted, prop.PropertyType );
        if ( unresolved != null && !IsExplicitNullRequest( valEl ) )
            return HandlerBase.Error(
                $"Could not resolve a {SchemaHelpers.FormatTypeName( prop.PropertyType )} for " +
                $"'{comp.GetType().Name}.{propName}' ({unresolved}) — the property was NOT written.",
                "set_property",
                "Check the asset path or GUID exists. Asset paths are relative to Assets/ " +
                "(no 'Assets/' prefix); GUIDs change on scene reload, so refresh with scene.summary. " +
                "Pass the string \"null\" if clearing the property really is what you want.",
                new { property = propName, requested = valEl.ToString() },
                McpErrorCode.NotFound );

        prop.SetValue( comp, converted );
        var readback = prop.GetValue( comp );

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            handle = SceneHelpers.BuildHandle( go ),
            component_type = comp.GetType().Name,
            component_handle = SceneHelpers.BuildHandle( comp ),
            property = propName,
            value = SchemaHelpers.FormatValue( readback ),
            value_schema = SchemaHelpers.DescribeType( prop.PropertyType ),
            message = $"Set '{comp.GetType().Name}.{propName}' = {readback}"
        } );
    }

    /// <summary>
    /// Describe why a converted value should not be written, or null when it is fine.
    /// Value types and successfully-resolved references return null.
    /// </summary>
    private static string UnresolvedReason( object converted, Type targetType )
    {
        // A null from a reference-typed conversion means the lookup failed. Value
        // types never arrive null here, so this cannot fire on an int or a Vector3.
        if ( converted == null )
            return targetType.IsValueType ? null : "the lookup returned nothing";

        // Model.Load does not return null for a missing .vmdl — it returns the
        // error model, which renders as a checkerboard and reads as a live asset.
        if ( converted is Model model && model.IsError )
            return "the path resolved to the engine's error model";

        if ( converted is Material material && !material.IsValid() )
            return "the material did not load";

        if ( converted is Texture texture && !texture.IsValid() )
            return "the texture did not load";

        return null;
    }

    /// <summary>
    /// True when the caller deliberately asked to clear a property — JSON null, or
    /// the strings "null" / "" that the converters treat as a clear.
    /// </summary>
    private static bool IsExplicitNullRequest( JsonElement el )
    {
        if ( el.ValueKind == JsonValueKind.Null ) return true;
        if ( el.ValueKind != JsonValueKind.String ) return false;
        var s = el.GetString();
        return string.IsNullOrEmpty( s ) || s == "null";
    }

    /// <summary>
    /// Convert a JSON value to the target CLR type.
    /// Supports: string, bool, int, float, double, Vector3, Enum, Model, Component, GameObject.
    /// Ported from OzmiumWriteHandlers.ConvertJsonValue.
    /// </summary>
    internal static object ConvertJsonValue( JsonElement el, Type targetType )
    {
        if ( targetType == typeof( string ) )
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();

        if ( targetType == typeof( bool ) )
        {
            if ( el.ValueKind == JsonValueKind.True ) return true;
            if ( el.ValueKind == JsonValueKind.False ) return false;
            if ( el.ValueKind == JsonValueKind.String ) return bool.Parse( el.GetString() );
            return el.GetBoolean();
        }

        if ( targetType == typeof( int ) )
        {
            if ( el.ValueKind == JsonValueKind.String ) return int.Parse( el.GetString() );
            return el.GetInt32();
        }

        if ( targetType == typeof( float ) )
        {
            if ( el.ValueKind == JsonValueKind.String )
                return float.Parse( el.GetString(), System.Globalization.CultureInfo.InvariantCulture );
            return el.GetSingle();
        }

        if ( targetType == typeof( double ) )
        {
            if ( el.ValueKind == JsonValueKind.String )
                return double.Parse( el.GetString(), System.Globalization.CultureInfo.InvariantCulture );
            return el.GetDouble();
        }

        if ( targetType == typeof( Vector3 ) )
        {
            // Accept "x,y,z" string or {x,y,z} object
            if ( el.ValueKind == JsonValueKind.String )
                return HandlerBase.ParseVector3( el.GetString() );
            if ( el.ValueKind == JsonValueKind.Object )
            {
                float vx = 0, vy = 0, vz = 0;
                if ( el.TryGetProperty( "x", out var xp ) ) vx = xp.GetSingle();
                if ( el.TryGetProperty( "y", out var yp ) ) vy = yp.GetSingle();
                if ( el.TryGetProperty( "z", out var zp ) ) vz = zp.GetSingle();
                return new Vector3( vx, vy, vz );
            }
        }

        if ( targetType == typeof( Vector2 ) )
        {
            // Accept "x,y" string or {x,y} / {u,v} object
            if ( el.ValueKind == JsonValueKind.String )
                return HandlerBase.ParseVector2( el.GetString() );
            if ( el.ValueKind == JsonValueKind.Object )
            {
                float u = 0, v = 0;
                if ( el.TryGetProperty( "x", out var xp ) ) u = xp.GetSingle();
                else if ( el.TryGetProperty( "u", out var up ) ) u = up.GetSingle();
                if ( el.TryGetProperty( "y", out var yp ) ) v = yp.GetSingle();
                else if ( el.TryGetProperty( "v", out var vp ) ) v = vp.GetSingle();
                return new Vector2( u, v );
            }
        }

        if ( targetType == typeof( Rotation ) )
        {
            if ( el.ValueKind == JsonValueKind.String )
            {
                var euler = HandlerBase.ParseVector3( el.GetString() );
                return Rotation.From( euler.x, euler.y, euler.z );
            }
            if ( el.ValueKind == JsonValueKind.Object )
            {
                float pitch = el.TryGetProperty( "pitch", out var p ) ? p.GetSingle() : 0f;
                float yaw = el.TryGetProperty( "yaw", out var y ) ? y.GetSingle() : 0f;
                float roll = el.TryGetProperty( "roll", out var r ) ? r.GetSingle() : 0f;
                return Rotation.From( pitch, yaw, roll );
            }
        }

        if ( targetType == typeof( Color ) || targetType == typeof( Color32 ) )
        {
            if ( el.ValueKind == JsonValueKind.String )
            {
                var s = el.GetString();
                var parts = s.Split( ',', StringSplitOptions.TrimEntries );
                if ( parts.Length >= 3 )
                {
                    float r = float.Parse( parts[0] ) / 255f;
                    float g = float.Parse( parts[1] ) / 255f;
                    float b = float.Parse( parts[2] ) / 255f;
                    float a = parts.Length > 3 ? float.Parse( parts[3] ) / 255f : 1f;
                    return targetType == typeof( Color32 ) ? (object)new Color32( (byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255) ) : new Color( r, g, b, a );
                }
                // Fallback to Color.Parse for hex ("#RRGGBB") and named colors.
                // Color.Parse returns null on anything it cannot read. Coalescing that
                // to default() turns a typo into transparent black and reports success —
                // the same silent-no-op class the reference-type guard in SetProperty
                // covers, except a value type never trips that null check. Refuse here.
                var parsed = Color.Parse( s );
                if ( !parsed.HasValue )
                    throw new ArgumentException(
                        $"Cannot parse '{s}' as a colour. Use 'r,g,b' or 'r,g,b,a' in 0-255, " +
                        "a hex string like '#RRGGBB' or '#RRGGBBAA', or a named colour." );
                return targetType == typeof( Color32 ) ? (object)(Color32)parsed.Value : parsed.Value;
            }
            if ( el.ValueKind == JsonValueKind.Object )
            {
                float r = el.TryGetProperty( "r", out var rp ) ? rp.GetSingle() : 255f;
                float g = el.TryGetProperty( "g", out var gp ) ? gp.GetSingle() : 255f;
                float b = el.TryGetProperty( "b", out var bp ) ? bp.GetSingle() : 255f;
                float a = el.TryGetProperty( "a", out var ap ) ? ap.GetSingle() : 255f;
                return targetType == typeof( Color32 ) ? (object)new Color32( (byte)r, (byte)g, (byte)b, (byte)a ) : new Color( r / 255f, g / 255f, b / 255f, a / 255f );
            }
        }

        if ( targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof( List<> ) && el.ValueKind == JsonValueKind.Array )
        {
            var itemType = targetType.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance( targetType );
            foreach ( var item in el.EnumerateArray() )
                list.Add( ConvertJsonValue( item, itemType ) );
            return list;
        }

        if ( targetType == typeof( BBox ) )
            return ParseBBox( el );

        // ParticleFloat — wrapper that varies a float per-particle. Modes:
        //   "5"            → Constant mode, value 5
        //   "0.75,2.6"     → Range mode, picks per-particle in [0.75, 2.6]
        //   5  (JSON num)  → Constant mode, value 5
        //   {Type,ConstantA,...} (JSON obj) → explicit; Curve / CurveRange route
        //                                     through the engine's own JsonRead
        if ( targetType == typeof( ParticleFloat ) )
            return ParseParticleFloat( el );

        // ParticleVector3 — three ParticleFloats (X/Y/Z fields). Format:
        //   "x,y,z"        → Constant mode on each axis
        //   {X:..,Y:..,Z:..} (JSON obj) → each axis routed through ParticleFloat parse
        if ( targetType == typeof( ParticleVector3 ) )
            return ParseParticleVector3( el );

        // ParticleGradient — wrapper for Color-over-life.
        //   "r,g,b,a"                    → Constant mode, single color
        //   "r1,g1,b1,a1;r2,g2,b2,a2"    → Range mode
        //   {Type,ConstantA,...}         → full control incl. Evaluation
        if ( targetType == typeof( ParticleGradient ) )
            return ParseParticleGradient( el );

        if ( targetType.IsEnum )
        {
            var str = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            return Enum.Parse( targetType, str, ignoreCase: true );
        }

        // Handle Sandbox.Model
        if ( targetType == typeof( Model ) )
        {
            var path = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if ( string.IsNullOrEmpty( path ) || path == "null" ) return null;
            return Model.Load( path );
        }

        // Handle Sandbox.Material from an asset path string (same pattern as Model)
        if ( targetType == typeof( Material ) )
        {
            var path = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if ( string.IsNullOrEmpty( path ) || path == "null" ) return null;
            return Material.Load( path );
        }

        // Handle Sandbox.Texture from an asset path string
        if ( targetType == typeof( Texture ) )
        {
            var path = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if ( string.IsNullOrEmpty( path ) || path == "null" ) return null;
            return Texture.Load( path );
        }

        // Generic GameResource — covers Sprite, PrefabFile, ClutterCollection,
        // TerrainStorage, SoundEvent and every project-defined GameResource
        // subclass via ResourceLibrary.Get<T>(path). Reflection-routed so a new
        // engine resource type does not need its own branch here. Must stay
        // AFTER the Model / Material / Texture branches — those derive from
        // Sandbox.Resource, not GameResource, and have their own loaders.
        if ( typeof( GameResource ).IsAssignableFrom( targetType ) )
        {
            var path = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if ( string.IsNullOrEmpty( path ) || path == "null" ) return null;

            // ResourceLibrary.Get<T>(string) — generic method, dispatch via reflection.
            var getMethod = typeof( ResourceLibrary )
                .GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static )
                .FirstOrDefault( m =>
                    m.Name == "Get" &&
                    m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof( string ) );

            if ( getMethod == null )
                throw new InvalidOperationException(
                    "ResourceLibrary.Get<T>(string) not found via reflection — engine API may have changed." );

            return getMethod.MakeGenericMethod( targetType ).Invoke( null, new object[] { path } );
        }

        // Handle Component references by GUID
        if ( typeof( Component ).IsAssignableFrom( targetType ) )
        {
            var guidStr = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if ( el.ValueKind == JsonValueKind.Object )
            {
                if ( el.TryGetProperty( "id", out var idProp ) ) guidStr = idProp.GetString();
            }

            if ( !string.IsNullOrEmpty( guidStr ) && Guid.TryParse( guidStr, out var compGuid ) )
            {
                var scene = SceneHelpers.ResolveScene();
                if ( scene != null )
                {
                    foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
                    {
                        var match = go.Components.GetAll().FirstOrDefault( c => c.Id == compGuid );
                        if ( match != null && targetType.IsAssignableFrom( match.GetType() ) )
                            return match;
                    }
                }
            }
            return null;
        }

        // Handle GameObject references by GUID
        if ( typeof( GameObject ).IsAssignableFrom( targetType ) )
        {
            var guidStr = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if ( el.ValueKind == JsonValueKind.Object )
            {
                if ( el.TryGetProperty( "id", out var idProp ) ) guidStr = idProp.GetString();
            }

            if ( !string.IsNullOrEmpty( guidStr ) )
            {
                var scene = SceneHelpers.ResolveScene();
                if ( scene != null )
                    return SceneHelpers.FindById( scene, guidStr );
            }
            return null;
        }

        // Fallback: try ChangeType from string
        var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return Convert.ChangeType( raw, targetType );
    }

    // ── BBox parser ──────────────────────────────────────────────────

    private static BBox ParseBBox( JsonElement el )
    {
        // Accept three string forms:
        //   "x1,y1,z1;x2,y2,z2"        — semicolon separates mins from maxs
        //   "mins x,y,z, maxs x,y,z"   — the get_properties readback format
        //   "x1,y1,z1,x2,y2,z2"        — six comma-separated floats
        if ( el.ValueKind == JsonValueKind.String )
        {
            var s = el.GetString().Trim();

            if ( s.Contains( "mins", StringComparison.OrdinalIgnoreCase ) &&
                 s.Contains( "maxs", StringComparison.OrdinalIgnoreCase ) )
            {
                var minsIdx = s.IndexOf( "mins", StringComparison.OrdinalIgnoreCase );
                var maxsIdx = s.IndexOf( "maxs", StringComparison.OrdinalIgnoreCase );
                var minsPart = s.Substring( minsIdx + 4, maxsIdx - minsIdx - 4 ).Trim().TrimEnd( ',' ).Trim();
                var maxsPart = s.Substring( maxsIdx + 4 ).Trim();
                return new BBox( HandlerBase.ParseVector3( minsPart ), HandlerBase.ParseVector3( maxsPart ) );
            }

            if ( s.Contains( ';' ) )
            {
                var halves = s.Split( ';' );
                if ( halves.Length != 2 )
                    throw new ArgumentException( $"BBox semicolon form must be 'x1,y1,z1;x2,y2,z2', got '{s}'." );
                return new BBox( HandlerBase.ParseVector3( halves[0] ), HandlerBase.ParseVector3( halves[1] ) );
            }

            var parts = s.Split( ',' );
            if ( parts.Length != 6 )
                throw new ArgumentException(
                    $"BBox must be 'x1,y1,z1;x2,y2,z2' or six comma-separated floats, got '{s}'." );
            return new BBox(
                new Vector3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) ),
                new Vector3( ParseFloat( parts[3] ), ParseFloat( parts[4] ), ParseFloat( parts[5] ) ) );
        }

        // JSON object: {"Mins": {x,y,z}, "Maxs": {x,y,z}} or lowercase keys
        if ( el.ValueKind == JsonValueKind.Object )
        {
            Vector3 mins = default, maxs = default;
            JsonElement mEl;
            if ( el.TryGetProperty( "Mins", out mEl ) || el.TryGetProperty( "mins", out mEl ) )
                mins = (Vector3)ConvertJsonValue( mEl, typeof( Vector3 ) );
            if ( el.TryGetProperty( "Maxs", out mEl ) || el.TryGetProperty( "maxs", out mEl ) )
                maxs = (Vector3)ConvertJsonValue( mEl, typeof( Vector3 ) );
            return new BBox( mins, maxs );
        }

        throw new ArgumentException( $"Cannot convert {el.ValueKind} to BBox. Use a string or {{Mins,Maxs}} object." );
    }

    private static float ParseFloat( string s ) =>
        float.Parse( s.Trim(), System.Globalization.CultureInfo.InvariantCulture );

    // ── Particle-wrapper parsers ─────────────────────────────────────
    //
    // ParticleFloat / ParticleVector3 / ParticleGradient are struct wrappers
    // used on every particle field (Lifetime, Rate, Scale, Velocity, Alpha,
    // Tint-over-life, ...). They hold a constant, a range, a curve, or a
    // curve-range. Convert.ChangeType has no path from string to these types,
    // so before this existed set_property failed with "Invalid cast from
    // System.String to Sandbox.ParticleFloat" on exactly the most-tuned fields
    // in the particle system. Constant + Range come from short string syntax
    // (the common case); Curve / CurveRange route through the engine's own
    // JsonRead so curve serialization is not re-implemented here.

    private static ParticleFloat ParseParticleFloat( JsonElement el )
    {
        if ( el.ValueKind == JsonValueKind.Number )
            return ConstantParticleFloat( el.GetSingle() );

        if ( el.ValueKind == JsonValueKind.String )
        {
            var str = el.GetString().Trim();

            // The MCP `value` parameter is always a string, so a caller passing
            // JSON ({"Type":"Range",...}) arrives here as a String wrapping that
            // text rather than as JsonValueKind.Object. Detect and re-parse.
            if ( str.StartsWith( "{" ) )
            {
                using var doc = JsonDocument.Parse( str );
                return ParseParticleFloat( doc.RootElement );
            }

            var parts = str.Split( ',' );

            if ( parts.Length == 1 )
                return ConstantParticleFloat( ParseFloat( parts[0] ) );

            if ( parts.Length == 2 )
            {
                // Two-arg ctor sets Range mode; Type is set explicitly anyway to
                // stay correct if that ctor is ever refactored.
                var pf = new ParticleFloat( ParseFloat( parts[0] ), ParseFloat( parts[1] ) );
                pf.Type = ParticleFloat.ValueType.Range;
                return pf;
            }

            throw new ArgumentException( $"ParticleFloat string must be 'value' or 'min,max', got '{str}'." );
        }

        if ( el.ValueKind == JsonValueKind.Object )
        {
            // Explicit property parsing covers Constant + Range plus the
            // Evaluation control (Life / Frame / Seed / Particle). This bypasses
            // ParticleFloat.JsonRead, whose internal schema is undocumented and
            // which upstream measured returning zeros for ConstantA/ConstantB
            // even when the input JSON set them. Curve / CurveRange still need
            // the engine deserializer — their CurveA/CurveB structs are not
            // reasonable to build by hand — so those fall through to it.
            if ( el.TryGetProperty( "Type", out var typeEl ) )
            {
                var typeStr = typeEl.GetString();
                if ( typeStr.Equals( "Curve", StringComparison.OrdinalIgnoreCase ) ||
                     typeStr.Equals( "CurveRange", StringComparison.OrdinalIgnoreCase ) )
                    return DeserializeParticleFloatJson( el.GetRawText() );
            }

            var result = new ParticleFloat();
            if ( el.TryGetProperty( "Type", out var t ) )
                result.Type = (ParticleFloat.ValueType)Enum.Parse(
                    typeof( ParticleFloat.ValueType ), t.GetString(), ignoreCase: true );
            if ( el.TryGetProperty( "Evaluation", out var ev ) )
                result.Evaluation = (ParticleFloat.EvaluationType)Enum.Parse(
                    typeof( ParticleFloat.EvaluationType ), ev.GetString(), ignoreCase: true );
            if ( el.TryGetProperty( "ConstantValue", out var cv ) ) result.ConstantValue = cv.GetSingle();
            if ( el.TryGetProperty( "ConstantA", out var ca ) ) result.ConstantA = ca.GetSingle();
            if ( el.TryGetProperty( "ConstantB", out var cb ) ) result.ConstantB = cb.GetSingle();
            return result;
        }

        throw new ArgumentException(
            $"Cannot convert {el.ValueKind} to ParticleFloat. " +
            "Use a number, 'value' string, 'min,max' string, or full JSON object." );
    }

    private static ParticleFloat DeserializeParticleFloatJson( string json )
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes( json );
        var reader = new System.Text.Json.Utf8JsonReader( bytes );
        reader.Read();   // advance to the first token (StartObject)
        // JsonRead is documented without a ref qualifier, but Utf8JsonReader is
        // a ref struct, so the call site must pass it by ref.
        return (ParticleFloat)ParticleFloat.JsonRead( ref reader, typeof( ParticleFloat ) );
    }

    private static ParticleFloat ConstantParticleFloat( float v )
    {
        var pf = new ParticleFloat();
        pf.Type = ParticleFloat.ValueType.Constant;
        pf.ConstantValue = v;
        return pf;
    }

    private static ParticleVector3 ParseParticleVector3( JsonElement el )
    {
        if ( el.ValueKind == JsonValueKind.String )
        {
            var str = el.GetString().Trim();

            if ( str.StartsWith( "{" ) )
            {
                using var doc = JsonDocument.Parse( str );
                return ParseParticleVector3( doc.RootElement );
            }

            var parts = str.Split( ',' );
            if ( parts.Length != 3 )
                throw new ArgumentException(
                    $"ParticleVector3 string must be 'x,y,z' or a {{X,Y,Z}} JSON object, got '{str}'." );

            var pv = new ParticleVector3();
            pv.X = ConstantParticleFloat( ParseFloat( parts[0] ) );
            pv.Y = ConstantParticleFloat( ParseFloat( parts[1] ) );
            pv.Z = ConstantParticleFloat( ParseFloat( parts[2] ) );
            return pv;
        }

        if ( el.ValueKind == JsonValueKind.Object )
        {
            // Per-axis recursive parse — accepts {X:5, Y:"0,2", Z:{..}} mixed shapes.
            var pv = new ParticleVector3();
            if ( el.TryGetProperty( "X", out var xp ) ) pv.X = ParseParticleFloat( xp );
            if ( el.TryGetProperty( "Y", out var yp ) ) pv.Y = ParseParticleFloat( yp );
            if ( el.TryGetProperty( "Z", out var zp ) ) pv.Z = ParseParticleFloat( zp );
            return pv;
        }

        throw new ArgumentException(
            $"Cannot convert {el.ValueKind} to ParticleVector3. Use an 'x,y,z' string or {{X,Y,Z}} object." );
    }

    private static ParticleGradient ParseParticleGradient( JsonElement el )
    {
        if ( el.ValueKind == JsonValueKind.String )
        {
            var s = el.GetString().Trim();

            if ( s.StartsWith( "{" ) )
            {
                using var doc = JsonDocument.Parse( s );
                return ParseParticleGradient( doc.RootElement );
            }

            // Range form: "r1,g1,b1,a1;r2,g2,b2,a2" — picks per-particle, or
            // per-frame, or over life, depending on Evaluation.
            if ( s.Contains( ';' ) )
            {
                var halves = s.Split( ';' );
                if ( halves.Length != 2 )
                    throw new ArgumentException(
                        $"ParticleGradient Range form must be 'r1,g1,b1,a1;r2,g2,b2,a2', got '{s}'." );
                var pgr = new ParticleGradient();
                pgr.Type = ParticleGradient.ValueType.Range;
                pgr.ConstantA = Color.Parse( halves[0].Trim() ) ?? default;
                pgr.ConstantB = Color.Parse( halves[1].Trim() ) ?? default;
                return pgr;
            }

            // Constant form: "r,g,b,a" or "#hex".
            var pg = new ParticleGradient();
            pg.Type = ParticleGradient.ValueType.Constant;
            pg.ConstantValue = Color.Parse( s ) ?? default;
            return pg;
        }

        if ( el.ValueKind == JsonValueKind.Object )
        {
            // Full control: {Type, Evaluation, ConstantA, ConstantB, ConstantValue}.
            // This is how you get fire colour cooling over particle life —
            // Range mode plus Life evaluation.
            var pg = new ParticleGradient();
            if ( el.TryGetProperty( "Type", out var t ) )
                pg.Type = (ParticleGradient.ValueType)Enum.Parse(
                    typeof( ParticleGradient.ValueType ), t.GetString(), ignoreCase: true );
            if ( el.TryGetProperty( "Evaluation", out var ev ) )
                pg.Evaluation = (ParticleGradient.EvaluationType)Enum.Parse(
                    typeof( ParticleGradient.EvaluationType ), ev.GetString(), ignoreCase: true );
            if ( el.TryGetProperty( "ConstantValue", out var cv ) )
                pg.ConstantValue = Color.Parse( cv.GetString() ) ?? default;
            if ( el.TryGetProperty( "ConstantA", out var ca ) )
                pg.ConstantA = Color.Parse( ca.GetString() ) ?? default;
            if ( el.TryGetProperty( "ConstantB", out var cb ) )
                pg.ConstantB = Color.Parse( cb.GetString() ) ?? default;
            return pg;
        }

        throw new ArgumentException(
            $"Cannot convert {el.ValueKind} to ParticleGradient. Use one of: " +
            "'r,g,b,a' (Constant), 'r1,g1,b1,a1;r2,g2,b2,a2' (Range), " +
            "or a {Type,ConstantA,ConstantB,Evaluation} JSON object." );
    }

    // ── set_enabled ──────────────────────────────────────────────────

    private static object SetEnabled( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "set_enabled" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "set_enabled" );

        var componentId = HandlerBase.GetString( args, "component_id" );
        if ( string.IsNullOrEmpty( componentId ) )
            return HandlerBase.Error( "Missing required 'component_id' parameter.", "set_enabled" );

        if ( !args.TryGetProperty( "enabled", out var enabledEl ) )
            return HandlerBase.Error( "Missing required 'enabled' parameter.", "set_enabled" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "set_enabled" );
        var comp = FindComponentByIdOrThrow( go, componentId, "set_enabled" );

        comp.Enabled = enabledEl.GetBoolean();

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            component_id = comp.Id.ToString(),
            component_type = comp.GetType().Name,
            enabled = comp.Enabled,
            message = $"Set '{comp.GetType().Name}' on '{go.Name}' to {(comp.Enabled ? "enabled" : "disabled")}."
        } );
    }

    // ── get_properties ───────────────────────────────────────────────

    private static object GetProperties( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "get_properties" );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", "get_properties" );

        var componentId = HandlerBase.GetString( args, "component_id" );
        if ( string.IsNullOrEmpty( componentId ) )
            return HandlerBase.Error( "Missing required 'component_id' parameter.", "get_properties" );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, "get_properties" );
        var comp = FindComponentByIdOrThrow( go, componentId, "get_properties" );

        var props = new List<object>();
        var type = comp.GetType();

        foreach ( var prop in type.GetProperties( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance ) )
        {
            if ( !prop.CanRead ) continue;

            object val;
            try
            {
                val = SchemaHelpers.FormatValue( prop.GetValue( comp ) );
            }
            catch
            {
                val = "<error reading value>";
            }

            props.Add( new
            {
                name = prop.Name,
                type = SchemaHelpers.FormatTypeName( prop.PropertyType ),
                value = val,
                canWrite = prop.CanWrite,
                schema = SchemaHelpers.DescribeType( prop.PropertyType )
            } );
        }

        return HandlerBase.Success( new
        {
            gameObjectId = go.Id.ToString(),
            gameObjectName = go.Name,
            handle = SceneHelpers.BuildHandle( go ),
            componentId = comp.Id.ToString(),
            componentType = comp.GetType().Name,
            componentHandle = SceneHelpers.BuildHandle( comp ),
            enabled = comp.Enabled,
            properties = props
        } );
    }

    // ── get_types ─────────────────────────────────────────────────────

    private static object GetTypes( JsonElement args )
    {
        var filter = HandlerBase.GetString( args, "filter" );

        var results = new List<object>();

        foreach ( var td in TypeLibrary.GetTypes<Component>() )
        {
            if ( td.TargetType != null && td.TargetType.IsAbstract ) continue;

            var name = td.Name;
            if ( !string.IsNullOrEmpty( filter )
                && name.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0
                && (td.TargetType?.Namespace ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                continue;

            results.Add( new
            {
                name,
                @namespace = td.TargetType?.Namespace ?? ""
            } );
        }

        results = results.OrderBy( r => ((dynamic)r).name ).ToList();

        return HandlerBase.Success( new
        {
            summary = $"Found {results.Count} component type(s)" +
                (!string.IsNullOrEmpty( filter ) ? $" matching '{filter}'" : "") + ".",
            results
        } );
    }

    // ── copy ──────────────────────────────────────────────────────────

    private static object Copy( JsonElement args )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", "copy" );

        var sourceComponentId = HandlerBase.GetString( args, "source_component_id" );
        if ( string.IsNullOrEmpty( sourceComponentId ) )
            return HandlerBase.Error( "Missing required 'source_component_id' parameter.", "copy" );

        var targetId = HandlerBase.GetString( args, "target_id" );
        if ( string.IsNullOrEmpty( targetId ) )
            return HandlerBase.Error( "Missing required 'target_id' parameter.", "copy" );

        // Find source component by scanning all objects
        Component sourceComp = null;
        if ( Guid.TryParse( sourceComponentId, out var sourceGuid ) )
        {
            foreach ( var go in SceneHelpers.WalkAll( scene, true ) )
            {
                sourceComp = go.Components.GetAll().FirstOrDefault( c => c.Id == sourceGuid );
                if ( sourceComp != null ) break;
            }
        }

        if ( sourceComp == null )
            return HandlerBase.Error( $"Source component '{sourceComponentId}' not found in scene.", "copy" );

        var targetGo = SceneHelpers.FindByIdOrThrow( scene, targetId, "copy" );

        // Find the TypeDescription for the source component's type
        var td = FindComponentTypeDescription( sourceComp.GetType().Name );
        if ( td == null )
            return HandlerBase.Error( $"Component type '{sourceComp.GetType().Name}' not found in TypeLibrary.", "copy" );

        var newComp = targetGo.Components.Create( td );

        return HandlerBase.Success( new
        {
            source_component_id = sourceComp.Id.ToString(),
            source_component_type = sourceComp.GetType().Name,
            source_object = sourceComp.GameObject.Name,
            target_id = targetGo.Id.ToString(),
            target_name = targetGo.Name,
            new_component_id = newComp.Id.ToString(),
            message = $"Copied '{sourceComp.GetType().Name}' from '{sourceComp.GameObject.Name}' to '{targetGo.Name}'."
        } );
    }

    // ── bulk_set ─────────────────────────────────────────────────────

    private static object BulkSet( JsonElement args, string action )
    {
        var scene = SceneHelpers.ResolveScene();
        if ( scene == null )
            return HandlerBase.Error( "No active scene.", action );

        var id = HandlerBase.GetString( args, "id" );
        if ( string.IsNullOrEmpty( id ) )
            return HandlerBase.Error( "Missing required 'id' parameter.", action );

        var componentId = HandlerBase.GetString( args, "component_id" );
        if ( string.IsNullOrEmpty( componentId ) )
            return HandlerBase.Error( "Missing required 'component_id' parameter.", action );

        if ( !args.TryGetProperty( "properties", out var propsEl ) )
            return HandlerBase.Error( "Missing required 'properties' parameter. Provide an object or JSON string of {property: value} pairs.", action );

        var go = SceneHelpers.FindByIdOrThrow( scene, id, action );
        var comp = FindComponentByIdOrThrow( go, componentId, action );

        JsonElement propsObj;
        if ( propsEl.ValueKind == JsonValueKind.String )
        {
            var propsJson = propsEl.GetString();
            if ( string.IsNullOrEmpty( propsJson ) )
                return HandlerBase.Error( "Missing required 'properties' parameter. Provide an object or JSON string of {property: value} pairs.", action );

            try
            {
                using var doc = JsonDocument.Parse( propsJson );
                propsObj = doc.RootElement.Clone();
            }
            catch ( JsonException ex )
            {
                return HandlerBase.Error( $"Failed to parse 'properties' JSON: {ex.Message}", action );
            }
        }
        else
        {
            propsObj = propsEl.Clone();
        }

        if ( propsObj.ValueKind != JsonValueKind.Object )
            return HandlerBase.Error( "'properties' must be a JSON object, e.g. {\"Model\": \"path.vmdl\", \"Tint\": \"1,0,0,1\"}.", action );

        var setResults = new List<object>();
        var errors = new List<object>();

        foreach ( var kvp in propsObj.EnumerateObject() )
        {
            var propName = kvp.Name;
            var valEl = kvp.Value;

            var prop = comp.GetType().GetProperty( propName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance );

            if ( prop == null )
            {
                errors.Add( new { property = propName, error = $"Property '{propName}' not found on '{comp.GetType().Name}'." } );
                continue;
            }

            if ( !prop.CanWrite )
            {
                errors.Add( new { property = propName, error = $"Property '{propName}' is read-only." } );
                continue;
            }

            try
            {
                object converted = ConvertJsonValue( valEl, prop.PropertyType );
                prop.SetValue( comp, converted );
                var readback = prop.GetValue( comp );
                setResults.Add( new { property = propName, value = readback?.ToString() } );
            }
            catch ( Exception ex )
            {
                errors.Add( new { property = propName, error = ex.Message } );
            }
        }

        return HandlerBase.Success( new
        {
            id = go.Id.ToString(),
            name = go.Name,
            component_id = comp.Id.ToString(),
            component_type = comp.GetType().Name,
            set_count = setResults.Count,
            error_count = errors.Count,
            set = setResults,
            errors,
            message = $"Set {setResults.Count} properties on '{comp.GetType().Name}', {errors.Count} errors."
        } );
    }
}
