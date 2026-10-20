using System.Windows;

namespace GlassDesk.UI.Controls;

internal enum LiquidGlassDispersionChannel
{
    Red,
    Green,
    Blue,
}

internal enum LiquidGlassDispersionRegion
{
    Face,
    Inner,
    Outer,
}

/// <summary>Channel-specific resampling on the existing dual-interface glass ray.</summary>
internal static class LiquidGlassDispersion
{
    private const double BaseUvDiagnosticRangeDip = 4;
    private const double DispersionUvDiagnosticRangeDip = .04;

    internal static double ChannelIor(double centerIor, double dispersion,
        LiquidGlassDispersionChannel channel)
    {
        if (!double.IsFinite(centerIor) || centerIor < 1)
            centerIor = LiquidGlassOptics.DefaultIndexOfRefraction;
        dispersion = double.IsFinite(dispersion) ? Math.Max(0, dispersion) : 0;
        return channel switch
        {
            LiquidGlassDispersionChannel.Red => Math.Max(1, centerIor - dispersion),
            LiquidGlassDispersionChannel.Green => centerIor,
            LiquidGlassDispersionChannel.Blue => centerIor + dispersion,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    internal static byte[] Apply(byte[] sourceBgra, byte[] chapter09Bgra,
        LiquidGlassOptics.Sample[] samples, int pixelWidth, int pixelHeight,
        Size sampleSizeDip, Rect glassBoundsDip, double normalTransitionDip,
        double glassThicknessDip, double backgroundDistanceDip, double refractionStrength,
        LiquidGlassSamplingParameters samplingParameters,
        LiquidGlassWeightParameters weightParameters, LiquidGlassLodParameters lodParameters,
        LiquidGlassDispersionParameters dispersionParameters, LiquidGlassDebugView debugView,
        double indexOfRefraction)
    {
        ArgumentNullException.ThrowIfNull(sourceBgra);
        ArgumentNullException.ThrowIfNull(chapter09Bgra);
        ArgumentNullException.ThrowIfNull(samples);
        if (sourceBgra.Length != pixelWidth * pixelHeight * 4
            || chapter09Bgra.Length != sourceBgra.Length || samples.Length < pixelWidth * pixelHeight)
            throw new ArgumentException("Dispersion inputs must describe the same complete BGRA frame.");

        dispersionParameters = dispersionParameters.Normalize();
        if (debugView == LiquidGlassDebugView.DispersionOff
            || dispersionParameters.DispersionStrength <= 0)
            return chapter09Bgra;

        var strengthScale = debugView == LiquidGlassDebugView.DispersionTimesTenDebug ? 10 : 1;
        var strength = dispersionParameters.DispersionStrength * strengthScale;
        var forceInnerOnly = debugView == LiquidGlassDebugView.InnerDispersionOnly;
        var forceOuterOnly = debugView == LiquidGlassDebugView.OuterDispersionOnly;
        var sampling = samplingParameters.Normalize();
        var weightsTuning = weightParameters.Normalize();
        var lod = lodParameters.Normalize();
        var pixelDipX = sampleSizeDip.Width / pixelWidth;
        var pixelDipY = sampleSizeDip.Height / pixelHeight;
        var physicalPerDipY = pixelHeight / sampleSizeDip.Height;
        var centerPixelX = (glassBoundsDip.X + glassBoundsDip.Width / 2) * pixelWidth
            / sampleSizeDip.Width - .5;
        var centerPixelY = (glassBoundsDip.Y + glassBoundsDip.Height / 2) * pixelHeight
            / sampleSizeDip.Height - .5;
        var faceStartDip = normalTransitionDip * 1.5;
        var faceEndDip = normalTransitionDip * 3;
        var faceOffsetX = (float)(lod.FaceFootprintDip * pixelWidth / sampleSizeDip.Width);
        var faceOffsetY = (float)(lod.FaceFootprintDip * pixelHeight / sampleSizeDip.Height);
        var innerOffsetX = (float)(lod.InnerFootprintDip * pixelWidth / sampleSizeDip.Width);
        var innerOffsetY = (float)(lod.InnerFootprintDip * pixelHeight / sampleSizeDip.Height);
        var outerOffsetX = (float)(lod.OuterFootprintDip * pixelWidth / sampleSizeDip.Width);
        var outerOffsetY = (float)(lod.OuterFootprintDip * pixelHeight / sampleSizeDip.Height);
        var result = (byte[])chapter09Bgra.Clone();

        for (var y = 0; y < pixelHeight; y++)
        for (var x = 0; x < pixelWidth; x++)
        {
            var pixel = y * pixelWidth + x;
            var sample = samples[pixel];
            if (!sample.Inside) continue;

            var weights = LiquidGlassWeights.FromSdf(sample, pixelDipX, pixelDipY, weightsTuning);
            var redFace = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Face,
                strength, dispersionParameters, forceInnerOnly, forceOuterOnly);
            var redInner = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Inner,
                strength, dispersionParameters, forceInnerOnly, forceOuterOnly);
            var redOuter = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Outer,
                strength, dispersionParameters, forceInnerOnly, forceOuterOnly);
            var blueFace = redFace;
            var blueInner = redInner;
            var blueOuter = redOuter;
            if (redFace + redInner + redOuter <= 1e-12) continue;

            var faceFootprint = (faceOffsetX, faceOffsetY, lod.FaceFootprintDip);
            var innerFootprint = (innerOffsetX, innerOffsetY, lod.InnerFootprintDip);
            var outerFootprint = (outerOffsetX, outerOffsetY, lod.OuterFootprintDip);
            var red = BlendRegionChannel(LiquidGlassDispersionChannel.Red, 2,
                redFace, redInner, redOuter);
            var blue = BlendRegionChannel(LiquidGlassDispersionChannel.Blue, 0,
                blueFace, blueInner, blueOuter);
            var index = pixel * 4;
            result[index] = blue;
            result[index + 2] = red;

            byte BlendRegionChannel(LiquidGlassDispersionChannel channel, int sourceChannel,
                double faceDelta, double innerDelta, double outerDelta)
            {
                var faceUv = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Face,
                    pixelWidth, pixelHeight, channel, faceDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    sampling, indexOfRefraction, glassThicknessDip, backgroundDistanceDip,
                    physicalPerDipY, refractionStrength);
                var innerUv = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Inner,
                    pixelWidth, pixelHeight, channel, innerDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    sampling, indexOfRefraction, glassThicknessDip, backgroundDistanceDip,
                    physicalPerDipY, refractionStrength);
                var outerUv = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Outer,
                    pixelWidth, pixelHeight, channel, outerDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    sampling, indexOfRefraction, glassThicknessDip, backgroundDistanceDip,
                    physicalPerDipY, refractionStrength);
                var faceColor = ReadFootprint(sourceBgra, faceUv.X, faceUv.Y, sourceChannel,
                    pixelWidth, pixelHeight, faceFootprint.Item1, faceFootprint.Item2, faceFootprint.Item3);
                var innerColor = ReadFootprint(sourceBgra, innerUv.X, innerUv.Y, sourceChannel,
                    pixelWidth, pixelHeight, innerFootprint.Item1, innerFootprint.Item2, innerFootprint.Item3);
                var outerColor = ReadFootprint(sourceBgra, outerUv.X, outerUv.Y, sourceChannel,
                    pixelWidth, pixelHeight, outerFootprint.Item1, outerFootprint.Item2, outerFootprint.Item3);
                return (byte)Math.Clamp((int)Math.Round(faceColor * weights.Face
                    + innerColor * weights.Inner + outerColor * weights.Outer), 0, 255);
            }
        }

        return result;
    }

    internal static byte[] RenderRayUv(LiquidGlassOptics.Sample[] samples,
        int pixelWidth, int pixelHeight, Size sampleSizeDip, Rect glassBoundsDip,
        double normalTransitionDip, double glassThicknessDip, double backgroundDistanceDip,
        double refractionStrength, LiquidGlassSamplingParameters samplingParameters,
        LiquidGlassWeightParameters weightParameters, LiquidGlassDispersionParameters dispersionParameters,
        double indexOfRefraction, LiquidGlassDispersionChannel channel)
    {
        ArgumentNullException.ThrowIfNull(samples);
        dispersionParameters = dispersionParameters.Normalize();
        var strength = dispersionParameters.DispersionStrength;
        var pixelDipX = sampleSizeDip.Width / pixelWidth;
        var pixelDipY = sampleSizeDip.Height / pixelHeight;
        var physicalPerDipY = pixelHeight / sampleSizeDip.Height;
        var centerPixelX = (glassBoundsDip.X + glassBoundsDip.Width / 2) * pixelWidth
            / sampleSizeDip.Width - .5;
        var centerPixelY = (glassBoundsDip.Y + glassBoundsDip.Height / 2) * pixelHeight
            / sampleSizeDip.Height - .5;
        var faceStartDip = normalTransitionDip * 1.5;
        var faceEndDip = normalTransitionDip * 3;
        samplingParameters = samplingParameters.Normalize();
        weightParameters = weightParameters.Normalize();
        var output = new byte[pixelWidth * pixelHeight * 4];

        for (var y = 0; y < pixelHeight; y++)
        for (var x = 0; x < pixelWidth; x++)
        {
            var pixel = y * pixelWidth + x;
            var sample = samples[pixel];
            if (!sample.Inside) continue;
            var weights = LiquidGlassWeights.FromSdf(sample, pixelDipX, pixelDipY, weightParameters.Normalize());
            var uv = PointFromRegions(channel);
            var reference = channel == LiquidGlassDispersionChannel.Green
                ? default
                : PointFromRegions(LiquidGlassDispersionChannel.Green);
            var differential = channel != LiquidGlassDispersionChannel.Green;
            var offsetX = differential ? (uv.X - reference.X) * pixelDipX : (uv.X - x) * pixelDipX;
            var offsetY = differential ? (uv.Y - reference.Y) * pixelDipY : (uv.Y - y) * pixelDipY;
            var index = pixel * 4;
            var rayColor = EncodeRayVector(offsetX, offsetY,
                differential ? DispersionUvDiagnosticRangeDip : BaseUvDiagnosticRangeDip);
            output[index] = rayColor.Blue;
            output[index + 1] = rayColor.Green;
            output[index + 2] = rayColor.Red;
            output[index + 3] = rayColor.Alpha;

            Point PointFromRegions(LiquidGlassDispersionChannel rayChannel)
            {
                var faceDelta = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Face,
                    strength, dispersionParameters, false, false);
                var innerDelta = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Inner,
                    strength, dispersionParameters, false, false);
                var outerDelta = DispersionForRegion(sample, weights, LiquidGlassDispersionRegion.Outer,
                    strength, dispersionParameters, false, false);
                var face = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Face,
                    pixelWidth, pixelHeight, rayChannel, faceDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    samplingParameters, indexOfRefraction, glassThicknessDip,
                    backgroundDistanceDip, physicalPerDipY, refractionStrength);
                var inner = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Inner,
                    pixelWidth, pixelHeight, rayChannel, innerDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    samplingParameters, indexOfRefraction, glassThicknessDip,
                    backgroundDistanceDip, physicalPerDipY, refractionStrength);
                var outer = RegionUv(sample, x, y, LiquidGlassDispersionRegion.Outer,
                    pixelWidth, pixelHeight, rayChannel, outerDelta, centerPixelX, centerPixelY, faceStartDip, faceEndDip,
                    samplingParameters, indexOfRefraction, glassThicknessDip,
                    backgroundDistanceDip, physicalPerDipY, refractionStrength);
                return new Point(face.X * weights.Face + inner.X * weights.Inner + outer.X * weights.Outer,
                    face.Y * weights.Face + inner.Y * weights.Inner + outer.Y * weights.Outer);
            }
        }

        return output;
    }

    private static double DispersionForRegion(in LiquidGlassOptics.Sample sample,
        LiquidGlassWeights weights, LiquidGlassDispersionRegion region, double strength,
        LiquidGlassDispersionParameters parameters, bool forceInnerOnly, bool forceOuterOnly)
    {
        if (forceInnerOnly && region != LiquidGlassDispersionRegion.Inner
            || forceOuterOnly && region != LiquidGlassDispersionRegion.Outer)
            return 0;

        var gain = region switch
        {
            LiquidGlassDispersionRegion.Face => parameters.FaceDispersionGain,
            LiquidGlassDispersionRegion.Inner => parameters.InnerDispersionGain,
            LiquidGlassDispersionRegion.Outer => parameters.OuterDispersionGain,
            _ => 0,
        };
        var regionWeight = region switch
        {
            LiquidGlassDispersionRegion.Face => weights.Face,
            LiquidGlassDispersionRegion.Inner => weights.Inner,
            LiquidGlassDispersionRegion.Outer => weights.Outer,
            _ => 0,
        };
        var normalZ = Math.Clamp(float.IsFinite(sample.SurfaceNormal.Z) ? sample.SurfaceNormal.Z : 1, 0, 1);
        var bevelMask = Math.Clamp(Math.Max(1 - Math.Clamp(sample.HeightFraction, 0, 1), 1 - normalZ), 0, 1);
        var curvatureSupport = Math.Clamp(sample.SurfaceCurvature * 5, 0, 1);
        var geometrySupport = bevelMask * (.25 + .75 * curvatureSupport);
        return Math.Clamp(strength * gain * regionWeight * geometrySupport, 0, .12);
    }

    private static Point RegionUv(in LiquidGlassOptics.Sample sample, int x, int y,
        LiquidGlassDispersionRegion region, int pixelWidth, int pixelHeight,
        LiquidGlassDispersionChannel channel, double dispersion,
        double centerPixelX, double centerPixelY, double faceStartDip, double faceEndDip,
        LiquidGlassSamplingParameters sampling, double indexOfRefraction,
        double glassThicknessDip, double backgroundDistanceDip, double physicalPerDipY,
        double refractionStrength)
    {
        var ray = RefractedUv(sample, x, y, pixelWidth, pixelHeight, channel, dispersion, indexOfRefraction,
            glassThicknessDip, backgroundDistanceDip, physicalPerDipY, refractionStrength);
        var refractionX = ray.X - x;
        var refractionY = ray.Y - y;
        return region switch
        {
            LiquidGlassDispersionRegion.Face => FaceUv(ray, sample.Depth, centerPixelX,
                centerPixelY, faceStartDip, faceEndDip, sampling.FaceOpticalCenterScale),
            LiquidGlassDispersionRegion.Inner => InnerUv(ray, sample, refractionX,
                refractionY, sampling.InnerRimBoost, sampling.InnerRimWidthDip),
            LiquidGlassDispersionRegion.Outer => OuterUv(sample, x, y, refractionX,
                refractionY, sampling.OuterRimStrength, sampling.OuterRimWidthDip),
            _ => throw new ArgumentOutOfRangeException(nameof(region)),
        };
    }

    private static Point RefractedUv(in LiquidGlassOptics.Sample sample, int x, int y,
        int pixelWidth, int pixelHeight, LiquidGlassDispersionChannel channel,
        double dispersion, double indexOfRefraction,
        double glassThicknessDip, double backgroundDistanceDip, double physicalPerDipY,
        double refractionStrength)
    {
        if (dispersion <= 0) return new Point(sample.X, sample.Y);
        var ior = ChannelIor(indexOfRefraction, dispersion, channel);
        if (ior == indexOfRefraction) return new Point(sample.X, sample.Y);
        var height = Math.Max(0, sample.HeightFraction) * glassThicknessDip * physicalPerDipY;
        if (!GlassRayTracer.TryTrace(new Point(x + .5, y + .5), sample.SurfaceNormal,
            height, backgroundDistanceDip * physicalPerDipY, ior, out var backgroundPoint))
            return new Point(sample.X, sample.Y);
        var targetX = backgroundPoint.X - .5;
        var targetY = backgroundPoint.Y - .5;
        if (targetX < 0 || targetX > pixelWidth - 1 || targetY < 0 || targetY > pixelHeight - 1)
            return new Point(sample.X, sample.Y);
        return new Point(x + (targetX - x) * refractionStrength,
            y + (targetY - y) * refractionStrength);
    }

    private static Point FaceUv(Point ray, double depth, double centerPixelX, double centerPixelY,
        double faceStartDip, double faceEndDip, double centerScale)
    {
        var scale = 1 + centerScale * SmoothStep(faceStartDip, faceEndDip, depth);
        return new Point(centerPixelX + (ray.X - centerPixelX) * scale,
            centerPixelY + (ray.Y - centerPixelY) * scale);
    }

    private static Point InnerUv(Point ray, in LiquidGlassOptics.Sample sample,
        double refractionX, double refractionY, double boost, double widthDip)
    {
        var weight = RimWeight(sample.Depth, widthDip);
        var normalDisplacement = refractionX * sample.InwardX + refractionY * sample.InwardY;
        return new Point(ray.X + sample.InwardX * normalDisplacement * boost * weight,
            ray.Y + sample.InwardY * normalDisplacement * boost * weight);
    }

    private static Point OuterUv(in LiquidGlassOptics.Sample sample, int x, int y,
        double refractionX, double refractionY, double strength, double widthDip)
    {
        var weight = RimWeight(sample.Depth, widthDip);
        var normalDisplacement = Math.Abs(refractionX * sample.InwardX + refractionY * sample.InwardY);
        return new Point(x - sample.InwardX * normalDisplacement * strength * weight,
            y - sample.InwardY * normalDisplacement * strength * weight);
    }

    private static byte ReadFootprint(byte[] source, double sampleX, double sampleY,
        int channel, int width, int height, float offsetX, float offsetY, double footprintDip)
    {
        if (footprintDip <= 0)
            return Read(source, width, height, sampleX, sampleY, channel);
        var value = Read(source, width, height, sampleX, sampleY, channel) * .5
            + (Read(source, width, height, sampleX - offsetX, sampleY, channel)
                + Read(source, width, height, sampleX + offsetX, sampleY, channel)
                + Read(source, width, height, sampleX, sampleY - offsetY, channel)
                + Read(source, width, height, sampleX, sampleY + offsetY, channel)) * .125;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte Read(byte[] source, int width, int height,
        double sampleX, double sampleY, int channel)
    {
        sampleX = Math.Clamp(sampleX, 0, width - 1);
        sampleY = Math.Clamp(sampleY, 0, height - 1);
        var x0 = (int)sampleX;
        var y0 = (int)sampleY;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = sampleX - x0;
        var fy = sampleY - y0;
        var top = source[(y0 * width + x0) * 4 + channel] * (1 - fx)
            + source[(y0 * width + x1) * 4 + channel] * fx;
        var bottom = source[(y1 * width + x0) * 4 + channel] * (1 - fx)
            + source[(y1 * width + x1) * 4 + channel] * fx;
        return (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
    }

    private static float RimWeight(float depthDip, double widthDip) =>
        SmoothStep(0, widthDip, (float)(widthDip - depthDip));

    private static float SmoothStep(double minimum, double maximum, double value)
    {
        var amount = (float)Math.Clamp((value - minimum) / Math.Max(.0001, maximum - minimum), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static (byte Blue, byte Green, byte Red, byte Alpha) EncodeRayVector(double x, double y, double range)
    {
        var magnitude = Math.Sqrt(x * x + y * y);
        var value = Math.Clamp(magnitude / range, 0, 1);
        if (value <= 0) return default;
        var hue = Math.Atan2(y, x) * 3 / Math.PI;
        if (hue < 0) hue += 6;
        var sector = (int)Math.Floor(hue) % 6;
        var fraction = hue - Math.Floor(hue);
        var descending = value * (1 - fraction);
        var ascending = value * fraction;
        var (red, green, blue) = sector switch
        {
            0 => (value, ascending, 0d),
            1 => (descending, value, 0d),
            2 => (0d, value, ascending),
            3 => (0d, descending, value),
            4 => (ascending, 0d, value),
            _ => (value, 0d, descending),
        };
        return (ToByte(blue), ToByte(green), ToByte(red), ToByte(value * 220));

        static byte ToByte(double component) =>
            (byte)Math.Clamp((int)Math.Round(component * 255), 0, 255);
    }
}
