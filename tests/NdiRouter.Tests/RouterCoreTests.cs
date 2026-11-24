using NdiRouter.Core;
using NdiRouter.Ndi;

namespace NdiRouter.Tests;

/// <summary>Records what the router asked NDI to do, instead of doing it.</summary>
internal sealed class FakeNdi : INdiOutputFactory
{
    public List<FakeOutput> Created { get; } = new();

    public INdiOutput CreateOutput(string name, string groups)
    {
        var output = new FakeOutput(name, groups);
        Created.Add(output);
        return output;
    }

    public FakeOutput Live(string name) => Created.Last(o => o.Name == name && !o.Disposed);
}

internal sealed class FakeOutput(string name, string groups) : INdiOutput
{
    public string Name { get; } = name;
    public string Groups { get; } = groups;
    public string? Source { get; private set; }
    public bool Disposed { get; private set; }
    public int Receivers { get; set; }

    public bool Route(string ndiSourceName) { Source = ndiSourceName; return true; }
    public bool Clear() { Source = null; return true; }
    public int Connections() => Receivers;
    public void Dispose() => Disposed = true;
}

internal static class TestRouter
{
    public static RouterCore Create(FakeNdi ndi, int inputs = 4, int outputs = 3, int presets = 2)
    {
        var ins = Enumerable.Range(0, inputs).Select(i => new RouterInput
        {
            Label = $"Cam {i + 1}",
            NdiSource = $"HOST (Cam {i + 1})"
        });
        var outs = Enumerable.Range(0, outputs).Select(RouterDefaults.Output);
        var pres = Enumerable.Range(0, presets).Select(RouterDefaults.Preset);
        return new RouterCore(ndi, ins, outs, pres);
    }
}

public class RouterCoreTests
{
    [Fact]
    public void Outputs_are_published_and_start_cleared()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);

        Assert.Equal(new[] { "Router Out 1", "Router Out 2", "Router Out 3" }, ndi.Created.Select(o => o.Name));
        Assert.All(ndi.Created, o => Assert.Null(o.Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Input 4")]
    [InlineData("8")]
    [InlineData("Cam 4")]
    public void Assigning_a_source_labels_the_input_with_the_sources_name(string label)
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        router.UpdateInput(3, i => i.Label = label);

        router.AssignSource(3, "THEBEAST (NVIDIA GeForce RTX 2070 SUPER 1)");

        Assert.Equal("NVIDIA GeForce RTX 2070 SUPER 1", router.GetInput(3).Label);
        Assert.Equal("THEBEAST (NVIDIA GeForce RTX 2070 SUPER 1)", router.GetInput(3).NdiSource);
    }

    [Fact]
    public void A_typed_label_stays_until_the_source_is_changed_or_unassigned()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);

        router.AssignSource(3, "THEBEAST (DistroAV NDI Output (Back Door))");
        Assert.Equal("DistroAV NDI Output (Back Door)", router.GetInput(3).Label); // the outer brackets only

        router.UpdateInput(3, i => i.Label = "Back Door");
        router.AssignSource(3, "THEBEAST (DistroAV NDI Output (Back Door))"); // the same one again
        Assert.Equal("Back Door", router.GetInput(3).Label);

        router.AssignSource(3, "EDIT-1 (Timeline)");
        Assert.Equal("Timeline", router.GetInput(3).Label);

        router.AssignSource(3, string.Empty);
        Assert.Equal("Input 4", router.GetInput(3).Label);
        Assert.Equal(string.Empty, router.GetInput(3).NdiSource);
    }

    [Fact]
    public void Routing_points_the_ndi_output_at_the_inputs_source_and_raises_an_event()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        RouteChangedEventArgs? seen = null;
        router.RouteChanged += (_, e) => seen = e;

        Assert.True(router.Route(1, 2, "test"));

        Assert.Equal("HOST (Cam 3)", ndi.Live("Router Out 2").Source);
        Assert.Equal(2, router.GetRoute(1));
        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Output);
        Assert.Equal(2, seen.Input);
        Assert.True(seen.Changed);
    }

    [Fact]
    public void Clearing_takes_the_output_off_its_source()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        router.Route(0, 1, "test");
        router.Route(0, null, "test");

        Assert.Null(router.GetRoute(0));
        Assert.Null(ndi.Live("Router Out 1").Source);
    }

    [Fact]
    public void Re_routing_the_same_input_is_still_announced()
    {
        // SW-P-08 controllers expect every CONNECT to be answered with CONNECTED.
        using var router = TestRouter.Create(new FakeNdi());
        router.Route(0, 1, "test");
        var events = 0;
        router.RouteChanged += (_, e) => { events++; Assert.False(e.Changed); };

        router.Route(0, 1, "test");
        Assert.Equal(1, events);
    }

    [Fact]
    public void Out_of_range_routes_are_refused()
    {
        using var router = TestRouter.Create(new FakeNdi());
        Assert.False(router.Route(3, 0, "test"));
        Assert.False(router.Route(0, 4, "test"));
    }

    [Fact]
    public void Repatching_an_input_reroutes_every_output_on_it()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        router.Route(0, 1, "test");
        router.Route(2, 1, "test");

        router.UpdateInput(1, i => i.NdiSource = "OTHER (Feed)");

        Assert.Equal("OTHER (Feed)", ndi.Live("Router Out 1").Source);
        Assert.Equal("OTHER (Feed)", ndi.Live("Router Out 3").Source);
        Assert.Null(ndi.Live("Router Out 2").Source);
    }

    [Fact]
    public void Renaming_an_output_republishes_it_with_its_route()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        router.Route(0, 3, "test");
        var old = ndi.Live("Router Out 1");

        router.UpdateOutput(0, o => o.NdiName = "Studio A PGM");

        Assert.True(old.Disposed);
        Assert.Equal("HOST (Cam 4)", ndi.Live("Studio A PGM").Source);
    }

    [Fact]
    public void Presets_store_and_recall_every_output()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Route(0, 0, "test");
        router.Route(1, 1, "test");
        router.Route(2, null, "test");
        router.StorePreset(0);

        router.Route(0, 3, "test");
        router.Route(1, 3, "test");
        router.Route(2, 3, "test");

        Assert.True(router.RecallPreset(0));
        Assert.Equal(new int?[] { 0, 1, null }, router.Routes());
        Assert.False(router.RecallPreset(1)); // never stored
    }

    [Fact]
    public void Shrinking_the_inputs_clears_outputs_routed_from_removed_ones()
    {
        var ndi = new FakeNdi();
        using var router = TestRouter.Create(ndi);
        router.Route(0, 3, "test");
        router.Route(1, 0, "test");

        router.Resize(2, 5, 2);

        Assert.Equal(new int?[] { null, 0, null, null, null }, router.Routes());
        Assert.Equal(5, ndi.Created.Count(o => !o.Disposed));
    }

    [Fact]
    public void Control_names_are_generated_per_length_unless_typed_in()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.UpdateInput(0, i => i.Name4 = "CAM1");

        Assert.Equal("CAM1", router.InputName(0, 4));   // typed
        Assert.Equal("Cam 1", router.InputName(0, 8));  // fits, so the label as it is
        Assert.Equal("Cam 1", router.InputName(0, 12));
        Assert.Equal("Cam2", router.InputName(1, 4));   // generated
    }

    [Fact]
    public void Editing_any_of_the_three_names_is_announced()
    {
        using var router = TestRouter.Create(new FakeNdi());
        var announced = 0;
        router.NamesChanged += (_, e) => { if (e.Outputs) announced++; };

        router.UpdateOutput(0, o => o.Name8 = "MON 1");
        router.UpdateOutput(0, o => o.Name8 = "MON 1"); // unchanged: nothing to announce

        Assert.Equal(1, announced);
    }
}
