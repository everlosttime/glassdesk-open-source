using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace NaiwaDesktop;

internal sealed class CrowdWindow : Form
{
	private readonly Assets assets;

	private readonly PetWindow owner;

	private readonly int index;

	private readonly Stopwatch watch = Stopwatch.StartNew();

	private readonly Timer timer = new Timer();

	private readonly LayeredSurface surface = new LayeredSurface();

	private readonly float fromX;

	private readonly float fromY;

	private readonly float groundY;

	private float targetX;

	private readonly int bw;

	private readonly int bh;

	private bool disposed;

	private bool audioStarted;
	private bool presentationPaused;

	protected override bool ShowWithoutActivation => true;

	protected override CreateParams CreateParams
	{
		get
		{
			CreateParams createParams = base.CreateParams;
			createParams.ExStyle |= 134742144;
			createParams.ClassStyle |= 8;
			return createParams;
		}
	}

	internal CrowdWindow(Assets a, int i, Point center, float ground, int petScale, PetWindow owner)
	{
		assets = a;
		index = i;
		this.owner = owner;
		fromX = center.X;
		fromY = center.Y;
		Rectangle workingArea = Screen.FromPoint(center).WorkingArea;
		targetX = (float)center.X + ((float)i - 2.5f) * (float)workingArea.Width * 0.13f;
		groundY = ground;
		bw = assets.Width * 6 / 10 * petScale / 100;
		bh = assets.Height * 6 / 10 * petScale / 100;
		ClientSize = new Size(bw + 24, bh + (int)((float)bh * 1.5f) + 24);
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = true;
		SetStyle(ControlStyles.UserPaint | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.AllPaintingInWmPaint, value: true);
		AllowDrop = true;
		DragEnter += (_, e) => e.Effect = CanAcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
		DragOver += (_, e) => e.Effect = CanAcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
		DragDrop += (_, e) =>
		{
			e.Effect = CanAcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
			if (e.Effect == DragDropEffects.Copy)
			{
				this.owner.FeedFromCrowd(e);
			}
		};
		Location = new Point((int)fromX - Width / 2, (int)fromY - Height / 2);
		assets.Dance.Ensure(assets);
		timer.Interval = 40;
		timer.Tick += (object param0, EventArgs param1) =>
		{
			Tick();
		};
		timer.Start();
		Show();
	}

	private bool CanAcceptFileDrop(DragEventArgs e)
	{
		if (presentationPaused || (e.AllowedEffect & DragDropEffects.Copy) == 0 ||
			e.Data?.GetDataPresent(DataFormats.FileDrop) != true)
		{
			return false;
		}
		Point point = PointToClient(new Point(e.X, e.Y));
		return surface.Hit(point.X, point.Y);
	}

	internal void SetPresentationPaused(bool paused)
	{
		if (disposed || presentationPaused == paused)
		{
			return;
		}
		presentationPaused = paused;
		AllowDrop = !paused;
		if (paused)
		{
			watch.Stop();
			timer.Stop();
			Hide();
		}
		else
		{
			watch.Start();
			Show();
			timer.Start();
		}
	}

	private void Tick()
	{
		try
		{
			if (disposed || presentationPaused || !IsHandleCreated)
			{
				return;
			}
			double totalSeconds = watch.Elapsed.TotalSeconds;
			double num = Math.Min(1.0, totalSeconds / 0.55);
			double num2 = 1.0 - Math.Pow(1.0 - num, 3.0);
			double num3 = (double)fromX + (double)(targetX - fromX) * num2 + Math.Sin(totalSeconds * 1.6 + (double)index) * 6.0;
			double num4 = (double)fromY - Math.Sin(num * Math.PI) * 130.0 + (double)(groundY - fromY) * num2;
			double num5 = 0.0;
			if (totalSeconds >= 0.55)
			{
				double num6 = totalSeconds - 0.55;
				if (num6 < 1.2)
				{
					num5 = Math.Abs(Math.Sin(num6 * 4.2)) * 55.0 * Math.Max(0.0, 1.0 - num6 * 1.2);
				}
			}
			num4 -= num5;
			double a = 0.0;
			Bitmap bitmap = assets.Frame(0);
			if (totalSeconds >= 1.05 && assets.Dance.Ready)
			{
				if (!audioStarted)
				{
					audioStarted = true;
					PetWindow.LogAudio("0号跳舞分支进入 t=" + totalSeconds.ToString("0.00"));
					if (index == 0)
					{
						try
						{
							owner.StartCrowdSound();
						}
						catch (Exception ex)
						{
							LogTick(ex);
						}
					}
				}
				Bitmap bitmap2 = assets.Dance.Frame(((int)((totalSeconds - 1.05) * 15.0) + index * 7) % assets.Dance.Count);
				if (bitmap2 != null)
				{
					bitmap = bitmap2;
				}
				double num7 = 1.35 + 0.12 * (double)(index % 3);
				double num8 = (totalSeconds - 1.05 + (double)index * 0.41) % num7 / num7;
				a = 4.0 * (double)((float)bh * 1.5f) * num8 * (1.0 - num8);
				double num9 = (Math.Sin(totalSeconds * 1.7 + (double)index * 2.1) * 0.36 + Math.Sin(totalSeconds * 3.1 + (double)index * 0.8) * 0.15) * (double)areaW();
				targetX += (float)Math.Max(-1.0, Math.Min(1.0, (num9 - (num3 - (double)fromX)) * 0.019999999552965164));
			}
			Location = new Point((int)Math.Round(num3 - (double)((float)Width / 2f)), (int)Math.Round(num4) - Height);
			using Bitmap image = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
			using (Graphics graphics = Graphics.FromImage(image))
			{
				graphics.Clear(Color.Transparent);
				graphics.CompositingMode = CompositingMode.SourceCopy;
				graphics.CompositingQuality = CompositingQuality.HighSpeed;
				graphics.SmoothingMode = SmoothingMode.HighSpeed;
				if (bitmap != null)
				{
					graphics.DrawImage(bitmap, (Width - bw) / 2, Height - bh - (int)Math.Round(a), bw, bh);
				}
			}
			surface.Update(Handle, image, Location, 255);
		}
		catch (Exception ex2)
		{
			LogTick(ex2);
			if (!disposed)
			{
			}
		}
	}

	private static void LogTick(Exception ex)
	{
		try
		{
			File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), string.Concat(DateTime.Now.ToString("s"), " CrowdWindow.Tick: ", ex, Environment.NewLine));
		}
		catch
		{
		}
	}

	private float areaW()
	{
		return Screen.FromPoint(Location).WorkingArea.Width;
	}

	protected override void Dispose(bool disposingNow)
	{
		if (disposingNow && !disposed)
		{
			disposed = true;
			timer.Stop();
			timer.Dispose();
			surface.Dispose();
		}
		base.Dispose(disposingNow);
	}
}
