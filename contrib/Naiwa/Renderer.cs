using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NaiwaDesktop;

internal static class Renderer
{
	internal const int BubbleHeight = 58;

	internal static Icon CreateIcon(Bitmap image)
	{
		using Bitmap bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.DrawImage(image, new Rectangle(6, 0, 52, 64), new Rectangle(60, 40, 175, 225), GraphicsUnit.Pixel);
		}
		IntPtr hicon = bitmap.GetHicon();
		try
		{
			using Icon icon = Icon.FromHandle(hicon);
			return (Icon)icon.Clone();
		}
		finally
		{
			Native.DestroyIcon(hicon);
		}
	}

	internal static Bitmap Render(Assets assets, Motion m, int scale)
	{
		return Render(assets, m, scale, mirror: false, 100, null, rotateRight: false);
	}

	private static Bitmap CompositeEyes(Assets a, bool mirror, double look, double lookV)
	{
		return new Bitmap(a.Eye(mirror ? (1.0 - look) : look));
	}

	internal static Bitmap Render(Assets assets, Motion m, int scale, bool mirror, int alpha, string bubble, bool rotateRight, float danceHq = 0f)
	{
		int num = scale;
		scale = num * 2;
		if (rotateRight)
		{
			int width = ((assets.Head.Width > 0) ? (assets.Head.Width * scale / 100) : (assets.Width * scale / 100));
			int height = ((assets.Head.Height > 0) ? (assets.Head.Height * scale / 100) : (assets.Height * scale / 100));
			Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.Clear(Color.Transparent);
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				assets.Head.Ensure(assets);
				if (assets.Head.Ready)
				{
					Draw(graphics, assets.Head.Frame(0), new Rectangle(0, 0, width, height), 1f);
				}
			}
			return Downsample(bitmap, (assets.Head.Width > 0) ? (assets.Head.Width * num / 100) : (assets.Width * num / 100), (assets.Head.Height > 0) ? (assets.Head.Height * num / 100) : (assets.Height * num / 100));
		}
		int width2 = assets.Width;
		if (m.Sleeping)
		{
			if (assets.Rest.Ready && assets.Rest.Width > width2)
			{
				width2 = assets.Rest.Width;
			}
			if (assets.Rest2.Ready && assets.Rest2.Width > width2)
			{
				width2 = assets.Rest2.Width;
			}
		}
		int num2 = width2 * scale / 100;
		int num3 = assets.Height * scale / 100;
		int num4 = num3;
		if (!rotateRight)
		{
			if (m.Dragging || m.HoverOpen)
			{
				num4 = Math.Max(num4, assets.Drag.Height * scale / 100);
			}
			if (m.Feeding)
			{
				num4 = Math.Max(num4, assets.Feed.Height * scale / 100);
			}
		}
		int num5 = (rotateRight ? (374 * scale / 100) : (num4 + 116));
		if (rotateRight)
		{
			num2 = 433 * scale / 100;
		}
		Bitmap bitmap2 = new Bitmap(num2, num5, PixelFormat.Format32bppPArgb);
		using (Graphics graphics2 = Graphics.FromImage(bitmap2))
		{
			graphics2.Clear(Color.Transparent);
			graphics2.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics2.PixelOffsetMode = PixelOffsetMode.HighQuality;
			if (rotateRight)
			{
				graphics2.TranslateTransform(-76f * (float)scale / 100f, 45f * (float)scale / 100f);
			}
			else
			{
				graphics2.TranslateTransform(0f, 116f);
			}
			bool flag = mirror;
			if (m.Holding && m.DragAngle < -0.5)
			{
				flag = !mirror;
			}
			if (m.Walking && m.WalkDir > 0)
			{
				flag = !mirror;
			}
			if (flag)
			{
				graphics2.ScaleTransform(-1f, 1f);
				graphics2.TranslateTransform(-num2, 0f);
			}
			if (rotateRight)
			{
				graphics2.TranslateTransform(106f * (float)scale / 100f, 60f * (float)scale / 100f);
				graphics2.RotateTransform(-30f);
				graphics2.TranslateTransform(-106f * (float)scale / 100f, -60f * (float)scale / 100f);
			}
			if (m.Moving)
			{
				float num6 = (float)num2 * 0.5f;
				float num7 = (float)num3 * (m.Holding ? 0.56f : 0.9565f);
				float num8 = (float)m.Squash;
				graphics2.TranslateTransform(num6, num7 - (m.Holding ? (2f * (float)scale / 100f) : 0f));
				graphics2.RotateTransform((float)m.DragAngle);
				graphics2.ScaleTransform(m.Holding ? 1.008f : (1f + num8), m.Holding ? 1.018f : (1f - num8));
				graphics2.TranslateTransform(0f - num6, 0f - num7);
			}
			else if (m.Idling)
			{
				if (m.IdleKind != 5 && m.IdleKind != 6)
				{
					double num9 = Math.Max(0.0, Math.Min(1.0, (m.Clock - m.IdleStart) / Math.Max(0.001, m.IdleEnd - m.IdleStart)));
					float num10 = (float)num2 * 0.5f;
					float num11 = (float)num3 * 0.5f;
					graphics2.TranslateTransform(num10, num11);
					if (m.IdleKind == 0)
					{
						graphics2.RotateTransform((float)(10.0 * Math.Sin(Math.PI * num9)));
					}
					else if (m.IdleKind == 1)
					{
						graphics2.TranslateTransform(0f, (float)(-26.0 * Math.Sin(Math.PI * num9)));
					}
					else if (m.IdleKind == 2)
					{
						graphics2.TranslateTransform((float)(14.0 * Math.Sin(Math.PI * 2.0 * num9)), (float)(-4.0 * Math.Sin(Math.PI * num9)));
					}
					else if (m.IdleKind == 3)
					{
						graphics2.TranslateTransform((float)(3.0 * Math.Sin(30.0 * num9)), 0f);
					}
					else if (m.IdleKind == 4)
					{
						graphics2.ScaleTransform(1f, (float)(1.0 - 0.06 * Math.Sin(Math.PI * num9)));
					}
					graphics2.TranslateTransform(0f - num10, 0f - num11);
				}
			}
			else if (m.HeadPoking)
			{
				double num12 = Math.Max(0.0, Math.Min(1.0, (m.Clock - m.HeadStart) / 1.0));
				float num13 = (float)num2 * 0.5f;
				float num14 = (float)num3 * 0.35f;
				graphics2.TranslateTransform(num13, num14);
				graphics2.RotateTransform((float)(4.0 * Math.Sin(Math.PI * num12)));
				graphics2.ScaleTransform(1f, (float)(1.0 - 0.08 * Math.Sin(Math.PI * num12)));
				graphics2.TranslateTransform(0f - num13, 0f - num14);
			}
			else if (m.Celebrating)
			{
				double num15 = m.Clock - m.CelebrateStart;
				float num16 = (float)num2 * 0.5f;
				float num17 = (float)num3 * 0.5f;
				graphics2.TranslateTransform(num16, num17);
				graphics2.TranslateTransform(0f, (float)(-26.0 * Math.Abs(Math.Sin(Math.PI * num15 * 2.0))));
				graphics2.RotateTransform((float)(2.0 * Math.Sin(Math.PI * num15 * 2.0)));
				graphics2.TranslateTransform(0f - num16, 0f - num17);
			}
			Rectangle rectangle;
			Rectangle rect;
			if (rotateRight)
			{
				rectangle = new Rectangle(0, 0, assets.Width * scale / 100, assets.Height * scale / 100);
				rect = rectangle;
			}
			else
			{
				rectangle = new Rectangle(0, num4 - num3, num2, num3);
				rect = rectangle;
				if (assets.Drag.Width > 0)
				{
					float num18 = (float)scale / 100f;
					rect = new Rectangle((int)(((float)num2 - (float)assets.Drag.Width * num18) / 2f), (int)((float)num4 - (float)assets.Drag.Height * num18), (int)((float)assets.Drag.Width * num18), (int)((float)assets.Drag.Height * num18));
				}
			}
			double look = (mirror ? (1.0 - m.Look) : m.Look);
			if (m.Holding || m.Falling)
			{
				assets.Hold.Ensure(assets);
				if (assets.Hold.Ready)
				{
					float num19 = (float)scale / 100f;
					Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Hold.Width * num19) / 2f), (int)((float)num4 - (float)assets.Hold.Height * num19), (int)((float)assets.Hold.Width * num19), (int)((float)assets.Hold.Height * num19)), g: graphics2, b: assets.Hold.Frame(0), alpha: 1f);
				}
			}
			else if (m.Dragging)
			{
				assets.Drag.Ensure(assets);
				if (assets.Drag.Ready)
				{
					Draw(graphics2, assets.Drag.Frame((int)((m.Clock - m.DragStart) * assets.Drag.Fps)), rect, 1f);
				}
			}
			else if (m.Walking)
			{
				assets.Walk.Ensure(assets);
				if (assets.Walk.Ready)
				{
					int index = (int)((m.Clock - m.WalkStart) * 12.0) % assets.Walk.Count;
					Draw(graphics2, assets.Walk.Frame(index), rect, 1f);
				}
			}
			else if (m.HoverOpen)
			{
				assets.Drag.Ensure(assets);
				if (assets.Drag.Ready)
				{
					int num20 = (int)((m.Clock - m.HoverStart) * 15.0);
					if (num20 >= 19)
					{
						num20 = 18;
					}
					Draw(graphics2, assets.Drag.Frame(1 + num20), rect, 1f);
				}
			}
			else if (m.Feeding)
			{
				assets.Feed.Ensure(assets);
				if (assets.Feed.Ready)
				{
					Draw(graphics2, assets.Feed.Frame(), rect, 1f);
				}
			}
			else if (m.Hungry)
			{
				assets.Hunger.Ensure(assets);
				if (assets.Hunger.Ready)
				{
					float num21 = (float)scale / 100f;
					Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Hunger.Width * num21) / 2f), (int)((float)num4 - (float)assets.Hunger.Height * num21), (int)((float)assets.Hunger.Width * num21), (int)((float)assets.Hunger.Height * num21)), g: graphics2, b: assets.Hunger.Frame(0), alpha: 1f);
				}
			}
			else if (m.Dancing)
			{
				assets.Dance.Ensure(assets);
				if (assets.Dance.Ready)
				{
					float num22 = (float)(assets.Height * scale) / 100f;
					float num23 = (float)assets.Dance.Width * num22 / (float)assets.Dance.Height;
					Draw(rect: new Rectangle((int)(((float)num2 - num23) / 2f), (int)((float)num4 - num22), (int)num23, (int)num22), g: graphics2, b: assets.Dance.Frame((int)((m.Clock - m.DanceStart) * 15.0) % assets.Dance.Count), alpha: 1f);
				}
			}
			else if (m.Fat)
			{
				assets.Fat.Ensure(assets);
				double num24 = m.Clock - m.FatStart;
				if (num24 < 1.2)
				{
					Draw(graphics2, assets.Frame(0), rectangle, 1f);
					for (int i = 0; i < 3; i++)
					{
						double num25 = num24 * 1.0 + (double)i * 0.4;
						if (num25 > 1.2)
						{
							num25 -= 1.2;
						}
						double num26 = num25 / 1.2;
						int num27 = num2 / 2 + (int)((num26 * 24.0 + (double)(i * 18) - 27.0) * 2.0);
						int num28 = (int)((double)(rectangle.Top - 36) - num26 * 58.0 * 2.0);
						int num29 = (int)((6.0 + num26 * 15.0) * 2.0);
						int val = (int)(215.0 * (1.0 - num26));
						using SolidBrush brush = new SolidBrush(Color.FromArgb(Math.Max(0, val), 132, 132, 132));
						graphics2.FillEllipse(brush, num27 - num29, num28 - num29, num29 * 2, num29 * 2);
					}
				}
				else if (assets.Fat.Ready)
				{
					float num30 = (float)scale / 100f;
					Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Fat.Width * num30) / 2f), (int)((float)num4 - (float)assets.Fat.Height * num30), (int)((float)assets.Fat.Width * num30), (int)((float)assets.Fat.Height * num30)), g: graphics2, b: assets.Fat.Frame(0), alpha: 1f);
				}
			}
			else if (m.HeadSequencing)
			{
				assets.Seq.Ensure(assets);
				if (m.HeadSeq == 3)
				{
					assets.Dance.Ensure(assets);
				}
				if (assets.Seq.Ready)
				{
					float num31 = (float)scale / 100f;
					double num32 = m.Clock - m.HeadSeqStart;
					if (m.HeadSeq != 3 || !(num32 >= 1.0))
					{
						Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Seq.Width * num31) / 2f), (int)((float)num4 - (float)assets.Seq.Height * num31), (int)((float)assets.Seq.Width * num31), (int)((float)assets.Seq.Height * num31)), g: graphics2, b: assets.Seq.Frame(m.HeadSeq - 1), alpha: 1f);
					}
					else
					{
						Draw(graphics2, assets.Frame(0), rectangle, 1f);
					}
				}
			}
			else if (m.Resting)
			{
				Draw(graphics2, assets.Frame(assets.Count - 1), rectangle, 1f);
			}
			else if (m.Waking)
			{
				float num33 = (float)Math.Min(1.0, (m.Clock - m.WakeStart) / 0.24);
				Draw(graphics2, assets.Frame(assets.Count - 1), rectangle, 1f - num33);
				using Bitmap b = CompositeEyes(assets, mirror, m.Look, m.LookV);
				Draw(graphics2, b, rectangle, num33);
			}
			else if (m.Sleeping)
			{
				assets.Rest.Ensure(assets);
				assets.Rest2.Ensure(assets);
				double num34 = m.Clock - m.SleepStart;
				if (assets.Rest.Ready && num34 < (double)assets.Rest.Count / assets.Rest.Fps)
				{
					int index2 = Math.Min(assets.Rest.Count - 1, (int)(num34 * assets.Rest.Fps));
					float num35 = (float)scale / 100f;
					Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Rest.Width * num35) / 2f), (int)((float)num4 - (float)assets.Rest.Height * num35), (int)((float)assets.Rest.Width * num35), (int)((float)assets.Rest.Height * num35)), g: graphics2, b: assets.Rest.Frame(index2), alpha: 1f);
				}
				else if (assets.Rest2.Ready)
				{
					float num36 = (float)scale / 100f;
					Draw(rect: new Rectangle((int)(((float)num2 - (float)assets.Rest2.Width * num36) / 2f), (int)((float)num4 - (float)assets.Rest2.Height * num36), (int)((float)assets.Rest2.Width * num36), (int)((float)assets.Rest2.Height * num36)), g: graphics2, b: assets.Rest2.Frame(0), alpha: 1f);
				}
				else
				{
					Draw(graphics2, assets.Blink(0.0, assets.BlinkCount - 1), rectangle, 1f);
				}
			}
			else if (m.Idling && m.IdleKind == 5)
			{
				assets.IdleGif.Ensure(assets);
				if (assets.IdleGif.Ready)
				{
					float num37 = (float)scale / 100f;
					float num38 = (float)assets.Height * num37;
					float num39 = (float)assets.IdleGif.Width * num38 / (float)assets.IdleGif.Height;
					Rectangle rect2 = new Rectangle((int)(((float)num2 - num39) / 2f), (int)((float)num4 - num38), (int)num39, (int)num38);
					Bitmap bitmap3 = assets.IdleGif.Frame((int)((m.Clock - m.IdleStart) * assets.IdleGif.Fps) % assets.IdleGif.Count, assets);
					if (bitmap3 != null)
					{
						Draw(graphics2, bitmap3, rect2, 1f);
					}
				}
			}
			else if (m.Idling && m.IdleKind == 6)
			{
				assets.Sad.Ensure(assets);
				if (assets.Sad.Ready)
				{
					float num37b = (float)scale / 100f;
					float num38b = (float)assets.Height * num37b;
					float num39b = (float)assets.Sad.Width * num38b / (float)assets.Sad.Height;
					Rectangle rect2b = new Rectangle((int)(((float)num2 - num39b) / 2f), (int)((float)num4 - num38b), (int)num39b, (int)num38b);
					Draw(graphics2, assets.Sad.Frame(0), rect2b, 1f);
				}
			}
			else if (!m.Playing)
			{
				Bitmap bitmap4 = (m.Blinking ? assets.Blink(look, (int)((m.Clock - m.BlinkStart) * assets.BlinkFps)) : CompositeEyes(assets, mirror, m.Look, m.LookV));
				if (m.Poking)
				{
					using Bitmap b2 = WarpBelly(bitmap4, m.PokeX, m.PokeY, m.PokeAmount);
					Draw(graphics2, b2, rectangle, 1f);
				}
				else
				{
					Draw(graphics2, bitmap4, rectangle, 1f);
				}
			}
			else if (m.Elapsed < 0.1)
			{
				using (Bitmap b3 = CompositeEyes(assets, mirror, m.Look, m.LookV))
				{
					Draw(graphics2, b3, rectangle, 1f);
				}
				Draw(graphics2, assets.Frame((int)(m.Elapsed * assets.Fps)), rectangle, (float)(m.Elapsed / 0.1));
			}
			else
			{
				Draw(graphics2, assets.Frame((int)(m.Elapsed * assets.Fps)), rectangle, 1f);
			}
			graphics2.ResetTransform();
			if (bubble != null && !rotateRight)
			{
				DrawBubble(graphics2, bubble, num2, 2f);
			}
		}
		return Downsample(bitmap2, num2 / 2, num5 / 2);
	}

	private static Bitmap Downsample(Bitmap big, int tw, int th)
	{
		if (big.Width == tw && big.Height == th)
		{
			return big;
		}
		Bitmap bitmap = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.Clear(Color.Transparent);
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			graphics.DrawImage(big, new Rectangle(0, 0, tw, th), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel);
		}
		big.Dispose();
		return bitmap;
	}

	private static void DrawBubble(Graphics g, string text, int canvasWidth, float sf)
	{
		Font font = null;
		try
		{
			font = new Font("Microsoft YaHei UI", 11f * sf);
			SizeF sizeF = g.MeasureString(text, font);
			while (sizeF.Width + 28f * sf > (float)canvasWidth - 16f * sf && font.Size > 6.5f * sf)
			{
				font.Dispose();
				font = new Font("Microsoft YaHei UI", font.Size - 0.8f * sf);
				sizeF = g.MeasureString(text, font);
			}
			float num = Math.Min((float)canvasWidth - 16f * sf, sizeF.Width + 28f * sf);
			float num2 = 40f * sf;
			float num3 = ((float)canvasWidth - num) / 2f;
			float num4 = 6f * sf;
			using (GraphicsPath path = RoundedRect(num3, num4, num, num2, 10f * sf))
			{
				using (SolidBrush brush = new SolidBrush(Color.FromArgb(216, 255, 255, 255)))
				{
					g.FillPath(brush, path);
				}
				using Pen pen = new Pen(Color.FromArgb(140, 90, 90, 90), 1f * sf);
				g.DrawPath(pen, path);
			}
			PointF[] points = new PointF[3]
			{
				new PointF(num3 + num / 2f - 7f * sf, num4 + num2),
				new PointF(num3 + num / 2f + 7f * sf, num4 + num2),
				new PointF(num3 + num / 2f, num4 + num2 + 11f * sf)
			};
			using (SolidBrush brush2 = new SolidBrush(Color.FromArgb(216, 255, 255, 255)))
			{
				g.FillPolygon(brush2, points);
			}
			using SolidBrush brush3 = new SolidBrush(Color.FromArgb(235, 45, 45, 45));
			g.DrawString(text, font, brush3, ((float)canvasWidth - sizeF.Width) / 2f, num4 + (num2 - sizeF.Height) / 2f);
		}
		finally
		{
			font?.Dispose();
		}
	}

	private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		graphicsPath.AddArc(x, y, r, r, 180f, 90f);
		graphicsPath.AddArc(x + w - r, y, r, r, 270f, 90f);
		graphicsPath.AddArc(x + w - r, y + h - r, r, r, 0f, 90f);
		graphicsPath.AddArc(x, y + h - r, r, r, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}

	internal static Bitmap WarpBelly(Bitmap original, double cx, double cy, double amount)
	{
		int width = original.Width;
		int height = original.Height;
		byte[] array = new byte[width * height * 4];
		BitmapData bitmapData = original.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
		try
		{
			Marshal.Copy(bitmapData.Scan0, array, 0, array.Length);
		}
		finally
		{
			original.UnlockBits(bitmapData);
		}
		byte[] array2 = (byte[])array.Clone();
		int num = Math.Max(0, (int)(cx - 96.0));
		int num2 = Math.Min(width - 2, (int)(cx + 96.0));
		int num3 = Math.Max(105, (int)(cy - 72.0));
		int num4 = Math.Min(Math.Min(300, height - 2), (int)(cy + 72.0));
		for (int i = num3; i <= num4; i++)
		{
			for (int j = num; j <= num2; j++)
			{
				double num5 = (double)j - cx;
				double num6 = (double)i - cy;
				double num7 = num5 * num5 / 9216.0 + num6 * num6 / 5184.0;
				if (!(num7 >= 1.0))
				{
					double num8 = Math.Min(1.0, (double)(i - 105) / 30.0);
					double num9 = Math.Min(1.0, (double)(300 - i) / 20.0);
					double num10 = num8 * num8 * (3.0 - 2.0 * num8) * num9 * num9 * (3.0 - 2.0 * num9);
					double num11 = (1.0 - num7) * (1.0 - num7) * amount * num10;
					double num12 = Math.Max(0.0, Math.Min((double)width - 1.001, (double)j + num5 * num11));
					double num13 = Math.Max(0.0, Math.Min((double)height - 1.001, (double)i + num6 * num11 * 0.6));
					int num14 = Math.Max(0, Math.Min(width - 2, (int)num12));
					int num15 = Math.Max(0, Math.Min(height - 2, (int)num13));
					double num16 = num12 - (double)num14;
					double num17 = num13 - (double)num15;
					int num18 = (i * width + j) * 4;
					int num19 = (num15 * width + num14) * 4;
					int num20 = num19 + 4;
					int num21 = num19 + width * 4;
					int num22 = num21 + 4;
					for (int k = 0; k < 4; k++)
					{
						array2[num18 + k] = (byte)Math.Max(0.0, Math.Min(255.0, ((double)(int)array[num19 + k] * (1.0 - num16) + (double)(int)array[num20 + k] * num16) * (1.0 - num17) + ((double)(int)array[num21 + k] * (1.0 - num16) + (double)(int)array[num22 + k] * num16) * num17));
					}
				}
			}
		}
		Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
		bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
		try
		{
			Marshal.Copy(array2, 0, bitmapData.Scan0, array2.Length);
		}
		finally
		{
			bitmap.UnlockBits(bitmapData);
		}
		return bitmap;
	}

	private static void Draw(Graphics g, Bitmap b, Rectangle rect, float alpha)
	{
		if (alpha <= 0f)
		{
			return;
		}
		using ImageAttributes imageAttributes = new ImageAttributes();
		imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
		if (alpha < 1f)
		{
			ColorMatrix colorMatrix = new ColorMatrix();
			colorMatrix.Matrix33 = alpha;
			imageAttributes.SetColorMatrix(colorMatrix);
		}
		g.DrawImage(b, rect, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, imageAttributes);
	}

	private static void DrawCrowdDance(Graphics g, Assets assets, int canvasW, int contentH, float hs, double t)
	{
		Bitmap bitmap = assets.Frame(0);
		double num = (float)canvasW / 2f;
		double num2 = (float)contentH * 0.92f;
		double num3 = (double)((float)assets.Width * hs) * 0.6 * 1.25;
		double num4 = (double)((float)assets.Height * hs) * 0.6 * 1.2;
		assets.Dance.Ensure(assets);
		for (int i = 0; i < 6; i++)
		{
			int num5 = i % 3;
			int num6 = i / 3;
			double num7 = num + (double)(num5 - 1) * num3;
			double num8 = num2 - (double)num6 * num4;
			double num9 = Math.Min(1.0, t / 0.55);
			double num10 = 1.0 - Math.Pow(1.0 - num9, 3.0);
			double num11 = num + (num7 - num) * num10 + Math.Sin(t * 1.6 + (double)i) * 6.0;
			double num12 = (double)contentH * 0.3 - Math.Sin(num9 * Math.PI) * (double)contentH * 0.16 + (num8 - (double)contentH * 0.3) * num10;
			double num13 = 0.0;
			if (t >= 0.55)
			{
				double num14 = t - 0.55;
				if (num14 < 1.2)
				{
					num13 = Math.Abs(Math.Sin(num14 * 4.2)) * (double)contentH * 0.06 * Math.Max(0.0, 1.0 - num14 * 1.2);
				}
			}
			num12 -= num13;
			double num15 = 0.6;
			Bitmap image = bitmap;
			if (t >= 1.05 && assets.Dance.Ready)
			{
				int index = ((int)((t - 1.05) * 15.0) + i * 7) % assets.Dance.Count;
				image = assets.Dance.Frame(index);
				num15 = 0.6 + 0.03 * Math.Sin(t * 6.0 + (double)i);
				double num16 = Math.Min(1.0, (t - 1.05) / 0.6);
				double num17 = (double)((float)canvasW / 2f) + (Math.Sin(t * 1.7 + (double)i * 2.1) * 0.36 + Math.Sin(t * 3.1 + (double)i * 0.8) * 0.15) * (double)canvasW;
				double num18 = 1.35 + 0.12 * (double)(i % 3);
				double num19 = (t - 1.05 + (double)i * 0.41) % num18 / num18;
				double num20 = 4.0 * (double)contentH * 0.15 * num19 * (1.0 - num19);
				num11 = num7 + (num17 - num7) * num16;
				num12 = num2 - num20;
			}
			double num21 = (double)((float)assets.Width * hs) * num15;
			double num22 = (double)((float)assets.Height * hs) * num15;
			g.TranslateTransform((float)num11, (float)num12);
			g.DrawImage(image, (0f - (float)num21) / 2f, (0f - (float)num22) / 2f, (float)num21, (float)num22);
			g.ResetTransform();
		}
	}
}
