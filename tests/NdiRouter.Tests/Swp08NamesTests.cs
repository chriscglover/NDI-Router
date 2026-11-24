using System.Text.Json;
using NdiRouter.Config;
using NdiRouter.Core;

namespace NdiRouter.Tests;

public class Swp08NamesTests
{
    [Theory]
    [InlineData("Cam 1", 4, "Cam1")]
    [InlineData("Camera 12", 4, "Cm12")]
    [InlineData("Camera 12", 8, "Camera12")]
    [InlineData("Camera 12", 12, "Camera 12")]
    [InlineData("Full Screen Cam", 4, "FSCm")]
    [InlineData("Full Screen Cam", 8, "FllScrCm")]
    [InlineData("Full Screen Cam", 12, "Fll Scrn Cam")]
    [InlineData("Back Door", 4, "BcDr")]
    [InlineData("OBS Pvw", 4, "OBPv")]
    [InlineData("MV-01 Out 1", 8, "MV01Out1")]
    [InlineData("Studio Camera 1", 12, "Std Camera 1")]
    [InlineData("Studio Camera 1", 4, "SCm1")]
    [InlineData("Cam 3 10", 6, "Cm3 10")] // two numbers are never run together
    [InlineData("NVIDIA GeForce RTX 2070 SUPER 1", 4, "NGR1")]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER 1", 8, "NGR1")] // not "NGR20701", nor "NGR207 1"
    [InlineData("NVIDIA GeForce RTX 2070 SUPER 1", 12, "NGFRT2070SP1")]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER 1", 9, "NGR2070S1")]
    public void Labels_are_abbreviated_to_fit(string label, int length, string expected)
    {
        Assert.Equal(expected, Swp08Names.Generate(label, length));
    }

    [Theory]
    [InlineData("Camera 101", 4)]
    [InlineData("Studio 3 Camera 10", 8)]
    [InlineData("Camera 1080i25", 8)]
    public void Generated_names_fit_and_keep_their_numbers_where_they_can(string label, int length)
    {
        var name = Swp08Names.Generate(label, length);
        Assert.True(name.Length <= length, name);
        var lastNumber = label.Split(' ').Last(w => w.Any(char.IsDigit));
        Assert.Contains(lastNumber, name);
    }

    [Fact]
    public void A_name_entered_as_the_generated_one_stays_generated()
    {
        var input = new RouterInput { Label = "Back Door" };
        Assert.Equal(string.Empty, Swp08Names.ToStore(input, 4, "BcDr", "IN 1"));
        Assert.Equal("DOOR", Swp08Names.ToStore(input, 4, "DOOR", "IN 1"));
        Assert.Equal(string.Empty, Swp08Names.ToStore(input, 4, "  ", "IN 1"));
    }

    [Fact]
    public void Generated_names_follow_the_label_and_typed_ones_do_not()
    {
        var input = new RouterInput { Label = "Back Door", Name8 = "REAR" };
        input.Label = "Front Door";
        Assert.Equal("FrDr", Swp08Names.For(input, 4, "IN 1"));
        Assert.Equal("REAR", Swp08Names.For(input, 8, "IN 1"));
    }

    [Fact]
    public void An_unlabelled_slot_is_named_from_its_number()
    {
        Assert.Equal("IN 7", Swp08Names.For(new RouterInput(), 4, "IN 7"));
    }

    [Fact]
    public void An_old_name_that_only_copied_the_label_goes_back_to_generated()
    {
        var json = """
            { "Inputs": [
                { "Label": "Back Door", "ShortName": "Back Door" },
                { "Label": "Full Screen Cam", "ShortName": "Full Screen Cam" } ] }
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var inputs = ConfigStore.Load(path).Inputs;

            Assert.All(inputs, i => Assert.True(Swp08Names.Lengths.All(n => Swp08Names.IsGenerated(i, n))));
            Assert.Equal("Fll Scrn Cam", Swp08Names.For(inputs[1], 12, "IN 2")); // abbreviated, not cut to "Full Screen "
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_old_single_name_moves_into_the_twelve_character_slot()
    {
        var json = """{ "Inputs": [ { "Label": "Camera 1", "ShortName": "CAM ONE" } ] }""";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var config = ConfigStore.Load(path);
            var input = config.Inputs[0];

            Assert.Equal("CAM ONE", input.Name12);
            Assert.Equal(string.Empty, input.Name4);
            Assert.Null(input.LegacyShortName);

            ConfigStore.Save(config, path);
            Assert.DoesNotContain("ShortName", File.ReadAllText(path)); // written back in the new form only
        }
        finally
        {
            File.Delete(path);
        }
    }
}
