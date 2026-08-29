using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace ListenSphere.Controller;

/// <summary>
/// Smoothly interpolates audio meter values without causing layout remeasurement.
/// </summary>
public static class AudioLevelBehavior
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached(
        "Level",
        typeof(double),
        typeof(AudioLevelBehavior),
        new PropertyMetadata(0d, OnLevelChanged));

    public static double GetLevel(DependencyObject element) =>
        (double)element.GetValue(LevelProperty);

    public static void SetLevel(DependencyObject element, double value) =>
        element.SetValue(LevelProperty, value);

    private static void OnLevelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not RangeBase meter)
        {
            return;
        }

        double target = Math.Clamp((double)args.NewValue, meter.Minimum, meter.Maximum);
        double current = meter.Value;
        bool isAttack = target > current;
        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(isAttack ? 55d : 145d),
            EasingFunction = new QuadraticEase
            {
                EasingMode = isAttack ? EasingMode.EaseOut : EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        meter.BeginAnimation(
            RangeBase.ValueProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }
}
