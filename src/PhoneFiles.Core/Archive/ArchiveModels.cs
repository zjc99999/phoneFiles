namespace PhoneFiles.Core.Archive;

/// <summary>Progress snapshot for a running archive task.</summary>
public sealed record ArchiveProgress(
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    string CurrentRelative,
    string Phase);

/// <summary>Result of a completed archive task.</summary>
public sealed record ArchiveResult(
    int FilesCopied,
    int FilesFailed,
    int FilesSkipped,
    long BytesCopied,
    string ManifestPath,
    TimeSpan Duration,
    IReadOnlyList<string> Errors);

/// <summary>A single selected node to archive, with its PC-side relative path.</summary>
public sealed record ArchiveSelection(IFileSystemNode Node, string RelativePath);
