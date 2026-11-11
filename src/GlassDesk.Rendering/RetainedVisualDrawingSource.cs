using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDesk.Rendering;

/// <summary>A producer increments this after replacing its own render content.
/// Mutable referenced drawing resources are observed separately by the source.</summary>
public interface IRetainedDrawingRevision { ulong DrawingRevision { get; } }
public sealed record RetainedSourceStatistics(int CachedVisuals,ulong DrawingCaptures,ulong DrawingReuses,ulong ContentBoundsQueries,ulong DescendantBoundsQueries,ulong Invalidations);

/// <summary>Live source owner. Capture after WPF layout/render settles. Track only
/// subtrees whose producer versions every non-pose content replacement. Unknown
/// visuals refresh normally. Dispose on backend deactivation; owner Close also
/// disposes. Returned snapshots and prepared scenes remain detached.</summary>
public sealed class RetainedVisualDrawingSource:DispatcherObject,IDisposable
{
    private Visual? _root;
    private Window? _window;
    private VisualSnapshotOptions? _options;
    private readonly DrawingCache _cache;
    private bool _disposed;
    public RetainedVisualDrawingSource(Visual root,VisualSnapshotOptions? options=null)
    {
        ArgumentNullException.ThrowIfNull(root);root.VerifyAccess();options??=new();VisualDrawingSnapshot.ValidateOptions(options);
        _root=root;_options=options;_cache=new(options.MaximumVisuals,options.MaximumDepth);BindOwner();
    }
    private void Check(){VerifyAccess();ObjectDisposedException.ThrowIf(_disposed,this);}
    public RetainedSourceStatistics Statistics{get{VerifyAccess();return _cache.Statistics;}}
    public void Track(Visual subtree,Func<ulong> revision)
    {
        Check();ArgumentNullException.ThrowIfNull(subtree);ArgumentNullException.ThrowIfNull(revision);subtree.VerifyAccess();
        if(!ReferenceEquals(subtree,_root) && !subtree.IsDescendantOf(_root!))throw new ArgumentException("Track a subtree of this source.",nameof(subtree));
        _cache.Track(subtree,revision);
    }
    public void Invalidate(){Check();_cache.Clear();}
    public VisualDrawingSnapshot Capture()
    {
        Check();BindOwner();
        try
        {
            _cache.Begin(_root!);
            var snapshot=VisualDrawingSnapshot.CaptureRetained(_root!,_options!,_cache);_cache.End();return snapshot;
        }
        catch{_cache.Clear();throw;} // Do not retain a partially visited graph after fallback.
    }
    private void BindOwner()
    {
        var owner=_root as Window??Window.GetWindow(_root!);if(ReferenceEquals(owner,_window))return;
        if(_window is not null)_window.Closed-=OnOwnerClosed;_window=owner;if(owner is not null)owner.Closed+=OnOwnerClosed;
    }
    private void OnOwnerClosed(object? sender,EventArgs e)=>Dispose();
    public void Dispose()
    {
        VerifyAccess();if(_disposed)return;_disposed=true;
        if(_window is not null)_window.Closed-=OnOwnerClosed;_window=null;_root=null;_options=null;_cache.Dispose();
    }

    internal sealed class DrawingCache(int capacity,int maximumDepth):IDisposable
    {
        private sealed class Domain(Func<ulong> revision){internal readonly Func<ulong> Revision=revision;internal ulong Version;}
        internal sealed class Entry(DrawingGroup? raw,DrawingGroup drawing,Rect bounds,ulong revision,object? domain,ulong domainVersion,Size size,DpiScale dpi,Action invalidated):IDisposable
        {
            internal readonly DrawingGroup Drawing=drawing;
            internal readonly bool HasDrawing=raw is not null;
            internal readonly Rect Bounds=bounds;
            internal readonly ulong Revision=revision,DomainVersion=domainVersion;
            internal readonly object? Domain=domain;
            internal readonly Size Size=size;
            internal readonly DpiScale Dpi=dpi;
            internal bool Dirty;
            private readonly DrawingGroup? _raw=raw;
            internal void Observe(){if(_raw is {IsFrozen:false})_raw.Changed+=Changed;}
            private void Changed(object? sender,EventArgs e){Dirty=true;invalidated();}
            public void Dispose(){if(_raw is {IsFrozen:false})_raw.Changed-=Changed;}
        }
        private readonly Dictionary<Visual,Domain> _domains=[];
        private readonly Dictionary<Visual,Entry> _entries=[];
        private readonly HashSet<Visual> _seen=[];
        private readonly List<Visual> _retired=[];
        private ulong _captures,_reuses,_contentQueries,_descendantQueries,_invalidations;
        internal RetainedSourceStatistics Statistics=>new(_entries.Count,_captures,_reuses,_contentQueries,_descendantQueries,_invalidations);
        internal void Track(Visual root,Func<ulong> revision)
        {if(!_domains.ContainsKey(root) && _domains.Count>=capacity)throw new NotSupportedException("Source registration budget exceeded.");_domains[root]=new(revision);}
        internal void Begin(Visual root)
        {
            _retired.Clear();foreach(var visual in _domains.Keys)if(!ReferenceEquals(visual,root) && !visual.IsDescendantOf(root))_retired.Add(visual);
            foreach(var visual in _retired)_domains.Remove(visual);_retired.Clear();
            _seen.Clear();foreach(var domain in _domains.Values)domain.Version=domain.Revision();
        }
        private Domain? Producer(Visual visual)
        {
            DependencyObject? node=visual;
            for(var depth=0;node is Visual current && depth<=maximumDepth;depth++,node=VisualTreeHelper.GetParent(current))
                if(_domains.TryGetValue(current,out var domain))return domain;
            return null;
        }
        internal bool TryGet(Visual visual,out Entry? entry)
        {
            _seen.Add(visual);var domain=Producer(visual);var revision=(visual as IRetainedDrawingRevision)?.DrawingRevision??0;
            var eligible=domain is not null || visual is IRetainedDrawingRevision;
            if(eligible && _entries.TryGetValue(visual,out entry) && !entry.Dirty && ReferenceEquals(entry.Domain,domain)
                && entry.DomainVersion==(domain?.Version??0) && entry.Revision==revision && entry.Size==(visual is UIElement element?element.RenderSize:Size.Empty)
                && entry.Dpi.Equals(VisualTreeHelper.GetDpi(visual)))
            {_reuses++;return true;}
            entry=null;return false;
        }
        internal void Store(Visual visual,DrawingGroup? raw,DrawingGroup drawing,Rect bounds)
        {
            var domain=Producer(visual);
            if(domain is null && visual is not IRetainedDrawingRevision)
            {if(_entries.Remove(visual,out var retired))retired.Dispose();return;}
            if(_entries.Remove(visual,out var old))old.Dispose();
            if(_entries.Count>=capacity)return; // Unknown/missed entries still capture correctly without caching.
            var entry=new Entry(raw,drawing,bounds,(visual as IRetainedDrawingRevision)?.DrawingRevision??0,domain,domain?.Version??0,
                visual is UIElement element?element.RenderSize:Size.Empty,VisualTreeHelper.GetDpi(visual),()=>_invalidations++);
            entry.Observe();_entries.Add(visual,entry);
        }
        internal void Capturing()=>_captures++;
        internal Rect ContentBounds(Visual visual){_contentQueries++;return VisualTreeHelper.GetContentBounds(visual);}
        internal Rect DescendantBounds(Visual visual,int children,Rect content)
        {
            // A leaf's public inner subgraph bounds are exactly its content
            // bounds (Visual.CalculateSubgraphBoundsInnerSpace, WPF MIT).
            if(children==0)
            {
                if(double.IsNaN(content.X)||double.IsNaN(content.Y)||double.IsNaN(content.Width)||double.IsNaN(content.Height))
                    return new(double.NegativeInfinity,double.NegativeInfinity,double.PositiveInfinity,double.PositiveInfinity);
                return content;
            }
            _descendantQueries++;return VisualTreeHelper.GetDescendantBounds(visual);
        }
        internal void End()
        {
            _retired.Clear();foreach(var visual in _entries.Keys)if(!_seen.Contains(visual))_retired.Add(visual);
            foreach(var visual in _retired){_entries[visual].Dispose();_entries.Remove(visual);}_retired.Clear();_seen.Clear();
        }
        internal void Clear(){foreach(var entry in _entries.Values)entry.Dispose();_entries.Clear();_seen.Clear();_retired.Clear();}
        public void Dispose(){Clear();_domains.Clear();}
    }
}
