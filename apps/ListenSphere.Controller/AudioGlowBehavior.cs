using System.Windows;
using System.Windows.Media.Animation;

namespace ListenSphere.Controller;

/// <summary>
/// Smoothly maps a 0-100 audio peak level to the opacity of a glow overlay.
/// </summary>
public static class AudioGlowBehavior
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached(
        "Level",
        typeof(double),
        typeof(AudioGlowBehavior),
        new PropertyMetadata(0d, OnLevelChanged));

    public static double GetLevel(DependencyObject element) =>
        (double)element.GetValue(LevelProperty);

    public static void SetLevel(DependencyObject element, double value) =>
        element.SetValue(LevelProperty, value);

    private static void OnLevelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
        {
            return;
        }

        double level = Math.Clamp((double)args.NewValue, 0d, 100d) / 100d;
        double targetOpacity = level <= 0.001d
            ? 0.035d
            : Math.Clamp(0.08d + (Math.Pow(level, 0.62d) * 0.78d), 0.08d, 0.86d);
        bool isAttack = targetOpacity > element.Opacity;

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(isAttack ? 80d : 220d),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isAttack ? EasingMode.EaseOut : EasingMode.EaseInOut
            }
        };
        Timeline.SetDesiredFrameRate(animation, 60);

        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
