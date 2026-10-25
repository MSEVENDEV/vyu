using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vyu.Kinetic;

/// <summary>
/// Kinetic motion primitives. Everything enters with the same "rise" (fade + 14px lift on the Out curve),
/// lists stagger by 35 ms, and nothing waits more than ~0.5 s. Respects Windows "Animation effects" off.
/// </summary>
public static class Motion
{
    public static readonly IEasingFunction Out = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    public static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
    public static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ---------- attached: Motion.Enter="True" (+ Motion.Delay / Motion.Stagger) ----------
    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterChanged));
    public static bool GetEnter(DependencyObject d) => (bool)d.GetValue(EnterProperty);
    public static void SetEnter(DependencyObject d, bool v) => d.SetValue(EnterProperty, v);

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(Motion), new PropertyMetadata(0.0));
    public static double GetDelay(DependencyObject d) => (double)d.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject d, double v) => d.SetValue(DelayProperty, v);

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    public static bool GetStagger(DependencyObject d) => (bool)d.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject d, bool v) => d.SetValue(StaggerProperty, v);

    private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        fe.Loaded += (_, _) => Rise(fe, GetDelay(fe) + StaggerDelay(fe));
    }

    private static double StaggerDelay(FrameworkElement fe)
    {
        if (!GetStagger(fe)) return 0;
        var container = fe.TemplatedParent as FrameworkElement ?? fe;
        DependencyObject? cur = container;
        while (cur is not null)
        {
            var parent = VisualTreeHelper.GetParent(cur);
            if (parent is Panel p && p.IsItemsHost)
            {
                int i = p.Children.IndexOf(cur as UIElement);
                return Math.Clamp(i, 0, 12) * 35;
            }
            cur = parent;
        }
        return 0;
    }

    /// <summary>Fade in while rising 14 px.</summary>
    public static void Rise(UIElement el, double delayMs = 0, double distance = 14)
    {
        if (!Enabled) { el.Opacity = 1; return; }
        var tt = EnsureTranslate(el);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { BeginTime = begin, EasingFunction = Out });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = begin, EasingFunction = Out });
    }

    public static void SlideInX(UIElement el, double from, double ms = 520)
    {
        if (!Enabled) { el.Opacity = 1; return; }
        var tt = EnsureTranslate(el);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.6)) { EasingFunction = Out });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    public static void SlideOutX(UIElement el, double to, double ms, Action? done = null)
    {
        if (!Enabled) { done?.Invoke(); return; }
        var tt = EnsureTranslate(el);
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (done is not null) a.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    public static void Fade(UIElement el, double to, double ms = 240, Action? done = null)
    {
        if (!Enabled) { el.Opacity = to; done?.Invoke(); return; }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        if (done is not null) a.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>Pop: scale 0.9 → 1 with a little overshoot.</summary>
    public static void Pop(UIElement el, double from = 0.9, double ms = 420)
    {
        if (!Enabled) return;
        var st = EnsureScale(el);
        var a = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Spring };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Animate a double property on any animatable (used for values like ring progress).</summary>
    public static void To(IAnimatable target, DependencyProperty dp, double to, double ms = 360, IEasingFunction? ease = null) =>
        target.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(Enabled ? ms : 0)) { EasingFunction = ease ?? Out });

    public static TranslateTransform EnsureTranslate(UIElement el)
    {
        switch (el.RenderTransform)
        {
            case TranslateTransform t when !t.IsFrozen: return t;
            case TransformGroup g when !g.IsFrozen:
                var existing = g.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (existing is not null) return existing;
                var nt = new TranslateTransform();
                g.Children.Add(nt);
                return nt;
            default:
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform());
                var tr = new TranslateTransform();
                group.Children.Add(tr);
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                el.RenderTransform = group;
                return tr;
        }
    }

    public static ScaleTransform EnsureScale(UIElement el)
    {
        EnsureTranslate(el);
        if (el.RenderTransform is TransformGroup g)
        {
            var s = g.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (s is null) { s = new ScaleTransform(); g.Children.Insert(0, s); }
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            return s;
        }
        var st = new ScaleTransform();
        el.RenderTransform = st;
        return st;
    }
}
