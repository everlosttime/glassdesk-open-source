using System;
using System.IO;

namespace NaiwaDesktop;

internal sealed class Settings
{
	internal int Scale = 50;

	internal int X;

	internal int Y;

	internal bool Position;

	internal bool Sound = true;

	internal bool OnTop = true;

	internal bool AutoHide = true;

	internal int Volume = 100;

	internal int Alpha = 100;

	internal int ReminderMinutes;

	internal bool Mirror;

	internal bool Chime;

	internal bool Edge;

	internal bool Recycle = true;

	internal int EdgeInset = 80;

	internal int Hunger = 5;

	internal long HungerStamp;

	internal static string Folder { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaiwaDesktopPet");

	internal static void UseManagedState(string directory)
	{
		Folder = directory;
	}

	internal static Settings Load()
	{
		Settings settings = new Settings();
		try
		{
			string path = Path.Combine(Folder, "settings.ini");
			if (!File.Exists(path))
			{
				return settings;
			}
			string[] array = File.ReadAllLines(path);
			foreach (string text in array)
			{
				string[] array2 = text.Split('=');
				if (array2.Length == 2)
				{
					if (array2[0] == "scale" && int.TryParse(array2[1], out var result))
					{
						settings.Scale = Math.Max(50, Math.Min(150, result));
					}
					if (array2[0] == "x" && int.TryParse(array2[1], out result))
					{
						settings.X = result;
						settings.Position = true;
					}
					if (array2[0] == "y" && int.TryParse(array2[1], out result))
					{
						settings.Y = result;
					}
					if (array2[0] == "sound")
					{
						settings.Sound = array2[1] == "True";
					}
					if (array2[0] == "top")
					{
						settings.OnTop = array2[1] != "False";
					}
					if (array2[0] == "fullscreen")
					{
						settings.AutoHide = array2[1] != "False";
					}
					if (array2[0] == "alpha" && int.TryParse(array2[1], out result))
					{
						settings.Alpha = Math.Max(40, Math.Min(100, result));
					}
					if (array2[0] == "mirror")
					{
						settings.Mirror = array2[1] == "True";
					}
					if (array2[0] == "reminder" && int.TryParse(array2[1], out result))
					{
						settings.ReminderMinutes = result;
					}
					if (array2[0] == "chime")
					{
						settings.Chime = array2[1] == "True";
					}
					if (array2[0] == "edge")
					{
						settings.Edge = array2[1] == "True";
					}
					if (array2[0] == "recycle")
					{
						settings.Recycle = array2[1] != "False";
					}
					if (array2[0] == "edgeInset" && int.TryParse(array2[1], out result))
					{
						settings.EdgeInset = Math.Max(0, Math.Min(160, result));
					}
					if (array2[0] == "volume" && int.TryParse(array2[1], out result))
					{
						settings.Volume = Math.Max(0, Math.Min(100, result));
					}
					if (array2[0] == "hunger" && int.TryParse(array2[1], out result))
					{
						settings.Hunger = Math.Max(0, Math.Min(10, result));
					}
					if (array2[0] == "hungerStamp" && long.TryParse(array2[1], out var result2))
					{
						settings.HungerStamp = result2;
					}
				}
			}
		}
		catch
		{
		}
		return settings;
	}

	internal void Save()
	{
		try
		{
			Directory.CreateDirectory(Folder);
			string path = Path.Combine(Folder, "settings.ini");
			string pending = path + ".tmp";
			File.WriteAllLines(pending, new string[16]
			{
				"scale=" + Scale,
				"x=" + X,
				"y=" + Y,
				"sound=" + Sound,
				"top=" + OnTop,
				"fullscreen=" + AutoHide,
				"alpha=" + Alpha,
				"mirror=" + Mirror,
				"reminder=" + ReminderMinutes,
				"chime=" + Chime,
				"edge=" + Edge,
				"recycle=" + Recycle,
				"edgeInset=" + EdgeInset,
				"hunger=" + Hunger,
				"hungerStamp=" + HungerStamp,
				"volume=" + Volume
			});
			if (File.Exists(path))
			{
				File.Replace(pending, path, null);
			}
			else
			{
				File.Move(pending, path);
			}
		}
		catch
		{
		}
	}
}
