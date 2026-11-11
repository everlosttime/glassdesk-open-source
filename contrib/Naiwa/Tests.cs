using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NaiwaDesktop;

internal static class Tests
{
	private static void Check(bool value, string name, List<string> log)
	{
		if (!value)
		{
			throw new Exception("FAIL: " + name);
		}
		log.Add("PASS: " + name);
	}

	private static void Advance(Motion m, double begin, double end, bool over, Assets a)
	{
		for (double num = begin; num <= end + 1E-05; num += 1.0 / 60.0)
		{
			m.Update(num, over, a.Duration, (double)a.BlinkCount / a.BlinkFps);
		}
	}

	private static bool Equal(Bitmap a, Bitmap b, int fromY, int toY)
	{
		for (int i = fromY; i < toY; i++)
		{
			for (int j = 0; j < a.Width; j++)
			{
				if (a.GetPixel(j, i) != b.GetPixel(j, i))
				{
					return false;
				}
			}
		}
		return true;
	}

	internal static void Run(string folder, string assetDirectory)
	{
		Directory.CreateDirectory(folder);
		List<string> list = new List<string>();
		try
		{
			using (Assets assets = new Assets(assetDirectory))
			{
				Check(assets.Count == 422 && Math.Abs(assets.Fps - 30.0) < 0.01, "original 422-frame clip at 30 fps", list);
				Check(assets.Eye(0.0).GetPixel(0, 0).A == 0 && assets.Eye(1.0).GetPixel(0, 0).A == 0, "transparent idle and hover backgrounds", list);
				int num = 0;
				int num2 = 0;
				for (int i = 0; i < assets.Height; i++)
				{
					for (int j = 0; j < assets.Width; j++)
					{
						if (assets.Eye(0.0).GetPixel(j, i) != assets.Eye(1.0).GetPixel(j, i))
						{
							num++;
							if (i > 184)
							{
								num2++;
							}
						}
					}
				}
				Check(num > 10 && num2 == 0, "hover changes only eye area, body is identical", list);
				byte[] array = File.ReadAllBytes(assets.AudioPath);
				Check(array.Length > 1000 && Encoding.ASCII.GetString(array, 0, 4) == "RIFF", "original soundtrack decodes as WAV", list);
				double num3 = (double)assets.BlinkCount / assets.BlinkFps;
				Check(assets.BlinkCount == 7 && assets.BlinkFps == 30.0, "blink uses seven original video frames", list);
				for (int k = 0; k < 2; k++)
				{
					for (int l = 0; l < assets.BlinkCount; l++)
					{
						Check(Equal(assets.Eye(k), assets.Blink(k, l), 92, assets.Height), "blink body preserved, bank " + k + " frame " + l, list);
					}
				}
				Check(!Equal(assets.Eye(0.0), assets.Blink(0.0, 3), 0, 92), "closed eye differs from open eye", list);
				Motion motion = new Motion();
				Advance(motion, 0.0, 0.3, over: true, assets);
				Check(motion.Look > 0.999, "hover turns eyes toward pointer", list);
				Advance(motion, 0.32, 0.62, over: false, assets);
				Check(motion.Look < 0.001, "mouse leave restores original eyes", list);
				Check(motion.Laugh(1.0) && !motion.Laugh(1.01) && motion.PlayCount == 1, "double-click playback does not overlap or restart", list);
				motion.Update(1.0 + assets.Duration - 0.05, over: false, assets.Duration, num3);
				Check(motion.Playing, "complete source clip plays through ending", list);
				motion.Update(1.0 + assets.Duration + 0.01, over: false, assets.Duration, num3);
				Check(!motion.Playing && !motion.Resting, "laugh ends back to idle", list);
				motion.Update(100.0, over: true, assets.Duration, num3);
				Check(!motion.Resting && !motion.Playing, "idle continues after laugh", list);
				motion.Laugh(101.0);
				motion.Update(101.0 + assets.Duration + 0.001, over: false, assets.Duration, num3);
				Check(motion.Laugh(120.0) && !motion.Resting, "can laugh again from resting pose", list);
				motion.Stop();
				Check(!motion.Playing && !motion.Resting, "stop action cancels playback", list);
				motion = new Motion();
				motion.Update(4.0, over: false, assets.Duration, num3);
				Check(motion.Blinking, "idle schedules a blink", list);
				motion.Update(4.0 + num3 + 0.01, over: false, assets.Duration, num3);
				Check(!motion.Blinking && motion.NextBlink >= motion.Clock + 4.0 && motion.NextBlink <= motion.Clock + 8.0, "blink ends and schedules random 4-8 second interval", list);
				motion.Poke(5.0, 143.0, 211.0);
				Check(motion.Poking && !motion.Blinking, "poke interrupts blink", list);
				motion.Update(5.1, over: false, assets.Duration, num3);
				using (Bitmap a = Renderer.WarpBelly(assets.Eye(0.0), motion.PokeX, motion.PokeY, motion.PokeAmount))
				{
					Check(Equal(a, assets.Eye(0.0), 0, 105), "belly feedback preserves face", list);
					Check(!Equal(a, assets.Eye(0.0), 160, 260), "belly feedback visibly deforms source pixels", list);
				}
				Advance(motion, 5.11, 6.0, over: false, assets);
				Check(!motion.Poking && Math.Abs(motion.PokeAmount) < 0.0001, "belly rebound settles", list);
				Check(!motion.Poke(6.0, 125.0, 67.0), "clicking face does not poke belly", list);
				motion.Poke(6.0, 144.0, 210.0);
				motion.Laugh(6.1);
				Check(!motion.Poking && motion.Playing, "double click cancels poke before original laugh", list);
				motion.BeginDrag(7.0);
				Check(motion.Holding && !motion.Playing, "drag interrupts playback", list);
				for (int m = 1; m <= 30; m++)
				{
					motion.Drag(7.0 + (double)m / 60.0, 12.0);
					motion.Update(7.0 + (double)m / 60.0, over: true, assets.Duration, num3);
				}
				Check(motion.DragAngle > 2.0 && motion.DragAngle <= 7.0, "drag tilts toward movement and stays bounded", list);
				motion.EndDrag(7.5);
				motion.Update(7.57, over: true, assets.Duration, num3);
				Check(!motion.Holding && motion.Landing && motion.Squash > 0.01, "release starts landing bounce", list);
				Advance(motion, 7.58, 9.0, over: false, assets);
				Check(!motion.Landing && Math.Abs(motion.DragAngle) < 0.05, "drag and landing settle cleanly", list);
				Gesture gesture = new Gesture();
				Point p = new Point(100, 100);
				gesture.Down(p);
				Check(!gesture.Up(p, 1.0), "single click does not laugh", list);
				gesture.Down(p);
				Check(gesture.Up(p, 1.12), "double click triggers laugh", list);
				gesture.Down(p);
				gesture.Move(new Point(140, 160));
				Check(!gesture.Up(new Point(140, 160), 2.0), "drag does not trigger laugh", list);
				gesture.Down(p);
				Check(!gesture.Up(p, 2.1), "drag resets double-click sequence", list);
				gesture.Cancel();
				Check(!gesture.Pressed, "cancelled pointer capture releases drag", list);
				Point point = PetWindow.Clamp(new Point(-3000, -50), new Size(245, 312), new Rectangle(-1920, 0, 1920, 1040), 10);
				Check(point.X == -1920 && point.Y == 0, "negative-coordinate monitor bounds", list);
				Rectangle rectangle = new Rectangle(0, 0, 1920, 1080);
				Check(Fullscreen.Covers(rectangle, rectangle, 0, "FullscreenPlayer"), "borderless fullscreen is detected", list);
				Check(!Fullscreen.Covers(rectangle, rectangle, 12582912, "NormalWindow"), "ordinary maximized window is ignored", list);
				Check(!Fullscreen.Covers(new Rectangle(0, 0, 1920, 1040), rectangle, 0, "Video"), "window that leaves taskbar visible is not fullscreen", list);
				Check(!Fullscreen.Covers(new Rectangle(-1920, 0, 1920, 1080), rectangle, 0, "Video"), "fullscreen on another monitor is ignored", list);
				Check(Fullscreen.Covers(new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1080), 0, "Video"), "fullscreen works on negative-coordinate monitor", list);
				Check(!Fullscreen.Covers(rectangle, rectangle, 0, "WorkerW"), "desktop background is not fullscreen app", list);
				FullscreenGate fullscreenGate = new FullscreenGate();
				Check(!fullscreenGate.Update(detected: true, 0.0) && !fullscreenGate.Update(detected: false, 0.08) && !fullscreenGate.Hidden, "brief fullscreen switch does not flash pet", list);
				fullscreenGate.Update(detected: true, 1.0);
				Check(fullscreenGate.Update(detected: true, 1.2) && fullscreenGate.Hidden, "stable fullscreen hides pet", list);
				fullscreenGate.Update(detected: false, 2.0);
				Check(fullscreenGate.Update(detected: false, 2.3) && !fullscreenGate.Hidden, "leaving fullscreen restores pet", list);
				using (var managed = new PetWindow(assets, test: true, managed: true, initiallySleeping: true))
				{
					managed.InitializeManagedView(sleeping: true);
					var petMenu = (ContextMenuStrip)typeof(PetWindow).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(managed);
					Check(petMenu.Items.Count == 3 && !petMenu.Items[0].Enabled && petMenu.Items[0].Text.Contains("5/10") &&
						petMenu.Items[1].Text == "打开回收站" && petMenu.Items[2].Text == "查看动作" &&
						((ToolStripMenuItem)petMenu.Items[2]).DropDownItems.Count == 12, "pet menu contains only hunger, recycle bin, and all 12 actions", list);
					Check(ManagedOptions.TryParse("options 80 35 0 0 0", out var options), "managed options parse host values", list);
					Check(!ManagedOptions.TryParse("options 80 35 2 0 0", out _), "managed options reject invalid booleans", list);
					managed.ApplyManagedOptions(options);
					var petSettings = (Settings)typeof(PetWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(managed);
					Check(!managed.Visible && managed.ScalePercent == 80 && petSettings.Volume == 35 &&
						!managed.SoundEnabled && !petSettings.Recycle && !petSettings.AutoHide, "live options apply to a sleeping pet without revealing it", list);
					managed.EatDown();
					Check(petMenu.Items[0].Text.Contains("4/10") && !managed.Visible, "hungry command updates the read-only hunger row while asleep", list);
					Point initialLocation = managed.Location;
					managed.Show();
					Application.DoEvents();
					Check(!Native.IsWindowVisible(managed.Handle) && !managed.AllowDrop, "managed pet starts asleep without flashing or accepting drops", list);
					managed.SetManagedSleeping(false);
					Application.DoEvents();
					Check(Native.IsWindowVisible(managed.Handle) && managed.AllowDrop, "managed pet wakes and accepts drops", list);
					managed.SetManagedSleeping(true);
					managed.Show();
					Application.DoEvents();
					Check(!Native.IsWindowVisible(managed.Handle) && !managed.AllowDrop, "managed sleep hides even after a show request", list);
					managed.SetManagedSleeping(false);
					Check(managed.Location == initialLocation, "managed wake retains the pet location", list);
					managed.Motion.Laugh(0.0);
					managed.ApplyFullscreen(true);
					Check(!managed.Visible && !managed.Motion.Playing, "Naiwa fullscreen hide resets laugh state", list);
					managed.ApplyFullscreen(false);
					Check(managed.Visible && !managed.Motion.Playing, "Naiwa fullscreen return shows idle state", list);
					managed.Motion.StartHeadSeq(0.0);
					managed.Motion.StartHeadSeq(0.0);
					managed.Motion.StartHeadSeq(0.0);
					System.Threading.Thread.Sleep(1100);
					managed.TickWithForeground(IntPtr.Zero);
					CrowdWindow[] crowd = Application.OpenForms.OfType<CrowdWindow>().ToArray();
					Check(crowd.Length == 6 && crowd.All(window => window.Visible), "managed pet creates all six dance windows", list);
					managed.SetManagedSleeping(true);
					Check(crowd.All(window => !window.Visible && !window.AllowDrop), "managed sleep hides the whole dance crowd", list);
					managed.SetManagedSleeping(false);
					Check(!managed.Visible && crowd.All(window => window.Visible && window.AllowDrop), "managed wake resumes the same dance windows", list);
				}
				using (PetWindow petWindow = new PetWindow(assets, test: true))
				{
					petWindow.Show();
					Application.DoEvents();
					petWindow.RenderNow(force: true);
					Check(Native.IsWindowVisible(petWindow.Handle), "native pet window is visible", list);
					int windowLong = Native.GetWindowLong(petWindow.Handle, -20);
					Check((windowLong & 0x80000) != 0 && (windowLong & 0x80) != 0 && (windowLong & 0x8000000) != 0, "alpha transparency, no taskbar entry, no focus stealing", list);
					petWindow.SetSize(65);
					Check(petWindow.Width == assets.Width * 65 / 100, "small size renders", list);
					petWindow.SetSize(110);
					Check(petWindow.Width == assets.Width * 110 / 100, "large size renders", list);
					petWindow.SetSize(85);
					petWindow.Location = new Point(-50000, -50000);
					petWindow.SetSize(85);
					Check(Screen.FromRectangle(petWindow.Bounds).WorkingArea.IntersectsWith(petWindow.Bounds), "lost window position recovers", list);
					petWindow.SetSound(enabled: true);
					Check(petWindow.SoundEnabled, "original audio switch can be enabled", list);
					petWindow.SetSound(enabled: false);
					Check(!petWindow.SoundEnabled, "sound switch can be muted", list);
					IntPtr l2 = (IntPtr)14286968;
					Native.SendMessage(petWindow.Handle, 513, (IntPtr)1, l2);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l2);
					Check(!petWindow.Motion.Playing && petWindow.Motion.Poking, "native single belly click triggers poke", list);
					Native.SendMessage(petWindow.Handle, 515, (IntPtr)1, l2);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l2);
					Check(petWindow.Motion.Playing && !petWindow.Motion.Poking, "native double-click events replace poke with playback", list);
					petWindow.StopLaugh();
					petWindow.Motion.ClearEffects(0.0);
					petWindow.Motion.StartHeadSeq(0.0);
					petWindow.RenderNow(force: true);
					Check(petWindow.Motion.HeadSequencing && petWindow.Motion.HeadSeq == 1, "seq stage1 starts", list);
					IntPtr l3 = (IntPtr)9830510;
					Native.SendMessage(petWindow.Handle, 513, (IntPtr)1, l3);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l3);
					Native.SendMessage(petWindow.Handle, 515, (IntPtr)1, l3);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l3);
					Check(petWindow.Motion.HeadSeq == 2, "native double-click on seq image advances stage", list);
					Native.SendMessage(petWindow.Handle, 513, (IntPtr)1, l3);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l3);
					Native.SendMessage(petWindow.Handle, 515, (IntPtr)1, l3);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l3);
					Check(petWindow.Motion.HeadSeq == 3, "native double-click on seq image advances to stage3", list);
					petWindow.Motion.EndHeadSeq();
					petWindow.RenderNow(force: true);
					petWindow.Motion.Laugh(0.0);
					petWindow.Motion.Update(assets.Duration, over: false, assets.Duration, num3);
					Check(!petWindow.Motion.Playing && !petWindow.Motion.Resting, "native laugh returns to idle", list);
					IntPtr l4 = (IntPtr)19529898;
					Native.SendMessage(petWindow.Handle, 513, (IntPtr)1, l4);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l4);
					Check(!petWindow.Motion.Playing, "native click after laugh stays idle", list);
					petWindow.StopLaugh();
					Native.SendMessage(petWindow.Handle, 513, (IntPtr)1, l2);
					Native.SendMessage(petWindow.Handle, 512, (IntPtr)1, (IntPtr)11796635);
					Check(petWindow.Motion.Holding, "native drag starts feedback", list);
					Native.SendMessage(petWindow.Handle, 514, IntPtr.Zero, l2);
					Check(!petWindow.Motion.Holding && petWindow.Motion.Landing && !petWindow.Motion.Playing, "native drag release bounces without laughing", list);
					petWindow.StopLaugh();
					using (Form form = new Form())
					{
						form.FormBorderStyle = FormBorderStyle.None;
						form.StartPosition = FormStartPosition.Manual;
						form.ShowInTaskbar = false;
						form.Opacity = 0.01;
						form.Bounds = Screen.FromRectangle(petWindow.Bounds).Bounds;
						form.Show();
						Application.DoEvents();
						Check(Fullscreen.IsActive(form.Handle, petWindow.Handle, form.Bounds), "native borderless fullscreen detection", list);
						Check(!Fullscreen.IsActive(petWindow.Handle, petWindow.Handle, form.Bounds), "pet window is excluded from fullscreen detection", list);
						petWindow.TickWithForeground(form.Handle);
						Thread.Sleep(220);
						petWindow.TickWithForeground(form.Handle);
						Check(petWindow.HiddenForFullscreen && !Native.IsWindowVisible(petWindow.Handle), "native fullscreen detection automatically hides pet through timer", list);
						form.Hide();
						Check(!Fullscreen.IsActive(form.Handle, petWindow.Handle, form.Bounds), "hidden window is excluded from fullscreen detection", list);
						Thread.Sleep(180);
						petWindow.TickWithForeground(form.Handle);
						Thread.Sleep(300);
						petWindow.TickWithForeground(form.Handle);
						Check(!petWindow.HiddenForFullscreen && Native.IsWindowVisible(petWindow.Handle), "leaving fullscreen automatically restores pet through timer", list);
						form.Close();
					}
					Point location = petWindow.Location;
					petWindow.Laugh();
					petWindow.ApplyFullscreen(hidden: true);
					Check(petWindow.HiddenForFullscreen && !Native.IsWindowVisible(petWindow.Handle) && !petWindow.Motion.Playing, "fullscreen hides native pet and interrupts playback", list);
					Check(!petWindow.Laugh(), "hidden pet cannot play audio or laugh", list);
					petWindow.ApplyFullscreen(hidden: false);
					Check(!petWindow.HiddenForFullscreen && Native.IsWindowVisible(petWindow.Handle) && petWindow.Location == location, "fullscreen exit restores same position", list);
					petWindow.Motion.Laugh(0.0);
					petWindow.Motion.Update(assets.Duration, over: false, assets.Duration, num3);
					petWindow.ApplyFullscreen(hidden: true);
					petWindow.ApplyFullscreen(hidden: false);
					Check(!petWindow.Motion.Playing && !petWindow.Motion.Resting, "laugh state survives fullscreen hide and restore", list);
					petWindow.ApplyFullscreen(hidden: true);
					petWindow.SetAutoHide(value: false);
					Check(Native.IsWindowVisible(petWindow.Handle) && !petWindow.HiddenForFullscreen, "disabling automatic hide reveals pet", list);
					petWindow.StopLaugh();
					for (int n = 0; n < 12; n++)
					{
						assets.Frame(n);
					}
					int guiResources = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
					Stopwatch stopwatch = Stopwatch.StartNew();
					petWindow.Motion.Laugh(0.0);
					for (int num4 = 0; num4 < assets.Count; num4++)
					{
						petWindow.Motion.Elapsed = (double)num4 / assets.Fps;
						petWindow.RenderNow(force: true);
					}
					stopwatch.Stop();
					int guiResources2 = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
					list.Add("METRIC: all-frame decode/render average " + (stopwatch.Elapsed.TotalMilliseconds / (double)assets.Count).ToString("F2") + " ms; GDI delta " + (guiResources2 - guiResources));
					for (int num5 = 0; num5 < assets.Count; num5++)
					{
						petWindow.Motion.Elapsed = (double)num5 / assets.Fps;
						petWindow.RenderNow(force: true);
					}
					int guiResources3 = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
					list.Add("METRIC: repeat-playback GDI delta " + (guiResources3 - guiResources2));
					Check(guiResources3 - guiResources2 < 10, "repeat playback does not leak GDI handles", list);
					Check(assets.CacheCount <= 12, "decoded frame cache stays bounded", list);
					petWindow.Motion.Stop();
					petWindow.RenderNow(force: true);
					petWindow.Close();
					Check(petWindow.IsDisposed, "window closes cleanly", list);
				}
				using (Icon icon = Renderer.CreateIcon(assets.Eye(0.0)))
				{
					using FileStream outputStream = File.Create(Path.Combine(folder, "naiwa.ico"));
					icon.Save(outputStream);
				}
				motion = new Motion();
				using (Bitmap bitmap = Renderer.Render(assets, motion, 100))
				{
					bitmap.Save(Path.Combine(folder, "native-idle.png"), ImageFormat.Png);
				}
				motion.Look = 1.0;
				using (Bitmap bitmap2 = Renderer.Render(assets, motion, 100))
				{
					bitmap2.Save(Path.Combine(folder, "native-hover.png"), ImageFormat.Png);
				}
				motion.Laugh(0.0);
				motion.Elapsed = 1.0;
				using (Bitmap bitmap3 = Renderer.Render(assets, motion, 100))
				{
					bitmap3.Save(Path.Combine(folder, "native-laugh.png"), ImageFormat.Png);
				}
				motion = new Motion();
				motion.Poke(0.0, 144.0, 211.0);
				motion.Update(0.1, over: false, assets.Duration, num3);
				using (Bitmap bitmap4 = Renderer.Render(assets, motion, 100))
				{
					bitmap4.Save(Path.Combine(folder, "native-poke.png"), ImageFormat.Png);
				}
				motion.BeginDrag(1.0);
				motion.DragAngle = 6.0;
				using (Bitmap bitmap5 = Renderer.Render(assets, motion, 100))
				{
					bitmap5.Save(Path.Combine(folder, "native-drag.png"), ImageFormat.Png);
				}
				motion = new Motion();
				motion.BlinkStart = 0.0;
				motion.Clock = 0.1;
				using (Bitmap bitmap6 = Renderer.Render(assets, motion, 100))
				{
					bitmap6.Save(Path.Combine(folder, "native-blink.png"), ImageFormat.Png);
				}
				motion.Stop();
				motion.Resting = true;
				using (Bitmap bitmap7 = Renderer.Render(assets, motion, 100))
				{
					bitmap7.Save(Path.Combine(folder, "native-rest.png"), ImageFormat.Png);
				}
				motion = new Motion();
				motion.FeedStart = 0.0;
				Renderer.Render(assets, motion, 100);
				assets.Feed.Ensure(assets);
				Check(assets.Feed.Ready, "feed art decodes", list);
				if (assets.Feed.Ready)
				{
					using Bitmap bitmap8 = Renderer.Render(assets, motion, 100);
					Check(bitmap8.Width == assets.Width && bitmap8.Height == assets.Height + 58, "feed renders into standard canvas", list);
					bitmap8.Save(Path.Combine(folder, "native-feed.png"), ImageFormat.Png);
				}
				if (assets.Drag.Width > 0)
				{
					Check(assets.Drag.Width == assets.Width, "drag art width unified with native size", list);
				}
				motion = new Motion();
				motion.HoverOpen = true;
				motion.Clock = 0.0;
				using (Bitmap bitmap9 = Renderer.Render(assets, motion, 100))
				{
					bitmap9.Save(Path.Combine(folder, "native-hoveropen.png"), ImageFormat.Png);
				}
				motion = new Motion();
				motion.BeginDrag(1.0);
				Renderer.Render(assets, motion, 100);
				assets.Walk.Ensure(assets);
				Check(assets.Walk.Ready && assets.Walk.Count == 8, "walk art decodes 8 frames", list);
				assets.Hold.Ensure(assets);
				Check(assets.Hold.Ready && assets.Hold.Count == 1, "hold art decodes 1 frame", list);
				if (assets.Walk.Ready)
				{
					motion.BeginDrag(0.0);
					motion.Clock = 0.0;
					using Bitmap bitmap10 = Renderer.Render(assets, motion, 100);
					bitmap10.Save(Path.Combine(folder, "native-walk.png"), ImageFormat.Png);
				}
				assets.Seq.Ensure(assets);
				Check(assets.Seq.Ready && assets.Seq.Count == 3, "seq art decodes 3 frames", list);
				if (assets.Seq.Ready)
				{
					motion = new Motion();
					motion.HeadSeqStart = 0.0;
					motion.HeadSeq = 1;
					motion.Clock = 0.0;
					using (Bitmap bitmap11 = Renderer.Render(assets, motion, 100))
					{
						int num6 = 0;
						for (int num7 = 0; num7 < bitmap11.Height; num7++)
						{
							for (int num8 = 0; num8 < bitmap11.Width; num8++)
							{
								if (bitmap11.GetPixel(num8, num7).A > 16)
								{
									num6++;
									if (num6 > 5000)
									{
										break;
									}
								}
							}
						}
						Check(num6 > 5000, "seq frame1 renders non-empty", list);
						bitmap11.Save(Path.Combine(folder, "native-seq1.png"), ImageFormat.Png);
					}
					motion.HeadSeq = 3;
					motion.Clock = 1.0;
					using (Bitmap bitmap12 = Renderer.Render(assets, motion, 100))
					{
						bitmap12.Save(Path.Combine(folder, "native-seq3.png"), ImageFormat.Png);
					}
					motion.Clock = 3.0;
					using Bitmap bitmap13 = Renderer.Render(assets, motion, 100);
					bitmap13.Save(Path.Combine(folder, "native-seqdance.png"), ImageFormat.Png);
				}
				motion = new Motion();
				motion.StartWalk(0.0, 1);
				using (Bitmap bitmap14 = Renderer.Render(assets, motion, 100))
				{
					int num9 = 0;
					for (int num10 = 0; num10 < bitmap14.Height; num10++)
					{
						for (int num11 = 0; num11 < bitmap14.Width; num11++)
						{
							if (bitmap14.GetPixel(num11, num10).A > 16)
							{
								num9++;
								if (num9 > 5000)
								{
									break;
								}
							}
						}
					}
					Check(num9 > 5000, "walk branch renders frames via internal Ensure", list);
					motion.Update(0.12, over: false, assets.Duration, num3);
					using Bitmap bitmap15 = Renderer.Render(assets, motion, 100);
					int num12 = 0;
					for (int num13 = 0; num13 < Math.Min(bitmap14.Height, bitmap15.Height); num13++)
					{
						for (int num14 = 0; num14 < Math.Min(bitmap14.Width, bitmap15.Width); num14++)
						{
							if (bitmap14.GetPixel(num14, num13) != bitmap15.GetPixel(num14, num13))
							{
								num12++;
								if (num12 > 50)
								{
									break;
								}
							}
						}
					}
					Check(num12 > 50, "walk frames advance while walking", list);
				}
				motion = new Motion();
				using Bitmap bitmap16 = Renderer.Render(assets, motion, 100, mirror: false, 100, null, rotateRight: true);
				bitmap16.Save(Path.Combine(folder, "native-rotate.png"), ImageFormat.Png);
			}
			list.Add("RESULT: PASS");
		}
		catch (Exception ex)
		{
			list.Add(ex.ToString());
			list.Add("RESULT: FAIL");
			Environment.ExitCode = 1;
		}
		File.WriteAllLines(Path.Combine(folder, "self-test.txt"), list.ToArray(), Encoding.UTF8);
	}

	internal static void Demo(string folder)
	{
		Directory.CreateDirectory(folder);
		using Assets assets = new Assets(AppDomain.CurrentDomain.BaseDirectory);
		Motion motion = new Motion();
		motion.NextBlink = 0.6;
		for (int i = 0; i < 720; i++)
		{
			double num = (double)i / 30.0;
			if (i == 60)
			{
				motion.Poke(num, 143.0, 205.0);
			}
			if (i == 90)
			{
				motion.BeginDrag(num);
			}
			if (i >= 90 && i < 126)
			{
				motion.Drag(num, (i < 108) ? 5 : (-5));
			}
			if (i == 126)
			{
				motion.EndDrag(num);
			}
			if (i == 174)
			{
				motion.Laugh(num);
			}
			if (i == 666)
			{
				motion.Stand(num);
			}
			motion.Update(num, num >= 1.1 && num < 5.8, assets.Duration, (double)assets.BlinkCount / assets.BlinkFps);
			using Bitmap bitmap = Renderer.Render(assets, motion, 100);
			bitmap.Save(Path.Combine(folder, i.ToString("0000") + ".png"), ImageFormat.Png);
		}
		// 新增动作渲染验证：转手舞(idle5)
		for (int k = 0; k < 12; k++)
		{
			Motion m5 = new Motion();
			m5.NextBlink = 0.6;
			double t5 = (double)k / 15.0;
			m5.StartIdle(0.0, 5, 16000);
			m5.Update(t5, false, assets.Duration, (double)assets.BlinkCount / assets.BlinkFps);
			using Bitmap b5 = Renderer.Render(assets, m5, 100);
			b5.Save(Path.Combine(folder, "idle5_" + k.ToString("00") + ".png"), ImageFormat.Png);
		}
		// 新增动作渲染验证：哭泣(idle6)
		Motion m6 = new Motion();
		m6.NextBlink = 0.6;
		m6.StartIdle(0.0, 6, 2000);
		m6.Update(0.0, false, assets.Duration, (double)assets.BlinkCount / assets.BlinkFps);
		using Bitmap b6 = Renderer.Render(assets, m6, 100);
		b6.Save(Path.Combine(folder, "idle6_0.png"), ImageFormat.Png);
	}

	internal static void DropTest(string file)
	{
		string path = Path.Combine(Settings.Folder, "drop-test.txt");
		try
		{
			Directory.CreateDirectory(Settings.Folder);
			using Assets assets = new Assets(AppDomain.CurrentDomain.BaseDirectory);
			using PetWindow petWindow = new PetWindow(assets, test: true);
			petWindow.Show();
			Application.DoEvents();
			petWindow.RenderNow(force: true);
			bool flag = File.Exists(file);
			DataObject dataObject = new DataObject();
			dataObject.SetData(DataFormats.FileDrop, new string[1] { file });
			petWindow.SimulateHoverEnter();
			Application.DoEvents();
			petWindow.RenderNow(force: true);
			bool hoverOpen = petWindow.Motion.HoverOpen;
			bool flag2 = false;
			if (hoverOpen && assets.Drag.Ready)
			{
				Motion motion = new Motion();
				motion.HoverOpen = true;
				motion.Clock = 0.0;
				Motion motion2 = new Motion();
				motion2.Clock = 0.0;
				using Bitmap bitmap = Renderer.Render(assets, motion, 100);
				using Bitmap bitmap2 = Renderer.Render(assets, motion2, 100);
				int num = 0;
				int num2 = Math.Min(bitmap.Height, bitmap2.Height);
				BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, num2), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				BitmapData bitmapData2 = bitmap2.LockBits(new Rectangle(0, 0, bitmap2.Width, num2), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				try
				{
					int num3 = bitmapData.Stride * num2;
					byte[] array = new byte[num3];
					byte[] array2 = new byte[num3];
					Marshal.Copy(bitmapData.Scan0, array, 0, num3);
					Marshal.Copy(bitmapData2.Scan0, array2, 0, num3);
					for (int i = 0; i < num3; i += 4)
					{
						if (Math.Abs(array[i] - array2[i]) + Math.Abs(array[i + 1] - array2[i + 1]) + Math.Abs(array[i + 2] - array2[i + 2]) + Math.Abs(array[i + 3] - array2[i + 3]) > 60)
						{
							num++;
						}
					}
				}
				finally
				{
					bitmap.UnlockBits(bitmapData);
					bitmap2.UnlockBits(bitmapData2);
				}
				flag2 = num > 500;
			}
			petWindow.SimulateHoverLeave();
			Application.DoEvents();
			bool flag3 = !petWindow.Motion.HoverOpen;
			bool feeding = petWindow.Motion.Feeding;
			petWindow.OnFileDrop(null, new DragEventArgs(dataObject, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy));
			Application.DoEvents();
			petWindow.RenderNow(force: true);
			bool dragging = petWindow.Motion.Dragging;
			bool feeding2 = petWindow.Motion.Feeding;
			bool flag4 = false;
			if (dragging && assets.Drag.Ready)
			{
				Motion motion3 = new Motion();
				motion3.DragStart = 0.0;
				motion3.DragDuration = assets.Drag.Duration;
				motion3.Clock = 0.0;
				Motion motion4 = new Motion();
				motion4.Clock = 0.0;
				using Bitmap bitmap3 = Renderer.Render(assets, motion3, 100);
				using Bitmap bitmap4 = Renderer.Render(assets, motion4, 100);
				int num4 = 0;
				int num5 = Math.Min(bitmap3.Height, bitmap4.Height);
				BitmapData bitmapData3 = bitmap3.LockBits(new Rectangle(0, 0, bitmap3.Width, num5), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				BitmapData bitmapData4 = bitmap4.LockBits(new Rectangle(0, 0, bitmap4.Width, num5), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				try
				{
					int num6 = bitmapData3.Stride * num5;
					byte[] array3 = new byte[num6];
					byte[] array4 = new byte[num6];
					Marshal.Copy(bitmapData3.Scan0, array3, 0, num6);
					Marshal.Copy(bitmapData4.Scan0, array4, 0, num6);
					for (int j = 0; j < num6; j += 4)
					{
						if (Math.Abs(array3[j] - array4[j]) + Math.Abs(array3[j + 1] - array4[j + 1]) + Math.Abs(array3[j + 2] - array4[j + 2]) + Math.Abs(array3[j + 3] - array4[j + 3]) > 60)
						{
							num4++;
						}
					}
				}
				finally
				{
					bitmap3.UnlockBits(bitmapData3);
					bitmap4.UnlockBits(bitmapData4);
				}
				flag4 = num4 > 500;
			}
			bool flag5 = File.Exists(file);
			File.WriteAllText(path, "ExistedBefore=" + flag + "\r\nHoverOpen=" + hoverOpen + "\r\nHoverRendered=" + flag2 + "\r\nHoverClosed=" + flag3 + "\r\nFeedingAfterLeave=" + feeding + "\r\nDragging=" + dragging + "\r\nDragRendered=" + flag4 + "\r\nFeedingAfterDrop=" + feeding2 + "\r\nFileExistsAfter=" + flag5 + "\r\nRESULT=" + ((flag && hoverOpen && flag2 && flag3 && feeding && dragging && flag4 && !feeding2 && !flag5) ? "PASS" : "FAIL"), Encoding.UTF8);
			Console.WriteLine("drop-test: hoverOpen=" + hoverOpen + " hoverRendered=" + flag2 + " hoverClosed=" + flag3 + " feedingAfterLeave=" + feeding + " dragging=" + dragging + " dragRendered=" + flag4 + " feedingAfterDrop=" + feeding2 + " existsAfter=" + flag5);
			petWindow.Close();
		}
		catch (Exception ex)
		{
			try
			{
				File.WriteAllText(path, "ERR " + ex, Encoding.UTF8);
			}
			catch
			{
			}
			Console.WriteLine("drop-test ERR: " + ex.Message);
		}
	}
}
