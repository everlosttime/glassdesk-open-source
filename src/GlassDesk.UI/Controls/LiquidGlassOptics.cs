using System.Windows;
using System.Numerics;

namespace GlassDesk.UI.Controls;

public sealed class LiquidGlassOptics
{
    internal readonly record struct Sample(float X, float Y, bool Inside, float Edge,
        float Depth, float HeightFraction, float InwardX, float InwardY,
        Vector3 SurfaceNormal, float SurfaceCurvature);
    private Sample[]? _map;
    private byte[]? _debugFrame;
    private LiquidGlassDebugView _debugFrameView;
    private (int Width, int Height, Size Size, Rect GlassBounds, double Radius, bool Squircle, double Strength,
        double Ior, double Thickness, double Transition, double BackgroundDistance) _key;

    public const double DefaultIndexOfRefraction = 1.46;

    public byte[] Render(byte[] sourceBgra, int pixelWidth, int pixelHeight, Size sizeDip,
        double cornerRadiusDip, bool squircle = false, double refractionStrength = 1, double detailSoftnessDip = 0,
        double indexOfRefraction = DefaultIndexOfRefraction, double glassThicknessDip = 4,
        double normalTransitionDip = 12, double backgroundDistanceDip = 8,
        LiquidGlassDebugView debugView = LiquidGlassDebugView.None,
        LiquidGlassSamplingParameters? samplingParameters = null,
        LiquidGlassWeightParameters? weightParameters = null, Rect? glassBoundsDip = null,
        LiquidGlassLodParameters? lodParameters = null,
        LiquidGlassDispersionParameters? dispersionParameters = null,
        bool deferDispersion = false)
    {
        ArgumentNullException.ThrowIfNull(sourceBgra);
        if (pixelWidth <= 0 || pixelHeight <= 0 || (long)pixelWidth * pixelHeight > 2_000_000
            || !double.IsFinite(sizeDip.Width) || !double.IsFinite(sizeDip.Height)
            || sizeDip.Width <= 0 || sizeDip.Height <= 0 || !double.IsFinite(cornerRadiusDip))
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (sourceBgra.Length != pixelWidth * pixelHeight * 4) throw new ArgumentException("Invalid BGRA frame.", nameof(sourceBgra));
        if (!double.IsFinite(indexOfRefraction) || indexOfRefraction < 1
            || !double.IsFinite(glassThicknessDip) || glassThicknessDip < 0
            || !double.IsFinite(normalTransitionDip) || normalTransitionDip <= 0
            || !double.IsFinite(backgroundDistanceDip) || backgroundDistanceDip < 0)
            throw new ArgumentOutOfRangeException(nameof(indexOfRefraction));
        if (!Enum.IsDefined(debugView)) throw new ArgumentOutOfRangeException(nameof(debugView));
        // Chapter 08 comparison modes select post-optics composition only. Keep them on
        // the existing normal refraction / LOD path if a caller passes one through here.
        debugView = LiquidGlassFinishComposer.OpticsView(debugView);
        refractionStrength = double.IsFinite(refractionStrength) ? Math.Clamp(refractionStrength, 0, 1) : 1;
        var glassBounds = glassBoundsDip ?? new Rect(sizeDip);
        if (glassBounds.IsEmpty || !double.IsFinite(glassBounds.X) || !double.IsFinite(glassBounds.Y)
            || !double.IsFinite(glassBounds.Width) || !double.IsFinite(glassBounds.Height)
            || glassBounds.Width <= 0 || glassBounds.Height <= 0
            || !new Rect(sizeDip).Contains(glassBounds))
            throw new ArgumentOutOfRangeException(nameof(glassBoundsDip));
        var tuning = (samplingParameters ?? LiquidGlassSamplingParameters.Default).Normalize();
        var weightTuning = (weightParameters ?? LiquidGlassWeightParameters.Default).Normalize();
        var lodTuning = (lodParameters ?? LiquidGlassLodParameters.Default).Normalize();
        var dispersionTuning = (dispersionParameters ?? LiquidGlassDispersionParameters.Default).Normalize();
        var key = (pixelWidth, pixelHeight, sizeDip, glassBounds, cornerRadiusDip, squircle, refractionStrength,
            indexOfRefraction, glassThicknessDip, normalTransitionDip, backgroundDistanceDip);
        if (_map is null || _key != key)
        {
            _key = key;
            _debugFrame = null;
            var sampleCount = pixelWidth * pixelHeight;
            if (_map is null || _map.Length < sampleCount) _map = new Sample[sampleCount];
            var bounds = glassBounds;
            var shoulder = Math.Min(15, Math.Min(sizeDip.Width, sizeDip.Height) * .24);
            var physicalSize = new Size(pixelWidth, pixelHeight);
            var physicalPerDipY = pixelHeight / sizeDip.Height;
            var sampleBoundsDip = new Rect(sizeDip);
            var differenceStep = Math.Clamp(normalTransitionDip / sizeDip.Height * .13, .003, .012) * sizeDip.Height;
            var flatDepth = normalTransitionDip * .82 + differenceStep;
            for (var y = 0; y < pixelHeight; y++)
            for (var x = 0; x < pixelWidth; x++)
            {
                var point = new Point((x + .5) * sizeDip.Width / pixelWidth,
                    (y + .5) * sizeDip.Height / pixelHeight);
                var inside = squircle
                    ? SquircleGlassGeometry.Contains(bounds, cornerRadiusDip, point)
                    : ContinuousGlassGeometry.Contains(bounds, cornerRadiusDip, point);
                double depth = 0;
                var inwardX = 0f; var inwardY = 0f;
                if (inside)
                {
                    if (squircle)
                    {
                        depth = -GlassSignedDistance.Squircle(bounds, cornerRadiusDip, point);
                        if (SquircleGlassGeometry.TryGetEdge(bounds, cornerRadiusDip, point, out _, out var inward))
                        { inwardX = (float)inward.X; inwardY = (float)inward.Y; }
                    }
                    else if (ContinuousGlassGeometry.TryGetEdge(bounds, cornerRadiusDip, point, out depth, out var inward))
                    { inwardX = (float)inward.X; inwardY = (float)inward.Y; }
                }
                var edge = inside ? Math.Clamp(1 - depth / Math.Max(.01, shoulder), 0, 1) : 0;
                var heightFraction = inside
                    ? (float)GlassHeightField.HeightFractionFromInsideDistance(depth, normalTransitionDip)
                    : 0;
                var sampleX = (float)x; var sampleY = (float)y;
                var surfaceNormal = Vector3.UnitZ;
                if (inside && depth <= flatDepth)
                {
                    var pixelPoint = new Point(x + .5, y + .5);
                    var field = GlassHeightField.SamplePhysical(bounds, cornerRadiusDip,
                        squircle ? GlassFieldShape.Squircle : GlassFieldShape.Continuous,
                        pixelPoint, physicalSize, sampleBoundsDip, normalTransitionDip, glassThicknessDip);
                    surfaceNormal = field.Normal;
                    if (refractionStrength > 0 && GlassRayTracer.TryTrace(pixelPoint, field.Normal,
                        field.HeightDip * physicalPerDipY, backgroundDistanceDip * physicalPerDipY,
                        indexOfRefraction, out var backgroundPoint))
                    {
                        var targetX = backgroundPoint.X - .5;
                        var targetY = backgroundPoint.Y - .5;
                        if (targetX >= 0 && targetX <= pixelWidth - 1
                            && targetY >= 0 && targetY <= pixelHeight - 1)
                        {
                            sampleX = (float)(x + (targetX - x) * refractionStrength);
                            sampleY = (float)(y + (targetY - y) * refractionStrength);
                        }
                    }
                }
                _map[y * pixelWidth + x] = new Sample(sampleX, sampleY, inside, (float)edge,
                    (float)depth, heightFraction, inwardX, inwardY, surfaceNormal, 0);
            }
            AddSurfaceCurvature(pixelWidth, pixelHeight, sizeDip);
        }
        if (debugView is LiquidGlassDebugView.RRayUv or LiquidGlassDebugView.GRayUv or LiquidGlassDebugView.BRayUv)
        {
            var channel = debugView switch
            {
                LiquidGlassDebugView.RRayUv => LiquidGlassDispersionChannel.Red,
                LiquidGlassDebugView.GRayUv => LiquidGlassDispersionChannel.Green,
                _ => LiquidGlassDispersionChannel.Blue,
            };
            return LiquidGlassDispersion.RenderRayUv(_map, pixelWidth, pixelHeight, sizeDip,
                glassBounds, normalTransitionDip, glassThicknessDip, backgroundDistanceDip,
                refractionStrength, tuning, weightTuning, dispersionTuning,
                indexOfRefraction, channel);
        }
        var isDispersionFrame = debugView is LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion or LiquidGlassDebugView.DispersionTimesTenDebug;
        if (debugView is LiquidGlassDebugView.FaceOnly or LiquidGlassDebugView.InnerOnly or LiquidGlassDebugView.OuterOnly)
            return RenderSampledColor(sourceBgra, pixelWidth, pixelHeight, sizeDip, glassBounds,
                normalTransitionDip, tuning, debugView, 0);
        if (debugView is LiquidGlassDebugView.FaceWeightOnly or LiquidGlassDebugView.InnerWeightOnly
            or LiquidGlassDebugView.OuterWeightOnly or LiquidGlassDebugView.CombinedWeightsPreview)
            return RenderWeightDebug(pixelWidth, pixelHeight, sizeDip, weightTuning, debugView);
        if (debugView is not LiquidGlassDebugView.None
            and not LiquidGlassDebugView.FinalMixedSampling
            and not LiquidGlassDebugView.LodOff
            and not LiquidGlassDebugView.FaceLodOnly
            and not LiquidGlassDebugView.InnerLodOnly
            and not LiquidGlassDebugView.OuterLodOnly
            and not LiquidGlassDebugView.FinalMixedWithLod
            && !isDispersionFrame)
            return RenderDebugView(pixelWidth, pixelHeight, sizeDip, glassBounds, cornerRadiusDip, squircle,
                glassThicknessDip, normalTransitionDip, debugView);

        // Two spatial scales remove tiny lettering/icon detail while retaining broad
        // color fields. No neutral tint is mixed into the captured colors.
        if (refractionStrength > 0)
        {
            detailSoftnessDip = double.IsFinite(detailSoftnessDip) ? Math.Clamp(detailSoftnessDip, 0, 6) : 0;
            var fine = Filter(sourceBgra, pixelWidth, pixelHeight,
                Math.Max(1, (int)Math.Round((4.2 + detailSoftnessDip) * pixelWidth / sizeDip.Width)),
                Math.Max(1, (int)Math.Round((4.2 + detailSoftnessDip) * pixelHeight / sizeDip.Height)));
            var broad = Filter(fine, pixelWidth, pixelHeight,
                Math.Max(1, (int)Math.Round(9.0 * pixelWidth / sizeDip.Width)),
                Math.Max(1, (int)Math.Round(9.0 * pixelHeight / sizeDip.Height)));
            for (var i = 0; i < fine.Length; i++)
            {
                var coarseWeight = .18 + .30 * _map[i / 4].Edge;
                fine[i] = (byte)Math.Round(sourceBgra[i] * (1 - refractionStrength)
                    + (fine[i] * (1 - coarseWeight) + broad[i] * coarseWeight) * refractionStrength);
            }
            sourceBgra = fine;
        }
        var samplingView = isDispersionFrame ? LiquidGlassDebugView.None : debugView;
        var chapter09 = RenderMixedColor(sourceBgra, pixelWidth, pixelHeight, sizeDip, glassBounds,
            normalTransitionDip, tuning, weightTuning, lodTuning, samplingView);
        if (deferDispersion || debugView == LiquidGlassDebugView.DispersionOff
            || debugView != LiquidGlassDebugView.None && !isDispersionFrame)
            return chapter09;

        return LiquidGlassDispersion.Apply(sourceBgra, chapter09, _map!, pixelWidth, pixelHeight,
            sizeDip, glassBounds, normalTransitionDip, glassThicknessDip, backgroundDistanceDip,
            refractionStrength, tuning, weightTuning, lodTuning, dispersionTuning, debugView,
            indexOfRefraction);
    }

    internal byte[] ApplyBrdf(byte[] opticalBgra, Size sampleSizeDip,
        LiquidGlassWeightParameters weightParameters, LiquidGlassBrdfParameters brdfParameters,
        LiquidGlassDebugView debugView)
    {
        if (_map is null) throw new InvalidOperationException("Optical samples must be built before BRDF composition.");
        return LiquidGlassBrdf.Apply(opticalBgra, _map, _key.Width, _key.Height,
            sampleSizeDip, weightParameters, brdfParameters,
            DefaultIndexOfRefraction, debugView);
    }

    internal byte[] ApplyDispersion(byte[] sourceBgra, byte[] finishedBgra,
        LiquidGlassSamplingParameters samplingParameters, LiquidGlassWeightParameters weightParameters,
        LiquidGlassLodParameters lodParameters, LiquidGlassDispersionParameters dispersionParameters,
        LiquidGlassDebugView debugView)
    {
        if (_map is null) throw new InvalidOperationException("Optical samples must be built before dispersion composition.");
        ArgumentNullException.ThrowIfNull(sourceBgra);
        ArgumentNullException.ThrowIfNull(finishedBgra);
        var expectedLength = checked(_key.Width * _key.Height * 4);
        if (sourceBgra.Length != expectedLength || finishedBgra.Length != expectedLength)
            throw new ArgumentException("Dispersion buffers must match the most recently rendered optical frame.");

        return LiquidGlassDispersion.Apply(sourceBgra, finishedBgra, _map,
            _key.Width, _key.Height, _key.Size, _key.GlassBounds, _key.Transition,
            _key.Thickness, _key.BackgroundDistance, _key.Strength,
            samplingParameters, weightParameters, lodParameters, dispersionParameters,
            debugView, _key.Ior);
    }

    private void AddSurfaceCurvature(int pixelWidth, int pixelHeight, Size sizeDip)
    {
        var pixelDipX = sizeDip.Width / pixelWidth;
        var pixelDipY = sizeDip.Height / pixelHeight;
        for (var y = 0; y < pixelHeight; y++)
        for (var x = 0; x < pixelWidth; x++)
        {
            var index = y * pixelWidth + x;
            var sample = _map![index];
            if (!sample.Inside || sample.Depth <= 0
                || Math.Abs(sample.InwardX) + Math.Abs(sample.InwardY) < 1e-5) continue;

            var stepX = (int)Math.Round(sample.InwardX / pixelDipX);
            var stepY = (int)Math.Round(sample.InwardY / pixelDipY);
            if (stepX == 0 && stepY == 0)
            {
                if (Math.Abs(sample.InwardX) / pixelDipX >= Math.Abs(sample.InwardY) / pixelDipY)
                    stepX = Math.Sign(sample.InwardX);
                else
                    stepY = Math.Sign(sample.InwardY);
            }
            var stepDistanceDip = Math.Sqrt(stepX * stepX * pixelDipX * pixelDipX
                + stepY * stepY * pixelDipY * pixelDipY);
            if (stepDistanceDip <= 1e-6) continue;

            var beforeX = Math.Clamp(x - stepX, 0, pixelWidth - 1);
            var beforeY = Math.Clamp(y - stepY, 0, pixelHeight - 1);
            var afterX = Math.Clamp(x + stepX, 0, pixelWidth - 1);
            var afterY = Math.Clamp(y + stepY, 0, pixelHeight - 1);
            var before = _map[beforeY * pixelWidth + beforeX].SurfaceNormal;
            var after = _map[afterY * pixelWidth + afterX].SurfaceNormal;
            var curvature = Math.Clamp(Vector3.Distance(before, after) / (2 * stepDistanceDip) * 4, 0, 1);
            _map[index] = sample with { SurfaceCurvature = (float)curvature };
        }
    }

    private byte[] RenderWeightDebug(int pixelWidth, int pixelHeight, Size sizeDip,
        LiquidGlassWeightParameters tuning, LiquidGlassDebugView debugView)
    {
        var result = new byte[pixelWidth * pixelHeight * 4];
        var pixelDipX = sizeDip.Width / pixelWidth;
        var pixelDipY = sizeDip.Height / pixelHeight;
        for (var pixel = 0; pixel < pixelWidth * pixelHeight; pixel++)
        {
            var sample = _map![pixel];
            if (!sample.Inside) continue;
            var weights = LiquidGlassWeights.FromSdf(sample, pixelDipX, pixelDipY, tuning);
            var index = pixel * 4;
            var face = EncodeWeight(weights.Face);
            var inner = EncodeWeight(weights.Inner);
            var outer = EncodeWeight(weights.Outer);
            switch (debugView)
            {
                case LiquidGlassDebugView.FaceWeightOnly:
                    result[index] = result[index + 1] = result[index + 2] = face;
                    break;
                case LiquidGlassDebugView.InnerWeightOnly:
                    result[index] = result[index + 1] = result[index + 2] = inner;
                    break;
                case LiquidGlassDebugView.OuterWeightOnly:
                    result[index] = result[index + 1] = result[index + 2] = outer;
                    break;
                case LiquidGlassDebugView.CombinedWeightsPreview:
                    result[index] = outer;
                    result[index + 1] = inner;
                    result[index + 2] = face;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(debugView));
            }
            result[index + 3] = 255;
        }
        return result;
    }

    private byte[] RenderMixedColor(byte[] sourceBgra, int pixelWidth, int pixelHeight,
        Size sampleSizeDip, Rect glassBoundsDip, double normalTransitionDip,
        LiquidGlassSamplingParameters sampling, LiquidGlassWeightParameters weightsTuning,
        LiquidGlassLodParameters lodTuning, LiquidGlassDebugView debugView)
    {
        var allLod = debugView is LiquidGlassDebugView.None or LiquidGlassDebugView.FinalMixedWithLod;
        var faceFootprintDip = allLod || debugView == LiquidGlassDebugView.FaceLodOnly
            ? lodTuning.FaceFootprintDip : 0;
        var innerFootprintDip = allLod || debugView == LiquidGlassDebugView.InnerLodOnly
            ? lodTuning.InnerFootprintDip : 0;
        var outerFootprintDip = allLod || debugView == LiquidGlassDebugView.OuterLodOnly
            ? lodTuning.OuterFootprintDip : 0;
        var face = RenderSampledColor(sourceBgra, pixelWidth, pixelHeight, sampleSizeDip,
            glassBoundsDip, normalTransitionDip, sampling, LiquidGlassDebugView.FaceOnly, faceFootprintDip);
        var inner = RenderSampledColor(sourceBgra, pixelWidth, pixelHeight, sampleSizeDip,
            glassBoundsDip, normalTransitionDip, sampling, LiquidGlassDebugView.InnerOnly, innerFootprintDip);
        var outer = RenderSampledColor(sourceBgra, pixelWidth, pixelHeight, sampleSizeDip,
            glassBoundsDip, normalTransitionDip, sampling, LiquidGlassDebugView.OuterOnly, outerFootprintDip);
        var result = new byte[sourceBgra.Length];
        var pixelDipX = sampleSizeDip.Width / pixelWidth;
        var pixelDipY = sampleSizeDip.Height / pixelHeight;
        for (var pixel = 0; pixel < pixelWidth * pixelHeight; pixel++)
        {
            var sample = _map![pixel];
            if (!sample.Inside) continue;
            var weights = LiquidGlassWeights.FromSdf(sample, pixelDipX, pixelDipY, weightsTuning);
            var index = pixel * 4;
            for (var channel = 0; channel < 3; channel++)
                result[index + channel] = (byte)Math.Clamp((int)Math.Round(
                    face[index + channel] * weights.Face
                    + inner[index + channel] * weights.Inner
                    + outer[index + channel] * weights.Outer), 0, 255);
            result[index + 3] = (byte)Math.Clamp((int)Math.Round(
                face[index + 3] * weights.Face
                + inner[index + 3] * weights.Inner
                + outer[index + 3] * weights.Outer), 0, 255);
        }
        return result;
    }

    private static byte EncodeWeight(double weight) =>
        (byte)Math.Clamp((int)Math.Round(Math.Clamp(weight, 0, 1) * 255), 0, 255);

    private byte[] RenderSampledColor(byte[] sourceBgra, int pixelWidth, int pixelHeight,
        Size sampleSizeDip, Rect glassBoundsDip, double normalTransitionDip,
        LiquidGlassSamplingParameters tuning, LiquidGlassDebugView debugView, double lodFootprintDip)
    {
        var output = new byte[sourceBgra.Length];
        lodFootprintDip = double.IsFinite(lodFootprintDip)
            ? Math.Clamp(lodFootprintDip, 0, LiquidGlassLodParameters.MaximumFootprintDip) : 0;
        var lodOffsetX = (float)(lodFootprintDip * pixelWidth / sampleSizeDip.Width);
        var lodOffsetY = (float)(lodFootprintDip * pixelHeight / sampleSizeDip.Height);
        var centerX = glassBoundsDip.X + glassBoundsDip.Width / 2;
        var centerY = glassBoundsDip.Y + glassBoundsDip.Height / 2;
        var centerPixelX = centerX * pixelWidth / sampleSizeDip.Width - .5;
        var centerPixelY = centerY * pixelHeight / sampleSizeDip.Height - .5;
        var faceStartDip = normalTransitionDip * 1.5;
        var faceEndDip = normalTransitionDip * 3;

        for (var y = 0; y < pixelHeight; y++)
        for (var x = 0; x < pixelWidth; x++)
        {
            var pixel = y * pixelWidth + x;
            var sample = _map![pixel];
            if (!sample.Inside) continue;
            var sampleX = sample.X;
            var sampleY = sample.Y;
            var refractionX = sample.X - x;
            var refractionY = sample.Y - y;

            switch (debugView)
            {
                case LiquidGlassDebugView.FaceOnly:
                {
                    var faceWeight = SmoothStep(faceStartDip, faceEndDip, sample.Depth);
                    var scale = 1 + tuning.FaceOpticalCenterScale * faceWeight;
                    sampleX = (float)(centerPixelX + (sampleX - centerPixelX) * scale);
                    sampleY = (float)(centerPixelY + (sampleY - centerPixelY) * scale);
                    break;
                }
                case LiquidGlassDebugView.InnerOnly:
                {
                    var weight = RimWeight(sample.Depth, tuning.InnerRimWidthDip);
                    var normalDisplacement = refractionX * sample.InwardX + refractionY * sample.InwardY;
                    sampleX += (float)(sample.InwardX * normalDisplacement * tuning.InnerRimBoost * weight);
                    sampleY += (float)(sample.InwardY * normalDisplacement * tuning.InnerRimBoost * weight);
                    break;
                }
                case LiquidGlassDebugView.OuterOnly:
                {
                    var weight = RimWeight(sample.Depth, tuning.OuterRimWidthDip);
                    var normalDisplacement = Math.Abs(refractionX * sample.InwardX + refractionY * sample.InwardY);
                    sampleX = (float)(x - sample.InwardX * normalDisplacement * tuning.OuterRimStrength * weight);
                    sampleY = (float)(y - sample.InwardY * normalDisplacement * tuning.OuterRimStrength * weight);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(debugView));
            }

            var index = pixel * 4;
            output[index] = ReadFootprint(sourceBgra, sampleX, sampleY, 0);
            output[index + 1] = ReadFootprint(sourceBgra, sampleX, sampleY, 1);
            output[index + 2] = ReadFootprint(sourceBgra, sampleX, sampleY, 2);
            output[index + 3] = Read(sourceBgra, sampleX, sampleY, 3);
        }
        return output;

        byte ReadFootprint(byte[] source, float sx, float sy, int channel)
        {
            if (channel == 3 || lodFootprintDip <= 0)
                return Read(source, sx, sy, channel);

            // A centered five-tap low-pass follows this region's already-computed
            // refraction coordinate. The footprint is in DIPs, so RT density only
            // changes tap placement, not the filter's physical size.
            var value = Read(source, sx, sy, channel) * .5
                + (Read(source, sx - lodOffsetX, sy, channel)
                    + Read(source, sx + lodOffsetX, sy, channel)
                    + Read(source, sx, sy - lodOffsetY, channel)
                    + Read(source, sx, sy + lodOffsetY, channel)) * .125;
            return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }

        byte Read(byte[] source, float sx, float sy, int channel)
        {
            sx = Math.Clamp(sx, 0, pixelWidth - 1);
            sy = Math.Clamp(sy, 0, pixelHeight - 1);
            var x0 = (int)sx; var y0 = (int)sy;
            var x1 = Math.Min(x0 + 1, pixelWidth - 1); var y1 = Math.Min(y0 + 1, pixelHeight - 1);
            var fx = sx - x0; var fy = sy - y0;
            var top = source[(y0 * pixelWidth + x0) * 4 + channel] * (1 - fx)
                + source[(y0 * pixelWidth + x1) * 4 + channel] * fx;
            var bottom = source[(y1 * pixelWidth + x0) * 4 + channel] * (1 - fx)
                + source[(y1 * pixelWidth + x1) * 4 + channel] * fx;
            return (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
        }
    }

    private static float RimWeight(float depthDip, double widthDip) =>
        SmoothStep(0, widthDip, (float)(widthDip - depthDip));

    private static float SmoothStep(double minimum, double maximum, double value)
    {
        var t = (float)Math.Clamp((value - minimum) / Math.Max(.0001, maximum - minimum), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private byte[] RenderDebugView(int pixelWidth, int pixelHeight, Size sizeDip, Rect glassBoundsDip,
        double cornerRadiusDip, bool squircle, double glassThicknessDip,
        double normalTransitionDip, LiquidGlassDebugView debugView)
    {
        if (_debugFrame is not null && _debugFrameView == debugView) return _debugFrame;

        var output = new byte[pixelWidth * pixelHeight * 4];
        var bounds = glassBoundsDip;
        var sampleBoundsDip = new Rect(sizeDip);
        var physicalSize = new Size(pixelWidth, pixelHeight);
        var shape = squircle ? GlassFieldShape.Squircle : GlassFieldShape.Continuous;
        const double distanceRangeDip = 16;
        const double uvComponentRangeDip = 4;
        var uvMagnitudeRangeDip = Math.Sqrt(2 * uvComponentRangeDip * uvComponentRangeDip);

        for (var y = 0; y < pixelHeight; y++)
        for (var x = 0; x < pixelWidth; x++)
        {
            var pixel = y * pixelWidth + x;
            var index = pixel * 4;
            switch (debugView)
            {
                case LiquidGlassDebugView.SignedDistance:
                {
                    var point = new Point((x + .5) * sizeDip.Width / pixelWidth,
                        (y + .5) * sizeDip.Height / pixelHeight);
                    var distance = squircle
                        ? GlassSignedDistance.Squircle(bounds, cornerRadiusDip, point)
                        : GlassSignedDistance.Continuous(bounds, cornerRadiusDip, point);
                    var gray = (byte)Math.Clamp((int)Math.Round((.5 + .5 * Math.Clamp(distance / distanceRangeDip, -1, 1)) * 255), 0, 255);
                    output[index] = output[index + 1] = output[index + 2] = gray;
                    break;
                }
                case LiquidGlassDebugView.Height:
                case LiquidGlassDebugView.Normal:
                {
                    var field = GlassHeightField.SamplePhysical(bounds, cornerRadiusDip, shape,
                        new Point(x + .5, y + .5), physicalSize, sampleBoundsDip,
                        normalTransitionDip, glassThicknessDip);
                    if (debugView == LiquidGlassDebugView.Height)
                    {
                        var height = glassThicknessDip > 0 ? field.HeightDip / glassThicknessDip : 0;
                        var gray = (byte)Math.Clamp((int)Math.Round(Math.Clamp(height, 0, 1) * 255), 0, 255);
                        output[index] = output[index + 1] = output[index + 2] = gray;
                    }
                    else
                    {
                        output[index] = EncodeNormal(field.Normal.Z);
                        output[index + 1] = EncodeNormal(field.Normal.Y);
                        output[index + 2] = EncodeNormal(field.Normal.X);
                    }
                    break;
                }
                case LiquidGlassDebugView.RefractionUvOffset:
                {
                    var sample = _map![pixel];
                    var offsetXDip = (sample.X - x) * sizeDip.Width / pixelWidth;
                    var offsetYDip = (sample.Y - y) * sizeDip.Height / pixelHeight;
                    output[index] = (byte)Math.Clamp((int)Math.Round(Math.Min(1,
                        Math.Sqrt(offsetXDip * offsetXDip + offsetYDip * offsetYDip) / uvMagnitudeRangeDip) * 255), 0, 255);
                    output[index + 1] = EncodeSignedOffset(offsetYDip, uvComponentRangeDip);
                    output[index + 2] = EncodeSignedOffset(offsetXDip, uvComponentRangeDip);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(debugView));
            }
            output[index + 3] = 255;
        }

        _debugFrameView = debugView;
        _debugFrame = output;
        return output;

        static byte EncodeNormal(float value) =>
            (byte)Math.Clamp((int)Math.Round((Math.Clamp(value, -1, 1) + 1) * 127.5), 0, 255);

        static byte EncodeSignedOffset(double value, double range) =>
            (byte)Math.Clamp((int)Math.Round((.5 + .5 * Math.Clamp(value / range, -1, 1)) * 255), 0, 255);
    }

    // Separable sliding windows are O(pixels), independent of the filter radius.
    private static byte[] Filter(byte[] source, int width, int height, int radiusX, int radiusY)
    {
        radiusX = Math.Clamp(radiusX, 1, Math.Min(width, 64));
        radiusY = Math.Clamp(radiusY, 1, Math.Min(height, 64));
        var horizontal = new byte[source.Length];
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var c = 0; c < 4; c++)
        {
            var sum = 0;
            for (var k = -radiusX; k <= radiusX; k++) sum += source[(y * width + Math.Clamp(k, 0, width - 1)) * 4 + c];
            for (var x = 0; x < width; x++)
            {
                horizontal[(y * width + x) * 4 + c] = (byte)((sum + radiusX) / (radiusX * 2 + 1));
                sum += source[(y * width + Math.Min(x + radiusX + 1, width - 1)) * 4 + c]
                    - source[(y * width + Math.Max(x - radiusX, 0)) * 4 + c];
            }
        }
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var sum = 0;
            for (var k = -radiusY; k <= radiusY; k++) sum += horizontal[(Math.Clamp(k, 0, height - 1) * width + x) * 4 + c];
            for (var y = 0; y < height; y++)
            {
                result[(y * width + x) * 4 + c] = (byte)((sum + radiusY) / (radiusY * 2 + 1));
                sum += horizontal[(Math.Min(y + radiusY + 1, height - 1) * width + x) * 4 + c]
                    - horizontal[(Math.Max(y - radiusY, 0) * width + x) * 4 + c];
            }
        }
        return result;
    }
}
