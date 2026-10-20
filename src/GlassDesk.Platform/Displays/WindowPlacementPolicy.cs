namespace GlassDesk.Platform.Displays;
/// <summary>Physical-pixel placement, independent of the primary monitor's WPF DPI.</summary>
public static class WindowPlacementPolicy
{
    public static PixelRect Centered(PixelRect workArea,double ratio,bool fullscreen)
    {
        if (fullscreen) return workArea;
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio,0.3,1.0) : 0.66;
        int width = Math.Max(1,(int)Math.Round(workArea.Width * ratio));
        int height = Math.Max(1,(int)Math.Round(workArea.Height * ratio));
        return new(workArea.X + (workArea.Width-width)/2,workArea.Y + (workArea.Height-height)/2,width,height);
    }
}
