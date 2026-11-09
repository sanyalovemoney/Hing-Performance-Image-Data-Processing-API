namespace ImageProcessing.Core.Storage;

/// <summary>
/// Abstraction over the on-disk file store used for uploads and results.
/// Paths are storage-relative; implementations resolve them against a root.
/// </summary>
public interface IFileStorage
{
    bool Exists(string path);

    string CreateUploadPath(Guid id, string extension);

    string CreateResultPath(Guid id, string extension);

    Stream OpenRead(string path);

    Stream OpenWrite(string path);

    void Delete(string path);

    void Move(string source, string destination);

    IEnumerable<StorageEntry> List();

    string ResolveFullPath(string path);
}

/// <summary>A single file tracked by the store (used by TTL cleanup / orphan sweep).</summary>
public sealed record StorageEntry(string RelativePath, DateTimeOffset ModifiedUtc);
