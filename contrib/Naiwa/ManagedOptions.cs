using System.Globalization;

namespace NaiwaDesktop;

internal readonly struct ManagedOptions
{
	internal readonly int Scale;
	internal readonly int Volume;
	internal readonly bool Sound;
	internal readonly bool Recycle;
	internal readonly bool AutoHide;

	private ManagedOptions(int scale, int volume, bool sound, bool recycle, bool autoHide)
	{
		Scale = scale;
		Volume = volume;
		Sound = sound;
		Recycle = recycle;
		AutoHide = autoHide;
	}

	internal static bool TryParse(string command, out ManagedOptions options)
	{
		options = default;
		if (command == null || !command.StartsWith("options ", System.StringComparison.Ordinal))
		{
			return false;
		}
		string[] parts = command.Split(' ');
		if (parts.Length != 6 ||
			!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int scale) ||
			scale < 50 || scale > 150 ||
			!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int volume) ||
			volume > 100 ||
			parts[3] is not ("0" or "1") || parts[4] is not ("0" or "1") || parts[5] is not ("0" or "1"))
		{
			return false;
		}
		options = new ManagedOptions(scale, volume, parts[3] == "1", parts[4] == "1", parts[5] == "1");
		return true;
	}
}
