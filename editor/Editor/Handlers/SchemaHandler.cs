using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Sandbox;

namespace Arenula;

/// <summary>
/// schema tool — component/property/resource/enum metadata for safer writes.
/// Migrated to the [McpTool] attribute scan in mcp-dynamic-discovery Phase 3
/// Wave 3.2 — read-only multi-action tool, MainThread dispatch.
/// </summary>
[McpTool( "schema",
    "Inspect component, property, resource, and enum metadata from the live editor TypeLibrary. Use this before component writes to discover exact type names, writable properties, accepted value kinds, and enum values. This tool is read-only.",
    ReadOnlyHint = true )]
internal static class SchemaHandler
{
    // [McpAction] thin wrappers. Each preserves the legacy HandlerBase.Error
    // response shape — the pre-migration Handle() switch wrapped every arm in
    // the same try/catch.

    [McpAction( "components", ReadOnly = true )]
    internal static object HandleComponents( JsonElement args )
    {
        try { return Components( args ); }
        catch ( Exception ex ) { return HandlerBase.Error( ex.Message, "components" ); }
    }

    [McpAction( "component", ReadOnly = true )]
    internal static object HandleComponent( JsonElement args )
    {
        try { return ComponentSchema( args ); }
        catch ( Exception ex ) { return HandlerBase.Error( ex.Message, "component" ); }
    }

    [McpAction( "property", ReadOnly = true )]
    internal static object HandleProperty( JsonElement args )
    {
        try { return PropertySchema( args ); }
        catch ( Exception ex ) { return HandlerBase.Error( ex.Message, "property" ); }
    }

    [McpAction( "resources", ReadOnly = true )]
    internal static object HandleResources( JsonElement args )
    {
        try { return Resources( args ); }
        catch ( Exception ex ) { return HandlerBase.Error( ex.Message, "resources" ); }
    }

    [McpAction( "enum", ReadOnly = true )]
    internal static object HandleEnum( JsonElement args )
    {
        try { return EnumSchema( args ); }
        catch ( Exception ex ) { return HandlerBase.Error( ex.Message, "enum" ); }
    }

    /// <summary>
    /// tools/list inputSchema fragment — verbatim copy of the legacy
    /// <c>ToolRegistry.Schema.inputSchema</c> literal.
    /// </summary>
    internal static object InputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", description = "Schema lookup operation.", @enum = new[] { "components", "component", "property", "resources", "enum" } },
            ["type"] = new { type = "string", description = "Component/resource/enum type name. Required for: component, property, enum." },
            ["property"] = new { type = "string", description = "Property name. Required for: property." },
            ["filter"] = new { type = "string", description = "Case-insensitive name or namespace filter. Used by: components, resources." },
            ["offset"] = new { type = "integer", description = "Pagination offset. Default: 0." },
            ["limit"] = new { type = "integer", description = "Max results. Default: 100." }
        },
        required = new[] { "action" },
        additionalProperties = false
    };

    private static object Components( JsonElement args )
    {
        var filter = HandlerBase.GetString( args, "filter" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 100 );

        var types = TypeLibrary.GetTypes<Component>()
            .Where( t => t.TargetType != null && !t.TargetType.IsAbstract )
            .Where( t => string.IsNullOrWhiteSpace( filter )
                || t.Name.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0
                || (t.TargetType.Namespace ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0 )
            .OrderBy( t => t.Name )
            .ToList();

        return HandlerBase.Paginate( types, offset, limit, t => new
        {
            name = t.Name,
            full_name = t.TargetType.FullName,
            @namespace = t.TargetType.Namespace ?? "",
            is_engine_type = (t.TargetType.Namespace ?? "").StartsWith( "Sandbox", StringComparison.OrdinalIgnoreCase )
        } );
    }

    private static object ComponentSchema( JsonElement args )
    {
        var typeName = HandlerBase.GetString( args, "type" );
        if ( string.IsNullOrWhiteSpace( typeName ) )
            return HandlerBase.Error( "Missing required 'type' parameter.", "component" );

        var type = FindType( typeName, typeof( Component ) );
        if ( type == null )
            return HandlerBase.Error( $"Component type '{typeName}' not found.", "component",
                "Use schema.components or component.get_types to find the exact component type name." );

        var props = GetPublicProperties( type )
            .Select( SchemaHelpers.DescribeProperty )
            .ToList();

        return HandlerBase.Success( new
        {
            type = type.Name,
            full_name = type.FullName,
            @namespace = type.Namespace ?? "",
            properties = props
        } );
    }

    private static object PropertySchema( JsonElement args )
    {
        var typeName = HandlerBase.GetString( args, "type" );
        if ( string.IsNullOrWhiteSpace( typeName ) )
            return HandlerBase.Error( "Missing required 'type' parameter.", "property" );

        var propertyName = HandlerBase.GetString( args, "property" );
        if ( string.IsNullOrWhiteSpace( propertyName ) )
            return HandlerBase.Error( "Missing required 'property' parameter.", "property" );

        var type = FindType( typeName, typeof( Component ) ) ?? FindType( typeName );
        if ( type == null )
            return HandlerBase.Error( $"Type '{typeName}' not found.", "property" );

        var prop = type.GetProperty( propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static );
        if ( prop == null )
            return HandlerBase.Error( $"Property '{propertyName}' not found on '{type.Name}'.", "property" );

        return HandlerBase.Success( SchemaHelpers.DescribeProperty( prop ) );
    }

    private static object Resources( JsonElement args )
    {
        var filter = HandlerBase.GetString( args, "filter" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 100 );

        var types = TypeLibrary.GetTypes<GameResource>()
            .Where( t => t.TargetType != null && !t.TargetType.IsAbstract )
            .Where( t => string.IsNullOrWhiteSpace( filter )
                || t.Name.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0
                || (t.TargetType.Namespace ?? "").IndexOf( filter, StringComparison.OrdinalIgnoreCase ) >= 0 )
            .OrderBy( t => t.Name )
            .ToList();

        return HandlerBase.Paginate( types, offset, limit, t => new
        {
            name = t.Name,
            full_name = t.TargetType.FullName,
            @namespace = t.TargetType.Namespace ?? ""
        } );
    }

    private static object EnumSchema( JsonElement args )
    {
        var typeName = HandlerBase.GetString( args, "type" );
        if ( string.IsNullOrWhiteSpace( typeName ) )
            return HandlerBase.Error( "Missing required 'type' parameter.", "enum" );

        var type = FindType( typeName );
        if ( type == null )
            return HandlerBase.Error( $"Enum type '{typeName}' not found.", "enum" );
        if ( !type.IsEnum )
            return HandlerBase.Error( $"Type '{type.Name}' is not an enum.", "enum" );

        var names = Enum.GetNames( type );
        var values = Enum.GetValues( type );
        var entries = new List<object>();
        for ( var i = 0; i < names.Length; i++ )
        {
            entries.Add( new
            {
                name = names[i],
                value = Convert.ToInt64( values.GetValue( i ) )
            } );
        }

        return HandlerBase.Success( new
        {
            type = type.Name,
            full_name = type.FullName,
            values = entries
        } );
    }

    private static IEnumerable<PropertyInfo> GetPublicProperties( Type type )
    {
        return type.GetProperties( BindingFlags.Public | BindingFlags.Instance )
            .Where( p => p.GetIndexParameters().Length == 0 )
            .OrderBy( p => p.Name );
    }

    private static Type FindType( string typeName, Type requiredBase = null )
    {
        if ( string.IsNullOrWhiteSpace( typeName ) )
            return null;

        var td = TypeLibrary.GetType( typeName );
        if ( td?.TargetType != null && (requiredBase == null || requiredBase.IsAssignableFrom( td.TargetType )) )
            return td.TargetType;

        if ( requiredBase == typeof( Component ) )
        {
            foreach ( var candidate in TypeLibrary.GetTypes<Component>() )
            {
                var t = candidate.TargetType;
                if ( t == null ) continue;
                if ( t.Name.Equals( typeName, StringComparison.OrdinalIgnoreCase )
                    || (t.FullName?.Equals( typeName, StringComparison.OrdinalIgnoreCase ) ?? false) )
                    return t;
            }
        }

        foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
        {
            if ( asm.IsDynamic ) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach ( var t in types )
            {
                if ( requiredBase != null && !requiredBase.IsAssignableFrom( t ) )
                    continue;

                if ( t.Name.Equals( typeName, StringComparison.OrdinalIgnoreCase )
                    || (t.FullName?.Equals( typeName, StringComparison.OrdinalIgnoreCase ) ?? false) )
                    return t;
            }
        }

        return null;
    }
}
