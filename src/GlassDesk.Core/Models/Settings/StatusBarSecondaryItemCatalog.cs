namespace GlassDesk.Core.Models.Settings;

/// <summary>Stable settings keys and visual order for the three optional status bar slots.</summary>
public static class StatusBarSecondaryItemCatalog
{
    public const int MaximumItems = 3;
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Fan = "fan";
    public const string MouseBattery = "mouseBattery";
    public const string HeadsetBattery = "headsetBattery";
    public const string KeyboardBattery = "keyboardBattery";
    public const string Music = "music";

    public static IReadOnlyList<string> RenderOrder { get; } = Array.AsReadOnly(
        new[] { Cpu, Memory, Fan, MouseBattery, HeadsetBattery, KeyboardBattery, Music });

    public static string[] DefaultItems() => [Cpu, Memory, Music];

    public static string[] Normalize(IEnumerable<string>? values) => values is null
        ? DefaultItems()
        : values.Where(value => RenderOrder.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(MaximumItems).ToArray();

    public static string[] GetRenderItems(IEnumerable<string>? values)
    {
        var selected = Normalize(values);
        return RenderOrder.Where(value => selected.Contains(value, StringComparer.Ordinal)).ToArray();
    }
}
