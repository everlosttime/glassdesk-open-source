using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NaiwaDesktop;

internal sealed class LayeredSurface : IDisposable
{
	private IntPtr dc;

	private IntPtr bmp;

	private IntPtr old;

	private IntPtr bits;

	private byte[] buffer;

	private int width;

	private int height;

	internal void Update(IntPtr window, Bitmap image, Point location, int alpha)
	{
		if (width != image.Width || height != image.Height)
		{
			Create(image.Width, image.Height);
		}
		BitmapData bitmapData = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
		try
		{
			Marshal.Copy(bitmapData.Scan0, buffer, 0, buffer.Length);
			Marshal.Copy(buffer, 0, bits, buffer.Length);
		}
		finally
		{
			image.UnlockBits(bitmapData);
		}
		Native.Point position = new Native.Point(location.X, location.Y);
		Native.Point origin = new Native.Point(0, 0);
		Native.Size size = new Native.Size(width, height);
		Native.Blend blend = new Native.Blend
		{
			Op = 0,
			Flags = 0,
			Alpha = (byte)Math.Max(0, Math.Min(255, alpha)),
			Format = 1
		};
		if (!Native.UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref origin, 0u, ref blend, 2u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	private void Create(int w, int h)
	{
		Dispose();
		width = w;
		height = h;
		buffer = new byte[w * h * 4];
		dc = Native.CreateCompatibleDC(IntPtr.Zero);
		Native.BitmapInfo info = new Native.BitmapInfo
		{
			Size = 40u,
			Width = w,
			Height = -h,
			Planes = 1,
			BitCount = 32,
			Compression = 0u,
			SizeImage = (uint)(w * h * 4)
		};
		bmp = Native.CreateDIBSection(dc, ref info, 0u, out bits, IntPtr.Zero, 0u);
		if (bmp == IntPtr.Zero)
		{
			throw new InvalidOperationException("无法创建透明窗口");
		}
		old = Native.SelectObject(dc, bmp);
	}

	internal bool Hit(int x, int y)
	{
		if (buffer != null && x >= 0 && y >= 0 && x < width && y < height)
		{
			return buffer[(y * width + x) * 4 + 3] > 24;
		}
		return false;
	}

	public void Dispose()
	{
		if (dc != IntPtr.Zero)
		{
			if (old != IntPtr.Zero)
			{
				Native.SelectObject(dc, old);
			}
			if (bmp != IntPtr.Zero)
			{
				Native.DeleteObject(bmp);
			}
			Native.DeleteDC(dc);
		}
		dc = (bmp = (old = (bits = IntPtr.Zero)));
		width = (height = 0);
		buffer = null;
	}
}
