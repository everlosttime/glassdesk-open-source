using System.Diagnostics;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class MediaSourceFilterTests
{
    [Theory]
    [InlineData("wallpaper32")]
    [InlineData("WALLPAPER64.EXE")]
    [InlineData(@"D:\steam\steamapps\common\wallpaper_engine\wallpaper32.exe")]
    [InlineData("WallpaperEngine!App")]
    [InlineData("Wallpaper Engine")]
    public void WallpaperApplicationIdentitiesAreExcluded(string source)
        => Assert.True(MediaSourceFilter.IsExcluded(source));

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("chrome.exe")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("MusicPlayer!Wallpaper Song")]
    [InlineData(null)]
    public void NormalPlayersRemainEligible(string? source)
        => Assert.False(MediaSourceFilter.IsExcluded(source));

    [Fact]
    public void NativeAudioFallbackNeverReturnsTheRunningWallpaperProcess()
    {
        var wallpaperIds = Process.GetProcesses().Where(process =>
            process.ProcessName.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase)
            || process.ProcessName.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase))
            .Select(process => process.Id).ToArray();
        var audio = ActiveAudioApplicationReader.Read();
        if (audio.Available) Assert.DoesNotContain(audio.ProcessId, wallpaperIds);
    }
}
