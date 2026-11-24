using static NdiRouter.Ndi.NdiNative;

namespace NdiRouter.Ndi;

/// <summary>Receives a copy of the latest preview picture: tightly packed 32-bit BGRX/BGRA rows.</summary>
public delegate void PreviewFrameReader(ReadOnlySpan<byte> bgra, int width, int height);

/// <summary>A picture-in-a-box monitor for one NDI source at a time.</summary>
public interface INdiPreview : IDisposable
{
    /// <summary>The source being watched, or empty for none.</summary>
    string Source { get; }

    /// <summary>Video frames received since the source was last changed.</summary>
    long FramesReceived { get; }

    /// <summary>When the last video frame arrived (UTC), or <see cref="DateTime.MinValue"/> for none since the source changed.</summary>
    DateTime LastFrameUtc { get; }

    /// <summary>Raised on the receive thread whenever a new picture is ready to read.</summary>
    event Action? FrameArrived;

    /// <summary>Watches a source by its full "MACHINE (Source)" name; null or empty stops watching.</summary>
    void Show(string? ndiSourceName);

    /// <summary>Hands the newest picture to <paramref name="reader"/> if one has arrived since the last read.</summary>
    bool ReadLatest(PreviewFrameReader reader);
}

/// <summary>
/// An NDI receiver on the sender's low-bandwidth (proxy) stream, so watching a source costs a
/// fraction of a full-resolution connection. It runs on its own thread; the receiver is created,
/// re-pointed and captured from only there.
/// </summary>
internal sealed class NdiPreview : INdiPreview
{
    private static readonly int FourCcBgra = Four('B', 'G', 'R', 'A');
    private static readonly int FourCcBgrx = Four('B', 'G', 'R', 'X');

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly string _receiverName;
    private volatile bool _running = true;

    private string _source = string.Empty;
    private bool _sourceChanged;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private bool _fresh;
    private long _frames;
    private DateTime _lastFrame = DateTime.MinValue;

    public NdiPreview(string receiverName)
    {
        _receiverName = receiverName;
        _thread = new Thread(Run) { IsBackground = true, Name = "NDI preview" };
        _thread.Start();
    }

    public event Action? FrameArrived;

    /// <summary>Why the receiver could not run, if it could not.</summary>
    public string? Error { get; private set; }

    public string Source { get { lock (_gate) return _source; } }
    public long FramesReceived { get { lock (_gate) return _frames; } }
    public DateTime LastFrameUtc { get { lock (_gate) return _lastFrame; } }

    public void Show(string? ndiSourceName)
    {
        var source = ndiSourceName?.Trim() ?? string.Empty;
        lock (_gate)
        {
            if (string.Equals(source, _source, StringComparison.Ordinal)) return;
            _source = source;
            _sourceChanged = true;
            _fresh = false;
            _width = _height = 0;
            _frames = 0;
            _lastFrame = DateTime.MinValue;
        }

        _wake.Set();
    }

    public bool ReadLatest(PreviewFrameReader reader)
    {
        lock (_gate)
        {
            if (!_fresh || _width == 0) return false;
            _fresh = false;
            reader(_pixels.AsSpan(0, _width * _height * 4), _width, _height);
            return true;
        }
    }

    private void Run()
    {
        var namePtr = Utf8(_receiverName);
        var receiver = IntPtr.Zero;
        try
        {
            var settings = new NDIlib_recv_create_v3_t
            {
                color_format = RecvColorFormatBgrxBgra,
                bandwidth = RecvBandwidthLowest,
                allow_video_fields = 0,
                p_ndi_recv_name = namePtr
            };
            receiver = NDIlib_recv_create_v3(ref settings);
            if (receiver == IntPtr.Zero)
            {
                Error = "NDI refused to create the preview receiver.";
                return;
            }

            var connected = string.Empty;
            while (_running)
            {
                string? wanted = null;
                lock (_gate)
                {
                    if (_sourceChanged)
                    {
                        wanted = _source;
                        _sourceChanged = false;
                    }
                }

                if (wanted is not null)
                {
                    Connect(receiver, wanted);
                    connected = wanted;
                }

                if (connected.Length == 0)
                {
                    _wake.WaitOne(500);
                    continue;
                }

                var frame = new NDIlib_video_frame_v2_t();
                var type = NDIlib_recv_capture_v3(receiver, ref frame, IntPtr.Zero, IntPtr.Zero, 100);
                if (type != FrameTypeVideo) continue;

                bool stored;
                try
                {
                    stored = Store(ref frame);
                }
                finally
                {
                    NDIlib_recv_free_video_v2(receiver, ref frame);
                }

                if (stored) FrameArrived?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Error = "The NDI preview stopped: " + ex.Message;
        }
        finally
        {
            if (receiver != IntPtr.Zero) NDIlib_recv_destroy(receiver);
            Free(namePtr);
        }
    }

    private static void Connect(IntPtr receiver, string source)
    {
        if (source.Length == 0)
        {
            NDIlib_recv_disconnect(receiver, IntPtr.Zero);
            return;
        }

        var namePtr = Utf8(source);
        try
        {
            // By name only, as the router does: the SDK finds the source, and keeps looking for one that is off.
            var target = new NDIlib_source_t { p_ndi_name = namePtr, p_url_address = IntPtr.Zero };
            NDIlib_recv_connect(receiver, ref target);
        }
        finally
        {
            Free(namePtr);
        }
    }

    /// <summary>Copies a frame in as the latest picture. False if it is not one to show.</summary>
    private unsafe bool Store(ref NDIlib_video_frame_v2_t frame)
    {
        if (frame.p_data == IntPtr.Zero || frame.xres <= 0 || frame.yres <= 0) return false;
        if (frame.FourCC != FourCcBgra && frame.FourCC != FourCcBgrx) return false;

        var width = frame.xres;
        var height = frame.yres;
        var row = width * 4;
        var stride = frame.line_stride_in_bytes > 0 ? frame.line_stride_in_bytes : row;

        lock (_gate)
        {
            if (_sourceChanged) return false; // captured from the source being left

            if (_pixels.Length < row * height) _pixels = new byte[row * height];
            var from = (byte*)frame.p_data;
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>(from + (long)y * stride, row).CopyTo(_pixels.AsSpan(y * row, row));

            _width = width;
            _height = height;
            _fresh = true;
            _frames++;
            _lastFrame = DateTime.UtcNow;
            return true;
        }
    }

    private static int Four(char a, char b, char c, char d) => a | (b << 8) | (c << 16) | (d << 24);

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
    }
}
