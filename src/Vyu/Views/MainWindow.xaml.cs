using System.Windows;
using Vyu.Kinetic;

namespace Vyu.Views;

/// <summary>The video window: black, frameless, hosts mpv's render surface. Controls live in <see cref="ControlsWindow"/>.</summary>
public partial class MainWindow : Window
{
    private bool _fitted;

    public IntPtr VideoHandle => Video.VideoHandle;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyDarkRounded(this, 0x00000000);
    }

    /// <summary>First video of the session: size the window to the video's shape (within 75% of the screen).</summary>
    public void FitToVideo(long w, long h)
    {
        if (_fitted || w <= 0 || h <= 0 || WindowState != WindowState.Normal || ResizeMode == ResizeMode.NoResize || Topmost) return;
        _fitted = true;
        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.75, maxH = wa.Height * 0.75;
        double scale = Math.Min(1, Math.Min(maxW / w, maxH / h));
        double nw = Math.Max(MinWidth, w * scale), nh = Math.Max(MinHeight, h * scale);
        if (Math.Abs(nw - Width) < 40 && Math.Abs(nh - Height) < 40) return;
        Left = wa.Left + (wa.Width - nw) / 2;
        Top = wa.Top + (wa.Height - nh) / 2;
        Width = nw;
        Height = nh;
    }
}
