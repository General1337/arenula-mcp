using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Arenula;

/// <summary>
/// reflection tool: search, get_type, get_members, list_assemblies.
/// Reflects over loaded S&Box assemblies for ground-truth API discovery.
/// Returns types, methods, properties, attributes from the running engine.
/// </summary>
internal static class ReflectionHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "search"          => Search( args ),
                "get_type"        => GetType( args ),
                "get_members"     => GetMembers( args ),
                "list_assemblies" => ListAssemblies( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action,
                    "Valid actions: search, get_type, get_members, list_assemblies" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    // ── search ──────────────────────────────────────────────────────
    // Search loaded types by name. Returns matching types with namespace,
    // base type, and whether they're abstract/sealed/static/interface/enum.

    private static object Search( JsonElement args )
    {
        var query = HandlerBase.GetString( args, "query" );
        if ( string.IsNullOrEmpty( query ) )
            return HandlerBase.Error( "Missing required 'query' parameter.", "search" );

        var ns = HandlerBase.GetString( args, "namespace" );
        var kind = HandlerBase.GetString( args, "kind" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 25 );

        var assemblies = GetSboxAssemblies();
        var results = new List<object>();

        foreach ( var asm in assemblies )
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch { continue; }

            foreach ( var t in types )
            {
                if ( t.Name.IndexOf( query, StringComparison.OrdinalIgnoreCase ) < 0
                    && (t.FullName ?? "").IndexOf( query, StringComparison.OrdinalIgnoreCase ) < 0 )
                    continue;

                if ( !string.IsNullOrEmpty( ns )
                    && (t.Namespace ?? "").IndexOf( ns, StringComparison.OrdinalIgnoreCase ) < 0 )
                    continue;

                if ( !string.IsNullOrEmpty( kind ) && !MatchesKind( t, kind ) )
                    continue;

                results.Add( FormatTypeInfo( t ) );
            }
        }

        // Also search TypeLibrary for game-assembly component types
        // (TypeLibrary may index types not visible via raw assembly reflection)
        try
        {
            foreach ( var td in TypeLibrary.GetTypes<Component>() )
            {
                if ( td.TargetType == null ) continue;
                if ( td.Name.IndexOf( query, StringComparison.OrdinalIgnoreCase ) < 0 ) continue;
                if ( results.Any( r => ((dynamic)r).full_name == (td.TargetType.FullName ?? "") ) ) continue;

                if ( !string.IsNullOrEmpty( ns )
                    && (td.TargetType.Namespace ?? "").IndexOf( ns, StringComparison.OrdinalIgnoreCase ) < 0 )
                    continue;

                if ( !string.IsNullOrEmpty( kind ) && !MatchesKind( td.TargetType, kind ) )
                    continue;

                results.Add( FormatTypeInfo( td.TargetType ) );
            }
        }
        catch { /* TypeLibrary may not be available in all contexts */ }

        results = results.OrderBy( r => ((dynamic)r).name as string ).ToList();

        return HandlerBase.Paginate( results, offset, limit, r => r );
    }

    // ── get_type ────────────────────────────────────────────────────
    // Get full details of a specific type: base class, interfaces,
    // constructors, methods, properties, fields, attributes.

    private static object GetType( JsonElement args )
    {
        var name = HandlerBase.GetString( args, "name" );
        if ( string.IsNullOrEmpty( name ) )
            return HandlerBase.Error( "Missing required 'name' parameter.", "get_type" );

        var type = ResolveType( name );
        if ( type == null )
            return HandlerBase.Error( $"Type '{name}' not found in loaded assemblies.", "get_type",
                "Try 'search' first to find the exact type name." );

        var includeInherited = HandlerBase.GetBool( args, "include_inherited", false );
        var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        if ( !includeInherited )
            bindingFlags |= BindingFlags.DeclaredOnly;

        var section = HandlerBase.GetString( args, "section" );

        var result = new Dictionary<string, object>
        {
            ["name"] = type.Name,
            ["full_name"] = type.FullName,
            ["namespace"] = type.Namespace ?? "",
            ["assembly"] = type.Assembly.GetName().Name,
            ["kind"] = GetKind( type ),
            ["base_type"] = type.BaseType?.FullName,
            ["is_abstract"] = type.IsAbstract,
            ["is_sealed"] = type.IsSealed,
            ["is_generic"] = type.IsGenericType
        };

        // Interfaces
        if ( section == null || section == "interfaces" )
        {
            var interfaces = type.GetInterfaces()
                .Where( i => i.IsPublic )
                .Select( i => i.Name )
                .OrderBy( i => i )
                .ToList();
            result["interfaces"] = interfaces;
        }

        // Attributes
        if ( section == null || section == "attributes" )
        {
            var attrs = type.GetCustomAttributes( false )
                .Select( a => a.GetType().Name )
                .Distinct()
                .OrderBy( a => a )
                .ToList();
            result["attributes"] = attrs;
        }

        // Constructors
        if ( section == null || section == "constructors" )
        {
            var ctors = type.GetConstructors( BindingFlags.Public | BindingFlags.Instance )
                .Select( c => FormatConstructor( c ) )
                .ToList();
            result["constructors"] = ctors;
        }

        // Properties
        if ( section == null || section == "properties" )
        {
            var props = type.GetProperties( bindingFlags )
                .Where( p => p.DeclaringType == type || includeInherited )
                .Select( p => FormatProperty( p ) )
                .ToList();
            result["properties"] = props;
        }

        // Methods (exclude property accessors and operators)
        if ( section == null || section == "methods" )
        {
            var methods = type.GetMethods( bindingFlags )
                .Where( m => !m.IsSpecialName )
                .Where( m => m.DeclaringType == type || includeInherited )
                .Select( m => FormatMethod( m ) )
                .ToList();
            result["methods"] = methods;
        }

        // Fields
        if ( section == null || section == "fields" )
        {
            var fields = type.GetFields( bindingFlags )
                .Where( f => f.DeclaringType == type || includeInherited )
                .Select( f => new
                {
                    name = f.Name,
                    type = SchemaHelpers.FormatTypeName( f.FieldType ),
                    is_static = f.IsStatic,
                    is_readonly = f.IsInitOnly
                } )
                .ToList();
            result["fields"] = fields;
        }

        // Enum values
        if ( type.IsEnum && (section == null || section == "values") )
        {
            result["values"] = Enum.GetNames( type ).ToList();
        }

        return HandlerBase.Success( result );
    }

    // ── get_members ─────────────────────────────────────────────────
    // Search for methods/properties across all types by member name.

    private static object GetMembers( JsonElement args )
    {
        var query = HandlerBase.GetString( args, "query" );
        if ( string.IsNullOrEmpty( query ) )
            return HandlerBase.Error( "Missing required 'query' parameter.", "get_members" );

        var memberKind = HandlerBase.GetString( args, "kind" );
        var offset = HandlerBase.GetInt( args, "offset", 0 );
        var limit = HandlerBase.GetInt( args, "limit", 25 );
        var results = new List<object>();

        var assemblies = GetSboxAssemblies();
        foreach ( var asm in assemblies )
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch { continue; }

            foreach ( var t in types )
            {
                if ( memberKind is null or "method" )
                {
                    foreach ( var m in t.GetMethods( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly ) )
                    {
                        if ( m.IsSpecialName ) continue;
                        if ( m.Name.IndexOf( query, StringComparison.OrdinalIgnoreCase ) < 0 ) continue;
                        results.Add( new
                        {
                            declaring_type = t.FullName,
                            kind = "method",
                            name = m.Name,
                            signature = FormatMethodSignature( m ),
                            is_static = m.IsStatic
                        } );
                    }
                }

                if ( memberKind is null or "property" )
                {
                    foreach ( var p in t.GetProperties( BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly ) )
                    {
                        if ( p.Name.IndexOf( query, StringComparison.OrdinalIgnoreCase ) < 0 ) continue;
                        results.Add( new
                        {
                            declaring_type = t.FullName,
                            kind = "property",
                            name = p.Name,
                            signature = $"{SchemaHelpers.FormatTypeName( p.PropertyType )} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}",
                            is_static = p.GetMethod?.IsStatic ?? false
                        } );
                    }
                }
            }
        }

        results = results.OrderBy( r => ((dynamic)r).name as string )
            .ThenBy( r => ((dynamic)r).declaring_type as string )
            .ToList();

        return HandlerBase.Paginate( results, offset, limit, r => r );
    }

    // ── list_assemblies ─────────────────────────────────────────────
    // List all loaded assemblies with type counts.

    private static object ListAssemblies( JsonElement args )
    {
        var filter = HandlerBase.GetString( args, "filter" );
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where( a => !a.IsDynamic )
            .OrderBy( a => a.GetName().Name );

        var results = new List<object>();
        foreach ( var asm in assemblies )
        {
            var name = asm.GetName().Name;
            if ( !string.IsNullOrEmpty( filter )
                && name.IndexOf( filter, StringComparison.OrdinalIgnoreCase ) < 0 )
                continue;

            int typeCount;
            try { typeCount = asm.GetExportedTypes().Length; }
            catch { typeCount = -1; }

            results.Add( new
            {
                name,
                version = asm.GetName().Version?.ToString(),
                type_count = typeCount,
                is_sbox = IsSboxAssembly( asm )
            } );
        }

        return HandlerBase.Success( new
        {
            summary = $"Found {results.Count} loaded assemblies" +
                (!string.IsNullOrEmpty( filter ) ? $" matching '{filter}'" : "") + ".",
            results
        } );
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static IEnumerable<Assembly> GetSboxAssemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where( a => !a.IsDynamic && IsSboxAssembly( a ) );
    }

    private static bool IsSboxAssembly( Assembly asm )
    {
        var name = asm.GetName().Name ?? "";
        return name.StartsWith( "Sandbox", StringComparison.OrdinalIgnoreCase )
            || name.StartsWith( "Facepunch", StringComparison.OrdinalIgnoreCase )
            || name.Equals( "Game", StringComparison.OrdinalIgnoreCase )
            || name.Equals( "game.editor", StringComparison.OrdinalIgnoreCase )
            || name.Contains( "sbox", StringComparison.OrdinalIgnoreCase );
    }

    private static Type ResolveType( string name )
    {
        // Try TypeLibrary first (knows about game types)
        var td = TypeLibrary.GetType( name );
        if ( td?.TargetType != null ) return td.TargetType;

        // Try full name match across S&Box assemblies
        foreach ( var asm in GetSboxAssemblies() )
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch { continue; }

            var match = types.FirstOrDefault( t =>
                t.Name.Equals( name, StringComparison.OrdinalIgnoreCase )
                || (t.FullName?.Equals( name, StringComparison.OrdinalIgnoreCase ) ?? false) );
            if ( match != null ) return match;
        }

        // Try all assemblies as last resort
        foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
        {
            if ( asm.IsDynamic ) continue;
            try
            {
                var match = asm.GetExportedTypes().FirstOrDefault( t =>
                    t.FullName?.Equals( name, StringComparison.OrdinalIgnoreCase ) ?? false );
                if ( match != null ) return match;
            }
            catch { }
        }

        return null;
    }

    private static object FormatTypeInfo( Type t ) => new
    {
        name = t.Name,
        full_name = t.FullName,
        @namespace = t.Namespace ?? "",
        kind = GetKind( t ),
        base_type = t.BaseType?.Name,
        is_abstract = t.IsAbstract && !t.IsInterface,
        is_sealed = t.IsSealed,
        assembly = t.Assembly.GetName().Name
    };

    private static string GetKind( Type t )
    {
        if ( t.IsEnum ) return "enum";
        if ( t.IsInterface ) return "interface";
        if ( t.IsValueType ) return "struct";
        return "class";
    }

    private static bool MatchesKind( Type t, string kind )
    {
        return kind.ToLowerInvariant() switch
        {
            "class" => t.IsClass && !t.IsEnum,
            "struct" => t.IsValueType && !t.IsEnum,
            "enum" => t.IsEnum,
            "interface" => t.IsInterface,
            _ => true
        };
    }

    private static object FormatProperty( PropertyInfo p ) => SchemaHelpers.DescribeProperty( p );

    private static object FormatMethod( MethodInfo m ) => new
    {
        name = m.Name,
        signature = FormatMethodSignature( m ),
        return_type = SchemaHelpers.FormatTypeName( m.ReturnType ),
        is_static = m.IsStatic,
        is_virtual = m.IsVirtual,
        is_abstract = m.IsAbstract,
        parameters = m.GetParameters().Select( p => new
        {
            name = p.Name,
            type = SchemaHelpers.FormatTypeName( p.ParameterType ),
            has_default = p.HasDefaultValue
        } ).ToList()
    };

    private static object FormatConstructor( ConstructorInfo c ) => new
    {
        parameters = c.GetParameters().Select( p => new
        {
            name = p.Name,
            type = SchemaHelpers.FormatTypeName( p.ParameterType ),
            has_default = p.HasDefaultValue
        } ).ToList()
    };

    private static string FormatMethodSignature( MethodInfo m )
    {
        var ps = string.Join( ", ", m.GetParameters().Select( p =>
            $"{SchemaHelpers.FormatTypeName( p.ParameterType )} {p.Name}" ) );
        return $"{SchemaHelpers.FormatTypeName( m.ReturnType )} {m.Name}({ps})";
    }
}
