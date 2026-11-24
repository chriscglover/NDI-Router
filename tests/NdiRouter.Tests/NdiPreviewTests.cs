using NdiRouter.Ndi;
using Xunit.Abstractions;

namespace NdiRouter.Tests;

public class NdiPreviewTests(ITestOutputHelper output)
{
    [Fact]
    public void The_preview_shows_a_source_and_follows_a_change_of_source()
    {
        var runtime = NdiRuntime.Initialize();
        output.WriteLine($"NDI runtime: {runtime.Status}");
        if (!runtime.IsAvailable) return;
        NdiTestInterop.Bind();

        var id = Guid.NewGuid().ToString("N")[..6];
        using var red = new TestPattern($"PreviewTest Red {id}", 640, 360, r: 220, g: 30, b: 30);
        using var blue = new TestPattern($"PreviewTest Blue {id}", 640, 360, r: 30, g: 30, b: 220);
        using var preview = runtime.CreatePreview($"PreviewTest Rx {id}")!;

        preview.Show(NdiName.OwnSourceName(red.Name));
        var seen = WaitForCentre(preview, c => c.R > 150 && c.B < 100, TimeSpan.FromSeconds(20));
        Assert.True(seen is not null, "the preview never showed the red source");
        output.WriteLine($"Red: {seen}, {preview.FramesReceived} frame(s)");

        preview.Show(NdiName.OwnSourceName(blue.Name));
        Assert.Equal(0, preview.FramesReceived); // nothing kept from the old source
        seen = WaitForCentre(preview, c => c.B > 150 && c.R < 100, TimeSpan.FromSeconds(20));
        Assert.True(seen is not null, "the preview did not follow the switch to the blue source");
        output.WriteLine($"Blue: {seen}, {preview.FramesReceived} frame(s)");

        preview.Show(null);
        Assert.Equal(string.Empty, preview.Source);
    }

    private sealed record Centre(int Width, int Height, byte R, byte G, byte B);

    private static Centre? WaitForCentre(INdiPreview preview, Func<Centre, bool> wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Centre? centre = null;
            preview.ReadLatest((pixels, width, height) =>
            {
                var at = ((height / 2) * width + width / 2) * 4;
                centre = new Centre(width, height, pixels[at + 2], pixels[at + 1], pixels[at]);
            });
            if (centre is not null && wanted(centre)) return centre;
            Thread.Sleep(40);
        }

        return null;
    }
}
