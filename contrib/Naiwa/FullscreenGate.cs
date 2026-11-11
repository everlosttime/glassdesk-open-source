namespace NaiwaDesktop;

internal sealed class FullscreenGate
{
	internal bool Hidden;

	private bool candidate;

	private double since;

	internal bool Update(bool detected, double now)
	{
		if (candidate != detected)
		{
			candidate = detected;
			since = now;
		}
		if (candidate != Hidden && now - since >= (candidate ? 0.15 : 0.25))
		{
			Hidden = candidate;
			return true;
		}
		return false;
	}

	internal void Reset()
	{
		Hidden = (candidate = false);
		since = 0.0;
	}

	internal void Set(bool value, double now)
	{
		Hidden = (candidate = value);
		since = now;
	}
}
