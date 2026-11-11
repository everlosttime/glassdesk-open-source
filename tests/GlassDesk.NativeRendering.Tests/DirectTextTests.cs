using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectTextTests
{
    [Fact]
    public void GlyphCacheIsObservableAndReleasesMasksOnResize()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();var create=Info.Create(100,100,new WindowInteropHelper(host).Handle);create.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref create,out var p));
        try
        {
            var stats=new Stats{Size=48,Version=1};Assert.Equal(0,GetStats(p,ref stats));Assert.Equal(0ul,stats.MaskBytes);
            var run=ManagedTextTests.MakeRun(out _,out _);Upload(p,1,run);GlyphDraw[] draws=[new(){Key=1,X=12,Y=40,ScaleX=1,ScaleY=1,Opacity=1,R=1,G=1,B=1,A=1}];
            Assert.Equal(0,Draw(p,draws));Assert.Equal(0,GetStats(p,ref stats));Assert.True(stats.MaskBytes>0);var initial=stats;
            WithInfo(run,info=>
            {
                info.Version=99;Assert.Equal(unchecked((int)0x80004002),SetRun(p,1,ref info));info.Version=1;
                info.EmSize=float.NaN;Assert.Equal(unchecked((int)0x80070057),SetRun(p,1,ref info));info.EmSize=18;
                Marshal.WriteInt16(info.Indices,unchecked((short)65535));Assert.Equal(unchecked((int)0x80070057),SetRun(p,1,ref info));
            });
            Assert.Equal(0,GetStats(p,ref stats));Assert.Equal(initial,stats);
            var before=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,before,40000,400));
            Assert.Equal(unchecked((int)0x80070490),Draw(p,[draws[0],new(){Key=99,ScaleX=1,ScaleY=1,Opacity=1,A=1}]));
            var after=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,after,40000,400));Assert.Equal(before,after);
            Assert.Equal(0,DirectPresenterTests.Resize(p,120,120));Assert.Equal(0,GetStats(p,ref stats));Assert.Equal(0ul,stats.MaskBytes);Assert.Equal(initial.SourceBytes,stats.SourceBytes);
        }
        finally{DirectPresenterTests.Destroy(p);host.Close();}
    });
    [Theory]
    [InlineData(0,"12:34",18,"Segoe UI",1f,0f,1f,0)]
    [InlineData(1,"2026/10/29",9.5,"Segoe UI",1f,0f,1f,0)]
    [InlineData(2,"\ue701",48,"Segoe Fluent Icons, Segoe MDL2 Assets",1f,0f,1f,0)]
    [InlineData(3,"\ue701",48,"Segoe Fluent Icons, Segoe MDL2 Assets",1.15f,.35f,.4f,0)]
    [InlineData(4,"12:34",18,"Segoe UI",1.12f,.2f,.7f,1)]
    [InlineData(5,"2026/10/29",9.5,"Segoe UI",1f,.35f,.4f,2)]
    [InlineData(6,"星期四",11.5,"Microsoft YaHei UI",1f,.2f,1f,0)]
    [InlineData(7,"مرحبا",18,"Segoe UI",1f,.35f,1f,0)]
    [InlineData(8,"12:34",18,"Segoe UI",1f,.2f,1f,3)]
    public void DirectGlyphRunsRetainProductionTextRasterAndLayout(int id,string text,double size,string family,float scale,float offset,float opacity,int tint)=>NativeEngineTests.OnSta(()=>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterSetGlyphRun",out _),"Direct output has no production glyph raster path.");}
        finally{NativeLibrary.Free(library);}
        var color=tint switch{1=>Colors.DeepSkyBlue,2=>Color.FromArgb(192,240,220,180),3=>Colors.Black,_=>Colors.White};
        var block=new TextBlock{Text=text,FontSize=size,FontFamily=new(family),Foreground=new SolidColorBrush(color),FontWeight=text=="12:34"?FontWeights.SemiBold:FontWeights.Normal,
            Opacity=opacity,RenderTransform=new ScaleTransform(scale,scale)};
        var root=new Canvas();root.Children.Add(block);Canvas.SetLeft(block,8+offset);Canvas.SetTop(block,5+offset);
        var referenceHost=DirectShadowTests.Host(80,80);referenceHost.Content=root;var nativeHost=DirectShadowTests.Host(80,80);
        try
        {
            referenceHost.Show();referenceHost.UpdateLayout();
            var runs=Glyphs(VisualTreeHelper.GetDrawing(block)).ToArray();Assert.NotEmpty(runs);
            var reference=WgcWindowCapture.Capture(new WindowInteropHelper(referenceHost).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Text reference missing.");
            Assert.True(reference.Pixels.Where((_,i)=>i%4==3).Count(a=>a>24)>10,"Reference text was empty.");
            nativeHost.Show();var info=Info.Create((uint)reference.Width,(uint)reference.Height,new WindowInteropHelper(nativeHost).Handle);
            Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
            try
            {
                var draws=new List<GlyphDraw>();
                for(var i=0;i<runs.Length;i++)
                {
                    Upload(p,(uint)i+1,runs[i]);draws.Add(new(){Key=(uint)i+1,X=8+offset+scale*(float)runs[i].BaselineOrigin.X,Y=5+offset+scale*(float)runs[i].BaselineOrigin.Y,ScaleX=scale,ScaleY=scale,Opacity=opacity,R=color.R/255f,G=color.G/255f,B=color.B/255f,A=color.A/255f});
                }
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));Assert.Equal(0,Draw(p,draws.ToArray()));Assert.Equal(0,DirectPresenterTests.Present(p));
                var actual=WgcWindowCapture.Capture(new WindowInteropHelper(nativeHost).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Glyph output missing.");
                Assert.Equal(unchecked((int)0x80070005),DirectPresenterTests.ReadPixel(p,1,1,out _));
                var directory=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"text-parity");System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"case-{id}.wpf.bgra"),reference.Pixels);System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"case-{id}.native.bgra"),actual.Pixels);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,$"case-{id}.runs.json"),System.Text.Json.JsonSerializer.Serialize(runs.Select(r=>new{font=r.GlyphTypeface.FontUri.ToString(),r.BaselineOrigin,r.FontRenderingEmSize,r.BidiLevel,count=r.GlyphIndices.Count})));
                ManagedVectorTests.Compare($"text-case-{id}",reference,actual);
            }
            finally{DirectPresenterTests.Destroy(p);}
        }
        finally{referenceHost.Close();nativeHost.Close();}
    });

    internal static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
    {
        if(drawing is GlyphRunDrawing run)yield return run.GlyphRun;
        else if(drawing is DrawingGroup group)foreach(var child in group.Children)foreach(var glyph in Glyphs(child))yield return glyph;
    }
    internal static void Upload(nint p,uint key,GlyphRun run)
        =>WithInfo(run,info=>Assert.Equal(0,SetRun(p,key,ref info)));
    internal static void WithInfo(GlyphRun run,Action<GlyphInfo> send)
    {
        var glyphs=run.GlyphIndices.ToArray();var advances=run.AdvanceWidths.Select(v=>(float)v).ToArray();
        var offsets=Enumerable.Range(0,glyphs.Length).Select(i=>run.GlyphOffsets?.Count>0?new Offset{X=(float)run.GlyphOffsets[i].X,Y=(float)run.GlyphOffsets[i].Y}:default).ToArray();
        var indicesPin=GCHandle.Alloc(glyphs,GCHandleType.Pinned);var advancePin=GCHandle.Alloc(advances,GCHandleType.Pinned);var offsetPin=GCHandle.Alloc(offsets,GCHandleType.Pinned);
        var uri=run.GlyphTypeface.FontUri;var path=Marshal.StringToHGlobalUni(uri.LocalPath);
        try
        {
            var face=string.IsNullOrEmpty(uri.Fragment)?0:uint.Parse(uri.Fragment.TrimStart('#'));
            var info=new GlyphInfo{Size=72,Version=1,Count=(uint)glyphs.Length,Face=face,Simulations=(uint)run.GlyphTypeface.StyleSimulations,Bidi=(uint)run.BidiLevel,Sideways=run.IsSideways?1u:0u,EmSize=(float)run.FontRenderingEmSize,
                FontPath=path,Indices=indicesPin.AddrOfPinnedObject(),Advances=advancePin.AddrOfPinnedObject(),Offsets=offsetPin.AddrOfPinnedObject()};
            send(info);
        }
        finally{Marshal.FreeHGlobal(path);indicesPin.Free();advancePin.Free();offsetPin.Free();}
    }
    internal static int Draw(nint p,GlyphDraw[] items)
    {
        var pin=GCHandle.Alloc(items,GCHandleType.Pinned);
        try{var batch=new Batch{Size=32,Version=1,Count=(uint)items.Length,HostWidth=80,HostHeight=80,Items=pin.AddrOfPinnedObject()};return DrawRuns(p,ref batch);}
        finally{pin.Free();}
    }
    [StructLayout(LayoutKind.Sequential)]internal struct Offset{internal float X,Y;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphInfo
    {internal uint Size,Version,Count,Face,Simulations,Bidi,Sideways,Reserved;internal float EmSize;internal uint Reserved2;internal nint FontPath,Indices,Advances,Offsets;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphDraw{internal uint Key,Reserved;internal float X,Y,ScaleX,ScaleY,Opacity,R,G,B,A;internal uint Reserved2;}
    [StructLayout(LayoutKind.Sequential)]internal struct Batch{internal uint Size,Version,Count,Reserved;internal float HostWidth,HostHeight;internal nint Items;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterSetGlyphRun",CallingConvention=CallingConvention.Cdecl)]internal static extern int SetRun(nint p,uint key,ref GlyphInfo info);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterDrawGlyphRuns",CallingConvention=CallingConvention.Cdecl)]internal static extern int DrawRuns(nint p,ref Batch batch);
    [StructLayout(LayoutKind.Sequential)]internal struct Stats{internal uint Size,Version,Count,Reserved;internal ulong SourceBytes,MaskBytes,Rasterizations,Draws;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetGlyphStatistics",CallingConvention=CallingConvention.Cdecl)]internal static extern int GetStats(nint p,ref Stats stats);
}
