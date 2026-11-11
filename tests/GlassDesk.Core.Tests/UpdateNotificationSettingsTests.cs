using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Tests;

public sealed class UpdateNotificationSettingsTests
{
    [Fact]
    public void SevenDayDeadlineSurvivesReloadAndExpiresExactlyAtDeadline()
    {
        var settings = new GlassDeskSettings();
        var property = typeof(GlassDeskSettings).GetProperty("UpdateNoticeSnoozedUntilUtc");
        Assert.NotNull(property);
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        property.SetValue(settings, now.AddDays(7));
        var reload = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, GlassDeskJsonContext.Default.GlassDeskSettings), GlassDeskJsonContext.Default.GlassDeskSettings)!;
        var shouldShow = typeof(GlassDeskSettings).GetMethod("ShouldShowUpdateNotice");
        Assert.NotNull(shouldShow);
        Assert.Equal(false, shouldShow.Invoke(reload, [false, now.AddDays(7).AddTicks(-1)]));
        Assert.Equal(true, shouldShow.Invoke(reload, [false, now.AddDays(7)]));
        Assert.Equal(true, shouldShow.Invoke(reload, [true, now]));
        Assert.Equal(true, shouldShow.Invoke(new GlassDeskSettings(), [false, now]));
        Assert.Equal(property.GetValue(reload), property.GetValue(SettingsPageEditor.Clone(reload)));
    }
}
