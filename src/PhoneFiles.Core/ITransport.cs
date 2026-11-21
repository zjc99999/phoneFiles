using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneFiles.Core;

/// <summary>
/// A node in the device's file hierarchy. This is the ONLY file abstraction
/// used by the rest of the application: it deliberately has NO string path.
/// MTP/WPD exposes opaque object IDs, not DOS paths, so any design that passes
/// paths around will fail in production.
/// </summary>
public interface IFileSystemNode
{
    /// <summary>Opaque transport-specific identifier (WPD object ID). Never parse it.</summary>
    string OpaqueId { get; }

    /// <summary>Human-readable name as shown by the device (WPD_OBJECT_NAME).</summary>
    string DisplayName { get; }

    /// <summary>OpaqueId of the parent node.</summary>
    string? ParentId { get; }

    bool IsDir { get; }

    /// <summary>Size in bytes for files; 0 for folders.</summary>
    ulong Size { get; }

    /// <summary>Last-modified time reported by the device; null if unsupported.</summary>
    DateTimeOffset? MTime { get; }

    /// <summary>Open the node for read-only streaming. Folders must throw.</summary>
    Task<Stream> OpenReadAsync(CancellationToken ct = default);

    /// <summary>Enumerate the direct children of a folder. Files must throw.</summary>
    IAsyncEnumerable<IFileSystemNode> EnumerateAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of a bounded, read-only capability probe on a node. MTP can report a
/// node as visible (exists) yet refuse enumeration or reads (Android 11+ scoped
/// storage). "Visible" must never be equated with "archivable"; the semantic
/// layer calls this probe before promising a directory can be copied to PC.
/// </summary>
public sealed record ProbeResult(
    bool CanEnumerate,
    bool CanRead,
    string? Reason = null)
{
    /// <summary>True only when at least one child was both enumerated and read.</summary>
    public bool Accessible => CanEnumerate && CanRead;

    public static ProbeResult VisibleOnly(string? reason = null) => new(false, false, reason);
    public static ProbeResult EnumerableOnly(string? reason = null) => new(true, false, reason);
    public static ProbeResult Full(string? reason = null) => new(true, true, reason);
}

/// <summary>Transport abstraction. Implementations: MTP/WPD first; ADB is opt-in later.</summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Human-readable transport name, e.g. "mtp" or "adb".</summary>
    string Name { get; }

    /// <summary>List device identifiers visible to this transport (no connection yet).</summary>
    Task<IReadOnlyList<DeviceRef>> ListDevicesAsync(CancellationToken ct = default);

    /// <summary>Connect to a device from <see cref="ListDevicesAsync"/>.</summary>
    Task ConnectAsync(DeviceRef device, CancellationToken ct = default);

    /// <summary>Get the root node of the connected device.</summary>
    Task<IFileSystemNode> GetRootAsync(CancellationToken ct = default);

    /// <summary>Convenience: children of a node.</summary>
    IAsyncEnumerable<IFileSystemNode> EnumerateAsync(IFileSystemNode node, CancellationToken ct = default);

    /// <summary>
    /// Bounded, read-only capability probe: does a real read succeed on the first
    /// readable child? Implementations must cap work (one level, a few bytes) and
    /// must never throw — report the failure in <see cref="ProbeResult.Reason"/>.
    /// The semantic layer uses this to distinguish "visible" from "archivable".
    /// </summary>
    Task<ProbeResult> ProbeAsync(IFileSystemNode node, CancellationToken ct = default);
}

/// <summary>Transport-neutral device reference. Id is opaque; no path semantics.</summary>
public sealed record DeviceRef(string Id, string DisplayName, string? Manufacturer = null, string? Description = null);

/// <summary>Metadata surfaced by the probe for the observation schema.</summary>
public sealed record NodeSnapshot(
    string NodePattern,
    string DisplayName,
    string? ParentId,
    bool IsDir,
    ulong Size,
    DateTimeOffset? MTime,
    string? ContentTypeGuid);

/// <summary>
/// A storage volume on the device. MTP devices can expose several volumes
/// (e.g. internal memory and an SD/TF card), each a top-level functional
/// object. Never assume which one holds "the phone's default storage".
/// </summary>
public sealed record VolumeInfo(
    string DisplayName,
    string OpaqueId,
    string? StorageType,
    string? FunctionalCategory,
    ulong CapacityBytes);
