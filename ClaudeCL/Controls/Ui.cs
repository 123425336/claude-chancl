using System.Windows;
using System.Windows.Media;

namespace ClaudeCL.Controls;

/// <summary>
/// 样式模板共用的附加属性：悬停/按下底色、圆角、占位文字、图标
/// </summary>
public static class Ui
{
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetHoverBackground(DependencyObject o) => (Brush?)o.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject o, Brush? v) => o.SetValue(HoverBackgroundProperty, v);

    public static readonly DependencyProperty PressBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetPressBackground(DependencyObject o) => (Brush?)o.GetValue(PressBackgroundProperty);
    public static void SetPressBackground(DependencyObject o, Brush? v) => o.SetValue(PressBackgroundProperty, v);

    public static readonly DependencyProperty HoverForegroundProperty = DependencyProperty.RegisterAttached(
        "HoverForeground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetHoverForeground(DependencyObject o) => (Brush?)o.GetValue(HoverForegroundProperty);
    public static void SetHoverForeground(DependencyObject o, Brush? v) => o.SetValue(HoverForegroundProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(10)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);

    /// <summary>按钮/导航项左侧的图标字形</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string v) => o.SetValue(IconProperty, v);
}
