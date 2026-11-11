using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;

namespace NaiwaDesktop;

internal sealed class DragArt : IDisposable
{
	internal readonly int Count;

	internal readonly int Width;

	internal readonly int Height;

	internal readonly double Fps;

	internal readonly double Duration;

	private readonly byte[][] encoded;

	private Bitmap[] frames;

	internal bool Ready => frames != null;

	internal DragArt(string folder)
	{
		string text = Path.Combine(folder, "drag.nwp");
		if (!File.Exists(text))
		{
			Count = 0;
			Width = (Height = 0);
			Fps = 15.0;
			Duration = 0.0;
			encoded = null;
			return;
		}
		using ZipArchive zipArchive = ZipFile.OpenRead(text);
		Dictionary<string, object> dictionary;
		using (StreamReader streamReader = new StreamReader(zipArchive.GetEntry("metadata.json").Open()))
		{
			dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(streamReader.ReadToEnd());
		}
		Count = Convert.ToInt32(dictionary["frames"]);
		Fps = Convert.ToDouble(dictionary["fps"]);
		Duration = (double)Count / Fps;
		Width = (dictionary.ContainsKey("width") ? Convert.ToInt32(dictionary["width"]) : 0) / 2;
		Height = (dictionary.ContainsKey("height") ? Convert.ToInt32(dictionary["height"]) : 0) / 2;
		if (Count < 1 || Count > 600 || Fps < 1.0 || Fps > 120.0)
		{
			throw new InvalidDataException("拖放素材信息无效");
		}
		encoded = new byte[Count][];
		for (int i = 0; i < Count; i++)
		{
			encoded[i] = Assets.Read(zipArchive, "drag/" + i.ToString("0000") + ".png");
		}
	}

	internal void Ensure(Assets a)
	{
		if (frames != null || encoded == null)
		{
			return;
		}
		int w = ((Width > 0) ? (Width * 2) : (a.Width * 2));
		int h = ((Height > 0) ? (Height * 2) : (a.Height * 2));
		frames = new Bitmap[Count];
		try
		{
			for (int i = 0; i < Count; i++)
			{
				frames[i] = a.DecodeTo(encoded[i], w, h);
			}
		}
		catch
		{
			for (int j = 0; j < Count; j++)
			{
				if (frames[j] != null)
				{
					frames[j].Dispose();
				}
			}
			frames = null;
		}
	}

	internal Bitmap Frame(int index)
	{
		return frames[Math.Max(0, Math.Min(Count - 1, index))];
	}

	public void Dispose()
	{
		if (frames != null)
		{
			Bitmap[] array = frames;
			for (int i = 0; i < array.Length; i++)
			{
				array[i]?.Dispose();
			}
		}
		frames = null;
	}
}
