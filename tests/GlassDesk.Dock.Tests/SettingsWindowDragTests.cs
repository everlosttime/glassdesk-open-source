using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public class SettingsWindowDragTests
{
    [Fact]
    public void HeaderWhitespaceAndTitleShareTheDragSurfaceWithoutCoveringSettingsControls()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                // The old title-only handler leaves the top padding, right-hand
                // whitespace and gap above the settings body without a drag target.
                foreach (var point in new[]
                {
                    new Point(200, 12), new Point(80, 40),
                    new Point(500, 40), new Point(700, 40), new Point(400, 65),
                })
                {
                    var hit = content.InputHitTest(point) as DependencyObject;
                    Assert.True(HasDragSurfaceAncestor(hit), $"No header drag surface at {point}; hit {hit}");
                }

                foreach (var name in new[] { "SettingsPageList", "AppearanceMaterialCombo",
                    "DockTransparencySlider", "ResetPageButton" })
                {
                    var control = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
                    var point = control.TranslatePoint(new Point(control.ActualWidth / 2,
                        control.ActualHeight / 2), content);
                    var hit = content.InputHitTest(point) as DependencyObject;
                    Assert.NotNull(hit);
                    Assert.False(HasDragSurfaceAncestor(hit), $"Header covers {name}");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static bool HasDragSurfaceAncestor(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Border { Name: "SettingsDragArea" }) return true;
        return false;
    }
}
