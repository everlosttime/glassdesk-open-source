using System.Windows.Media;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassFinishingTests
{
    [Fact]
    public void Chapter08ExposesPostOpticsComparisonModesWithIsolatedEffects()
    {
        var comparisons = new[]
        {
            (LiquidGlassDebugView.TintOff, false, false, false, true),
            (LiquidGlassDebugView.TintOnly, true, false, false, true),
            (LiquidGlassDebugView.AmbientShadowOnly, false, true, false, false),
            (LiquidGlassDebugView.ContactShadowOnly, false, false, true, false),
            (LiquidGlassDebugView.TintAndShadowFinal, true, true, true, true),
        };

        foreach (var (view, tint, ambient, contact, baselineShadow) in comparisons)
        {
            Assert.True(LiquidGlassFinishComposer.IsComparisonView(view));
            Assert.Equal(LiquidGlassDebugView.None, LiquidGlassFinishComposer.OpticsView(view));
            Assert.Equal(tint, LiquidGlassFinishComposer.UsesTint(view));
            Assert.Equal((ambient, contact), LiquidGlassFinishComposer.ShadowSelection(view));
            Assert.Equal(baselineShadow, LiquidGlassFinishComposer.IncludesBaselineShadow(view));
        }

        Assert.False(LiquidGlassFinishComposer.IsComparisonView(LiquidGlassDebugView.FinalMixedWithLod));
        Assert.Equal((true, true), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.None));
        Assert.True(LiquidGlassFinishComposer.IncludesBaselineShadow(LiquidGlassDebugView.None));
        Assert.False(LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.FinalMixedWithLod).Ambient);
    }

    [Fact]
    public void Chapter08ZeroTintReturnsTheChapter07OpticalFrameByteForByte()
    {
        byte[] chapter07 = [21, 53, 112, 255, 177, 203, 239, 193, 0, 0, 0, 0];
        var noTint = LiquidGlassFinishingParameters.Default with { TintOpacity = 0 };

        var final = LiquidGlassFinishComposer.ApplyTint(chapter07, noTint);

        Assert.Same(chapter07, final);
        Assert.Equal(chapter07, final);
    }

    [Fact]
    public void Chapter09ModesKeepTheChapter08BaselineAndSeparateSupportDiagnostics()
    {
        var views = new[]
        {
            (LiquidGlassDebugView.BrdfOff, true, false),
            (LiquidGlassDebugView.FresnelOnly, false, true),
            (LiquidGlassDebugView.GgxSpecularOnly, false, true),
            (LiquidGlassDebugView.BrdfSupportWeight, false, true),
            (LiquidGlassDebugView.FinalWithBrdf, true, true),
        };

        foreach (var (view, tint, brdf) in views)
        {
            Assert.True(LiquidGlassFinishComposer.IsComparisonView(view));
            Assert.Equal(LiquidGlassDebugView.None, LiquidGlassFinishComposer.OpticsView(view));
            Assert.Equal(tint, LiquidGlassFinishComposer.UsesTint(view));
            Assert.Equal(brdf, LiquidGlassFinishComposer.UsesBrdf(view));
        }

        Assert.Equal((true, true), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.BrdfOff));
        Assert.Equal((true, true), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.FinalWithBrdf));
        Assert.Equal((false, false), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.BrdfSupportWeight));
        Assert.False(LiquidGlassFinishComposer.IncludesBaselineShadow(LiquidGlassDebugView.BrdfSupportWeight));
        Assert.Equal((false, false), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.FresnelOnly));
        Assert.Equal((false, false), LiquidGlassFinishComposer.ShadowSelection(LiquidGlassDebugView.GgxSpecularOnly));
    }

    [Fact]
    public void Chapter10FinalViewsRetainFinishingWhileRayDiagnosticsStayIsolated()
    {
        foreach (var view in new[]
        {
            LiquidGlassDebugView.DispersionOff,
            LiquidGlassDebugView.InnerDispersionOnly,
            LiquidGlassDebugView.OuterDispersionOnly,
            LiquidGlassDebugView.FinalWithDispersion,
        })
        {
            Assert.True(LiquidGlassFinishComposer.IsComparisonView(view));
            Assert.Equal(view, LiquidGlassFinishComposer.OpticsView(view));
            Assert.True(LiquidGlassFinishComposer.UsesTint(view));
            Assert.True(LiquidGlassFinishComposer.UsesBrdf(view));
            Assert.Equal((true, true), LiquidGlassFinishComposer.ShadowSelection(view));
            Assert.True(LiquidGlassFinishComposer.IncludesBaselineShadow(view));
        }

        foreach (var view in new[]
        {
            LiquidGlassDebugView.RRayUv,
            LiquidGlassDebugView.GRayUv,
            LiquidGlassDebugView.BRayUv,
            LiquidGlassDebugView.DispersionTimesTenDebug,
        })
        {
            Assert.False(LiquidGlassFinishComposer.IsComparisonView(view));
            Assert.Equal(view, LiquidGlassFinishComposer.OpticsView(view));
            Assert.False(LiquidGlassFinishComposer.UsesTint(view));
            Assert.False(LiquidGlassFinishComposer.UsesBrdf(view));
            Assert.Equal((false, false), LiquidGlassFinishComposer.ShadowSelection(view));
            Assert.False(LiquidGlassFinishComposer.IncludesBaselineShadow(view));
        }
    }

    [Fact]
    public void FullProductionCompositionIncludesBrdfBeforeDispersion()
    {
        Assert.True(LiquidGlassFinishComposer.UsesTint(LiquidGlassDebugView.None));
        Assert.True(LiquidGlassFinishComposer.UsesBrdf(LiquidGlassDebugView.None));
        Assert.True(LiquidGlassFinishComposer.UsesDispersion(LiquidGlassDebugView.None));
        Assert.True(LiquidGlassFinishComposer.UsesBrdf(LiquidGlassDebugView.TintAndShadowFinal));
    }

    [Fact]
    public void TintUsesSourceLuminanceAndPreservesAlphaWithoutAddingAWhiteWash()
    {
        byte[] opticalFrame =
        [
            0, 0, 0, 255,
            38, 52, 70, 193,
            255, 255, 255, 255,
            12, 80, 145, 0,
        ];
        var parameters = LiquidGlassFinishingParameters.Default with
        {
            TintColor = Color.FromRgb(226, 236, 250),
            TintOpacity = .04,
            TintLuminanceInfluence = 1,
        };

        var tinted = LiquidGlassFinishComposer.ApplyTint(opticalFrame, parameters);

        Assert.NotSame(opticalFrame, tinted);
        Assert.Equal(opticalFrame[3], tinted[3]);
        Assert.InRange(tinted[0], 0, 10);
        Assert.InRange(tinted[1], 0, 10);
        Assert.InRange(tinted[2], 0, 12);
        Assert.Equal((byte)255, tinted[8]);
        Assert.Equal((byte)255, tinted[9]);
        Assert.Equal((byte)255, tinted[10]);
        Assert.Equal(opticalFrame[11], tinted[11]);
        Assert.Equal((byte)0, tinted[15]);
        Assert.Equal(new byte[]
        {
            0, 0, 0, 255,
            38, 52, 70, 193,
            255, 255, 255, 255,
            12, 80, 145, 0,
        }, opticalFrame);
    }

    [Fact]
    public void FinishingParametersNormalizeToDocumentedRangesAndSubtleDefaults()
    {
        var defaults = LiquidGlassFinishingParameters.Default;
        var normalized = (defaults with
        {
            TintOpacity = double.NaN,
            TintLuminanceInfluence = 3,
            AmbientShadowOpacity = -1,
            AmbientShadowExtentDip = double.PositiveInfinity,
            AmbientShadowOffsetDip = 50,
            ContactShadowOpacity = .5,
            ContactShadowExtentDip = -2,
            ContactShadowOffsetDip = double.NaN,
        }).Normalize();

        Assert.Equal(defaults.TintOpacity, normalized.TintOpacity);
        Assert.Equal(1, normalized.TintLuminanceInfluence);
        Assert.Equal(0, normalized.AmbientShadowOpacity);
        Assert.Equal(defaults.AmbientShadowExtentDip, normalized.AmbientShadowExtentDip);
        Assert.Equal(LiquidGlassFinishingParameters.MaximumAmbientShadowOffsetDip, normalized.AmbientShadowOffsetDip);
        Assert.Equal(LiquidGlassFinishingParameters.MaximumContactShadowOpacity, normalized.ContactShadowOpacity);
        Assert.Equal(LiquidGlassFinishingParameters.MinimumContactShadowExtentDip, normalized.ContactShadowExtentDip);
        Assert.Equal(defaults.ContactShadowOffsetDip, normalized.ContactShadowOffsetDip);

        Assert.InRange(defaults.TintOpacity, 0, .03);
        Assert.InRange(defaults.AmbientShadowOpacity, 0, .10);
        Assert.InRange(defaults.ContactShadowOpacity, 0, .06);
    }
}
