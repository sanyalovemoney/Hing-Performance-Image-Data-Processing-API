using System.IO;

namespace ImageProcessing.Core.Storage;

/// <summary>
/// Filesystem-backed implementation: uploads land under <c>uploads/</c>,
/// results under <c>results/</c>, both relative to <paramref name="root"/>.
/// </summary>
public sealed class TempFileStorage : IFileStorage
{
    private readonly string _root;

    public TempFileStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "uploads"));
        Directory.CreateDirectory(Path.Combine(_root, "results"));
    }

    public bool Exists(string path) => File.Exists(ResolveFullPath(path));

    public string CreateUploadPath(Guid id, string extension) =>
        Path.Combine("uploads", $"{id:N}{NormalizeExtension(extension)}");

    public string CreateResultPath(Guid id, string extension) =>
        Path.Combine("results", $"{id:N}{NormalizeExtension(extension)}");

    public Stream OpenRead(string path)
    {
        var full = ResolveFullPath(path);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    public Stream OpenWrite(string path)
    {
        var full = ResolveFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
    }

    public void Delete(string path)
    {
        try
        {
            var full = ResolveFullPath(path);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (IOException)
        {
            // Best effort — cleanup must not throw.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public void Move(string source, string destination)
    {
        var srcFull = ResolveFullPath(source);
        var dstFull = ResolveFullPath(destination);
        var dir = Path.GetDirectoryName(dstFull);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Move(srcFull, dstFull, overwrite: true);
    }

    public IEnumerable<StorageEntry> List()
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, file);
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            yield return new StorageEntry(relative, modified);
        }
    }

    public string ResolveFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path must not be empty", nameof(path));
        }

        // Traversal-safe: resolve and ensure the result stays under the root.
        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(full, _root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"path '{path}' escapes the storage root");
        }

        return full;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }
}
