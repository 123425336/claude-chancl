using System;
using System.Windows;
using System.Windows.Media;

namespace ClaudeCL.Controls;

/// <summary>
/// 星芒加载动画：十二道圆头光芒绕中心缓慢旋转，每道光芒的长度依次起伏，
/// 像在安静思考时轻轻呼吸
/// </summary>
public sealed class SparkLoader : FrameworkElement
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(SparkLoader),
        new FrameworkPropertyMetadata(Brushes.Coral, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SparkLoader),
        new FrameworkPropertyMetadata(true, (d, _) => ((SparkLoader)d).UpdateTicking()));

    public static readonly DependencyProperty RaysProperty = DependencyProperty.Register(
        nameof(Rays), typeof(int), typeof(SparkLoader),
        new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public int Rays { get => (int)GetValue(RaysProperty); set => SetValue(RaysProperty, value); }

    private readonly DateTime _start = DateTime.Now;
    private bool _ticking;

    public SparkLoader()
    {
        SetResourceReference(BrushProperty, "AccentBrush");
        Loaded += (_, _) => UpdateTicking();
        Unloaded += (_, _) => StopTicking();
        IsVisibleChanged += (_, _) => UpdateTicking();
    }

    private void UpdateTicking()
    {
        if (IsActive && IsLoaded && IsVisible) StartTicking();
        else StopTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
        InvalidateVisual();
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var t = (DateTime.Now - _start).TotalSeconds;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2;
        var rays = Math.Max(3, Rays);
        var thickness = Math.Max(1.5, radius * 0.15);
        var inner = radius * 0.16;
        var maxLen = radius - inner - thickness / 2;
        var rotation = IsActive ? t * 38 : 0;

        for (var i = 0; i < rays; i++)
        {
            // 每道光芒的相位错开，形成一圈接一圈的“呼吸”
            var phase = IsActive ? Math.Sin(t * 3.2 - i * (Math.PI * 2 / rays) * 1.5) : 1;
            var lenFactor = 0.42 + 0.58 * (0.5 + 0.5 * phase);
            // 奇偶光芒长短交错，让星芒有手绘感
            if (i % 2 == 1) lenFactor *= 0.78;
            var angle = (i * 360.0 / rays + rotation) * Math.PI / 180;
            var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
            var p1 = center + dir * inner;
            var p2 = center + dir * (inner + maxLen * lenFactor);
            var pen = new Pen(Brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.PushOpacity(0.55 + 0.45 * (0.5 + 0.5 * phase));
            dc.DrawLine(pen, p1, p2);
            dc.Pop();
        }
    }
}
