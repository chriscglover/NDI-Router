using static NdiRouter.Ndi.NdiNative;

namespace NdiRouter.Ndi;

/// <summary>One router output as the NDI network sees it: a source whose content can be switched.</summary>
public interface INdiOutput : IDisposable
{
    string Name { get; }

    /// <summary>Points this output at an NDI source, by its full "MACHINE (Source)" name.</summary>
    bool Route(string ndiSourceName);

    /// <summary>Takes this output off any source; receivers see no video.</summary>
    bool Clear();

    /// <summary>How many NDI receivers are watching this output right now.</summary>
    int Connections();
}

/// <summary>Makes router outputs. Abstracted so the router logic can be exercised without NDI.</summary>
public interface INdiOutputFactory
{
    INdiOutput CreateOutput(string name, string groups);
}

/// <summary>
/// The NDI runtime, loaded once. When it cannot be loaded the application still runs - routes,
/// SW-P-08 and TSL all work - and outputs are simply not published on the network, which keeps
/// the tool usable for testing a control system on a machine without NDI.
/// </summary>
public sealed class NdiRuntime : INdiOutputFactory
{
    private NdiRuntime(bool available, string status, string version)
    {
        IsAvailable = available;
        Status = status;
        Version = version;
    }

    public bool IsAvailable { get; }
    public string Status { get; }
    public string Version { get; }

    /// <summary>A runtime that publishes nothing - for tests, and for running without NDI on purpose.</summary>
    public static NdiRuntime Disabled(string reason) => new(false, reason, string.Empty);

    public static NdiRuntime Initialize()
    {
        string? error;
        try
        {
            error = TryLoad();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null) return new NdiRuntime(false, error, string.Empty);

        try
        {
            if (!NDIlib_initialize())
                return new NdiRuntime(false, "NDI could not start: this CPU is not supported by the NDI runtime.", string.Empty);

            // The runtime reports e.g. "NDI SDK WIN64 16:38:09 Apr 14 2026 6.3.2.0"; the number on the end is the version.
            var full = FromUtf8(NDIlib_version());
            var number = System.Text.RegularExpressions.Regex.Match(full, @"\d+(\.\d+){1,3}\s*$");
            var version = number.Success ? number.Value.Trim() : full;
            return new NdiRuntime(true, $"{full} ({LoadedFrom})", version);
        }
        catch (Exception ex)
        {
            return new NdiRuntime(false, "NDI could not start: " + ex.Message, string.Empty);
        }
    }

    public INdiOutput CreateOutput(string name, string groups) =>
        IsAvailable ? new NdiRoutingOutput(name, groups) : new OfflineOutput(name);

    /// <summary>A preview monitor, or null without an NDI runtime.</summary>
    public INdiPreview? CreatePreview(string receiverName) => IsAvailable ? new NdiPreview(receiverName) : null;
}

/// <summary>An NDI routing instance: an advertised source that forwards receivers to another source.</summary>
internal sealed class NdiRoutingOutput : INdiOutput
{
    private readonly object _gate = new();
    private IntPtr _instance;

    public NdiRoutingOutput(string name, string groups)
    {
        Name = name;

        var namePtr = Utf8(name);
        var groupsPtr = Utf8(string.IsNullOrWhiteSpace(groups) ? null : groups.Trim());
        try
        {
            var settings = new NDIlib_routing_create_t { p_ndi_name = namePtr, p_groups = groupsPtr };
            _instance = NDIlib_routing_create(ref settings);
        }
        finally
        {
            Free(namePtr);
            Free(groupsPtr);
        }

        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException($"NDI refused to create the routing output \"{name}\".");
    }

    public string Name { get; }

    public bool Route(string ndiSourceName)
    {
        lock (_gate)
        {
            if (_instance == IntPtr.Zero) return false;

            var namePtr = Utf8(ndiSourceName);
            try
            {
                // No URL: the SDK finds the source by name, and keeps looking if it is not on the
                // network yet, so a route to a source that is switched off comes up on its own.
                var source = new NDIlib_source_t { p_ndi_name = namePtr, p_url_address = IntPtr.Zero };
                return NDIlib_routing_change(_instance, ref source);
            }
            finally
            {
                Free(namePtr);
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            return _instance != IntPtr.Zero && NDIlib_routing_clear(_instance);
        }
    }

    public int Connections()
    {
        lock (_gate)
        {
            return _instance == IntPtr.Zero ? 0 : Math.Max(0, NDIlib_routing_get_no_connections(_instance, 0));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_instance == IntPtr.Zero) return;
            NDIlib_routing_destroy(_instance);
            _instance = IntPtr.Zero;
        }
    }
}

/// <summary>Stands in for an output when there is no NDI runtime.</summary>
internal sealed class OfflineOutput(string name) : INdiOutput
{
    public string Name { get; } = name;
    public bool Route(string ndiSourceName) => true;
    public bool Clear() => true;
    public int Connections() => 0;
    public void Dispose() { }
}
