using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PhoneFiles.Core;
using Vanara.PInvoke;
using static Vanara.PInvoke.PortableDeviceApi;

namespace PhoneFiles.Transport.Mtp;

/// <summary>
/// MTP/WPD transport. All file identity flows through opaque WPD object IDs;
/// there is deliberately no string path anywhere in this implementation.
/// Read-only by design: OpenRead uses STGM_READ, and no delete/write methods are exposed.
/// </summary>
public sealed class MtpTransport : ITransport
{
    public string Name => "mtp";

    private IPortableDevice? _device;
    private IPortableDeviceContent? _content;
    private IPortableDeviceProperties? _properties;
    private bool _disposed;

    public Task<IReadOnlyList<DeviceRef>> ListDevicesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var mgr = (IPortableDeviceManager)new PortableDeviceManager();
        try
        {
            var ids = mgr.GetDevices(true);
            var result = new List<DeviceRef>(ids.Length);
            foreach (var id in ids)
            {
                string friendly; string manufacturer; string description;
                try { friendly = mgr.GetDeviceFriendlyName(id); } catch { friendly = id; }
                try { manufacturer = mgr.GetDeviceManufacturer(id); } catch { manufacturer = string.Empty; }
                try { description = mgr.GetDeviceDescription(id); } catch { description = string.Empty; }
                result.Add(new DeviceRef(id, friendly, manufacturer, description));
            }
            return Task.FromResult<IReadOnlyList<DeviceRef>>(result);
        }
        finally { Release(mgr); }
    }

    public Task ConnectAsync(DeviceRef device, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var deviceObj = (IPortableDevice)new PortableDeviceFTM();

        // WPD requires client identification on Open, otherwise some devices refuse.
        IPortableDeviceValues clientInfo = (IPortableDeviceValues)new PortableDeviceValues();
        try
        {
            clientInfo.SetStringValue(WPD_CLIENT_NAME, "PhoneFiles Spike Probe");
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MAJOR_VERSION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MINOR_VERSION, 1);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_REVISION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_DESIRED_ACCESS, unchecked((uint)0x80000000)); // GENERIC_READ
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_SHARE_MODE, 1); // FILE_SHARE_READ

            deviceObj.Open(device.Id, clientInfo);
        }
        finally { Release(clientInfo); }

        _content = deviceObj.Content();
        _properties = _content.Properties();
        _device = deviceObj;
        return Task.CompletedTask;
    }

    public Task<IFileSystemNode> GetRootAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.FromResult<IFileSystemNode>(new MtpNode(this, WPD_DEVICE_OBJECT_ID, "Device root", null, true, 0, null, null));
    }

    /// <summary>
    /// Discover top-level storage volumes. We read WPD_STORAGE_TYPE (VT_UI4)
    /// to classify each volume conservatively instead of trusting display names.
    /// Returns every root child; callers decide what to observe.
    /// </summary>
    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var root = await GetRootAsync(ct);
        var result = new List<VolumeInfo>();
        await foreach (var child in EnumerateAsync(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!child.IsDir) continue;
            var mtp = (MtpNode)child;
            string? storageType = null;
            string? category = null;
            ulong capacity = 0;
            if (mtp.ContentTypeGuid == WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT.ToString("D"))
            {
                // Functional object: read its category + storage properties if present.
                var keys = (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
                IPortableDeviceValues? values = null;
                try
                {
                    keys.Add(WPD_FUNCTIONAL_OBJECT_CATEGORY);
                    keys.Add(WPD_STORAGE_TYPE);
                    keys.Add(WPD_STORAGE_CAPACITY);
                    values = _properties!.GetValues(mtp.OpaqueId, keys);
                    category = TryGetGuid(values, WPD_FUNCTIONAL_OBJECT_CATEGORY)?.ToString("D");
                    storageType = MapStorageType(TryGetUInt(values, WPD_STORAGE_TYPE));
                    capacity = TryGetULong(values, WPD_STORAGE_CAPACITY) ?? 0;
                }
                catch (Exception) { /* best effort: keep nulls */ }
                finally { if (values is not null) Release(values); Release(keys); }
            }
            result.Add(new VolumeInfo(mtp.DisplayName, mtp.OpaqueId, storageType, category, capacity));
        }
        return result;
    }

    private static string? MapStorageType(uint? raw) => raw switch
    {
        null => null,
        0 => "UNDEFINED",
        1 => "FIXED_ROM",
        2 => "REMOVABLE_ROM",
        3 => "FIXED_RAM",
        4 => "REMOVABLE_RAM",
        _ => "UNDEFINED"
    };

    /// <summary>
    /// Bounded read-only probe. Enumerates up to <paramref name="maxNodes"/> children
    /// and tries to open+read a few bytes from the first file found. Never throws:
    /// any COM/IO failure is reported as a restricted result with a reason string.
    /// This is how we detect Android 11+ "visible but not readable" directories.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(IFileSystemNode node, CancellationToken ct = default)
        => ProbeAsync(node, ct, maxNodes: 16);

    /// <summary>Explicit overload with a configurable child cap (for tests/tuning).</summary>
    public async Task<ProbeResult> ProbeAsync(IFileSystemNode node, CancellationToken ct, int maxNodes)
    {
        if (!node.IsDir || string.IsNullOrEmpty(node.OpaqueId))
            return ProbeResult.VisibleOnly("not a folder");

        bool enumerated = false;
        int seen = 0;
        // Sample several files, not just the first: on real devices the first
        // child is often a zero-byte hidden marker (.escheck.tmp, ._config).
        var fileSamples = new List<IFileSystemNode>();
        const int sampleFiles = 4;
        try
        {
            await foreach (var child in EnumerateAsync(node, ct))
            {
                enumerated = true;
                seen++;
                if (!child.IsDir && fileSamples.Count < sampleFiles) fileSamples.Add(child);
                if (seen >= maxNodes) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProbeResult.VisibleOnly($"enumerate failed: {ex.GetType().Name}");
        }
        if (!enumerated)
            return ProbeResult.VisibleOnly("no children or enumeration blocked");
        if (fileSamples.Count == 0)
            return ProbeResult.EnumerableOnly("no file in first nodes");

        // A directory is readable if ANY sampled file can be opened and read.
        // Zero bytes read is a legal empty file — opening the stream succeeded,
        // so the file (and hence the directory) is accessible. Only an actual
        // COM/IO failure means "visible but not readable".
        string? lastReason = null;
        foreach (var file in fileSamples)
        {
            try
            {
                await using var stream = await file.OpenReadAsync(ct);
                var buffer = new byte[1];
                await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
                return ProbeResult.Full();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastReason = $"read failed: {ex.GetType().Name}";
            }
        }
        return ProbeResult.EnumerableOnly(lastReason ?? "all sampled files unreadable");
    }

    public async IAsyncEnumerable<IFileSystemNode> EnumerateAsync(IFileSystemNode node, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureConnected();
        if (!node.IsDir || string.IsNullOrEmpty(node.OpaqueId))
            throw new InvalidOperationException("Only folder nodes with an opaque id can be enumerated.");
        // OpaqueId is on the interface — no need to downcast to MtpNode. This lets
        // lightweight volume-root nodes be enumerated too.
        var enumerator = _content!.EnumObjects(0, node.OpaqueId, null);
        foreach (var id in enumerator.Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            yield return await BuildNodeAsync(id).ConfigureAwait(false);
        }
        Release(enumerator);
    }

    private Task<MtpNode> BuildNodeAsync(string id)
    {
        var keys = (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
        IPortableDeviceValues? values = null;
        try
        {
            keys.Add(WPD_OBJECT_NAME);
            keys.Add(WPD_OBJECT_PARENT_ID);
            keys.Add(WPD_OBJECT_SIZE);
            keys.Add(WPD_OBJECT_CONTENT_TYPE);
            keys.Add(WPD_OBJECT_DATE_MODIFIED);

            values = _properties!.GetValues(id, keys);
            var name = TryGetString(values, WPD_OBJECT_NAME) ?? id;
            var parent = TryGetString(values, WPD_OBJECT_PARENT_ID);
            var size = TryGetULong(values, WPD_OBJECT_SIZE) ?? 0;
            var type = TryGetGuid(values, WPD_OBJECT_CONTENT_TYPE);
            var isDir = type == WPD_CONTENT_TYPE_FOLDER || type == WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT;
            var mtime = TryGetFileTime(values, WPD_OBJECT_DATE_MODIFIED);
            return Task.FromResult(new MtpNode(this, id, name, parent, isDir, size, mtime, type?.ToString("D")));
        }
        finally
        {
            if (values is not null) Release(values);
            Release(keys);
        }
    }

    internal Task<Stream> OpenReadAsync(string objectId, CancellationToken ct)
    {
        EnsureConnected();
        var resources = _content!.Transfer();
        var keys = (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
        try
        {
            keys.Add(WPD_RESOURCE_DEFAULT);
            var stream = resources.GetStream(objectId, WPD_RESOURCE_DEFAULT, STGM.STGM_READ, out _);
            return Task.FromResult<Stream>(new ComStreamWrapper(stream));
        }
        finally { Release(keys); Release(resources); }
    }

    private static string? TryGetString(IPortableDeviceValues values, Ole32.PROPERTYKEY key)
    {
        try { return values.GetStringValue(key); }
        catch (COMException) { return null; }
    }

    private static ulong? TryGetULong(IPortableDeviceValues values, Ole32.PROPERTYKEY key)
    {
        try { return values.GetUnsignedLargeIntegerValue(key); }
        catch (COMException) { return null; }
    }

    private static Guid? TryGetGuid(IPortableDeviceValues values, Ole32.PROPERTYKEY key)
    {
        try { return values.GetGuidValue(key); }
        catch (COMException) { return null; }
    }

    private static uint? TryGetUInt(IPortableDeviceValues values, Ole32.PROPERTYKEY key)
    {
        try { return values.GetUnsignedIntegerValue(key); }
        catch (Exception) { return null; }
    }

    private static DateTimeOffset? TryGetFileTime(IPortableDeviceValues values, Ole32.PROPERTYKEY key)
    {
        try
        {
            var pv = values.GetValue(key);
            if (pv.IsNullOrEmpty) return null;
            var ft = pv.filetime;
            if (ft.dwHighDateTime == 0 && ft.dwLowDateTime == 0) return null;
            long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (Exception) { return null; }
    }

    private static void Release(object comObject)
    {
        try { Marshal.ReleaseComObject(comObject); } catch { /* best effort */ }
    }

    private void EnsureConnected()
    {
        if (_device is null || _content is null || _properties is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        if (_disposed) throw new ObjectDisposedException(nameof(MtpTransport));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_device is not null) { try { _device.Close(); } catch { } Release(_device); }
        if (_content is not null) Release(_content);
        if (_properties is not null) Release(_properties);
        _device = null; _content = null; _properties = null;
        return ValueTask.CompletedTask;
    }

    private sealed class MtpNode : IFileSystemNode
    {
        private readonly MtpTransport _transport;
        public MtpNode(MtpTransport transport, string opaqueId, string displayName, string? parentId,
            bool isDir, ulong size, DateTimeOffset? mtime, string? contentTypeGuid)
        {
            _transport = transport;
            OpaqueId = opaqueId;
            DisplayName = displayName;
            ParentId = parentId;
            IsDir = isDir;
            Size = size;
            MTime = mtime;
            ContentTypeGuid = contentTypeGuid;
        }
        public string OpaqueId { get; }
        public string DisplayName { get; }
        public string? ParentId { get; }
        public bool IsDir { get; }
        public ulong Size { get; }
        public DateTimeOffset? MTime { get; }
        public string? ContentTypeGuid { get; }
        public Task<Stream> OpenReadAsync(CancellationToken ct = default)
            => IsDir ? throw new InvalidOperationException("Cannot open a folder for reading.")
                     : _transport.OpenReadAsync(OpaqueId, ct);
        public IAsyncEnumerable<IFileSystemNode> EnumerateAsync(CancellationToken ct = default)
            => _transport.EnumerateAsync(this, ct);
        public override string ToString() => $"{(IsDir ? "[D]" : "[F]")} {DisplayName} ({Size} bytes) {OpaqueId}";
    }
}
