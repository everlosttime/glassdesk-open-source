using System;

namespace NaiwaDesktop;

internal sealed class Motion
{
	private const double PokeDuration = 0.95;

	internal double Look;

	internal double Start = -1.0;

	internal double Elapsed;

	internal double Clock;

	internal double BlinkStart = -1.0;

	internal double NextBlink = 4.0;

	internal double WakeStart = -1.0;

	internal double PokeStart = -1.0;

	internal double PokeX = 144.0;

	internal double PokeY = 205.0;

	internal double LandingStart = -1.0;

	internal double DragAngle;

	internal double WalkStart = -1.0;

	internal int WalkDir = 1;

	internal bool Resting;

	internal bool Holding;

	internal int PlayCount;

	internal bool AutoLook;

	internal double LookGoal = 0.5;

	internal double LookV = 0.5;

	internal double LookGoalV = 0.5;

	internal double DragStart = -1.0;

	internal double DragDuration = 8.0;

	internal bool HoverOpen;

	internal double HoverStart = -1.0;

	internal double FeedStart = -1.0;

	internal double HungryStart = -1.0;

	internal double DanceStart = -1.0;

	internal double FatStart = -1.0;

	internal bool Falling;

	internal double IdleStart = -1.0;

	internal double IdleEnd;

	internal int IdleKind = -1;

	internal double SleepStart = -1.0;

	internal double HeadStart = -1.0;

	internal double CelebrateStart = -1.0;

	internal double HeadSeqStart = -1.0;

	internal int HeadSeq;

	internal double LandedWalkAt = -1.0;

	private double previous = -1.0;

	private double dragTarget;

	private double dragVelocity;

	private double lastDragTime;

	private readonly Random random = new Random();

	internal bool Playing => Start >= 0.0;

	internal bool Blinking => BlinkStart >= 0.0;

	internal bool Poking => PokeStart >= 0.0;

	internal bool Landing => LandingStart >= 0.0;

	internal bool Waking => WakeStart >= 0.0;

	internal bool Moving
	{
		get
		{
			if (!Holding && !Landing)
			{
				return Math.Abs(DragAngle) > 0.02;
			}
			return true;
		}
	}

	internal double PokeAmount
	{
		get
		{
			double num = Clock - PokeStart;
			if (!Poking || num < 0.0 || num >= 0.95)
			{
				return 0.0;
			}
			if (!(num < 0.12))
			{
				return 0.85 * Math.Exp(-4.8 * (num - 0.12)) * Math.Cos(18.0 * (num - 0.12));
			}
			return 0.85 * Math.Sin(num / 0.12 * Math.PI / 2.0);
		}
	}

	internal double Squash
	{
		get
		{
			double num = Clock - LandingStart;
			if (!Landing || !(num >= 0.0))
			{
				return 0.0;
			}
			return 0.055 * Math.Exp(-6.0 * num) * Math.Sin(19.0 * num);
		}
	}

	internal bool Dragging => DragStart >= 0.0;

	internal bool Feeding => FeedStart >= 0.0;

	internal bool Hungry => HungryStart >= 0.0;

	internal bool Dancing => DanceStart >= 0.0;

	internal bool Fat => FatStart >= 0.0;

	internal bool Sleeping => SleepStart >= 0.0;

	internal bool Idling => IdleStart >= 0.0;

	internal bool HeadPoking => HeadStart >= 0.0;

	internal bool Celebrating => CelebrateStart >= 0.0;

	internal bool HasExtra
	{
		get
		{
			if (!(IdleStart >= 0.0) && !(SleepStart >= 0.0) && !(HeadStart >= 0.0) && !(CelebrateStart >= 0.0) && !(WalkStart >= 0.0) && !(HeadSeqStart >= 0.0) && !(DanceStart >= 0.0))
			{
				return FatStart >= 0.0;
			}
			return true;
		}
	}

	internal bool Walking
	{
		get
		{
			if (!Holding)
			{
				return WalkStart >= 0.0;
			}
			return false;
		}
	}

	internal bool HeadSequencing => HeadSeqStart >= 0.0;

	internal void StartHungry(double now)
	{
		ClearEffects(now);
		WalkStart = -1.0;
		HungryStart = now;
	}

	internal void EndHungry()
	{
		HungryStart = -1.0;
	}

	internal void StartDance(double now)
	{
		ClearEffects(now);
		WalkStart = -1.0;
		DanceStart = now;
	}

	internal void EndDance()
	{
		DanceStart = -1.0;
	}

	internal void StartFat(double now)
	{
		ClearEffects(now);
		WalkStart = -1.0;
		DanceStart = -1.0;
		FatStart = now;
	}

	internal void EndFat()
	{
		FatStart = -1.0;
	}

	internal void StartDrag(double now, double duration)
	{
		DragStart = now;
		DragDuration = Math.Max(0.5, duration);
		Look = 0.0;
		LookV = 0.5;
		HoverOpen = false;
		FeedStart = -1.0;
		ClearEffects(now);
	}

	internal void StartWalk(double now, int dir)
	{
		ClearAll(now);
		WalkStart = now;
		WalkDir = ((dir > 0) ? 1 : (-1));
		LandedWalkAt = -1.0;
	}

	internal void StartWalkLanded(double now, int dir)
	{
		StartWalk(now, dir);
		LandedWalkAt = now;
	}

	internal void StartHeadSeq(double now)
	{
		if (!Playing && !Resting && !Holding && !Sleeping && !Dragging && !HoverOpen && !Feeding && HeadSeq < 3)
		{
			int headSeq = ((HeadSeq >= 3) ? 1 : (HeadSeq + 1));
			ClearAll(now);
			HeadSeq = headSeq;
			HeadSeqStart = now;
		}
	}

	internal void EndHeadSeq()
	{
		HeadSeqStart = -1.0;
		HeadSeq = 0;
	}

	internal void Update(double now, bool over, double duration, double blinkDuration)
	{
		Clock = now;
		double num = ((previous < 0.0) ? 0.0 : Math.Min(0.05, Math.Max(0.0, now - previous)));
		previous = now;
		double num2 = (AutoLook ? LookGoal : ((double)(over ? 1 : 0)));
		double num3 = num / 0.18;
		Look += Math.Max(0.0 - num3, Math.Min(num3, num2 - Look));
		double num4 = (AutoLook ? LookGoalV : 0.5);
		LookV += Math.Max(0.0 - num3, Math.Min(num3, num4 - LookV));
		if (Playing)
		{
			Elapsed = Math.Max(0.0, now - Start);
			if (Elapsed >= duration)
			{
				Start = -1.0;
				Elapsed = duration;
				Resting = false;
			}
		}
		if (Waking && now - WakeStart >= 0.24)
		{
			WakeStart = -1.0;
		}
		if (Poking && now - PokeStart >= 0.95)
		{
			PokeStart = -1.0;
		}
		if (Landing && now - LandingStart >= 0.9)
		{
			LandingStart = -1.0;
		}
		if (Blinking && now - BlinkStart >= blinkDuration)
		{
			BlinkStart = -1.0;
			ScheduleBlink(now);
		}
		if (Idling && now >= IdleEnd)
		{
			IdleStart = -1.0;
		}
		if (Sleeping && now - SleepStart >= 600.0)
		{
			SleepStart = -1.0;
		}
		if (HeadPoking && now - HeadStart >= 1.0)
		{
			HeadStart = -1.0;
		}
		if (Celebrating && now - CelebrateStart >= 2.8)
		{
			CelebrateStart = -1.0;
		}
		if (Feeding && now - FeedStart >= 1.0)
		{
			FeedStart = -1.0;
		}
		if (Hungry && now - HungryStart >= 10.0)
		{
			HungryStart = -1.0;
		}
		if (Dancing && now - DanceStart >= 8.7)
		{
			DanceStart = -1.0;
		}
		if (Fat && now - FatStart >= 6.0)
		{
			FatStart = -1.0;
		}
		if (Walking && now - WalkStart >= 4.0)
		{
			WalkStart = -1.0;
			LandedWalkAt = -1.0;
		}
		if (!Walking && LandedWalkAt >= 0.0)
		{
			LandedWalkAt = -1.0;
		}
		if (HeadSequencing && now - HeadSeqStart >= 8.0)
		{
			EndHeadSeq();
		}
		if (!Playing && !Resting && !Holding && !Landing && !Poking && !Waking && !Blinking && !HasExtra && now >= NextBlink)
		{
			BlinkStart = now;
		}
		if (!Holding || now - lastDragTime > 0.1)
		{
			dragTarget = 0.0;
		}
		int num5 = Math.Max(1, (int)Math.Ceiling(num / 0.008));
		double num6 = num / (double)num5;
		for (int i = 0; i < num5; i++)
		{
			dragVelocity += (160.0 * (dragTarget - DragAngle) - 25.0 * dragVelocity) * num6;
			DragAngle += dragVelocity * num6;
		}
		DragAngle = Math.Max(-7.0, Math.Min(7.0, DragAngle));
		if (!Holding && Math.Abs(DragAngle) < 0.02 && Math.Abs(dragVelocity) < 0.05)
		{
			DragAngle = 0.0;
			dragVelocity = 0.0;
		}
	}

	private void ScheduleBlink(double now)
	{
		NextBlink = now + 4.0 + random.NextDouble() * 4.0;
	}

	internal void ClearEffects(double now)
	{
		Clock = now;
		BlinkStart = (PokeStart = (WakeStart = (LandingStart = (IdleStart = (SleepStart = (HeadStart = (CelebrateStart = (HungryStart = (DanceStart = (FatStart = -1.0))))))))));
		Holding = false;
		DragAngle = (dragTarget = (dragVelocity = 0.0));
		ScheduleBlink(now);
	}

	private void ClearAll(double now)
	{
		DragStart = -1.0;
		HoverOpen = false;
		FeedStart = -1.0;
		WalkStart = -1.0;
		HeadSeqStart = -1.0;
		HeadSeq = 0;
		ClearEffects(now);
	}

	internal bool Laugh(double now)
	{
		if (Playing)
		{
			return false;
		}
		ClearAll(now);
		Resting = false;
		Start = now;
		Elapsed = 0.0;
		PlayCount++;
		return true;
	}

	internal void Stop()
	{
		Start = -1.0;
		Elapsed = 0.0;
		Resting = false;
		ClearEffects(Clock);
	}

	internal void Stand(double now)
	{
		if (Resting)
		{
			Resting = false;
			ClearAll(now);
			WakeStart = now;
		}
	}

	internal static bool InEye(double x, double y)
	{
		double num = (x - 145.0) / 60.0;
		double num2 = (y - 95.0) / 45.0;
		if (num * num + num2 * num2 <= 1.0)
		{
			return y >= 55.0;
		}
		return false;
	}

	internal static bool InBelly(double x, double y)
	{
		double num = (x - 107.0) / 75.0;
		double num2 = (y - 211.0) / 75.0;
		return num * num + num2 * num2 <= 1.0;
	}

	internal bool Poke(double now, double x, double y)
	{
		if (Playing || Resting || Holding || !InBelly(x, y))
		{
			return false;
		}
		ClearAll(now);
		PokeStart = now;
		PokeX = x;
		PokeY = y;
		return true;
	}

	internal void BeginDrag(double now)
	{
		Start = -1.0;
		Elapsed = 0.0;
		ClearAll(now);
		Holding = true;
		WalkStart = now;
		lastDragTime = now;
	}

	internal void Drag(double now, double dx)
	{
		if (!Holding)
		{
			BeginDrag(now);
		}
		double num = Math.Max(1.0 / 120.0, now - lastDragTime);
		lastDragTime = now;
		dragTarget = Math.Max(-7.0, Math.Min(7.0, dx / num * 0.013));
		Clock = now;
	}

	internal void EndDrag(double now)
	{
		if (Holding)
		{
			Holding = false;
			dragTarget = 0.0;
			Clock = now;
			LandingStart = now;
			ScheduleBlink(now);
		}
	}

	internal void Suspend(double now)
	{
		Start = -1.0;
		Elapsed = 0.0;
		Look = 0.0;
		LookV = 0.5;
		DragStart = -1.0;
		HoverOpen = false;
		FeedStart = -1.0;
		ClearEffects(now);
	}

	internal void StartIdle(double now, int kind, int durationMs)
	{
		ClearAll(now);
		IdleStart = now;
		IdleEnd = now + (double)durationMs / 1000.0;
		IdleKind = kind;
	}

	internal bool Sleep(double now)
	{
		if (Playing || Resting || Holding || Poking || HeadPoking || Celebrating || Dragging || HoverOpen || Feeding)
		{
			return false;
		}
		ClearAll(now);
		SleepStart = now;
		return true;
	}

	internal bool Wake(double now)
	{
		if (!Sleeping)
		{
			return false;
		}
		SleepStart = -1.0;
		BlinkStart = now;
		ScheduleBlink(now);
		return true;
	}

	internal bool HeadPoke(double now)
	{
		if (Playing || Resting || Holding || Sleeping || Celebrating || Dragging || HoverOpen || Feeding)
		{
			return false;
		}
		ClearAll(now);
		HeadStart = now;
		return true;
	}

	internal void Celebrate(double now)
	{
		ClearAll(now);
		CelebrateStart = now;
		Resting = false;
	}

	internal static bool InHead(double x, double y)
	{
		double num = (x - 145.0) / 56.0;
		double num2 = (y - 62.0) / 52.0;
		if (num * num + num2 * num2 <= 1.0)
		{
			return y >= 10.0;
		}
		return false;
	}
}
