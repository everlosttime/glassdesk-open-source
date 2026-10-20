internal static class DiagnosticMath
{
    internal static double ReadbackMiBPerSecond(int bytesPerFrame, int frames, double elapsedSeconds)
        => (double)bytesPerFrame * frames / elapsedSeconds / 1024 / 1024;
    internal static object Distribution(IEnumerable<double> samples)
    {
        var values=samples.Order().ToArray();
        if(values.Length==0) return new { count=0,meanMs=(double?)null,p50Ms=(double?)null,p95Ms=(double?)null,p99Ms=(double?)null,maxMs=(double?)null };
        double P(double p)=>values[Math.Clamp((int)Math.Ceiling(values.Length*p)-1,0,values.Length-1)];
        return new { count=values.Length,meanMs=(double?)values.Average(),p50Ms=(double?)P(.5),p95Ms=(double?)P(.95),p99Ms=(double?)P(.99),maxMs=(double?)values[^1] };
    }
}
