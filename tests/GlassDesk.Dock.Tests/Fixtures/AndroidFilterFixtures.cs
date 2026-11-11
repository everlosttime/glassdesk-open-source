using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests.Fixtures;

internal sealed record AndroidFilterFixture(int Width,int Height,Size Size,Rect Bounds,double Radius,
    bool Squircle,bool Dark,double Strength,double Clarity,bool VaryAlpha=false,bool MaximumDispersion=false,bool Launchpad=false)
{
    internal byte[] Source()
    {
        var pixels=new byte[Width*Height*4];
        for(var i=0;i<pixels.Length;i++)pixels[i]=(byte)(i%4==3 && !VaryAlpha ? 255 : (i*17+(i>>6))&255);
        return pixels;
    }
    internal byte[] Draw(AndroidLiquidGlassRenderer renderer,byte[] pixels)=>renderer.Render(pixels,Width,Height,Size,Bounds,
        Radius,Squircle,Dark,Strength,backgroundClarity:Clarity,
        dispersionParameters:MaximumDispersion ? new(.012,1,1,.25) : null,launchpadOptics:Launchpad);
}

internal static class AndroidFilterFixtures
{
    internal static AndroidFilterFixture Get(int id)=>id switch
    {
        0=>new(900,280,new(900,280),new(100,100,700,80),26,true,true,1,.5),
        1=>new(301,119,new(380,150),new(10,12,350,120),40,false,false,.73,.25),
        2=>new(97,73,new(97,73),new(0,0,35,25),12,true,true,1,0),
        3=>new(97,73,new(97,73),new(60,48,37,25),12,false,false,1,.5,true,true),
        4=>new(127,95,new(127,95),new(33,23,61,49),22,true,true,1,1,true,true),
        5=>new(67,65,new(67,65),new(20,21,27,21),10,true,false,.4,.5,true,true),
        6=>new(8,5,new(8,5),new(0,0,8,5),2,false,true,1,0,true,true),
        7=>new(97,73,new(97,73),new(40,20,35,35),15,true,false,0,.5,true),
        8=>new(31,27,new(15.5,13.5),new(0,0,15.5,13.5),6,true,true,1,.5,true,true),
        9=>new(241,173,new(241,173),new(20,20,200,130),50,true,false,1,.2,true,true,true),
        10=>new(7,5,new(7,5),new(.01,.01,.01,.01),0,false,false,1,.5),
        _=>throw new ArgumentOutOfRangeException(nameof(id))
    };
}
