using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;

namespace NaiwaDesktop;

internal sealed class FeedArt : IDisposable
{
	internal readonly int Width;

	internal readonly int Height;

	private byte[] data;

	private Bitmap frame;

	internal bool Ready => frame != null;

	internal FeedArt(string folder)
	{
		string text = Path.Combine(folder, "feed.nwp");
		if (!File.Exists(text))
		{
			Width = (Height = 0);
			data = null;
			return;
		}
		using ZipArchive zipArchive = ZipFile.OpenRead(text);
		Dictionary<string, object> dictionary;
		using (StreamReader streamReader = new StreamReader(zipArchive.GetEntry("metadata.json").Open()))
		{
			dictionary = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(streamReader.ReadToEnd());
		}
		Width = Convert.ToInt32(dictionary["width"]) / 2;
		Height = Convert.ToInt32(dictionary["height"]) / 2;
		data = Assets.Read(zipArchive, "feed/0000.png");
	}

	internal void Ensure(Assets a)
	{
		if (frame != null || data == null)
		{
			return;
		}
		try
		{
			frame = a.DecodeTo(data, (Width > 0) ? (Width * 2) : (a.Width * 2), (Height > 0) ? (Height * 2) : (a.Height * 2));
		}
		catch
		{
			data = null;
		}
	}

	internal Bitmap Frame()
	{
		return frame;
	}

	public void Dispose()
	{
		if (frame != null)
		{
			frame.Dispose();
		}
		frame = null;
	}
}
