using System.Globalization;

namespace GlassDesk.Core.Models.Settings;

/// <summary>User preferences for the yellow Recycle Bin. Pet position and hunger are stored by Naiwa.</summary>
public sealed class NaiwaOptions
{
    public int Scale { get; set; } = 50;
    public int Volume { get; set; } = 100;
    public bool Sound { get; set; } = true;
    public bool Recycle { get; set; } = true;
    public bool AutoHide { get; set; } = true;

    public void Normalize()
    {
        Scale = Math.Clamp(Scale, 50, 150);
        Volume = Math.Clamp(Volume, 0, 100);
    }

    public static NaiwaOptions FromLegacyIni(IEnumerable<string> lines)
    {
        var options = new NaiwaOptions();
        foreach (var line in lines)
        {
            var entry = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (entry.Length != 2) continue;

            var value = entry[1];
            switch (entry[0])
            {
                case "scale" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale):
                    options.Scale = scale;
                    break;
                case "volume" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume):
                    options.Volume = volume;
                    break;
                case "sound" when bool.TryParse(value, out var sound):
                    options.Sound = sound;
                    break;
                case "recycle" when bool.TryParse(value, out var recycle):
                    options.Recycle = recycle;
                    break;
                case "fullscreen" when bool.TryParse(value, out var autoHide):
                    options.AutoHide = autoHide;
                    break;
            }
        }
        options.Normalize();
        return options;
    }

    public string ToPipeCommand()
        => FormattableString.Invariant($"options {Scale} {Volume} {(Sound ? 1 : 0)} {(Recycle ? 1 : 0)} {(AutoHide ? 1 : 0)}");
}
