using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeCL.Controls;

/// <summary>
/// 圆环进度：底环 + 圆头的陶土橙弧线，数值变化时平滑过渡
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Coral, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 ~ 1</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public RingGauge()
    {
        SetResourceReference(TrackBrushProperty, "AccentSoftBrush");
        SetResourceReference(FillBrushProperty, "AccentBrush");
    }

    /// <summary>
    /// 平滑地变到新数值
    /// </summary>
    public void AnimateTo(double value)
    {
        BeginAnimation(ValueProperty, new DoubleAnimation(Math.Clamp(value, 0, 1), TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), c, r, r);

        var v = Math.Clamp(Value, 0, 0.9999);
        if (v <= 0.001) return;
        // 从正上方顺时针画弧
        var start = new Point(c.X, c.Y - r);
        var angle = v * 2 * Math.PI - Math.PI / 2;
        var end = new Point(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(FillBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}
