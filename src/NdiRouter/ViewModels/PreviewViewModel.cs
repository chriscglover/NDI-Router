using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NdiRouter.Ndi;

namespace NdiRouter.Gui.ViewModels;

/// <summary>
/// The preview monitor in the middle of the router panel: the picture, a caption saying what it
/// is, and a status line. Frames arrive on the NDI thread; at most one copy into the bitmap is
/// queued on the UI thread at a time, so a slow UI drops frames rather than falling behind.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IDisposable
{
    private readonly INdiPreview? _preview;
    private int _updateQueued;
    private long _framesAtLastTick;
    private DateTime _lastTick = DateTime.UtcNow;
    private string _fps = string.Empty;
    private bool _disposed;

    public PreviewViewModel(INdiPreview? preview)
    {
        _preview = preview;
        if (_preview is not null) _preview.FrameArrived += OnFrameArrived;
        Message = preview is null ? "No preview: the NDI runtime is not loaded" : "Click a source or destination to preview it";
    }

    public bool IsAvailable => _preview is not null;

    /// <summary>Raised after the bitmap's pixels change, so the view can redraw it.</summary>
    public event Action? PictureUpdated;

    /// <summary>The live picture; null while there is nothing to show.</summary>
    [ObservableProperty] private WriteableBitmap? _picture;

    /// <summary>What is on the monitor, e.g. "OUT 2  Studio Monitor  ◀  IN 3  Graphics".</summary>
    [ObservableProperty] private string _caption = "PREVIEW";

    /// <summary>Size and frame rate, or why there is no picture.</summary>
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Shown across the monitor when there is no picture.</summary>
    [ObservableProperty] private string _message;

    /// <summary>The NDI source the monitor is watching; empty for none.</summary>
    public string SourceName { get; private set; } = string.Empty;

    /// <param name="caption">What is being previewed.</param>
    /// <param name="ndiSource">The NDI source to watch; empty when there is none.</param>
    /// <param name="emptyReason">Shown on the monitor when there is no source.</param>
    public void Show(string caption, string ndiSource, string emptyReason)
    {
        Caption = caption;
        if (_preview is null)
        {
            SourceName = ndiSource;
            return; // the monitor keeps saying the runtime is not loaded
        }

        if (ndiSource.Length == 0) Message = emptyReason;
        if (ndiSource == SourceName) return;

        SourceName = ndiSource;
        _preview.Show(ndiSource);
        Picture = null;
        _fps = string.Empty;
        _framesAtLastTick = 0;
        if (ndiSource.Length > 0) Message = "Connecting...";
        Tick();
    }

    /// <summary>Once a second: frame rate, and "no video" when a source has gone quiet.</summary>
    public void Tick()
    {
        if (_preview is null)
        {
            Status = string.Empty;
            return;
        }

        var now = DateTime.UtcNow;
        var frames = _preview.FramesReceived;
        var seconds = (now - _lastTick).TotalSeconds;
        if (seconds > 0.2 && frames >= _framesAtLastTick)
            _fps = $"{(frames - _framesAtLastTick) / seconds:0} fps";
        _framesAtLastTick = frames;
        _lastTick = now;

        if (SourceName.Length == 0)
        {
            Status = string.Empty;
            return;
        }

        var last = _preview.LastFrameUtc;
        if (last == DateTime.MinValue)
        {
            Status = "Waiting for " + SourceName;
        }
        else if (now - last > TimeSpan.FromSeconds(2))
        {
            Status = "No video from " + SourceName;
            Message = "No video";
            Picture = null;
        }
        else
        {
            var size = Picture is { } p ? $"{p.PixelSize.Width}×{p.PixelSize.Height}" : string.Empty;
            Status = string.Join("  ·  ", new[] { size, _fps, SourceName }.Where(part => part.Length > 0));
        }
    }

    private void OnFrameArrived()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(UpdatePicture, DispatcherPriority.Render);
    }

    private void UpdatePicture()
    {
        Interlocked.Exchange(ref _updateQueued, 0);
        if (_disposed || _preview is null) return;

        var replaced = false;
        _preview.ReadLatest((pixels, width, height) =>
        {
            var bitmap = Picture;
            if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            {
                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                replaced = true;
            }

            using (var buffer = bitmap.Lock())
            {
                var row = width * 4;
                for (var y = 0; y < height; y++)
                    unsafe
                    {
                        pixels.Slice(y * row, row).CopyTo(new Span<byte>((byte*)buffer.Address + (long)y * buffer.RowBytes, row));
                    }
            }

            if (replaced) Picture = bitmap;
        });

        if (Picture is not null && !replaced) PictureUpdated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_preview is null) return;
        _preview.FrameArrived -= OnFrameArrived;
        _preview.Dispose();
    }
}
