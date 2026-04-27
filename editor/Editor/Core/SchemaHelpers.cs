using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox;

namespace Arenula;

internal static class SchemaHelpers
{
    internal static object DescribeType( Type type )
    {
        if ( type == null )
            return new { kind = "unknown", display = "unknown" };

        var underlying = Nullable.GetUnderlyingType( type );
        var actual = underlying ?? type;

        if ( actual.IsEnum )
            return new { kind = "enum", display = FormatTypeName( type ), nullable = underlying != null, values = Enum.GetNames( actual ) };
        if ( actual == typeof( string ) )
            return new { kind = "string", display = "string", nullable = underlying != null, example = "text" };
        if ( actual == typeof( bool ) )
            return new { kind = "boolean", display = "bool", nullable = underlying != null, example = true };
        if ( actual == typeof( int ) || actual == typeof( long ) || actual == typeof( short ) )
            return new { kind = "integer", display = FormatTypeName( type ), nullable = underlying != null, example = 1 };
        if ( actual == typeof( float ) || actual == typeof( double ) || actual == typeof( decimal ) )
            return new { kind = "number", display = FormatTypeName( type ), nullable = underlying != null, example = 1.0 };
        if ( actual == typeof( Vector2 ) )
            return new { kind = "vector2", display = "Vector2", nullable = underlying != null, example = new { x = 0, y = 0 } };
        if ( actual == typeof( Vector3 ) )
            return new { kind = "vector3", display = "Vector3", nullable = underlying != null, example = new { x = 0, y = 0, z = 0 } };
        if ( actual == typeof( Vector4 ) )
            return new { kind = "vector4", display = "Vector4", nullable = underlying != null, example = new { x = 0, y = 0, z = 0, w = 0 } };
        if ( actual == typeof( Rotation ) )
            return new { kind = "rotation", display = "Rotation", nullable = underlying != null, example = new { pitch = 0, yaw = 0, roll = 0 } };
        if ( actual == typeof( Color ) || actual == typeof( Color32 ) )
            return new { kind = "color", display = actual.Name, nullable = underlying != null, example = new { r = 255, g = 255, b = 255, a = 255 } };
        if ( actual == typeof( Model ) )
            return new { kind = "asset_reference", display = "Model", nullable = underlying != null, asset_kind = "model", example = "models/citizen_props/crate01.vmdl" };
        if ( typeof( GameObject ).IsAssignableFrom( actual ) )
            return new { kind = "gameobject_reference", display = actual.Name, nullable = underlying != null, example = new { id = "gameobject-guid" } };
        if ( typeof( Component ).IsAssignableFrom( actual ) )
            return new { kind = "component_reference", display = actual.Name, nullable = underlying != null, example = new { id = "component-guid" } };

        if ( actual.IsArray )
            return new { kind = "array", display = FormatTypeName( type ), nullable = underlying != null, element = DescribeType( actual.GetElementType() ) };

        if ( TryGetEnumerableElementType( actual, out var elementType ) )
            return new { kind = "list", display = FormatTypeName( type ), nullable = underlying != null, element = DescribeType( elementType ) };

        return new { kind = "object", display = FormatTypeName( type ), nullable = underlying != null };
    }

    internal static object DescribeProperty( PropertyInfo prop ) => new
    {
        name = prop.Name,
        type = FormatTypeName( prop.PropertyType ),
        can_read = prop.CanRead,
        can_write = prop.CanWrite,
        is_static = prop.GetMethod?.IsStatic ?? false,
        attributes = prop.GetCustomAttributes( false )
            .Select( a => a.GetType().Name )
            .Where( a => a != "CompilerGeneratedAttribute" )
            .Distinct()
            .OrderBy( a => a )
            .ToList(),
        schema = DescribeType( prop.PropertyType )
    };

    internal static object FormatValue( object raw ) => raw switch
    {
        null => null,
        bool b => b,
        int i => i,
        long l => l,
        float f => MathF.Round( f, 4 ),
        double d => Math.Round( d, 4 ),
        decimal m => m,
        string s => s,
        Enum e => e.ToString(),
        Vector2 v => new { x = MathF.Round( v.x, 3 ), y = MathF.Round( v.y, 3 ) },
        Vector3 v => HandlerBase.V3( v ),
        Vector4 v => new { x = MathF.Round( v.x, 3 ), y = MathF.Round( v.y, 3 ), z = MathF.Round( v.z, 3 ), w = MathF.Round( v.w, 3 ) },
        Rotation r => HandlerBase.Rot( r ),
        Color c => new { r = MathF.Round( c.r, 3 ), g = MathF.Round( c.g, 3 ), b = MathF.Round( c.b, 3 ), a = MathF.Round( c.a, 3 ) },
        GameObject go => new { id = go.Id.ToString(), name = go.Name, handle = SceneHelpers.BuildHandle( go ) },
        Component comp => new { id = comp.Id.ToString(), type = comp.GetType().Name, handle = SceneHelpers.BuildHandle( comp ) },
        Model model => model.ResourceName ?? model.ToString(),
        IEnumerable enumerable when raw is not string => enumerable.Cast<object>().Take( 10 ).Select( FormatValue ).ToList(),
        _ => raw.ToString()
    };

    internal static string FormatTypeName( Type t )
    {
        if ( t == null ) return "void";
        if ( t == typeof( void ) ) return "void";
        if ( t == typeof( int ) ) return "int";
        if ( t == typeof( float ) ) return "float";
        if ( t == typeof( double ) ) return "double";
        if ( t == typeof( bool ) ) return "bool";
        if ( t == typeof( string ) ) return "string";
        if ( t == typeof( object ) ) return "object";

        if ( t.IsGenericType )
        {
            var gen = t.GetGenericTypeDefinition();
            var args = string.Join( ", ", t.GetGenericArguments().Select( FormatTypeName ) );
            var baseName = gen.Name.Contains( '`' ) ? gen.Name[..gen.Name.IndexOf( '`' )] : gen.Name;
            if ( gen == typeof( Nullable<> ) ) return $"{args}?";
            if ( gen == typeof( List<> ) ) return $"List<{args}>";
            if ( gen == typeof( Dictionary<,> ) ) return $"Dictionary<{args}>";
            if ( gen == typeof( IEnumerable<> ) ) return $"IEnumerable<{args}>";
            return $"{baseName}<{args}>";
        }

        if ( t.IsArray )
            return $"{FormatTypeName( t.GetElementType() )}[]";

        return t.Name;
    }

    private static bool TryGetEnumerableElementType( Type type, out Type elementType )
    {
        elementType = null;
        if ( type == typeof( string ) ) return false;

        if ( type.IsGenericType && type.GetGenericArguments().Length == 1 )
        {
            var genericDef = type.GetGenericTypeDefinition();
            if ( genericDef == typeof( IEnumerable<> ) || genericDef == typeof( IList<> ) || genericDef == typeof( List<> ) || genericDef == typeof( HashSet<> ) )
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        var enumerable = type.GetInterfaces().FirstOrDefault( i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof( IEnumerable<> ) );
        if ( enumerable != null )
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        return false;
    }
}
