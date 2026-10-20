using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace GlassDesk.UI.Effects;

internal sealed class TransitionGlassEffect : ShaderEffect
{
    private static readonly PixelShader Shader = LoadShader();
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input), typeof(TransitionGlassEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty PanelProperty = DependencyProperty.Register(
        nameof(Panel), typeof(Point4D), typeof(TransitionGlassEffect),
        new UIPropertyMetadata(new Point4D(0, 0, 1, 1), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(Point4D), typeof(TransitionGlassEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 0, 1), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty DispersionProperty = DependencyProperty.Register(
        nameof(Dispersion), typeof(Point4D), typeof(TransitionGlassEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 1, 0), PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(Point4D), typeof(TransitionGlassEffect),
        new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(3)));

    public Brush? Input { get => (Brush?)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Panel { get => (Point4D)GetValue(PanelProperty); set => SetValue(PanelProperty, value); }
    public Point4D Material { get => (Point4D)GetValue(MaterialProperty); set => SetValue(MaterialProperty, value); }
    public Point4D Dispersion { get => (Point4D)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    public Point4D Profile { get => (Point4D)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }

    public TransitionGlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(PanelProperty);
        UpdateShaderValue(MaterialProperty);
        UpdateShaderValue(DispersionProperty);
        UpdateShaderValue(ProfileProperty);
    }

    private static PixelShader LoadShader()
    {
        using var stream = typeof(TransitionGlassEffect).Assembly.GetManifestResourceStream("GlassDesk.UI.Effects.TransitionGlass.ps")
            ?? throw new InvalidOperationException("Transition glass shader is missing.");
        var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.HardwareOnly };
        shader.SetStreamSource(stream);
        shader.Freeze();
        return shader;
    }
}
