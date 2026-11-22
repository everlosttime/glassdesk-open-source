using System.Diagnostics;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropCadenceTests
{
    [Fact]
    public void SustainedOverloadDropsOneTierAndFiveSecondsOfHeadroomRestoresIt()
    {
        var cadence = new BackdropCadence();
        cadence.SetDisplayPeriod(1000d / 240);

        cadence.Observe(Ticks(1), 6);
        cadence.Observe(Ticks(2.9), 6);
        Assert.Equal(1000d / 240, cadence.PeriodMilliseconds);
        cadence.Observe(Ticks(3), 6);
        Assert.Equal(1000d / 120, cadence.PeriodMilliseconds);

        cadence.Observe(Ticks(4), 2);
        cadence.Observe(Ticks(8.9), 2);
        Assert.Equal(1000d / 120, cadence.PeriodMilliseconds);
        cadence.Observe(Ticks(9), 2);
        Assert.Equal(1000d / 240, cadence.PeriodMilliseconds);
    }

    [Fact]
    public void FractionalDisplayPeriodIsRestoredAfterBackoff()
    {
        const double period = 1000d * 1001 / 60000;
        var cadence = new BackdropCadence();
        cadence.SetDisplayPeriod(period);
        cadence.Observe(Ticks(1), 20);
        cadence.Observe(Ticks(3), 20);
        Assert.Equal(1000d / 30, cadence.PeriodMilliseconds);
        cadence.Observe(Ticks(4), 4);
        cadence.Observe(Ticks(9), 4);
        Assert.Equal(period, cadence.PeriodMilliseconds);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
}
