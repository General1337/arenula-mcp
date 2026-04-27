using System;
using System.IO;
using System.Linq;
using Editor;
using Sandbox;

namespace Arenula;

internal static class WorkspacePaths
{
    private static string _projectRoot;

    internal static string ProjectRoot
    {
        get
        {
            _projectRoot ??= ResolveProjectRoot();
            return _projectRoot;
        }
    }

    internal static string Combine( params string[] parts )
    {
        if ( string.IsNullOrEmpty( ProjectRoot ) )
            return null;

        var path = ProjectRoot;
        foreach ( var part in parts )
            path = Path.Combine( path, part );
        return path;
    }

    internal static string EnsureDirectory( params string[] parts )
    {
        var path = Combine( parts );
        if ( !string.IsNullOrEmpty( path ) && !Directory.Exists( path ) )
            Directory.CreateDirectory( path );
        return path;
    }

    private static string ResolveProjectRoot()
    {
        try
        {
            var anyAsset = AssetSystem.All.FirstOrDefault();
            if ( anyAsset != null )
            {
                var relative = anyAsset.Path?.Replace( '/', Path.DirectorySeparatorChar );
                if ( !string.IsNullOrEmpty( relative ) && anyAsset.AbsolutePath.EndsWith( relative, StringComparison.OrdinalIgnoreCase ) )
                    return anyAsset.AbsolutePath[..^relative.Length].TrimEnd( Path.DirectorySeparatorChar, '/' );
            }
        }
        catch
        {
        }

        try
        {
            var dir = new DirectoryInfo( AppContext.BaseDirectory );
            while ( dir != null )
            {
                if ( dir.GetFiles( "*.sbproj" ).Any() )
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
        }

        return null;
    }
}
