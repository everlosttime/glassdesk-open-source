using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDesk.Rendering;

// Logical BGRA8 textures only. Excludes pipeline/driver/managed/allocator costs;
// these counters do not establish whole-process private commit or GPU residency.
public sealed record DirectGroupStatistics(uint Depth,uint CachedSlots,ulong ColorBytes,ulong MaskBytes,ulong Allocations,ulong Composites);

public sealed partial class NativeDirectPresenter
{
    /// <summary>Isolates ordered draws until PopGroup. Opacity applies once.
    /// Optional bounds are in target pixels, including the caller's antialiasing margin;
    /// they round outward and intersect the parent, affecting WPF-compatible edge scans.</summary>
    public void PushGroup(float opacity=1,Rect? deviceBounds=null)
        =>PushGroupCore(opacity,deviceBounds,false);
    internal void PushEffectGroup(Rect deviceBounds)=>PushGroupCore(1,deviceBounds,true);
    private void PushGroupCore(float opacity,Rect? deviceBounds,bool effect)
    {
        EnsureUsable();if(!float.IsFinite(opacity) || opacity<0 || opacity>1)throw new ArgumentOutOfRangeException(nameof(opacity));
        var info=new NativeMethods.GroupInfo{Size=32,Version=1,Opacity=opacity};
        if(deviceBounds is {} b)
        {
            if(b.IsEmpty || new[]{b.Left,b.Top,b.Right,b.Bottom}.Any(v=>!double.IsFinite(v) || Math.Abs(v)>1048576))throw new ArgumentOutOfRangeException(nameof(deviceBounds));
            info.Left=(float)b.Left;info.Top=(float)b.Top;info.Right=(float)b.Right;info.Bottom=(float)b.Bottom;info.Bounded=1;
        }
        Marshal.ThrowExceptionForHR(effect?NativeMethods.PresenterPushEffectGroup(_handle,in info):NativeMethods.PresenterPushGroup(_handle,in info));
    }
    /// <summary>Subsequent draws populate the current group's alpha mask, including nested groups. Only once per push.</summary>
    public void BeginGroupMask(){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterBeginGroupMask(_handle));}
    public void PopGroup(){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterPopGroup(_handle));}
    /// <summary>Abandons unfinished groups without compositing them. The caller must discard the failed candidate frame.</summary>
    public void DiscardGroups(){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterDiscardGroups(_handle));}
    /// <summary>Releases idle GPU targets. Requires no active group; no forced collection or working-set trimming.</summary>
    public void TrimGroupCache(){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterTrimGroupCache(_handle));}
    public DirectGroupStatistics GroupStatistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.GroupStats{Size=48,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetGroupStatistics(_handle,ref s));
            return new(s.Depth,s.CachedSlots,s.ColorBytes,s.MaskBytes,s.Allocations,s.Composites);
        }
    }
}
