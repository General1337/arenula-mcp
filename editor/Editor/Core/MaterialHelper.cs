using System.Linq;
using Sandbox;

namespace Arenula;

/// <summary>
/// Helper utilities for material loading and application operations on PolygonMesh.
/// Provides centralized methods for material handling in mesh editing tools.
/// </summary>
internal static class MaterialHelper
{
    private const string DefaultMaterialPath = "materials/dev/reflectivity_30.vmat";

    internal static Material LoadMaterial( string materialPath )
    {
        if ( string.IsNullOrEmpty( materialPath ) )
            return null;
        try
        {
            return Material.Load( materialPath );
        }
        catch
        {
            return null;
        }
    }

    internal static Material LoadMaterialOrDefault( string materialPath )
    {
        var material = LoadMaterial( materialPath );
        if ( material != null )
            return material;
        return Material.Load( DefaultMaterialPath );
    }

    internal static bool ApplyMaterialToFace( PolygonMesh mesh, int faceIndex, Material material )
    {
        if ( mesh == null || material == null )
            return false;
        try
        {
            var faceHandle = mesh.FaceHandleFromIndex( faceIndex );
            if ( !faceHandle.IsValid )
                return false;
            mesh.SetFaceMaterial( faceHandle, material );
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ApplyMaterialToFace( PolygonMesh mesh, int faceIndex, string materialPath )
    {
        var material = LoadMaterial( materialPath );
        if ( material == null )
            return false;
        return ApplyMaterialToFace( mesh, faceIndex, material );
    }

    /// <summary>
    /// Set a face's texture projection parameters. Mirrors
    /// <c>PolygonMesh.SetFaceTextureParameters</c> argument-for-argument on purpose:
    /// the axes arrive already packed as Vector4, with the texture offset in the w
    /// components (axisU.w -> offset.x, axisV.w -> offset.y).
    ///
    /// Do not "simplify" this to take Vector3 axes plus a separate Vector2 offset.
    /// Vector2 and Vector3 convert into each other implicitly in s&amp;box, so that shape
    /// lets a misordered call compile and silently write each value into the wrong slot
    /// — which is exactly what happened here between 2026-04-04 and 2026-08-15. Vector3
    /// -> Vector4 is explicit, so this signature rejects the same mistake at compile time.
    /// </summary>
    internal static bool SetTextureParameters( PolygonMesh mesh, int faceIndex,
        Vector4 axisU, Vector4 axisV, Vector2 scale )
    {
        if ( mesh == null )
            return false;
        try
        {
            var faceHandle = mesh.FaceHandleFromIndex( faceIndex );
            if ( !faceHandle.IsValid )
                return false;
            mesh.SetFaceTextureParameters( faceHandle, axisU, axisV, scale );
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static Material GetFaceMaterial( PolygonMesh mesh, int faceIndex )
    {
        if ( mesh == null )
            return null;
        try
        {
            var faceHandle = mesh.FaceHandleFromIndex( faceIndex );
            if ( !faceHandle.IsValid )
                return null;
            return mesh.GetFaceMaterial( faceHandle );
        }
        catch
        {
            return null;
        }
    }

    internal static int GetFaceCount( PolygonMesh mesh )
        => mesh?.FaceHandles?.Count() ?? 0;

    internal static int GetVertexCount( PolygonMesh mesh )
        => mesh?.VertexHandles?.Count() ?? 0;

    internal static int GetEdgeCount( PolygonMesh mesh )
        => mesh?.HalfEdgeHandles?.Count() ?? 0;
}
