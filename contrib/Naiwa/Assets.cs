using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;

namespace NaiwaDesktop;

internal sealed class Assets : IDisposable
{
	internal readonly int Width;

	internal readonly int Height;

	internal readonly int Count;

	internal readonly int EyeCount;

	internal readonly int BlinkCount;

	internal readonly double Fps;

	internal readonly double Duration;

	internal readonly double BlinkFps;

	private readonly byte[][] encoded;

	private readonly Bitmap[] eyes;

	private readonly Bitmap[][] blinks;

	private readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();

	private readonly Queue<int> order = new Queue<int>();

	internal readonly string AudioPath;

	internal readonly string Folder;

	internal readonly EyeArt Eyes;

	internal readonly DragArt Drag;

	internal readonly FeedArt Feed;

	internal readonly WalkArt Walk;

	internal readonly WalkArt Hold;

	internal readonly WalkArt Head;

	internal readonly WalkArt Seq;

	internal readonly WalkArt Dance;

	internal readonly WalkArt Hunger;

	internal readonly WalkArt Fat;

	internal readonly WalkArt Rest;

	internal readonly WalkArt Rest2;

	internal readonly WalkArt Sad;

	internal readonly GifArt IdleGif;

	internal int CacheCount => cache.Count;

	internal Assets(string folder)
	{
		Folder = folder;
		AudioPath = Path.Combine(folder, "原版笑声.wav");
		using (ZipArchive zipArchive = ZipFile.OpenRead(Path.Combine(folder, "素材.nwp")))
		{
			Dictionary<string, object> dictionary;
			using (StreamReader streamReader = new StreamReader(zipArchive.GetEntry("metadata.json").Open()))
			{
				dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(streamReader.ReadToEnd());
			}
			Width = Convert.ToInt32(dictionary["width"]) / 2;
			Height = Convert.ToInt32(dictionary["height"]) / 2;
			Count = Convert.ToInt32(dictionary["frames"]);
			Fps = Convert.ToDouble(dictionary["fps"]);
			EyeCount = Convert.ToInt32(dictionary["eyeFrames"]);
			Duration = (double)Count / Fps;
			if (Width < 1 || Height < 1 || Width > 1024 || Height > 1024 || Count < 1 || Count > 2000 || Fps < 1.0 || Fps > 120.0 || EyeCount < 2 || EyeCount > 60)
			{
				throw new InvalidDataException("素材信息无效");
			}
			eyes = new Bitmap[EyeCount];
			for (int i = 0; i < EyeCount; i++)
			{
				eyes[i] = Decode(Read(zipArchive, "look/" + i.ToString("00") + ".png"));
			}
			BlinkCount = Convert.ToInt32(dictionary["blinkFrames"]);
			BlinkFps = Convert.ToDouble(dictionary["blinkFps"]);
			if (BlinkCount < 2 || BlinkCount > 60 || BlinkFps < 1.0 || BlinkFps > 120.0)
			{
				throw new InvalidDataException("眨眼素材信息无效");
			}
			blinks = new Bitmap[2][];
			for (int j = 0; j < 2; j++)
			{
				blinks[j] = new Bitmap[BlinkCount];
				for (int k = 0; k < BlinkCount; k++)
				{
					blinks[j][k] = Decode(Read(zipArchive, "blink/" + j + "/" + k.ToString("00") + ".png"));
				}
			}
			encoded = new byte[Count][];
			for (int l = 0; l < Count; l++)
			{
				encoded[l] = Read(zipArchive, "laugh/" + l.ToString("0000") + ".png");
			}
		}
		Eyes = LoadEyeArt(folder);
		Drag = new DragArt(folder);
		Feed = new FeedArt(folder);
		Walk = new WalkArt(folder, "walk.nwp");
		Hold = new WalkArt(folder, "hold.nwp");
		Head = new WalkArt(folder, "head.nwp");
		Seq = new WalkArt(folder, "seq.nwp");
		Dance = new WalkArt(folder, "dance.nwp");
		Hunger = new WalkArt(folder, "hunger.nwp");
		Fat = new WalkArt(folder, "fat.nwp");
		Rest = new WalkArt(folder, "rest.nwp");
		Rest2 = new WalkArt(folder, "rest2.nwp");
		Sad = new WalkArt(folder, "sad.nwp");
		IdleGif = new GifArt(folder, "idle_gif.nwp");
		Frame(0);
		Frame(1);
		Frame(2);
	}

	private static EyeArt LoadEyeArt(string folder)
	{
		try
		{
			string text = Path.Combine(folder, "eye_art.nwp");
			if (!File.Exists(text))
			{
				return null;
			}
			using ZipArchive zipArchive = ZipFile.OpenRead(text);
			Dictionary<string, object> dictionary;
			using (StreamReader streamReader = new StreamReader(zipArchive.GetEntry("metadata.json").Open()))
			{
				dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(streamReader.ReadToEnd());
			}
			Bitmap blank = DecodeAny(Read(zipArchive, (string)dictionary["blank"]));
			Bitmap l = DecodeAny(Read(zipArchive, (string)dictionary["left"]));
			Bitmap r = DecodeAny(Read(zipArchive, (string)dictionary["right"]));
			ArrayList arrayList = (ArrayList)dictionary["leftCenter"];
			ArrayList arrayList2 = (ArrayList)dictionary["rightCenter"];
			double rxx = 2.5;
			double ryy = 2.0;
			if (dictionary.ContainsKey("radiusX"))
			{
				rxx = Convert.ToDouble(dictionary["radiusX"]);
			}
			if (dictionary.ContainsKey("radiusY"))
			{
				ryy = Convert.ToDouble(dictionary["radiusY"]);
			}
			if (dictionary.ContainsKey("radius"))
			{
				rxx = (ryy = Convert.ToDouble(dictionary["radius"]));
			}
			return new EyeArt(blank, l, r, Convert.ToInt32(arrayList[0]), Convert.ToInt32(arrayList[1]), Convert.ToInt32(arrayList2[0]), Convert.ToInt32(arrayList2[1]), rxx, ryy);
		}
		catch
		{
			return null;
		}
	}

	private static Bitmap DecodeAny(byte[] data)
	{
		using MemoryStream stream = new MemoryStream(data);
		using Image image = Image.FromStream(stream);
		Bitmap bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.CompositingMode = CompositingMode.SourceCopy;
			graphics.DrawImageUnscaled(image, 0, 0);
		}
		return bitmap;
	}

	internal static byte[] Read(ZipArchive zip, string name)
	{
		ZipArchiveEntry entry = zip.GetEntry(name);
		if (entry == null)
		{
			throw new InvalidDataException("缺少动作素材：" + name);
		}
		using Stream stream = entry.Open();
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	private Bitmap Decode(byte[] data)
	{
		return DecodeTo(data, Width * 2, Height * 2);
	}

	internal Bitmap DecodeTo(byte[] data, int w, int h)
	{
		using MemoryStream stream = new MemoryStream(data);
		using Image image = Image.FromStream(stream);
		if (image.Width != w || image.Height != h)
		{
			throw new InvalidDataException("动作尺寸不一致");
		}
		Bitmap bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.CompositingMode = CompositingMode.SourceCopy;
			graphics.DrawImageUnscaled(image, 0, 0);
		}
		return bitmap;
	}

	internal Bitmap Eye(double value)
	{
		return eyes[Math.Max(0, Math.Min(EyeCount - 1, (int)Math.Round(value * (double)(EyeCount - 1))))];
	}

	internal Bitmap Blink(double look, int frame)
	{
		return blinks[(look > 0.5) ? 1u : 0u][Math.Max(0, Math.Min(BlinkCount - 1, frame))];
	}

	internal Bitmap Frame(int index)
	{
		index = Math.Max(0, Math.Min(Count - 1, index));
		if (cache.TryGetValue(index, out var value))
		{
			return value;
		}
		value = Decode(encoded[index]);
		cache.Add(index, value);
		order.Enqueue(index);
		while (order.Count > 12)
		{
			int key = order.Dequeue();
			cache[key].Dispose();
			cache.Remove(key);
		}
		return value;
	}

	public void Dispose()
	{
		if (eyes != null)
		{
			Bitmap[] array = eyes;
			for (int i = 0; i < array.Length; i++)
			{
				array[i]?.Dispose();
			}
		}
		if (blinks != null)
		{
			Bitmap[][] array2 = blinks;
			foreach (Bitmap[] array3 in array2)
			{
				Bitmap[] array4 = array3;
				for (int k = 0; k < array4.Length; k++)
				{
					array4[k]?.Dispose();
				}
			}
		}
		foreach (Bitmap value in cache.Values)
		{
			value.Dispose();
		}
		cache.Clear();
		if (Eyes != null)
		{
			Eyes.Dispose();
		}
		if (Drag != null)
		{
			Drag.Dispose();
		}
		if (Feed != null)
		{
			Feed.Dispose();
		}
		if (Walk != null)
		{
			Walk.Dispose();
		}
		if (Hold != null)
		{
			Hold.Dispose();
		}
		if (Head != null)
		{
			Head.Dispose();
		}
		if (Seq != null)
		{
			Seq.Dispose();
		}
		if (Dance != null)
		{
			Dance.Dispose();
		}
		if (Hunger != null)
		{
			Hunger.Dispose();
		}
		if (Fat != null)
		{
			Fat.Dispose();
		}
		if (Rest != null)
		{
			Rest.Dispose();
		}
		if (Rest2 != null)
		{
			Rest2.Dispose();
		}
		if (Sad != null)
		{
			Sad.Dispose();
		}
		if (IdleGif != null)
		{
			IdleGif.Dispose();
		}
	}
}
