using System.Diagnostics;

// Owned by one capture loop. Thresholds describe elapsed acquisition work, not
// display refresh or the time spent waiting for the next scheduled acquisition.
internal sealed class BackdropCadence
{
    private static readonly double[] FallbackPeriods = [1000d / 120, 1000d / 60, 1000d / 30];
    private double _displayPeriod;
    private int _tier;
    private long? _overBudgetSince;
    private long? _headroomSince;

    internal double PeriodMilliseconds => PeriodFor(_tier);

    internal void SetDisplayPeriod(double period)
    {
        if (_displayPeriod == period) return;
        _displayPeriod = period;
        _tier = 0;
        ResetObservation();
    }

    internal void ResetObservation()
    {
        _overBudgetSince = null;
        _headroomSince = null;
    }

    internal void Observe(long now, double acquisitionMilliseconds)
    {
        var slower = _tier + 1;
        while (slower <= FallbackPeriods.Length && PeriodFor(slower) <= PeriodMilliseconds) slower++;
        var faster = _tier - 1;
        while (faster >= 0 && PeriodFor(faster) >= PeriodMilliseconds) faster--;
        if (slower <= FallbackPeriods.Length && acquisitionMilliseconds > PeriodMilliseconds * 1.10)
        {
            _headroomSince = null;
            _overBudgetSince ??= now;
            if (Stopwatch.GetElapsedTime(_overBudgetSince.Value, now) >= TimeSpan.FromSeconds(2))
            {
                _tier = slower;
                ResetObservation();
            }
        }
        else if (faster >= 0 && acquisitionMilliseconds < PeriodFor(faster) * .90)
        {
            _overBudgetSince = null;
            _headroomSince ??= now;
            if (Stopwatch.GetElapsedTime(_headroomSince.Value, now) >= TimeSpan.FromSeconds(5))
            {
                _tier = faster;
                ResetObservation();
            }
        }
        else ResetObservation();
    }

    private double PeriodFor(int tier) => tier == 0 ? _displayPeriod : Math.Max(_displayPeriod, FallbackPeriods[tier - 1]);
}
