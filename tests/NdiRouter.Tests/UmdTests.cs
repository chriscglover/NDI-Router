using System.Net;
using System.Net.Sockets;
using NdiRouter.Core;
using NdiRouter.Tsl;
using NdiRouter.Umd;

namespace NdiRouter.Tests;

public class UmdTests
{
    private static UmdContext Context(int? input = 0) => new(
        1,
        new RouterOutput { Label = "MV 2", NdiName = "Router Out 2", Input = input },
        input,
        input is null ? null : new RouterInput { Label = "Camera 1", Name4 = "CAM1", NdiSource = "STUDIO-PC (Camera 1 Main)" });

    [Theory]
    [InlineData("{input.label}", "Camera 1")]
    [InlineData("{input.name4}", "CAM1")]
    [InlineData("{input.name8}", "Camera 1")]
    [InlineData("{output.name4}", "MV 2")]
    [InlineData("{input.machine}", "STUDIO-PC")]
    [InlineData("{input.channel}", "Camera 1 Main")]
    [InlineData("{input.number} > {output.number}", "1 > 2")]
    [InlineData("{output.label}: {input.label}", "MV 2: Camera 1")]
    [InlineData("{addr:000}", "007")]
    public void Templates_resolve(string template, string expected)
    {
        Assert.Equal(expected, UmdLabel.Format(template, Context(), 7));
    }

    [Fact]
    public void Alternatives_and_literals_fall_back_in_order()
    {
        Assert.Equal("NO SOURCE", UmdLabel.Format("{input.label|\"NO SOURCE\"}", Context(input: null), 1));
    }

    [Fact]
    public void Long_labels_are_fitted_to_sixteen_characters_for_tsl_31()
    {
        Assert.Equal("PCAP Replay THEB", UmdLabel.Fit("PCAP Replay THEBEAST:3210", 16, FitMode.Truncate));
        Assert.Equal("THEBEAST:3210", UmdLabel.Fit("PCAP Replay THEBEAST:3210", 16, FitMode.KeepEnd));
        Assert.Equal("PCPRplyTHBS:3210", UmdLabel.Fit("PCAP Replay THEBEAST:3210", 16, FitMode.Squeeze));
    }

    [Fact]
    public void Cleared_outputs_use_the_cleared_template_and_lamp()
    {
        var settings = new TslSettings { ClearedTemplate = "{output.label} OFF", ClearedLamp = TallyColour.Red };
        var display = UmdEngine.Build(0, new RouterOutput { Label = "MON", UmdAddress = 4 }, Array.Empty<RouterInput>(), settings);

        Assert.False(display.Routed);
        Assert.Equal("MON OFF", display.Message.Text);
        Assert.Equal(TallyColour.Red, display.Message.LeftTally);
        Assert.Equal(4, display.Message.Address);
    }

    [Fact]
    public void Tsl_output_sends_each_display_and_follows_route_changes()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Client.ReceiveTimeout = 3000;
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var router = TestRouter.Create(new FakeNdi(), inputs: 4, outputs: 2);
        router.Route(0, 0, "test");
        router.UpdateOutput(1, o => o.UmdEnabled = false);

        using var tsl = new TslOutput(router);
        tsl.Apply(new TslSettings
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
            Version = TslVersion.V31,
            RepeatIntervalMs = 60000 // only changes, during the test
        });

        var first = Receive(listener);
        Assert.Equal(0x80 | 1, first[0]); // address 1 = output 1
        Assert.Equal("Cam 1           ", System.Text.Encoding.ASCII.GetString(first, 2, 16));

        router.Route(0, 2, "test");
        var changed = Receive(listener);
        Assert.Equal("Cam 3           ", System.Text.Encoding.ASCII.GetString(changed, 2, 16));
    }

    private static byte[] Receive(UdpClient client)
    {
        IPEndPoint? from = null;
        return client.Receive(ref from);
    }
}
