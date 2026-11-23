using System.Reflection;
using System.Runtime.InteropServices;

namespace NdiRouter.Ndi;

/// <summary>
/// The parts of the NDI SDK this application uses: discovery (Find) and routing.
///
/// The runtime is not shipped with the application. It is installed by NDI Tools or the NDI
/// runtime installer, which set NDI_RUNTIME_DIR_V6 (or _V5) to its folder, and it is loaded from
/// there at start-up. Signatures follow the SDK headers Processing.NDI.Find.h,
/// Processing.NDI.Routing.h and Processing.NDI.structs.h.
/// </summary>
internal static class NdiNative
{
    private const string Library = "Processing.NDI.Lib";

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static bool _resolverInstalled;

    public static string? LoadedFrom { get; private set; }

    public static string FileName =>
        Environment.Is64BitProcess ? "Processing.NDI.Lib.x64.dll" : "Processing.NDI.Lib.x86.dll";

    /// <summary>Loads the NDI runtime. Returns null on success, or a reason it could not be loaded.</summary>
    public static string? TryLoad()
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero) return null;

            var failures = new List<string>();
            foreach (var path in CandidatePaths())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    _handle = NativeLibrary.Load(path);
                    LoadedFrom = path;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add($"{path}: {ex.Message}");
                }
            }

            if (_handle == IntPtr.Zero)
            {
                return failures.Count > 0
                    ? "The NDI runtime could not be loaded: " + string.Join("; ", failures)
                    : $"The NDI runtime ({FileName}) was not found. Install NDI Tools or the NDI runtime.";
            }

            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, Resolve);
                _resolverInstalled = true;
            }

            return null;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library ? _handle : IntPtr.Zero;

    private static IEnumerable<string> CandidatePaths()
    {
        foreach (var variable in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
        {
            var folder = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(folder)) yield return Path.Combine(folder, FileName);
        }

        yield return Path.Combine(AppContext.BaseDirectory, FileName);
    }

    // ------------------------------------------------------------------ structures

    [StructLayout(LayoutKind.Sequential)]
    public struct NDIlib_source_t
    {
        public IntPtr p_ndi_name;   // "MACHINE (Source)", UTF-8
        public IntPtr p_url_address; // may be null; the SDK resolves the name itself
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NDIlib_find_create_t
    {
        public byte show_local_sources; // C bool
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NDIlib_routing_create_t
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
    }

    // ------------------------------------------------------------------ library

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_initialize();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_version();

    // ------------------------------------------------------------------ find

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_create_v2(ref NDIlib_find_create_t settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_find_destroy(IntPtr instance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_find_wait_for_sources(IntPtr instance, uint timeoutMs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_get_current_sources(IntPtr instance, out uint count);

    // ------------------------------------------------------------------ routing

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_routing_create(ref NDIlib_routing_create_t settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_routing_destroy(IntPtr instance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_routing_change(IntPtr instance, ref NDIlib_source_t source);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_routing_clear(IntPtr instance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NDIlib_routing_get_no_connections(IntPtr instance, uint timeoutMs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_routing_get_source_name(IntPtr instance);

    // ------------------------------------------------------------------ helpers

    /// <summary>A null-terminated UTF-8 copy of a string, or null for an empty one. Free with <see cref="Free"/>.</summary>
    public static IntPtr Utf8(string? value) =>
        string.IsNullOrEmpty(value) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);

    public static void Free(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
    }

    public static string FromUtf8(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
}
