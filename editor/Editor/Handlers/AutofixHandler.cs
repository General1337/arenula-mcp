using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arenula;

internal static class AutofixHandler
{
    internal static object Handle( string action, JsonElement args )
    {
        try
        {
            return action switch
            {
                "prefab_fixups" => PrefabFixups( args ),
                "create_sent_pair" => CreateSentPair( args ),
                _ => HandlerBase.Error( $"Unknown action '{action}'", action, "Valid actions: prefab_fixups, create_sent_pair" )
            };
        }
        catch ( Exception ex )
        {
            return HandlerBase.Error( ex.Message, action );
        }
    }

    private static object PrefabFixups( JsonElement args )
    {
        var path = HandlerBase.GetString( args, "path" );
        if ( string.IsNullOrWhiteSpace( path ) )
            return HandlerBase.Error( "Missing required 'path' parameter.", "prefab_fixups" );

        var abs = Path.IsPathRooted( path ) ? path : WorkspacePaths.Combine( path.Replace( '/', Path.DirectorySeparatorChar ) );
        if ( string.IsNullOrEmpty( abs ) || !File.Exists( abs ) )
            return HandlerBase.Error( $"Prefab file not found: '{path}'.", "prefab_fixups" );

        var root = JsonNode.Parse( File.ReadAllText( abs ) )?.AsObject();
        var rootObj = root?["RootObject"]?.AsObject();
        if ( rootObj == null )
            return HandlerBase.Error( "Prefab JSON missing RootObject.", "prefab_fixups" );

        var fixes = new List<string>();
        if ( (rootObj["NetworkMode"]?.GetValue<int>() ?? 0) != 2 )
        {
            rootObj["NetworkMode"] = 2;
            fixes.Add( "Normalized RootObject.NetworkMode to 2." );
        }

        var components = rootObj["Components"]?.AsArray();
        bool hasRigidBody = false;
        bool hasCollider = false;
        foreach ( var comp in components ?? new JsonArray() )
        {
            var obj = comp?.AsObject();
            var typeName = obj?["__type"]?.GetValue<string>() ?? string.Empty;
            hasRigidBody |= typeName == "Sandbox.Rigidbody";
            hasCollider |= typeName.Contains( "Collider", StringComparison.OrdinalIgnoreCase );
            if ( typeName == "Sandbox.ModelRenderer" )
            {
                var model = obj?["Model"]?.GetValue<string>();
                if ( !string.IsNullOrEmpty( model ) && model.Contains( "models/dev/box.vmdl", StringComparison.OrdinalIgnoreCase ) )
                {
                    obj["Model"] = null;
                    fixes.Add( "Cleared broken dev model path on ModelRenderer." );
                }
            }
        }

        if ( components != null && !hasRigidBody )
        {
            components.Add( new JsonObject { ["__type"] = "Sandbox.Rigidbody" } );
            fixes.Add( "Added missing Sandbox.Rigidbody component." );
        }

        if ( components != null && !hasCollider )
        {
            components.Add( new JsonObject { ["__type"] = "Sandbox.BoxCollider" } );
            fixes.Add( "Added missing Sandbox.BoxCollider component." );
        }

        File.WriteAllText( abs, root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
        return HandlerBase.Success( new { path = path.Replace( '\\', '/' ), changed = fixes.Count > 0, fixes } );
    }

    private static object CreateSentPair( JsonElement args )
    {
        var prefabPath = HandlerBase.GetString( args, "prefab_path" );
        if ( string.IsNullOrWhiteSpace( prefabPath ) )
            return HandlerBase.Error( "Missing required 'prefab_path' parameter.", "create_sent_pair" );

        var normalized = prefabPath.Replace( '\\', '/' );
        if ( normalized.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
            normalized = normalized["Assets/".Length..];

        var sentPath = Path.ChangeExtension( normalized, ".sent" )?.Replace( '\\', '/' );
        var absSent = WorkspacePaths.Combine( "Assets", sentPath );
        if ( File.Exists( absSent ) )
            return HandlerBase.Success( new { created = false, path = $"Assets/{sentPath}", message = "Sibling .sent file already exists." } );

        var dir = Path.GetDirectoryName( absSent );
        if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
            Directory.CreateDirectory( dir );

        var title = HandlerBase.GetString( args, "title" ) ?? Path.GetFileNameWithoutExtension( normalized ).Replace( '-', ' ' );
        var description = HandlerBase.GetString( args, "description" ) ?? $"Spawnable entity for {title}.";
        var payload = new { Prefab = normalized, Title = title, Description = description, IncludeCode = false, __references = Array.Empty<string>(), __version = 0 };
        File.WriteAllText( absSent, JsonSerializer.Serialize( payload, new JsonSerializerOptions { WriteIndented = true } ) );

        return HandlerBase.Success( new { created = true, prefab = normalized, path = $"Assets/{sentPath}" } );
    }
}
