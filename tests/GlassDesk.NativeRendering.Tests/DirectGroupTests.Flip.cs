using System.Windows.Interop;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class DirectGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearOnlyFrameAfterGroupUsesCurrentFlipBuffer(bool diagnostics)=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();host.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
        var hwnd=new WindowInteropHelper(host).Handle;var info=Info.Create(100,100,hwnd);info.Flags=diagnostics?1u:0u;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
        try
        {
            Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,1,1));Assert.Equal(0,DirectPresenterTests.Present(p));
            _=Capture(host);
            var group=new GroupInfo{Size=32,Version=1,Opacity=.7f};Assert.Equal(0,PushGroup(p,ref group));
            Assert.Equal(0,DirectPresenterTests.Clear(p,1,0,0,1));Assert.Equal(0,PopGroup(p));Assert.Equal(0,DirectPresenterTests.Present(p));
            _=Capture(host);
            Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,1,1));
            var read=DirectPresenterTests.ReadPixel(p,50,50,out var pixel);
            Assert.Equal(diagnostics?0:unchecked((int)0x80070005),read);
            Assert.Equal(0,DirectPresenterTests.Present(p));var frame=Capture(host);
            var output=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"group-parity");System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(System.IO.Path.Combine(output,$"clear-after-group-{diagnostics}.json"),System.Text.Json.JsonSerializer.Serialize(new{diagnostics,read,pixel,visible=frame.Center.Select(b=>(int)b).ToArray()}));
            if(diagnostics)Assert.Equal(0xff0000ffu,pixel);
            Assert.Equal(new byte[]{255,0,0,255},frame.Center);
        }
        finally{DirectPresenterTests.Destroy(p);host.Close();}
    });
}
