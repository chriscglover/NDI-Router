using System.Runtime.InteropServices;
using static NdiRouter.Ndi.NdiNative;

namespace NdiRouter.Ndi;

/// <summary>
/// Keeps a live list of the NDI sources on the network, on a background thread, and reports
/// each change. The list is plain full names - "MACHINE (Source)" - which is all routing needs.
/// </summary>
public sealed class NdiFinder : IDisposable
{
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private IReadOnlyList<string> _sources = Array.Empty<string>();

    /// <summary>Raised on the finder thread whenever the set of sources changes.</summary>
    public event Action<IReadOnlyList<string>>? SourcesChanged;

    public IReadOnlyList<string> Sources
    {
        get { lock (_gate) return _sources; }
    }

    public void Start(bool showLocalSources, string groups, string extraIps)
    {
        Stop();

        _running = true;
        _thread = new Thread(() => Run(showLocalSources, groups, extraIps))
        {
            IsBackground = true,
            Name = "NDI finder"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(3000);
        _thread = null;
    }

    private void Run(bool showLocalSources, string groups, string extraIps)
    {
        var groupsPtr = Utf8(string.IsNullOrWhiteSpace(groups) ? null : groups.Trim());
        var extraPtr = Utf8(string.IsNullOrWhiteSpace(extraIps) ? null : extraIps.Trim());
        var instance = IntPtr.Zero;

        try
        {
            var settings = new NDIlib_find_create_t
            {
                show_local_sources = (byte)(showLocalSources ? 1 : 0),
                p_groups = groupsPtr,
                p_extra_ips = extraPtr
            };
            instance = NDIlib_find_create_v2(ref settings);
            if (instance == IntPtr.Zero) return;

            while (_running)
            {
                // Waits up to a second for a change. The list is re-read regardless, which is cheap
                // and means a missed change notification can never leave the list stale.
                NDIlib_find_wait_for_sources(instance, 1000);
                if (!_running) break;

                var list = ReadSources(instance);
                bool changed;
                lock (_gate)
                {
                    changed = !list.SequenceEqual(_sources, StringComparer.Ordinal);
                    if (changed) _sources = list;
                }

                if (changed) SourcesChanged?.Invoke(list);
            }
        }
        finally
        {
            if (instance != IntPtr.Zero) NDIlib_find_destroy(instance);
            Free(groupsPtr);
            Free(extraPtr);
        }
    }

    private static List<string> ReadSources(IntPtr instance)
    {
        var array = NDIlib_find_get_current_sources(instance, out var count);
        var list = new List<string>((int)count);
        var size = Marshal.SizeOf<NDIlib_source_t>();

        for (var i = 0; i < count; i++)
        {
            var source = Marshal.PtrToStructure<NDIlib_source_t>(array + i * size);
            var name = FromUtf8(source.p_ndi_name);
            if (name.Length > 0) list.Add(name);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public void Dispose() => Stop();
}

/// <summary>Splits an NDI source name, "MACHINE (Source)", into its two parts.</summary>
public static class NdiName
{
    public static string Machine(string fullName)
    {
        var open = fullName.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? fullName[..open] : fullName;
    }

    public static string Channel(string fullName)
    {
        var open = fullName.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !fullName.EndsWith(')')) return fullName;
        return fullName[(open + 2)..^1];
    }

    /// <summary>
    /// The full name an output of this router will appear under. NDI publishes a source as the
    /// computer name in upper case followed by the source name in brackets.
    /// </summary>
    public static string OwnSourceName(string outputName) =>
        $"{Environment.MachineName.ToUpperInvariant()} ({outputName})";
}
