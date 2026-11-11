using System.Security.Cryptography;
using GlassDesk.Dock.Tests.Fixtures;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class AndroidGlassBoundedFilterTests
{
    // Frozen outputs from the installed, accepted 1.5.11 renderer before this change.
    [Theory]
    [InlineData(0,"8828E85E4463A601B60002F882EBF56E35C24CD9ED6F82F476D46D2F4B8BEE6D")]
    [InlineData(1,"E5BEAE09FD84A8C08F8C3732D35A3643795A59D2C320DEF495065C936BE161D0")]
    [InlineData(2,"8FB092649F8B1B0F52AF64822609EA39B965F67622E6B221306D9C112CD90FD5")]
    [InlineData(3,"BD9C315158A87919B90F62CBDF5881FAC9D6F757F2EC10AA61A2BFE5AE9C4440")]
    [InlineData(4,"96DD10F11D464A7504270D360E06D767C764FE8A034945473BBA94730AF17BC9")]
    [InlineData(5,"7E286084DB1FB7862F664F65AACF23E6612DD2F37D29BA0557DF8751FCA53DB7")]
    [InlineData(6,"EFCE49823383968B0154333249EC5CE496915F4671078DF56B8EF5706BB18504")]
    [InlineData(7,"1A8977082FAA370F4CC977164C0973BBA3F3542F4EA397866AB4F48A82640D5D")]
    [InlineData(8,"1A57CD1160614AA15AC85DC685DBC1968011A028CAA55D879B0BA65C846DF8F1")]
    [InlineData(9,"6A6273C9C37EEFD8D2C5C2D4310677933E5A2551598DC0FEFC8B1C69F932551F")]
    [InlineData(10,"24045C10C12A89F4C11E3B88EA34558FCDF926A8C1008CD08CC33BC71407C774")]
    public void BoundedFilteringKeepsAcceptedPixelsAcrossGeometryAndMaterialChanges(int id,string expected)
    {
        var renderer=new AndroidLiquidGlassRenderer();var fixture=AndroidFilterFixtures.Get(id);
        var source=fixture.Source();var original=source.ToArray();
        var anchor=AndroidFilterFixtures.Get(0);var anchorSource=anchor.Source();
        _=anchor.Draw(renderer,anchorSource);
        var actual=fixture.Draw(renderer,source);
        Assert.Equal(expected,Convert.ToHexString(SHA256.HashData(actual)));
        Assert.Equal(original,source);
        var retained=actual.ToArray();_=anchor.Draw(renderer,anchorSource);
        Assert.Equal(retained,actual);
        Assert.Equal(expected,Convert.ToHexString(SHA256.HashData(fixture.Draw(renderer,source))));
    }

    [Fact]
    public void SamplingBoundsFollowDispersionChangesWhileGeometryStaysTheSame()
    {
        var renderer=new AndroidLiquidGlassRenderer();var fixture=AndroidFilterFixtures.Get(0);var source=fixture.Source();
        Assert.Equal("8828E85E4463A601B60002F882EBF56E35C24CD9ED6F82F476D46D2F4B8BEE6D",Hash(fixture));
        Assert.Equal("4C99D228FE9D847DAC735DBF95594E84D1698A09631BE98C305FD8C2A6A52756",Hash(fixture with{MaximumDispersion=true}));
        Assert.Equal("60A454E1C3456AE3FB098E3243F7B6CBD4342FF2DD3CDDE31E3492CE6ED3BB80",Hash(fixture with{Clarity=1}));
        Assert.Equal("8828E85E4463A601B60002F882EBF56E35C24CD9ED6F82F476D46D2F4B8BEE6D",Hash(fixture));
        string Hash(AndroidFilterFixture f)=>Convert.ToHexString(SHA256.HashData(f.Draw(renderer,source)));
    }

    [Fact]
    public void SmallPanelInsideLargeCaptureDoesNotAllocateTwoFullFramesOnEveryLiveUpdate()
    {
        var fixture=AndroidFilterFixtures.Get(0);var renderer=new AndroidLiquidGlassRenderer();var source=fixture.Source();
        for(var warmup=0;warmup<4;warmup++)_=fixture.Draw(renderer,source);
        for(var frame=0;frame<3;frame++)
        {
            source[0]=(byte)frame;
            var before=GC.GetAllocatedBytesForCurrentThread();
            var result=fixture.Draw(renderer,source);
            var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.Equal(source.Length,result.Length);
            Assert.InRange(allocated,source.Length,source.Length*17L/10);
        }
    }
}
