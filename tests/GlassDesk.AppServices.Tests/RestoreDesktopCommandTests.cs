using GlassDesk.App.Startup;

namespace GlassDesk.AppServices.Tests;

public sealed class RestoreDesktopCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("--exit")]
    [InlineData("--selftest")]
    [InlineData("--autostart")]
    [InlineData("--restore-desktop-icons")]
    [InlineData("--unknown")]
    public void DesktopRestorationRequiresExactlyOneStandaloneArgument(string? extra)
    {
        var options = CommandLineOptions.Parse(extra is null ? ["--restore-desktop-icons"] : ["--restore-desktop-icons", extra]);
        var property = typeof(CommandLineOptions).GetProperty("RestoreDesktopIcons");
        Assert.NotNull(property);
        Assert.True((bool)property.GetValue(options)!);
        Assert.Equal(extra is null, options.Unknown.Count == 0);
    }
}
