using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;

namespace NaiwaDesktop;

internal sealed class GifArt : IDisposable
{
	internal readonly int Count;

	internal readonly double Fps;

	internal readonly int Width;

	internal readonly int Height;

	private readonly byte[][] encoded;

	private Bitmap[] frames;

	internal bool Ready => frames != null;

	internal GifArt(string folder, string name)
	{
		string text = Path.Combine(folder, name);
		if (!File.Exists(text))
		{
			Count = 0;
			Width = (Height = 0);
			Fps = 15.0;
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
		Width = (dictionary.ContainsKey("width") ? Convert.ToInt32(dictionary["width"]) : 0) / 2;
		Height = (dictionary.ContainsKey("height") ? Convert.ToInt32(dictionary["height"]) : 0) / 2;
		if (Count < 1 || Count > 600 || Fps < 1.0 || Fps > 60.0)
		{
			throw new InvalidDataException("待机GIF素材信息无效");
		}
		encoded = new byte[Count][];
		for (int i = 0; i < Count; i++)
		{
			encoded[i] = Assets.Read(zipArchive, "idle_gif/" + i.ToString("0000") + ".png");
		}
	}

	internal void Ensure(Assets a)
	{
		if (frames == null && encoded != null)
		{
			frames = new Bitmap[Count];
		}
	}

	internal Bitmap Frame(int index, Assets a)
	{
		if (frames == null)
		{
			Ensure(a);
		}
		if (frames == null)
		{
			return null;
		}
		index = Math.Max(0, Math.Min(Count - 1, index));
		if (frames[index] == null)
		{
			try
			{
				frames[index] = a.DecodeTo(encoded[index], (Width > 0) ? (Width * 2) : (a.Width * 2), (Height > 0) ? (Height * 2) : (a.Height * 2));
			}
			catch
			{
				frames[index] = null;
			}
		}
		Bitmap result = frames[index];
		for (int i = 0; i < frames.Length; i++)
		{
			if (i != index && frames[i] != null)
			{
				frames[i].Dispose();
				frames[i] = null;
			}
		}
		return result;
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
