using NdiRouter.Ndi;
using Xunit.Abstractions;

namespace NdiRouter.Tests;

/// <summary>
/// Exercises the real NDI runtime. These do nothing on a machine without it, so the rest of the
/// suite still runs anywhere.
/// </summary>
public class NdiIntegrationTests(ITestOutputHelper output)
{
    private static readonly Lazy<NdiRuntime> Runtime = new(NdiRuntime.Initialize);

    [Fact]
    public void A_routing_output_is_published_on_the_network_and_can_be_routed()
    {
        var runtime = Runtime.Value;
        output.WriteLine($"NDI runtime: {runtime.Status}");
        if (!runtime.IsAvailable) return; // no NDI on this machine

        var name = $"NdiRouter test {Guid.NewGuid():N}"[..28];
        using var routed = runtime.CreateOutput(name, string.Empty);
        using var finder = new NdiFinder();

        var seen = new ManualResetEventSlim();
        finder.SourcesChanged += sources =>
        {
            if (sources.Contains(NdiName.OwnSourceName(name), StringComparer.OrdinalIgnoreCase)) seen.Set();
        };
        finder.Start(showLocalSources: true, groups: string.Empty, extraIps: string.Empty);

        Assert.True(seen.Wait(TimeSpan.FromSeconds(15)),
            $"\"{NdiName.OwnSourceName(name)}\" never appeared. Found: {string.Join(", ", finder.Sources)}");

        // Routing to a source by name is accepted even before that source exists - the SDK keeps looking.
        Assert.True(routed.Route("NOWHERE (Not a real source)"));
        Assert.True(routed.Clear());
        Assert.Equal(0, routed.Connections());
        output.WriteLine($"Published and found: {NdiName.OwnSourceName(name)}; {finder.Sources.Count} source(s) visible.");
    }
}
