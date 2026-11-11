using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDesk.Rendering;

/// <summary>Opt-in foreground owner on an existing HWND. No backend/input/window policy is selected here.</summary>
public sealed partial class NativeForegroundPresenter:DispatcherObject,IDisposable
{
    private readonly NativeDirectPresenter _presenter;
    private readonly HashSet<NativeForegroundScene> _scenes=[];
    private uint _nextKey;
    private readonly Stack<uint> _freeKeys=new(768);
    private bool _disposed;
    private readonly int _overscan;
    private readonly ForegroundVectorCompileCache _compileCache=new();
    private int _visibleWidth,_visibleHeight;
    public NativeForegroundPresenter(nint window,int width,int height):this(window,width,height,0){}
    public NativeForegroundPresenter(nint window,int width,int height,int overscan)
    {
        if(overscan is <0 or >256 || width<1 || height<1)throw new ArgumentOutOfRangeException(nameof(overscan));
        _overscan=overscan;_visibleWidth=width;_visibleHeight=height;
        _presenter=new(window,checked(width+2*overscan),checked(height+2*overscan));
        try{if(overscan>0)_presenter.SetVisualOffset(-overscan,-overscan);}catch{_presenter.Dispose();throw;}
    }
    private NativeForegroundPresenter(NativeDirectPresenter presenter,int width,int height,int overscan)
    {
        _presenter=presenter;_overscan=overscan;_visibleWidth=width;_visibleHeight=height;
        try{if(overscan>0)_presenter.SetVisualOffset(-overscan,-overscan);}catch{_presenter.Dispose();throw;}
    }
    public NativeForegroundPresenter CreateSibling(nint window,int width,int height)
    {
        Check();if(width<1 || height<1)throw new ArgumentOutOfRangeException(nameof(width));
        return new(_presenter.CreateSibling(window,checked(width+2*_overscan),checked(height+2*_overscan)),width,height,_overscan);
    }
    private void Check(){VerifyAccess();ObjectDisposedException.ThrowIf(_disposed,this);}
    public nint FrameLatencyHandle{get{Check();return _presenter.FrameLatencyHandle;}}
    public DirectDeviceStatistics DeviceStatistics{get{Check();return _presenter.DeviceStatistics;}}
    public DirectVectorStatistics VectorStatistics{get{Check();return _presenter.VectorStatistics;}}
    public DirectImageStatistics ImageStatistics{get{Check();return _presenter.ImageStatistics;}}
    public DirectGlyphStatistics GlyphStatistics{get{Check();return _presenter.GlyphStatistics;}}
    public DirectGroupStatistics GroupStatistics{get{Check();return _presenter.GroupStatistics;}}
    public DirectGroupEffectStatistics GroupEffectStatistics{get{Check();return _presenter.GroupEffectStatistics;}}
    public ForegroundCompileStatistics CompileStatistics{get{Check();return _compileCache.Statistics;}}
    public void Clear(Color color){Check();_presenter.Clear(color);}
    public bool Present(){Check();return _presenter.Present();}
    public void TrimGroupCache(){Check();_presenter.TrimGroupCache();}
    public void Resize(int width,int height){Check();if(width<1 || height<1)throw new ArgumentOutOfRangeException(nameof(width));_presenter.Resize(checked(width+2*_overscan),checked(height+2*_overscan));_visibleWidth=width;_visibleHeight=height;}

    public NativeForegroundScene Prepare(VisualDrawingSnapshot snapshot,Size hostSize)
    {
        Check();ArgumentNullException.ThrowIfNull(snapshot);
        if(_scenes.Count>=2)throw new NotSupportedException("Dispose the retired foreground before preparing a third scene.");
        var output=_presenter.Statistics;
        if(!double.IsFinite(hostSize.Width) || !double.IsFinite(hostSize.Height) || hostSize.Width<1 || hostSize.Height<1)throw new ArgumentOutOfRangeException(nameof(hostSize));
        var scaleX=_visibleWidth/hostSize.Width;var scaleY=_visibleHeight/hostSize.Height;
        var nativeHost=new Size(output.Width/scaleX,output.Height/scaleY);
        uint logicalKey=0;
        uint LogicalKey(){if(logicalKey>=768)throw new NotSupportedException("Foreground resource identities exceed the bounded plan.");return ++logicalKey;}
        var plan=ForegroundCompiler.Compile(snapshot,nativeHost,new(output.Width,output.Height),LogicalKey,_compileCache,new(_overscan/scaleX,_overscan/scaleY));
        var acquired=new List<ForegroundResource>(plan.Assets.Count);var used=new HashSet<ForegroundResource>();var remap=new Dictionary<uint,uint>(plan.Assets.Count);
        var glyphDraws=new Dictionary<uint,DirectGlyphDraw>();foreach(var command in plan.Commands)if(command is ForegroundDrawGlyph glyph)glyphDraws.Add(glyph.Draw.Key,glyph.Draw);
        try
        {
            foreach(var asset in plan.Assets)
            {
                var resource=Acquire(asset,used,glyphDraws,nativeHost,output.Width,output.Height);
                acquired.Add(resource);used.Add(resource);remap.Add(asset.Key,resource.Key);
            }
            for(var i=0;i<plan.Commands.Count;i++)plan.Commands[i]=plan.Commands[i] switch
            {
                ForegroundDrawVector c=>c with{Key=remap[c.Key]},
                ForegroundDrawImage c=>c with{Draw=c.Draw with{Key=remap[c.Draw.Key]}},
                ForegroundDrawGlyph c=>c with{Draw=c.Draw with{Key=remap[c.Draw.Key]}},
                var other=>other
            };
            var scene=new NativeForegroundScene(this,hostSize,nativeHost,output.Width,output.Height,plan,acquired.ToArray());
            // Publish source aliases only after every native acquisition has
            // succeeded; failed candidates cannot leave extra memo payloads.
            for(var i=0;i<acquired.Count;i++)RememberCompiled(_resources[acquired[i]],plan.Assets[i]);
            plan.Assets.Clear();_scenes.Add(scene);return scene;
        }
        catch
        {
            foreach(var resource in acquired)try{ReleaseResource(resource);}catch{/* Preserve the original error; Dispose still owns the native presenter. */}
            throw;
        }
    }

    public void Draw(NativeForegroundScene scene)
    {
        Check();ArgumentNullException.ThrowIfNull(scene);
        if(scene.Owner!=this || scene.Disposed)throw new InvalidOperationException("Foreground scene is not live on this presenter.");
        var output=_presenter.Statistics;
        if(output.Width!=scene.Width || output.Height!=scene.Height)throw new InvalidOperationException("Recompile the foreground after a target resize.");
        try
        {
            foreach(var command in scene.Plan.Commands)switch(command)
            {
                case ForegroundPush c:_presenter.PushGroup(c.Opacity,c.DeviceBounds);break;
                case ForegroundEffectPush c:_presenter.PushEffectGroup(c.DeviceBounds);break;
                case ForegroundShadow c:_presenter.ApplyGroupShadow(c.Shadow);break;
                case ForegroundMask:_presenter.BeginGroupMask();break;
                case ForegroundPop:_presenter.PopGroup();break;
                case ForegroundDrawVector c:_presenter.DrawForegroundVector(c.Key,scene.NativeHostSize,c.Bounds,c.Opacity);break;
                case ForegroundDrawImage c:_presenter.DrawImages(scene.NativeHostSize,[c.Draw]);break;
                case ForegroundDrawGlyph c:_presenter.DrawGlyphRuns(scene.NativeHostSize,[c.Draw]);break;
            }
        }
        catch{_presenter.DiscardGroups();throw;}
    }
    private void Remove(ForegroundResource asset)
    {switch(asset.Kind){case ForegroundResourceKind.Vector:_presenter.RemoveForegroundVector(asset.Key);break;case ForegroundResourceKind.Image:_presenter.RemoveImage(asset.Key);break;case ForegroundResourceKind.Glyph:_presenter.RemoveGlyphRun(asset.Key);break;}}
    internal void Release(NativeForegroundScene scene)
    {
        VerifyAccess();if(scene.Disposed)return;
        Exception? failure=null;
        try{if(!_disposed)foreach(var asset in scene.Resources)try{ReleaseResource(asset);}catch(Exception error){failure??=error;}}
        finally
        {
            _scenes.Remove(scene);scene.Disposed=true;scene.Plan.Assets.Clear();scene.Plan.Commands.Clear();Array.Clear(scene.Resources);
        }
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose()
    {
        VerifyAccess();if(_disposed)return;Exception? failure=null;
        try{foreach(var scene in _scenes.ToArray())try{Release(scene);}catch(Exception error){failure??=error;}}
        finally{_presenter.Dispose();_compileCache.Clear();_disposed=true;}
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
