using GlassDesk.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedTextTests
{
    [Theory]
    [InlineData("12:34",18,"Segoe UI",TextFormattingMode.Ideal,8.35,5.2)]
    [InlineData("星期四",11.5,"Microsoft YaHei UI",TextFormattingMode.Ideal,8.35,5.2)]
    [InlineData("\ue701",48,"Segoe Fluent Icons, Segoe MDL2 Assets",TextFormattingMode.Ideal,8.35,5.2)]
    [InlineData("مرحبا",18,"Segoe UI",TextFormattingMode.Ideal,8.35,5.2)]
    [InlineData("12:34",18,"Segoe UI",TextFormattingMode.Display,8.35,5.2)]
    [InlineData("12:34",18,"Segoe UI",TextFormattingMode.Display,8.4,5.2)]
    public void ManagedGlyphSnapshotPreservesTheRealLayoutAndFontFace(string text,double size,string family,TextFormattingMode formatting,double left,double top)=>NativeEngineTests.OnSta(()=>
    {
        var block=new TextBlock{Text=text,FontSize=size,FontFamily=new(family),Foreground=Brushes.White};TextOptions.SetTextFormattingMode(block,formatting);
        var canvas=new Canvas();canvas.Children.Add(block);Canvas.SetLeft(block,left);Canvas.SetTop(block,top);
        var first=DirectShadowTests.Host(80,80);first.Content=canvas;var second=DirectShadowTests.Host(80,80);
        try
        {
            first.Show();first.UpdateLayout();var runs=DirectTextTests.Glyphs(VisualTreeHelper.GetDrawing(block)).ToArray();Assert.NotEmpty(runs);
            var reference=WgcWindowCapture.Capture(new WindowInteropHelper(first).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Managed glyph reference missing.");
            second.Show();var hwnd=new WindowInteropHelper(second).Handle;using var p=new NativeDirectPresenter(hwnd,reference.Width,reference.Height);
            for(var i=0;i<runs.Length;i++)p.SetGlyphRun((uint)i+1,runs[i],formatting);
            p.Clear(Colors.Transparent);p.DrawGlyphRuns(new(80,80),runs.Select((r,i)=>new DirectGlyphDraw((uint)i+1,new(left+r.BaselineOrigin.X,top+r.BaselineOrigin.Y),Colors.White)).ToArray());Assert.True(p.Present());
            var actual=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Managed glyph output missing.");
            var directory=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"text-parity");System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"managed-{formatting}-{left}.wpf.bgra"),reference.Pixels);System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"managed-{formatting}-{left}.native.bgra"),actual.Pixels);
            ManagedVectorTests.Compare($"managed-text-{(int)size}-{runs[0].BidiLevel}-{(family=="Segoe UI"?"ui":"other")}-{formatting}-{left}",reference,actual);
        }
        finally{first.Close();second.Close();}
    });

    [Fact]
    public void GlyphMasksAreReusedInvalidatedAndReleasedWithoutRetainingTheCallerArrays()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;using var p=new NativeDirectPresenter(hwnd,100,100);var run=MakeRun(out var indices,out var advances);
            Assert.Equal(0u,p.GlyphStatistics.RunCount);p.SetGlyphRun(1,run);Assert.Equal(1u,p.GlyphStatistics.RunCount);Assert.Equal(0ul,p.GlyphStatistics.MaskBytes);
            DirectGlyphDraw[] draws=[new(1,new(12.35,40.2),Colors.White)];
            p.Clear(Colors.Transparent);p.DrawGlyphRuns(new(80,80),draws);Assert.True(p.Present());var initial=p.GlyphStatistics;
            Assert.Equal(1ul,initial.Rasterizations);Assert.True(initial.MaskBytes>0);Assert.True(initial.SourceBytes>0);
            var before=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Initial glyph output missing.");
            indices[0]=indices[1];advances[0]=60;p.InvalidateGlyphRasters();Assert.Equal(0ul,p.GlyphStatistics.MaskBytes);
            p.Clear(Colors.Transparent);p.DrawGlyphRuns(new(80,80),draws);Assert.True(p.Present());
            ManagedVectorTests.Compare("glyph-source-snapshot",before,WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Snapshot glyph output missing."));
            var realizations=p.GlyphStatistics.Rasterizations;draws[0]=draws[0] with{Baseline=new(12.7,41.2),Opacity=.4f,Color=Colors.DeepSkyBlue};p.DrawGlyphRuns(new(80,80),draws);
            Assert.Equal(realizations,p.GlyphStatistics.Rasterizations);
            draws[0]=draws[0] with{ScaleX=1.1f,ScaleY=1.1f};p.DrawGlyphRuns(new(80,80),draws);Assert.Equal(realizations+1,p.GlyphStatistics.Rasterizations);
            p.Resize(120,120);Assert.Equal(0ul,p.GlyphStatistics.MaskBytes);Assert.Equal(initial.SourceBytes,p.GlyphStatistics.SourceBytes);
            p.DrawGlyphRuns(new(80,80),draws);Assert.True(p.GlyphStatistics.MaskBytes>0);
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>p.InvalidateGlyphRasters())).GetAwaiter().GetResult());
            Assert.True(p.RemoveGlyphRun(1));Assert.False(p.RemoveGlyphRun(1));Assert.Equal(0ul,p.GlyphStatistics.MaskBytes);Assert.Equal(0ul,p.GlyphStatistics.SourceBytes);
            p.Dispose();Assert.Throws<ObjectDisposedException>(()=>{_=p.GlyphStatistics;});
        }
        finally{host.Close();}
    });

    internal static GlyphRun MakeRun(out ushort[] indices,out double[] advances)
    {
        Assert.True(new Typeface("Segoe UI").TryGetGlyphTypeface(out var face));indices=[face.CharacterToGlyphMap['A'],face.CharacterToGlyphMap['B']];
        advances=indices.Select(i=>face.AdvanceWidths[i]*18).ToArray();
        return new GlyphRun(face,0,false,18,1.25f,indices,new Point(0,20),advances,null,null,null,null,null,null);
    }
}
