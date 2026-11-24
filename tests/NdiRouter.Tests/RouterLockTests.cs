using NdiRouter.Config;
using NdiRouter.Core;

namespace NdiRouter.Tests;

public class RouterLockTests
{
    private static readonly OutputLock Hub = new("Videohub 10.0.0.5");

    [Fact]
    public void A_locked_output_only_takes_routes_from_its_owner()
    {
        using var router = TestRouter.Create(new FakeNdi());
        var log = new List<string>();
        router.Log += log.Add;
        Assert.True(router.Lock(1, Hub, "test"));

        Assert.False(router.Route(1, 2, "panel", requester: OutputLock.Panel));
        Assert.Null(router.GetRoute(1));
        Assert.Contains(log, line => line.Contains("locked by Videohub 10.0.0.5") && line.Contains("refused"));

        Assert.True(router.Route(1, 2, "hub", requester: Hub.Owner));
        Assert.Equal(2, router.GetRoute(1));
    }

    [Fact]
    public void The_router_itself_is_not_stopped_by_locks()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Lock(0, Hub, "test");
        Assert.True(router.Route(0, 3, "import")); // no requester
        Assert.Equal(3, router.GetRoute(0));
    }

    [Fact]
    public void Only_the_owner_unlocks_unless_forced()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Lock(2, Hub, "test");

        Assert.False(router.Lock(2, new OutputLock(OutputLock.Panel), "panel"));
        Assert.False(router.Unlock(2, OutputLock.Panel, "panel"));
        Assert.Equal(Hub, router.GetLock(2));

        Assert.True(router.Unlock(2, OutputLock.Panel, "panel", force: true));
        Assert.Null(router.GetLock(2));
        Assert.True(router.Unlock(2, "anyone", "test")); // already free
    }

    [Fact]
    public void Relocking_is_confirmed_and_takes_the_new_device()
    {
        using var router = TestRouter.Create(new FakeNdi());
        var seen = new List<LockChangedEventArgs>();
        router.LockChanged += (_, e) => seen.Add(e);

        router.Lock(0, new OutputLock("SW-P-08 10.0.0.9", 4), "test");
        router.Lock(0, new OutputLock("SW-P-08 10.0.0.9", 7), "test");

        Assert.Equal(2, seen.Count);
        Assert.True(seen[1].Changed);
        Assert.Equal(7, router.GetLock(0)!.Device);
        Assert.Equal("SW-P-08 10.0.0.9 (device 7)", router.GetLock(0)!.ToString());
    }

    [Fact]
    public void Presets_leave_outputs_locked_by_others_alone()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Route(0, 0, "test");
        router.Route(1, 0, "test");
        router.StorePreset(0); // both on input 0
        router.Route(0, 3, "test");
        router.Route(1, 3, "test");
        router.Lock(1, Hub, "test");

        router.RecallPreset(0, OutputLock.Panel);

        Assert.Equal(0, router.GetRoute(0));
        Assert.Equal(3, router.GetRoute(1));
    }

    [Fact]
    public void Panel_take_reports_the_outputs_it_could_not_route()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Lock(2, Hub, "test");

        var refused = router.Route(new[] { 0, 1, 2 }, 1, "panel", OutputLock.Panel);

        Assert.Equal(new[] { 2 }, refused);
        Assert.Equal(new int?[] { 1, 1, null }, router.Routes());
    }

    [Fact]
    public void Editing_an_output_cannot_change_its_lock()
    {
        using var router = TestRouter.Create(new FakeNdi());
        router.Lock(0, Hub, "test");
        router.UpdateOutput(0, o => o.Lock = null);
        Assert.Equal(Hub, router.GetLock(0));
    }

    [Fact]
    public void Locks_are_saved_and_come_back()
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = AppConfig.CreateDefault();
            config.Outputs[1].Lock = new OutputLock("SW-P-08 10.0.0.9", 12);
            ConfigStore.Save(config, path);

            var loaded = ConfigStore.Load(path);
            using var router = new RouterCore(new FakeNdi(), loaded.Inputs, loaded.Outputs, loaded.Presets);
            Assert.Equal(new OutputLock("SW-P-08 10.0.0.9", 12), router.GetLock(1));
            Assert.Null(router.GetLock(0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
