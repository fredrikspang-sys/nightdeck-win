using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nightdeck;

/// <summary>
/// Hi-fi volume wedge beside the fader: wide edge at the top, point at the bottom,
/// right angle on the side next to the fader. The path is static. The fill under the
/// current level is decoration and never sends a command.
/// </summary>
public sealed class VolumeWedge : FrameworkElement
{
    const double Thumb = 10;
    const double SideGap = 4;

    /// <summary>Unit triangle. Stretch maps it onto the fader travel. Right angle is the top-right corner.</summary>
    static readonly Geometry Triangle = CreateTriangle();

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VolumeWedge),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((VolumeWedge)d).Refit()));

    readonly Path _body = new() { Data = Triangle, Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = true };
    readonly Path _level = new() { Data = Triangle, Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = true };
    readonly RectangleGeometry _clip = new();

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public VolumeWedge()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        var margin = new Thickness(0, Thumb / 2, SideGap, Thumb / 2);
        _body.Margin = margin;
        _level.Margin = margin;
        _body.StrokeThickness = 1;
        _body.StrokeLineJoin = PenLineJoin.Miter;
        _level.Clip = _clip;
        AddVisualChild(_body);
        AddVisualChild(_level);
        SizeChanged += (_, _) => Refit();
        Loaded += (_, _) =>
        {
            var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
            _body.Fill = muted;
            _body.Stroke = muted;
            _body.Opacity = 0.4;
            _level.Fill = TryFindResource("VolumeBrush") as Brush ?? Brushes.CadetBlue;
            Refit();
        };
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _body : _level;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsPositiveInfinity(available.Width) ? 32 : available.Width;
        double h = double.IsPositiveInfinity(available.Height) ? 240 : available.Height;
        var slot = new Size(Math.Max(0, w), Math.Max(0, h));
        _body.Measure(slot);
        _level.Measure(slot);
        return slot;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var slot = new Rect(finalSize);
        _body.Arrange(slot);
        _level.Arrange(slot);
        Refit();
        return finalSize;
    }

    /// <summary>Show the volume accent on the part of the wedge at or below the fader thumb.</summary>
    void Refit()
    {
        double h = _level.ActualHeight;
        double w = _level.ActualWidth;
        if (w < 1 || h < 1 || Level <= 0)
        {
            _clip.Rect = Rect.Empty;
            return;
        }
        double y = (1 - Math.Clamp(Level, 0, 100) / 100.0) * h;
        _clip.Rect = new Rect(-1, y, w + 2, Math.Max(0, h - y));
    }

    static Geometry CreateTriangle()
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(1, 0), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(1, 1), isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }
}
