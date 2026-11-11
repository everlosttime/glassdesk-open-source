using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

// WPF CSnappingFrame (MIT; full notice in docs/third-party/native-rendering):
// transform guides to float device coordinates, then offset each control point
// by the nearest guide's rounding correction. Ties select the lower guide.
internal sealed class ForegroundSnap((float At,float Offset)[] x,(float At,float Offset)[] y,Matrix device):IEquatable<ForegroundSnap>
{
    private (float At,float Offset)[] X=>x;
    private (float At,float Offset)[] Y=>y;
    private Matrix Device=>device;
    internal long StorageBytes=>((long)x.Length+y.Length)*8;
    public bool Equals(ForegroundSnap? other)=>other is not null && device==other.Device && x.AsSpan().SequenceEqual(other.X) && y.AsSpan().SequenceEqual(other.Y);
    public override bool Equals(object? obj)=>obj is ForegroundSnap other && Equals(other);
    public override int GetHashCode()
    {var hash=new HashCode();hash.Add(device);foreach(var point in x)hash.Add(point);foreach(var point in y)hash.Add(point);return hash.ToHashCode();}
    internal static ForegroundSnap? Create(DoubleCollection? x,DoubleCollection? y,Matrix worldToDevice,Matrix device)
    {
        if(!(x?.Count>0 || y?.Count>0))return null;
        if(worldToDevice.M12!=0 || worldToDevice.M21!=0 || worldToDevice.M11<=0 || worldToDevice.M22<=0)
            throw new NotSupportedException("Rotated/reflected snapping guides require the existing renderer.");
        static (float,float)[] Axis(DoubleCollection? guides,double scale,double offset)=>guides?.Select(v=>{
            var at=(float)scale*(float)v+(float)offset;
            if(!float.IsFinite(at) || Math.Abs(at)>1048576)throw new NotSupportedException("Snapping guide exceeds the coordinate budget.");
            return(at,MathF.Floor(at+.5f)-at);
        }).OrderBy(p=>p.at).ToArray()??[];
        return new(Axis(x,worldToDevice.M11,worldToDevice.OffsetX),Axis(y,worldToDevice.M22,worldToDevice.OffsetY),device);
    }
    private static float Snap(float value,(float At,float Offset)[] guides)
    {
        if(guides.Length==0)return value;
        var best=0;for(var i=1;i<guides.Length;i++)if(Math.Abs(guides[i].At-value)<Math.Abs(guides[best].At-value))best=i;
        return value+guides[best].Offset;
    }
    internal Point Point(Point p,bool deviceCoordinates)
    {
        if(!deviceCoordinates)p=device.Transform(p);
        p=new(Snap((float)p.X,x),Snap((float)p.Y,y));
        return deviceCoordinates?p:new(p.X/device.M11,p.Y/device.M22);
    }
    internal Geometry Geometry(Geometry geometry,bool deviceCoordinates)
    {
        var path=PathGeometry.CreateFromGeometry(geometry).CloneCurrentValue();var transform=path.Transform?.Value??Matrix.Identity;
        if(!deviceCoordinates)transform.Append(device);
        Point Map(Point p)
        {
            // WPF's guideline traversal transforms MilPoint2F with a float
            // matrix before choosing the closest guide. Rounding only after a
            // double transform can choose the opposite guide at a midpoint.
            var px=(float)p.X;var py=(float)p.Y;
            var mapped=new Point(px*(float)transform.M11+py*(float)transform.M21+(float)transform.OffsetX,
                px*(float)transform.M12+py*(float)transform.M22+(float)transform.OffsetY);
            mapped=Point(mapped,true);
            return deviceCoordinates?mapped:new(mapped.X/device.M11,mapped.Y/device.M22);
        }
        foreach(var figure in path.Figures)
        {
            figure.StartPoint=Map(figure.StartPoint);
            foreach(var segment in figure.Segments)switch(segment)
            {
                case LineSegment s:s.Point=Map(s.Point);break;
                case BezierSegment s:s.Point1=Map(s.Point1);s.Point2=Map(s.Point2);s.Point3=Map(s.Point3);break;
                case QuadraticBezierSegment s:s.Point1=Map(s.Point1);s.Point2=Map(s.Point2);break;
                case PolyLineSegment s:for(var i=0;i<s.Points.Count;i++)s.Points[i]=Map(s.Points[i]);break;
                case PolyBezierSegment s:for(var i=0;i<s.Points.Count;i++)s.Points[i]=Map(s.Points[i]);break;
                case PolyQuadraticBezierSegment s:for(var i=0;i<s.Points.Count;i++)s.Points[i]=Map(s.Points[i]);break;
                default:throw new NotSupportedException("This snapped geometry requires the existing renderer.");
            }
        }
        path.Transform=Transform.Identity;path.Freeze();return path;
    }
}
