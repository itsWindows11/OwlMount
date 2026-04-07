using NfsSharp;
using NfsSharp.Protocol;
using OwlCore.Storage;

namespace OwlMount.WinFspHost.Providers.Nfs;

internal static class NfsHelpers
{
    /// <summary>Combines a parent NFS path with a child name using forward-slash separators.</summary>
    public static string CombinePath(string parent, string child)
    {
        var trimmed = parent.TrimEnd('/');
        return trimmed.Length == 0 ? "/" + child : trimmed + "/" + child;
    }

    /// <summary>Returns the parent path of <paramref name="path"/>, or <c>null</c> if it is the root.</summary>
    public static string? GetParentPath(string path)
    {
        if (path is "/" || path.Length == 0)
            return null;

        var normalized = path.TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');

        if (lastSlash <= 0)
            return "/";

        return normalized[..lastSlash];
    }

    /// <summary>
    /// Resolves an NFS path to an <see cref="IStorable"/> by inspecting the item's attributes.
    /// Returns <c>null</c> if the path does not exist on the server.
    /// </summary>
    internal static async Task<IStorable?> GetStorableAsync(
        NfsClient client, string path, CancellationToken cancellationToken = default)
    {
        if (path == "/")
            return new NfsFolder(client, "/");

        try
        {
            var attrs = await client.GetAttrAsync(path, cancellationToken);

            return attrs.Type switch
            {
                NfsFileType.Directory   => new NfsFolder(client, path, attrs),
                NfsFileType.Regular     => new NfsFile(client, path, attrs),
                NfsFileType.SymbolicLink => new NfsFile(client, path, attrs),
                _ => throw new NotSupportedException(
                    $"NFS file type '{attrs.Type}' is not supported.")
            };
        }
        catch (NfsException ex) when (ex.Status == NfsStatus.NoEnt)
        {
            return null;
        }
    }
}
