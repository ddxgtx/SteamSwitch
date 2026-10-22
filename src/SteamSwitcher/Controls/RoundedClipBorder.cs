using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamSwitcher.Controls;

/// <summary>A uniform rounded border that also clips its children to its outline.</summary>
public sealed class RoundedClipBorder : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty) UpdateClip();
    }

    private void UpdateClip()
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var radius = Math.Clamp(CornerRadius.TopLeft, 0, Math.Min(RenderSize.Width, RenderSize.Height) / 2);
        var geometry = new RectangleGeometry(new Rect(RenderSize), radius, radius);
        geometry.Freeze();
        Clip = geometry;
    }
}
