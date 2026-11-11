using System;
using System.Drawing;
using System.Windows.Forms;

namespace NaiwaDesktop;

internal sealed class Gesture
{
	internal bool Pressed;

	internal bool Dragged;

	internal Point Origin;

	private Point lastClickPoint;

	private double lastClick = -100.0;

	internal void Down(Point p)
	{
		Pressed = true;
		Dragged = false;
		Origin = p;
	}

	internal void Move(Point p)
	{
		if (Pressed && (Math.Abs(p.X - Origin.X) > 5 || Math.Abs(p.Y - Origin.Y) > 5))
		{
			Dragged = true;
		}
	}

	internal bool Up(Point p, double now)
	{
		if (!Pressed)
		{
			return false;
		}
		Pressed = false;
		if (Dragged)
		{
			lastClick = -100.0;
			return false;
		}
		bool flag = now - lastClick <= (double)SystemInformation.DoubleClickTime / 1000.0 && Math.Abs(p.X - lastClickPoint.X) <= SystemInformation.DoubleClickSize.Width && Math.Abs(p.Y - lastClickPoint.Y) <= SystemInformation.DoubleClickSize.Height;
		lastClick = (flag ? (-100.0) : now);
		lastClickPoint = p;
		return flag;
	}

	internal void Cancel()
	{
		Pressed = false;
		Dragged = false;
		lastClick = -100.0;
	}
}
