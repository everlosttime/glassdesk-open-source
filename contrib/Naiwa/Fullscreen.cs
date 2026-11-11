using System;
using System.Drawing;
using System.Text;

namespace NaiwaDesktop;

internal static class Fullscreen
{
	internal static bool Covers(Rectangle window, Rectangle screen, int style, string name)
	{
		if ((style & 0xC00000) == 0)
		{
			switch (name)
			{
			case "Progman":
			case "WorkerW":
			case "Shell_TrayWnd":
			case "Shell_SecondaryTrayWnd":
				break;
			default:
				if (window.Left <= screen.Left + 2 && window.Top <= screen.Top + 2 && window.Right >= screen.Right - 2)
				{
					return window.Bottom >= screen.Bottom - 2;
				}
				return false;
			}
		}
		return false;
	}

	internal static bool IsActive(IntPtr window, IntPtr pet, Rectangle screen)
	{
		if (window == IntPtr.Zero || window == pet || !Native.IsWindowVisible(window) || Native.IsIconic(window))
		{
			return false;
		}
		if (!Native.GetWindowRect(window, out var r))
		{
			return false;
		}
		try
		{
			if (Native.DwmInt(window, 14, out var value, 4) == 0 && value != 0)
			{
				return false;
			}
			if (Native.DwmRect(window, 9, out var value2, 16) == 0)
			{
				r = value2;
			}
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
		StringBuilder stringBuilder = new StringBuilder(256);
		Native.GetClassName(window, stringBuilder, stringBuilder.Capacity);
		return Covers(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), screen, Native.GetWindowLong(window, -16), stringBuilder.ToString());
	}
}
