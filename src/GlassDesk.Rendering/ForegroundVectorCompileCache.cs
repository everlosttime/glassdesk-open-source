using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

// Logical numeric payload only; descriptor bytes, object overhead, GPU and
// process memory are reported separately. No WPF source objects are retained.
public sealed record ForegroundCompileStatistics(int Contexts,int Payloads,long PayloadBytes,ulong Hits,ulong Misses);
internal readonly record struct ForegroundVectorCompileKey(ulong Geometry,ulong Paint,ulong Pen,Matrix Matrix,Size Size,ForegroundSnap? Snap);

internal sealed class ForegroundVectorCompileCache
{
    private sealed record SourceIdentity(ulong Value);
    private sealed class Payload(NativeDirectPresenter.PreparedForegroundVector vector,long bytes)
    {
        internal readonly NativeDirectPresenter.PreparedForegroundVector Vector=vector;
        internal readonly long Bytes=bytes;
        internal readonly List<ForegroundVectorCompileKey> Keys=new(4);
    }
    private readonly ConditionalWeakTable<Freezable,SourceIdentity> _identities=new();
    private readonly Dictionary<ForegroundVectorCompileKey,Payload> _contexts=[];
    private readonly Dictionary<ForegroundResource,Payload> _payloads=[];
    private ulong _identity,_hits,_misses;
    private long _bytes;
    private const long MaximumPayloadBytes=8*1024*1024;
    internal ForegroundCompileStatistics Statistics=>new(_contexts.Count,_payloads.Count,_bytes,_hits,_misses);
    private ulong Identity(Freezable source)
    {
        if(!_identities.TryGetValue(source,out var identity)){identity=new(checked(++_identity));_identities.Add(source,identity);}
        return identity.Value;
    }
    internal ForegroundVectorCompileKey? Find(Geometry geometry,Brush paint,Pen? pen,Matrix matrix,Size size,ForegroundSnap? snap,
        out NativeDirectPresenter.PreparedForegroundVector? prepared)
    {
        prepared=null;
        if(!geometry.IsFrozen || !paint.IsFrozen || pen is {IsFrozen:false}){_misses++;return null;}
        var key=new ForegroundVectorCompileKey(Identity(geometry),Identity(paint),pen is null?0:Identity(pen),matrix,size,snap);
        if(_contexts.TryGetValue(key,out var payload)){prepared=payload.Vector;_hits++;}else _misses++;
        return key;
    }
    internal void Remember(ForegroundResource resource,ForegroundVectorCompileKey? key,NativeDirectPresenter.PreparedForegroundVector prepared)
    {
        // A duplicate in-scene coverage slot may have the same key. One live
        // owner is sufficient; its release can cause a conservative miss.
        if(key is null || _contexts.ContainsKey(key.Value))return;
        _payloads.TryGetValue(resource,out var payload);
        var bytes=payload is null?(long)prepared.Commands.Length*32+(prepared.Brush?.Stops.Length??0)*24:0;
        var keyBytes=key.Value.Snap?.StorageBytes??0;
        var retiredBytes=payload?.Keys.Count==4?(payload.Keys[0].Snap?.StorageBytes??0):0;
        if(bytes+keyBytes>MaximumPayloadBytes-_bytes+retiredBytes || payload is null && _payloads.Count>=768)return;
        if(payload is null)
        {
            payload=new(prepared,bytes);_payloads.Add(resource,payload);_bytes+=bytes;
        }
        if(payload.Keys.Count==4){_contexts.Remove(payload.Keys[0]);payload.Keys.RemoveAt(0);_bytes-=retiredBytes;}
        payload.Keys.Add(key.Value);_contexts.Add(key.Value,payload);_bytes+=keyBytes;
    }
    internal void Forget(ForegroundResource resource)
    {
        if(!_payloads.Remove(resource,out var payload))return;
        foreach(var key in payload.Keys){_contexts.Remove(key);_bytes-=key.Snap?.StorageBytes??0;}_bytes-=payload.Bytes;
    }
    internal void Clear(){_contexts.Clear();_payloads.Clear();_identities.Clear();_bytes=0;}
}
