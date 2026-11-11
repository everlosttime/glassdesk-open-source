using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

// Diagnostic only. Capture desktop output to check pixels in the fixture's own Dock.
// No monitor image is exported or retained in evidence.
internal static unsafe class MonitorCaptureReference
{
    // Continuously reads just a two-pixel strip over the fixture's opaque
    // magenta underlay. Retains geometry/timestamps only, never monitor images.
    internal sealed class MotionSampler : IDisposable
    {
        private nint _device, _context, _dxgi, _inspectable;
        private IDirect3DDevice? _projected;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private readonly WgcWindowCapture.PixelReadback _readback = new();
        private readonly object _gate = new();
        private readonly PixelBounds _crop;
        private bool _disposed;
        internal readonly List<MotionSample> Samples = [];
        internal string? Failure;
        internal MotionSampler(nint window, PixelBounds crop)
        {
            _crop = crop;
            try
            {
                Check(D3D11CreateDevice(0,1,0,0x20,0,0,7,out _device,out _,out _context));
                var id=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");Check(Marshal.QueryInterface(_device,in id,out _dxgi));
                Check(CreateDirect3D11DeviceFromDXGIDevice(_dxgi,out _inspectable));_projected=WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(_inspectable);
                var item=CreateItem(MonitorFromWindow(window,1));
                _pool=Direct3D11CaptureFramePool.CreateFreeThreaded(_projected,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
                _session=_pool.CreateCaptureSession(item);_session.IsCursorCaptureEnabled=false;
                if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                    _session.MinUpdateInterval=TimeSpan.FromMilliseconds(1);
                _pool.FrameArrived += OnFrame; _session.StartCapture();
            }
            catch { Dispose(); throw; }
        }
        private void OnFrame(Direct3D11CaptureFramePool pool, object args)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    using var frame=pool.TryGetNextFrame();if(frame is null)return;
                    var started=Stopwatch.GetTimestamp();
                    var pixels=_readback.ReadPixels(_device,_context,frame,reusePixels:true,crop:_crop);
                    var top=-1;var bottom=-1;var greenPixels=0;long colorSum=0;
                    for(var y=0;y<pixels.Height;y++)
                    {
                        if(pixels.Pixels[y*pixels.Width*4+1]>40){if(top<0)top=y;bottom=y;greenPixels++;}
                        for(var c=0;c<3;c++)colorSum+=pixels.Pixels[y*pixels.Width*4+c];
                    }
                    uint Probe(double fraction)=>BitConverter.ToUInt32(pixels.Pixels,(int)(pixels.Height*fraction)*pixels.Width*4);
                    Samples.Add(new(pixels.SourceQpcTicks,top,bottom,Stopwatch.GetElapsedTime(started).TotalMilliseconds,greenPixels,colorSum,Probe(.25),Probe(.5),Probe(.75)));
                }
                catch(Exception error) { Failure=error.ToString(); }
            }
        }
        internal MotionSample[] Snapshot() { lock(_gate)return Samples.ToArray(); }
        public void Dispose()
        {
            lock(_gate)
            {
                if(_disposed)return;_disposed=true;
                if(_pool is not null)_pool.FrameArrived-=OnFrame;
                _session?.Dispose();_pool?.Dispose();_readback.Dispose();_projected?.Dispose();
                Release(_inspectable);Release(_dxgi);Release(_context);Release(_device);
            }
        }
    }
    internal sealed record MotionSample(long SourceQpc,int Top,int Bottom,double ReadbackMilliseconds,int GreenPixels,long ColorSum,uint UpperBgra,uint CenterBgra,uint LowerBgra);
    internal static CapturePixels? Capture(nint window)
    {
        nint device=0,context=0,dxgi=0,inspectable=0;
        IDirect3DDevice? projected=null;
        try
        {
            Check(D3D11CreateDevice(0,1,0,0x20,0,0,7,out device,out _,out context));
            var id=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");Check(Marshal.QueryInterface(device,in id,out dxgi));
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out inspectable));projected=WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            var item=CreateItem(MonitorFromWindow(window,1));
            using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(projected,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
            using var session=pool.CreateCaptureSession(item);session.IsCursorCaptureEnabled=false;session.StartCapture();
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<3000)
            {
                using var frame=pool.TryGetNextFrame();if(frame is not null) return WgcWindowCapture.ReadPixels(device,context,frame);
                Program.Pump(TimeSpan.FromMilliseconds(15));
            }
            return null;
        }
        finally {projected?.Dispose();Release(inspectable);Release(dxgi);Release(context);Release(device);}
    }
    private static GraphicsCaptureItem CreateItem(nint monitor)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";nint text=0,factory=0,result=0;
        try
        {
            Check(WindowsCreateString(name,name.Length,out text));var iid=new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");Check(RoGetActivationFactory(text,ref iid,out factory));
            var itemId=new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");Check(((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)(*(nint**)factory)[4])(factory,monitor,&itemId,&result));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally {Release(result);Release(factory);if(text!=0) WindowsDeleteString(text);}
    }
    private static void Check(int hr)=>Marshal.ThrowExceptionForHR(hr);
    private static void Release(nint value) {if(value!=0) Marshal.Release(value);}
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,uint type,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint inspectable);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string text,int length,out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name,ref Guid iid,out nint factory);
}
