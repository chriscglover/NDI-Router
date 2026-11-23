using System.Reflection;
using System.Runtime.InteropServices;
using NdiRouter.Core;
using NdiRouter.Ndi;
using Xunit.Abstractions;

namespace NdiRouter.Tests;

/// <summary>
/// NDI send and receive, which the application never needs but this test does: it publishes test
/// sources and watches a router output the way a downstream receiver would.
/// </summary>
internal static class NdiTestInterop
{
    private const string Library = "Processing.NDI.Lib";
    private static bool _resolver;

    public static void Bind()
    {
        if (_resolver) return;
        var handle = NativeLibrary.Load(NdiNative.LoadedFrom!);
        NativeLibrary.SetDllImportResolver(typeof(NdiTestInterop).Assembly,
            (name, _, _) => name == Library ? handle : IntPtr.Zero);
        _resolver = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrame
    {
        public int xres, yres;
        public int FourCC;
        public int frame_rate_N, frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
        public byte clock_video;
        public byte clock_audio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreate
    {
        public NdiNative.NDIlib_source_t source_to_connect_to;
        public int color_format;
        public int bandwidth;
        public byte allow_video_fields;
        public IntPtr p_ndi_recv_name;
    }

    public const int FourCcBgra = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
    public const int FrameTypeVideo = 1;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr instance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr instance, ref VideoFrame frame);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_recv_create_v3(ref RecvCreate settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_destroy(IntPtr instance);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NDIlib_recv_capture_v3(IntPtr instance, ref VideoFrame video, IntPtr audio, IntPtr metadata, uint timeoutMs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_free_video_v2(IntPtr instance, ref VideoFrame video);
}

/// <summary>An NDI source sending a flat picture of a given size until disposed.</summary>
internal sealed class TestPattern : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _running = true;

    public TestPattern(string name, int width, int height)
    {
        Name = name;
        _thread = new Thread(() => Run(width, height)) { IsBackground = true };
        _thread.Start();
    }

    public string Name { get; }

    private void Run(int width, int height)
    {
        var namePtr = NdiNative.Utf8(Name);
        var settings = new NdiTestInterop.SendCreate { p_ndi_name = namePtr, clock_video = 1 };
        var sender = NdiTestInterop.NDIlib_send_create(ref settings);
        var buffer = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            var frame = new NdiTestInterop.VideoFrame
            {
                xres = width,
                yres = height,
                FourCC = NdiTestInterop.FourCcBgra,
                frame_rate_N = 25,
                frame_rate_D = 1,
                picture_aspect_ratio = 16f / 9f,
                frame_format_type = 1,
                timecode = long.MaxValue,
                p_data = buffer,
                line_stride_in_bytes = width * 4
            };

            while (_running) NdiTestInterop.NDIlib_send_send_video_v2(sender, ref frame); // clocked to 25 fps
        }
        finally
        {
            NdiTestInterop.NDIlib_send_destroy(sender);
            Marshal.FreeHGlobal(buffer);
            NdiNative.Free(namePtr);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
    }
}

public class NdiRoutedVideoTests(ITestOutputHelper output)
{
    [Fact]
    public void A_receiver_on_a_router_output_sees_whichever_input_is_routed()
    {
        var runtime = NdiRuntime.Initialize();
        output.WriteLine($"NDI runtime: {runtime.Status}");
        if (!runtime.IsAvailable) return;
        NdiTestInterop.Bind();

        var id = Guid.NewGuid().ToString("N")[..6];
        using var a = new TestPattern($"RouterTest A {id}", 320, 180);
        using var b = new TestPattern($"RouterTest B {id}", 640, 360);

        var inputs = new[]
        {
            new RouterInput { Label = "A", NdiSource = NdiName.OwnSourceName(a.Name) },
            new RouterInput { Label = "B", NdiSource = NdiName.OwnSourceName(b.Name) }
        };
        var outputName = $"RouterTest Out {id}";
        using var router = new RouterCore(runtime, inputs,
            new[] { new RouterOutput { Label = "Out", NdiName = outputName } }, Array.Empty<RouterPreset>());

        router.Route(0, 0, "test");

        var namePtr = NdiNative.Utf8(NdiName.OwnSourceName(outputName));
        var receiver = IntPtr.Zero;
        try
        {
            var settings = new NdiTestInterop.RecvCreate
            {
                source_to_connect_to = new NdiNative.NDIlib_source_t { p_ndi_name = namePtr },
                color_format = 0,   // BGRX/BGRA
                bandwidth = 100,    // highest
                allow_video_fields = 0
            };
            receiver = NdiTestInterop.NDIlib_recv_create_v3(ref settings);
            Assert.NotEqual(IntPtr.Zero, receiver);

            Assert.True(WaitForWidth(receiver, 320, TimeSpan.FromSeconds(20)), "never received input A through the router output");
            output.WriteLine($"Routed to A: receiving 320x180. Receivers on the output: {router.Connections(0)}");
            Assert.True(router.Connections(0) >= 1);

            router.Route(0, 1, "test");
            Assert.True(WaitForWidth(receiver, 640, TimeSpan.FromSeconds(20)), "the receiver did not follow the route to input B");
            output.WriteLine("Routed to B: receiving 640x360 on the same receiver, without reconnecting it.");
        }
        finally
        {
            if (receiver != IntPtr.Zero) NdiTestInterop.NDIlib_recv_destroy(receiver);
            NdiNative.Free(namePtr);
        }
    }

    private static bool WaitForWidth(IntPtr receiver, int width, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = new NdiTestInterop.VideoFrame();
            var type = NdiTestInterop.NDIlib_recv_capture_v3(receiver, ref frame, IntPtr.Zero, IntPtr.Zero, 500);
            if (type != NdiTestInterop.FrameTypeVideo) continue;

            var seen = frame.xres;
            NdiTestInterop.NDIlib_recv_free_video_v2(receiver, ref frame);
            if (seen == width) return true;
        }

        return false;
    }
}
