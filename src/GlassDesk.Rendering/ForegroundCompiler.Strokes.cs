using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

internal sealed partial class ForegroundCompiler
{
    private static Geometry WidenStroke(Geometry geometry,Pen pen,Matrix device,double scale)
    {
        // WPF CRectangle::WidenToShape (MIT; native-rendering NOTICE): solid
        // circular rounded-rectangle strokes use exact outer/inner rectangles,
        // not the general curve widener exposed by GetWidenedPathGeometry.
        if(geometry is RectangleGeometry rect && rect.RadiusX==rect.RadiusY && rect.RadiusX!=0
            && (rect.Transform?.Value??Matrix.Identity).IsIdentity && !(pen.DashStyle?.Dashes.Count>0))
        {
            var left=(float)rect.Rect.Left;var top=(float)rect.Rect.Top;
            var right=left+(float)rect.Rect.Width;var bottom=top+(float)rect.Rect.Height;
            var radius=Math.Abs((float)rect.RadiusX);var half=.5f*(float)pen.Thickness;
            var result=new PathGeometry{FillRule=FillRule.EvenOdd};
            void Add(float l,float t,float r,float b,float corner)
            {
                var path=PathGeometry.CreateFromGeometry(new RectangleGeometry(new Rect(l,t,r-l,b-t),corner,corner));
                foreach(var figure in path.Figures)result.Figures.Add(figure.CloneCurrentValue());
            }
            Add(left-half,top-half,right+half,bottom+half,radius+half);
            if(2*half<right-left && 2*half<bottom-top)Add(left+half,top+half,right-half,bottom-half,Math.Max(radius-half,0));
            result.Transform=new MatrixTransform(device);return result;
        }
        var copy=pen.CloneCurrentValue();copy.Thickness*=scale;
        return Transform(geometry,device).GetWidenedPathGeometry(copy,.25,ToleranceType.Absolute);
    }
}
