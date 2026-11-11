using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Rendering;

// Descriptor bytes are bounded managed metadata, not a process/GPU memory measurement.
public sealed record ForegroundResourceStatistics(int Resources,int References,long DescriptorBytes,ulong Uploads,ulong Reuses);

public sealed partial class NativeForegroundPresenter
{
    private sealed class Entry(ForegroundResource resource,byte[] identity,ulong hash)
    {
        internal readonly ForegroundResource Resource=resource;
        internal readonly byte[] Identity=identity;
        internal readonly ulong Hash=hash;
        internal int References=1;
    }
    private sealed record BitmapIdentity(ulong Value);
    private readonly ConditionalWeakTable<BitmapSource,BitmapIdentity> _bitmapIdentities=new();
    private readonly Dictionary<ForegroundResource,Entry> _resources=[];
    private readonly Dictionary<(ForegroundResourceKind,ulong),List<Entry>> _buckets=[];
    private ulong _bitmapIdentity,_uploads,_reuses;
    private long _descriptorBytes;
    private const long DescriptorBudget=8*1024*1024;

    public ForegroundResourceStatistics ResourceStatistics
    {
        get{Check();var references=0;foreach(var entry in _resources.Values)references+=entry.References;return new(_resources.Count,references,_descriptorBytes,_uploads,_reuses);}
    }
    private static ulong Fingerprint(ReadOnlySpan<byte> bytes)
    {unchecked{var hash=14695981039346656037ul;foreach(var value in bytes)hash=(hash^value)*1099511628211;return hash;}}

    private byte[] Identity(ForegroundAsset asset,Dictionary<uint,DirectGlyphDraw> glyphDraws,Size host,uint width,uint height)
    {
        if(asset is ForegroundVector vector)return vector.Prepared.Identity;
        if(asset is ForegroundImage image)
        {
            // The cache must never retain a BitmapSource just to recognize it.
            // Immutable source identity avoids both pixel readback and false
            // reuse between different bitmaps with the same size or URI.
            if(!_bitmapIdentities.TryGetValue(image.Bitmap,out var id))
            {id=new(checked(++_bitmapIdentity));_bitmapIdentities.Add(image.Bitmap,id);}
            return BitConverter.GetBytes(id.Value);
        }
        var glyph=(ForegroundGlyph)asset;var run=glyph.Run;var face=run.GlyphTypeface;
        var count=run.GlyphIndices.Count;
        if(count<1 || count>4096 || run.AdvanceWidths.Count!=count || (run.GlyphOffsets?.Count>0 && run.GlyphOffsets.Count!=count))
            throw new NotSupportedException("Glyph source is not a bounded complete run.");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(face.FontUri.AbsoluteUri);writer.Write((int)face.StyleSimulations);writer.Write((int)glyph.Mode);
        writer.Write((float)run.FontRenderingEmSize);writer.Write(run.BidiLevel);writer.Write(run.IsSideways);writer.Write(count);
        // Native glyph resources own a single scale-dependent mask. Keep
        // independent scale variants instead of causing redraw cache thrash.
        var draw=glyphDraws[glyph.Key];writer.Write(draw.ScaleX*width/(float)host.Width);writer.Write(draw.ScaleY*height/(float)host.Height);
        for(var i=0;i<count;i++)
        {
            writer.Write(run.GlyphIndices[i]);writer.Write((float)run.AdvanceWidths[i]);
            var offset=run.GlyphOffsets?.Count>0?run.GlyphOffsets[i]:default;
            writer.Write((float)offset.X);writer.Write((float)offset.Y);
        }
        writer.Flush();return stream.ToArray();
    }
    private ForegroundResource Acquire(ForegroundAsset asset,HashSet<ForegroundResource> used,Dictionary<uint,DirectGlyphDraw> glyphDraws,Size host,uint width,uint height)
    {
        var kind=asset switch{ForegroundVector=>ForegroundResourceKind.Vector,ForegroundImage=>ForegroundResourceKind.Image,_=>ForegroundResourceKind.Glyph};
        var identity=Identity(asset,glyphDraws,host,width,height);var hash=Fingerprint(identity);var bucketKey=(kind,hash);
        if(_buckets.TryGetValue(bucketKey,out var bucket))foreach(var existing in bucket)
        {
            // Preserve independent slots inside one scene. Vector coverage also
            // depends on active layer bounds, even for identical source data.
            if(!used.Contains(existing.Resource) && identity.AsSpan().SequenceEqual(existing.Identity))
            {existing.References++;_reuses++;return existing.Resource;}
        }
        if(identity.Length>DescriptorBudget-_descriptorBytes)throw new NotSupportedException("Foreground descriptor budget exceeded.");
        if(!_freeKeys.TryPop(out var key))
        {if(_nextKey>=768)throw new NotSupportedException("Foreground resource identities exceed the bounded pool.");key=++_nextKey;}
        var resource=new ForegroundResource(kind,key);var entry=new Entry(resource,identity,hash);var accounted=false;
        try
        {
            _resources.Add(resource,entry);
            if(bucket is null){bucket=[];_buckets.Add(bucketKey,bucket);}bucket.Add(entry);
            _descriptorBytes+=identity.Length;accounted=true;
            switch(asset)
            {
                case ForegroundVector vector:_presenter.UploadForegroundVector(key,vector);break;
                case ForegroundImage image:_presenter.SetImage(key,image.Bitmap);break;
                case ForegroundGlyph glyph:_presenter.SetGlyphRun(key,glyph.Run,glyph.Mode);break;
            }
            _uploads++;return resource;
        }
        catch
        {
            _resources.Remove(resource);bucket?.Remove(entry);if(bucket?.Count==0)_buckets.Remove(bucketKey);
            if(accounted)_descriptorBytes-=identity.Length;
            try{Remove(resource);}catch{/* Preserve partial-upload failure. */}finally{_freeKeys.Push(key);}
            throw;
        }
    }
    private void RememberCompiled(Entry entry,ForegroundAsset asset)
    {
        if(asset is ForegroundVector vector)
        {
            var prepared=vector.Prepared;
            if(!ReferenceEquals(prepared.Identity,entry.Identity))prepared=prepared with{Identity=entry.Identity};
            _compileCache.Remember(entry.Resource,vector.CompileKey,prepared);
        }
    }
    private void ReleaseResource(ForegroundResource resource)
    {
        var entry=_resources[resource];if(--entry.References!=0)return;
        try{Remove(resource);}
        finally
        {
            _compileCache.Forget(resource);_resources.Remove(resource);var bucketKey=(resource.Kind,entry.Hash);var bucket=_buckets[bucketKey];bucket.Remove(entry);
            if(bucket.Count==0)_buckets.Remove(bucketKey);_descriptorBytes-=entry.Identity.Length;_freeKeys.Push(resource.Key);
            if(_resources.Count==0)_compileCache.Clear();
        }
    }
}
