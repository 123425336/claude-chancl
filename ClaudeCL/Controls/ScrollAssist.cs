using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCL.Controls;

/// <summary>
/// 滚动增强：按住页面任意空白处拖动即可滚动（松手后带惯性），滚轮滚动也变得平滑。
/// 在输入框、滑块、滚动条等需要拖动的控件上不会触发。
/// </summary>
public static class ScrollAssist
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollAssist), new FrameworkPropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    /// <summary>移动超过这个距离才算拖动，避免误伤点击</summary>
    private const double DragThreshold = 6;

    private sealed class State
    {
        public bool Pressed;
        public bool Dragging;
        public Point Start;
        public double StartOffset;
        public double LastY;
        public long LastTicks;
        public double Velocity;      // 拖动松手后的速度（像素/秒）
        public double WheelTarget;   // 滚轮平滑滚动的目标位置
        public bool Inertia;
        public bool Wheeling;
        public long FrameTicks;
        public EventHandler? Frame;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseLeftButtonDown += OnDown;
            sv.PreviewMouseMove += OnMove;
            sv.PreviewMouseLeftButtonUp += OnUp;
            sv.LostMouseCapture += OnLostCapture;
            sv.PreviewMouseWheel += OnWheel;
            sv.Unloaded += (_, _) => Stop(sv);
        }
        else
        {
            sv.PreviewMouseLeftButtonDown -= OnDown;
            sv.PreviewMouseMove -= OnMove;
            sv.PreviewMouseLeftButtonUp -= OnUp;
            sv.LostMouseCapture -= OnLostCapture;
            sv.PreviewMouseWheel -= OnWheel;
        }
    }

    /// <summary>
    /// 文本框内部的滚动区、按条目滚动的列表不做处理
    /// </summary>
    private static bool Unsupported(ScrollViewer sv)
    {
        if (sv.TemplatedParent is TextBoxBase) return true;
        if (sv.CanContentScroll && sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Item)
            return true;
        return false;
    }

    /// <summary>
    /// 鼠标按在这些控件上时，交给控件自己处理拖动
    /// </summary>
    private static bool OnInteractive(object source, ScrollViewer sv)
    {
        var d = source as DependencyObject;
        while (d is not null && d != sv)
        {
            if (d is TextBoxBase or PasswordBox or ScrollBar or Slider or Thumb or ComboBox) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        var s = States.GetOrCreateValue(sv);
        // 惯性滚动中按下，直接停住
        StopAnimation(sv, s);
        if (Unsupported(sv) || sv.ScrollableHeight <= 0 || OnInteractive(e.OriginalSource, sv)) return;
        s.Pressed = true;
        s.Dragging = false;
        s.Start = e.GetPosition(sv);
        s.StartOffset = sv.VerticalOffset;
        s.LastY = s.Start.Y;
        s.LastTicks = Stopwatch.GetTimestamp();
        s.Velocity = 0;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (!States.TryGetValue(sv, out var s) || !s.Pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            s.Pressed = false;
            return;
        }
        var p = e.GetPosition(sv);
        var dy = p.Y - s.Start.Y;
        if (!s.Dragging)
        {
            if (Math.Abs(dy) < DragThreshold) return;
            // 开始拖动：抢走鼠标捕获，被按下的按钮因此不会触发点击
            s.Dragging = true;
            sv.CaptureMouse();
            sv.Cursor = Cursors.ScrollNS;
        }
        sv.ScrollToVerticalOffset(s.StartOffset - dy);

        // 记录速度，松手后用于惯性滚动
        var now = Stopwatch.GetTimestamp();
        var dt = (now - s.LastTicks) / (double)Stopwatch.Frequency;
        if (dt > 0.004)
        {
            var v = -(p.Y - s.LastY) / dt;
            s.Velocity = s.Velocity * 0.4 + v * 0.6;
            s.LastY = p.Y;
            s.LastTicks = now;
        }
        e.Handled = true;
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (!States.TryGetValue(sv, out var s)) return;
        var wasDragging = s.Dragging;
        s.Pressed = false;
        s.Dragging = false;
        if (!wasDragging) return;
        e.Handled = true;
        sv.ReleaseMouseCapture();
        sv.ClearValue(FrameworkElement.CursorProperty);
        // 停顿后再松手就不带惯性
        var idle = (Stopwatch.GetTimestamp() - s.LastTicks) / (double)Stopwatch.Frequency;
        if (idle < 0.08 && Math.Abs(s.Velocity) > 120)
        {
            s.Inertia = true;
            StartAnimation(sv, s);
        }
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (States.TryGetValue(sv, out var s) && s.Dragging && Mouse.Captured != sv)
        {
            s.Dragging = false;
            s.Pressed = false;
            sv.ClearValue(FrameworkElement.CursorProperty);
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (Unsupported(sv) || sv.ScrollableHeight <= 0) return;
        // 鼠标下面还有能滚动的内层区域时，让内层先滚
        if (InnerCanScroll(e.OriginalSource, sv, e.Delta)) return;
        e.Handled = true;
        var s = States.GetOrCreateValue(sv);
        s.Inertia = false;
        var from = s.Wheeling ? s.WheelTarget : sv.VerticalOffset;
        s.WheelTarget = Math.Clamp(from - e.Delta * 1.1, 0, sv.ScrollableHeight);
        s.Wheeling = true;
        StartAnimation(sv, s);
    }

    private static bool InnerCanScroll(object source, ScrollViewer outer, int delta)
    {
        var d = source as DependencyObject;
        while (d is not null && d != outer)
        {
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                if (delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight) return true;
            }
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void StartAnimation(ScrollViewer sv, State s)
    {
        if (s.Frame is not null) return;
        s.FrameTicks = Stopwatch.GetTimestamp();
        s.Frame = (_, _) => Tick(sv, s);
        CompositionTarget.Rendering += s.Frame;
    }

    private static void Tick(ScrollViewer sv, State s)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Min(0.05, (now - s.FrameTicks) / (double)Stopwatch.Frequency);
        s.FrameTicks = now;
        if (dt <= 0) return;

        if (s.Wheeling)
        {
            // 指数逼近目标位置
            var offset = sv.VerticalOffset;
            var next = offset + (s.WheelTarget - offset) * Math.Min(1, dt * 14);
            if (Math.Abs(s.WheelTarget - next) < 0.5) next = s.WheelTarget;
            sv.ScrollToVerticalOffset(next);
            if (next == s.WheelTarget) s.Wheeling = false;
        }
        else if (s.Inertia)
        {
            var next = sv.VerticalOffset + s.Velocity * dt;
            s.Velocity *= Math.Pow(0.05, dt); // 每秒衰减到 5%
            sv.ScrollToVerticalOffset(next);
            if (Math.Abs(s.Velocity) < 15 || next <= 0 || next >= sv.ScrollableHeight) s.Inertia = false;
        }

        if (!s.Wheeling && !s.Inertia) StopAnimation(sv, s);
    }

    private static void StopAnimation(ScrollViewer sv, State s)
    {
        s.Wheeling = false;
        s.Inertia = false;
        if (s.Frame is null) return;
        CompositionTarget.Rendering -= s.Frame;
        s.Frame = null;
    }

    private static void Stop(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var s)) StopAnimation(sv, s);
    }
}
