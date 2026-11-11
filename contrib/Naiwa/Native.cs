using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NaiwaDesktop;

internal static class Native
{
	internal struct Point
	{
		internal int X;

		internal int Y;

		internal Point(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	internal struct Size
	{
		internal int W;

		internal int H;

		internal Size(int w, int h)
		{
			W = w;
			H = h;
		}
	}

	internal struct Rect
	{
		internal int Left;

		internal int Top;

		internal int Right;

		internal int Bottom;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	internal struct Blend
	{
		internal byte Op;

		internal byte Flags;

		internal byte Alpha;

		internal byte Format;
	}

	internal struct BitmapInfo
	{
		internal uint Size;

		internal int Width;

		internal int Height;

		internal ushort Planes;

		internal ushort BitCount;

		internal uint Compression;

		internal uint SizeImage;

		internal int XPels;

		internal int YPels;

		internal uint Used;

		internal uint Important;
	}

	internal const string Title = "NaiwaDesktop.Source.v1";

	internal const int Wake = 32769;

	internal const int Status = 32770;

	internal const int Hungry = 32771;

	[DllImport("user32.dll")]
	internal static extern bool SetProcessDPIAware();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern IntPtr FindWindow(string cls, string text);

	[DllImport("user32.dll")]
	internal static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

	[DllImport("user32.dll")]
	internal static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

	[DllImport("user32.dll")]
	internal static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll")]
	internal static extern bool IsIconic(IntPtr h);

	[DllImport("user32.dll")]
	internal static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern int GetClassName(IntPtr h, StringBuilder name, int count);

	[DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
	internal static extern int DwmRect(IntPtr h, int attr, out Rect value, int size);

	[DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
	internal static extern int DwmInt(IntPtr h, int attr, out int value, int size);

	[DllImport("user32.dll")]
	internal static extern bool GetWindowRect(IntPtr h, out Rect r);

	[DllImport("user32.dll")]
	internal static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll")]
	internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);

	[DllImport("user32.dll")]
	internal static extern IntPtr GetDC(IntPtr h);

	[DllImport("user32.dll")]
	internal static extern int ReleaseDC(IntPtr h, IntPtr dc);

	[DllImport("user32.dll")]
	internal static extern short GetAsyncKeyState(int vKey);

	[DllImport("gdi32.dll")]
	internal static extern IntPtr CreateCompatibleDC(IntPtr h);

	[DllImport("gdi32.dll")]
	internal static extern bool DeleteDC(IntPtr h);

	[DllImport("gdi32.dll")]
	internal static extern bool DeleteObject(IntPtr h);

	[DllImport("gdi32.dll")]
	internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

	[DllImport("gdi32.dll")]
	internal static extern IntPtr CreateDIBSection(IntPtr h, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dest, ref Point position, ref Size size, IntPtr src, ref Point origin, uint key, ref Blend blend, uint flags);

	[DllImport("user32.dll")]
	internal static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll")]
	internal static extern int GetGuiResources(IntPtr process, int flags);
}
