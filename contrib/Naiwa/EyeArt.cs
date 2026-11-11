using System;
using System.Drawing;

namespace NaiwaDesktop;

internal sealed class EyeArt : IDisposable
{
	internal readonly Bitmap Blank;

	internal readonly Bitmap IrisL;

	internal readonly Bitmap IrisR;

	internal readonly int Lx;

	internal readonly int Ly;

	internal readonly int Rx;

	internal readonly int Ry;

	internal readonly double RxR;

	internal readonly double RyR;

	internal EyeArt(Bitmap blank, Bitmap l, Bitmap r, int lx, int ly, int rx, int ry, double rxx, double ryy)
	{
		Blank = blank;
		IrisL = l;
		IrisR = r;
		Lx = lx;
		Ly = ly;
		Rx = rx;
		Ry = ry;
		RxR = rxx;
		RyR = ryy;
	}

	public void Dispose()
	{
		if (Blank != null)
		{
			Blank.Dispose();
		}
		if (IrisL != null)
		{
			IrisL.Dispose();
		}
		if (IrisR != null)
		{
			IrisR.Dispose();
		}
	}
}
