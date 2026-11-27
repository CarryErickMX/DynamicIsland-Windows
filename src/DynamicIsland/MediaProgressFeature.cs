using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Media.Control;

namespace DynamicIsland;

// =====================================================================
//  Barra de progreso de la canción (tiempo transcurrido / restante)
//  con arrastre para adelantar o regresar. Archivo independiente.
// =====================================================================
public partial class MainWindow
{
    private readonly DispatcherTimer _pTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Grid? _pRow, _pHit;
    private Border? _pFill;
    private Ellipse? _pThumb;
    private TextBlock? _pElapsed, _pRemaining;

    private TimeSpan _pStart, _pLength;
    private bool _pCanSeek, _pDragging;
    private double _pDragFraction;

    // Tras adelantar, algunas apps tardan en reportar la nueva posición
    private DateTime _pHoldSetAt = DateTime.MinValue;
    private TimeSpan _pHoldPos;

    private void InitMediaProgress()
    {
        if (TitleText.Parent is not StackPanel info) return;

        _pRow = new Grid { Margin = new Thickness(0, 7, 0, 0), Visibility = Visibility.Collapsed };
        _pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _pElapsed = TimeLabel(TextAlignment.Left);
        _pRemaining = TimeLabel(TextAlignment.Right);
        Grid.SetColumn(_pRemaining, 2);

        _pHit = new Grid { Height = 14, Margin = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent };
        Grid.SetColumn(_pHit, 1);

        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");

        _pFill = new Border
        {
            Height = 3, Width = 0, CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        _pFill.SetResourceReference(Border.BackgroundProperty, "TextPrimary");

        // El círculo solo aparece al pasar el mouse, como en el iPhone
        _pThumb = new Ellipse
        {
            Width = 9, Height = 9, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        _pThumb.SetResourceReference(Shape.FillProperty, "TextPrimary");

        _pHit.Children.Add(track);
        _pHit.Children.Add(_pFill);
        _pHit.Children.Add(_pThumb);

        _pHit.MouseEnter += (_, _) => { if (_pCanSeek) FadeThumb(1); };
        _pHit.MouseLeave += (_, _) => { if (!_pDragging) FadeThumb(0); };
        _pHit.MouseLeftButtonDown += Progress_MouseDown;
        _pHit.MouseMove += Progress_MouseMove;
        _pHit.MouseLeftButtonUp += Progress_MouseUp;
        _pHit.SizeChanged += (_, _) => UpdateProgress();

        _pRow.Children.Add(_pElapsed);
        _pRow.Children.Add(_pHit);
        _pRow.Children.Add(_pRemaining);
        info.Children.Add(_pRow);

        _pTimer.Tick += (_, _) => UpdateProgress();
        _pTimer.Start();
    }

    private static TextBlock TimeLabel(TextAlignment align)
    {
        var tb = new TextBlock
        {
            FontSize = 10, MinWidth = 28, TextAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        return tb;
    }

    private void FadeThumb(double to) =>
        _pThumb?.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    // ---------- Lectura de la posición ----------
    private void UpdateProgress()
    {
        if (_pRow == null) return;
        if (MediaView.Visibility != Visibility.Visible && _pRow.Visibility == Visibility.Visible) return;

        var s = _session;
        if (s == null || !_hasMedia) { _pRow.Visibility = Visibility.Collapsed; return; }

        GlobalSystemMediaTransportControlsSessionTimelineProperties t;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? pi;
        try
        {
            t = s.GetTimelineProperties();
            pi = s.GetPlaybackInfo();
        }
        catch
        {
            _pRow.Visibility = Visibility.Collapsed;
            return;
        }

        var length = t.EndTime - t.StartTime;
        if (length.TotalSeconds < 1)
        {
            // La app no informa duración (algunas radios o transmisiones en vivo)
            _pRow.Visibility = Visibility.Collapsed;
            return;
        }

        _pStart = t.StartTime;
        _pLength = length;
        _pCanSeek = pi?.Controls?.IsPlaybackPositionEnabled == true;
        _pHit!.Cursor = _pCanSeek ? Cursors.Hand : Cursors.Arrow;

        bool playing = pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double rate = pi?.PlaybackRate ?? 1.0;

        TimeSpan pos;
        if (DateTime.Now - _pHoldSetAt < TimeSpan.FromSeconds(1.5))
        {
            pos = _pHoldPos + (playing ? DateTime.Now - _pHoldSetAt : TimeSpan.Zero);
        }
        else
        {
            pos = t.Position - t.StartTime;
            // Windows solo actualiza la posición de vez en cuando: se calcula el avance desde la última lectura
            if (playing && t.LastUpdatedTime.Year > 2000)
            {
                var since = DateTimeOffset.Now - t.LastUpdatedTime;
                if (since > TimeSpan.Zero)
                    pos += TimeSpan.FromTicks((long)(since.Ticks * rate));
            }
        }

        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (pos > length) pos = length;

        if (!_pDragging) SetProgressUI(pos);
        _pRow.Visibility = Visibility.Visible;
    }

    private void SetProgressUI(TimeSpan pos)
    {
        double f = _pLength.Ticks > 0 ? (double)pos.Ticks / _pLength.Ticks : 0;
        double w = _pHit!.ActualWidth;
        _pFill!.Width = Math.Max(0, w * f);
        _pThumb!.Margin = new Thickness(Math.Clamp(w * f - 4.5, 0, Math.Max(0, w - 9)), 0, 0, 0);
        _pElapsed!.Text = FormatTime(pos);
        _pRemaining!.Text = "-" + FormatTime(_pLength - pos);
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---------- Arrastrar para adelantar / regresar ----------
    private void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pCanSeek) return;
        _pDragging = true;
        _draggingVol = true;          // mantiene la isla abierta mientras se arrastra
        _pHit!.CaptureMouse();
        PreviewSeek(e.GetPosition(_pHit).X);
        e.Handled = true;
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pDragging) PreviewSeek(e.GetPosition(_pHit).X);
    }

    private async void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pDragging) return;
        _pDragging = false;
        _draggingVol = false;
        _pHit!.ReleaseMouseCapture();
        if (!_pHit.IsMouseOver) FadeThumb(0);

        var target = TimeSpan.FromTicks((long)(_pLength.Ticks * _pDragFraction));
        _pHoldPos = target;
        _pHoldSetAt = DateTime.Now;
        SetProgressUI(target);

        if (!Island.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }

        try
        {
            if (_session != null)
                await _session.TryChangePlaybackPositionAsync((_pStart + target).Ticks);
        }
        catch { }
    }

    private void PreviewSeek(double x)
    {
        double w = _pHit!.ActualWidth;
        if (w <= 0) return;
        _pDragFraction = Math.Clamp(x / w, 0, 1);
        SetProgressUI(TimeSpan.FromTicks((long)(_pLength.Ticks * _pDragFraction)));
    }
}
