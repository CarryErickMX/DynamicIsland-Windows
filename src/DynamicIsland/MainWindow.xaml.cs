using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.System.Power;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    // ---------- Tamaños de cada estado (ancho, alto, radio) ----------
    private const double CompactW = 180, CompactMediaW = 230, CompactH = 36, CompactR = 18;
    private const double MediaW = 400, MediaH = 180, MediaR = 38;
    private const double InfoW = 340, InfoH = 110, InfoR = 34;
    private const double VolRowExtra = 28;   // alto extra cuando aparece la barra de volumen
    private const double ActivityW = 320, ActivityH = 44, ActivityR = 22;
    private const double VolumeW = 210, VolumeH = 36, VolumeR = 18;   // aviso de volumen compacto

    private static readonly CultureInfo Es = new("es-MX");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DynamicIsland";

    // ---------- Estado ----------
    private bool _hover;
    private bool _activity;
    private bool _hasMedia;
    private bool _isPlaying;
    private string _lastTitle = "";
    private bool _firstMediaLoad = true;
    private Grid? _currentView;
    private Grid? _activityView;
    private double _tW = -1, _tH = -1, _tR = -1;   // último destino de la animación

    // ---------- Volumen ----------
    private IAudioEndpointVolume? _audio;
    private int _audioRefresh;
    private float _volume = -1;
    private bool _muted;
    private bool _volReady;
    private bool _draggingVol;
    private readonly DispatcherTimer _volTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _volRecent;   // true solo unos segundos después de un cambio de volumen
    private readonly DispatcherTimer _volRowTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private IntPtr _hwnd;
    private Rect _lastRegion = Rect.Empty;

    // ---------- Radio de esquinas animable ----------
    public static readonly DependencyProperty IslandRadiusProperty =
        DependencyProperty.Register(nameof(IslandRadius), typeof(double), typeof(MainWindow),
            new PropertyMetadata(CompactR, (d, e) =>
            {
                var w = (MainWindow)d;
                var cr = new CornerRadius(Math.Max(0, (double)e.NewValue));
                w.Island.CornerRadius = cr;
                w.Sheen.CornerRadius = cr;
            }));

    public double IslandRadius
    {
        get => (double)GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();

        _clockTimer.Tick += (_, _) => UpdateClock();
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (_draggingVol) return;     // no cerrar mientras se arrastra la barra
            _hover = false;
            UpdateState();
        };
        _volTimer.Tick += (_, _) => PollVolume();
        _volRowTimer.Tick += (_, _) =>
        {
            _volRowTimer.Stop();
            if (_draggingVol) { _volRowTimer.Start(); return; }
            _volRecent = false;
            UpdateState();
        };
        _activityTimer.Tick += (_, _) => { _activityTimer.Stop(); _activity = false; UpdateState(); };
    }

    // =====================================================================
    //  Inicio
    // =====================================================================
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = 0;

        UpdateClock();
        _clockTimer.Start();

        UpdateBattery();
        PowerManager.PowerSupplyStatusChanged += (_, _) => Dispatcher.InvokeAsync(OnPowerChanged);
        PowerManager.RemainingChargePercentChanged += (_, _) => Dispatcher.InvokeAsync(UpdateBattery);

        StartupItem.IsChecked = IsStartupEnabled();

        _themeMode = LoadThemeMode();
        ApplyTheme(animate: false);
        SystemEvents.UserPreferenceChanged += (_, ev) =>
        {
            if (_themeMode == ThemeMode.Auto && ev.Category == UserPreferenceCategory.General)
                Dispatcher.InvokeAsync(() => ApplyTheme(animate: true));
        };

        _currentView = CompactView;
        CompactView.Opacity = 1;

        PollVolume();
        _volTimer.Start();

        await InitMediaAsync();
        UpdateState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Oculta de Alt+Tab y evita que robe el foco
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Desenfoque DWM desactivado: en ventanas transparentes de WPF
        // Windows lo aplica a todo el rectángulo y genera un recuadro gris.
    }

    // =====================================================================
    //  Liquid Glass: desenfoque real del fondo (DWM) recortado a la isla
    // =====================================================================
    private void EnableBlur()
    {
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void UpdateBlurRegion()
    {
        if (_hwnd == IntPtr.Zero || !Island.IsLoaded) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        Point p = Island.TransformToAncestor(this).Transform(new Point(0, 0));
        var r = new Rect(
            Math.Round(p.X * dpi.DpiScaleX),
            Math.Round(p.Y * dpi.DpiScaleY),
            Math.Round(Island.ActualWidth * dpi.DpiScaleX),
            Math.Round(Island.ActualHeight * dpi.DpiScaleY));

        if (r == _lastRegion) return;
        _lastRegion = r;

        int d = (int)Math.Round(IslandRadius * 2 * dpi.DpiScaleX);
        IntPtr rgn = CreateRoundRectRgn((int)r.Left, (int)r.Top, (int)r.Right + 1, (int)r.Bottom + 1, d, d);
        SetWindowRgn(_hwnd, rgn, true); // Windows se queda con la región; no se libera aquí
    }

    // =====================================================================
    //  Máquina de estados + animaciones
    // =====================================================================
    private void UpdateState()
    {
        // La barra solo aparece si el volumen acaba de cambiar
        bool showRow = _hover && _volRecent;
        ShowVolumeRow(showRow);
        double extra = showRow ? VolRowExtra : 0;
        MediaView.Margin = new Thickness(20, 14, 20, showRow ? 38 : 12);
        InfoView.Margin = new Thickness(24, 12, 24, showRow ? 38 : 12);

        if (_hover)
        {
            if (_hasMedia) Morph(MediaW, MediaH + extra, MediaR, MediaView);
            else Morph(InfoW, InfoH + extra, InfoR, InfoView);
        }
        else if (_activity && _activityView == VolumeActivityView)
        {
            Morph(VolumeW, VolumeH, VolumeR, VolumeActivityView);
        }
        else if (_activity && _activityView != null)
        {
            Morph(ActivityW, ActivityH, ActivityR, _activityView);
        }
        else
        {
            Morph(_hasMedia ? CompactMediaW : CompactW, CompactH, CompactR, CompactView);
        }
    }

    private void Morph(double w, double h, double r, Grid target)
    {
        var dur = TimeSpan.FromMilliseconds(480);
        var spring = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Si ya va hacia ese tamaño y vista, no reiniciar (evita trabones al mantener una tecla)
        if (w == _tW && h == _tH && r == _tR && _currentView == target) return;
        _tW = w; _tH = h; _tR = r;

        Island.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = spring });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = spring });
        BeginAnimation(IslandRadiusProperty, new DoubleAnimation(r, dur) { EasingFunction = smooth });

        if (_currentView == target) return;

        if (_currentView != null)
        {
            _currentView.BeginAnimation(OpacityProperty, null);
            _currentView.Opacity = 0;
            _currentView.Visibility = Visibility.Collapsed;
        }

        target.Visibility = Visibility.Visible;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(140),
            EasingFunction = smooth
        });
        _currentView = target;
    }

    private void ShowActivity(string glyph, Brush iconBrush, string text, string right)
    {
        if (_hover) return;
        ActivityIcon.Text = glyph;
        ActivityIcon.Foreground = iconBrush;
        ActivityText.Text = text;
        ActivityRight.Text = right;

        _activityView = ActivityView;
        _activity = true;
        _activityTimer.Stop();
        _activityTimer.Start();
        UpdateState();
    }

    // =====================================================================
    //  Volumen
    // =====================================================================
    private void PollVolume()
    {
        try
        {
            // Se vuelve a obtener el dispositivo cada ~3 s por si cambias a audífonos, etc.
            if (_audio == null || ++_audioRefresh >= 75)
            {
                if (_audio != null) Marshal.ReleaseComObject(_audio);
                _audio = AudioHelper.GetDefaultEndpointVolume();
                _audioRefresh = 0;
            }
            if (_audio == null) return;

            _audio.GetMasterVolumeLevelScalar(out float v);
            _audio.GetMute(out bool m);

            bool changed = Math.Abs(v - _volume) > 0.004f || m != _muted;
            _volume = v;
            _muted = m;

            if (changed || !_volReady)
            {
                UpdateVolumeUI();
                if (_volReady && !_draggingVol) ShowVolumeActivity();
            }
            _volReady = true;
        }
        catch
        {
            _audio = null;
        }
    }

    private void SetVolume(float v)
    {
        if (_audio == null) return;
        v = Math.Clamp(v, 0f, 1f);
        var ctx = Guid.Empty;
        try
        {
            if (_muted && v > 0) _audio.SetMute(false, ref ctx);
            _audio.SetMasterVolumeLevelScalar(v, ref ctx);
        }
        catch { }
        PollVolume();
    }

    private void UpdateVolumeUI()
    {
        if (_volume < 0) return;
        int pct = (int)Math.Round(_volume * 100);
        string glyph = VolumeGlyph();

        VolText.Text = pct.ToString();
        VolActText.Text = pct.ToString();
        VolIcon.Text = glyph;
        VolActIcon.Text = glyph;

        double shown = _muted ? 0 : _volume;
        double w = VolHit.ActualWidth;
        double fill = Math.Max(0, w * shown);
        var thumb = new Thickness(Math.Clamp(w * shown - 5, 0, Math.Max(0, w - 10)), 0, 0, 0);

        if (_draggingVol)
        {
            // Al arrastrar: respuesta inmediata bajo el cursor
            VolFill.BeginAnimation(WidthProperty, null);
            VolThumb.BeginAnimation(MarginProperty, null);
            VolFill.Width = fill;
            VolThumb.Margin = thumb;
        }
        else
        {
            var d = TimeSpan.FromMilliseconds(140);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            VolFill.BeginAnimation(WidthProperty, new DoubleAnimation(fill, d) { EasingFunction = ease });
            VolThumb.BeginAnimation(MarginProperty, new ThicknessAnimation(thumb, d) { EasingFunction = ease });
        }
        VolActScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(shown, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private string VolumeGlyph()
    {
        if (_muted || _volume <= 0.001f) return "\uE74F";   // silencio
        if (_volume < 0.34f) return "\uE993";
        if (_volume < 0.67f) return "\uE994";
        return "\uE995";
    }

    private void ShowVolumeActivity()
    {
        if (_hover)
        {
            // Isla expandida: mostrar la barra unos segundos dentro de ella
            bool wasShown = _volRecent;
            _volRecent = true;
            _volRowTimer.Stop();
            _volRowTimer.Start();
            if (!wasShown) UpdateState();
            return;
        }

        if (_activity && _activityView == VolumeActivityView)
        {
            _activityTimer.Stop();
            _activityTimer.Start();
            return;
        }
        _activityView = VolumeActivityView;
        _activity = true;
        _activityTimer.Stop();
        _activityTimer.Start();
        UpdateState();
    }

    private void ShowVolumeRow(bool show)
    {
        if (show)
        {
            if (VolumeRow.Visibility == Visibility.Visible) return;
            VolumeRow.Visibility = Visibility.Visible;
            VolumeRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(140)
            });
        }
        else
        {
            VolumeRow.BeginAnimation(OpacityProperty, null);
            VolumeRow.Opacity = 0;
            VolumeRow.Visibility = Visibility.Collapsed;
        }
    }

    private void SetVolumeFromMouse(double x)
    {
        double w = VolHit.ActualWidth;
        if (w <= 0) return;
        SetVolume((float)(x / w));
    }

    private void Vol_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingVol = true;
        VolHit.CaptureMouse();
        SetVolumeFromMouse(e.GetPosition(VolHit).X);
        e.Handled = true;
    }

    private void Vol_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingVol) SetVolumeFromMouse(e.GetPosition(VolHit).X);
    }

    private void Vol_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingVol = false;
        VolHit.ReleaseMouseCapture();
        _volRowTimer.Stop();
        _volRowTimer.Start();
        if (!Island.IsMouseOver) { _leaveTimer.Stop(); _leaveTimer.Start(); }
    }

    private void VolHit_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVolumeUI();

    private void Island_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_volume < 0) return;
        SetVolume(_volume + (e.Delta > 0 ? 0.02f : -0.02f));
        ShowVolumeActivity();
        e.Handled = true;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_audio == null) return;
        var ctx = Guid.Empty;
        try { _audio.SetMute(!_muted, ref ctx); } catch { }
        PollVolume();
    }

    // =====================================================================
    //  Mouse
    // =====================================================================
    private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (_hover) return;
        _hover = true;
        UpdateState();
    }

    private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    // =====================================================================
    //  Reloj
    // =====================================================================
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        BigClock.Text = now.ToString("HH:mm");
        var date = now.ToString("dddd d 'de' MMMM", Es);
        DateText.Text = char.ToUpper(date[0]) + date[1..];
        MediaHeaderClock.Text = $"{now:HH:mm} · {DateText.Text}";
    }

    // =====================================================================
    //  Batería
    // =====================================================================
    private bool HasBattery => PowerManager.BatteryStatus != BatteryStatus.NotPresent;
    private bool IsPluggedIn => PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

    private void UpdateBattery()
    {
        if (!HasBattery)
        {
            BatteryCompact.Text = "";
            MediaHeaderBattery.Text = "";
            InfoBatteryIcon.Text = "\uE7F4";
            InfoBatteryText.Text = "Corriente AC";
            UpdateCompactIndicators();
            return;
        }

        int pct = PowerManager.RemainingChargePercent;
        SetBatteryText(BatteryCompact, pct);
        SetBatteryText(MediaHeaderBattery, pct);
        InfoBatteryIcon.Text = IsPluggedIn ? "\uE83E" : BatteryGlyph(pct);
        InfoBatteryText.Text = $"{pct}%";
        UpdateCompactIndicators();
    }

    // Escribe "⚡ 93%"; en tema claro el rayo va en verde, como en el iPhone
    private void SetBatteryText(System.Windows.Controls.TextBlock tb, int pct)
    {
        tb.Inlines.Clear();
        if (IsPluggedIn)
        {
            var bolt = new System.Windows.Documents.Run("⚡ ");
            if (_isLight)
                bolt.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x9D, 0x45));
            tb.Inlines.Add(bolt);
        }
        tb.Inlines.Add(new System.Windows.Documents.Run($"{pct}%"));
    }

    private static string BatteryGlyph(int pct)
    {
        int level = Math.Clamp(pct / 10, 0, 10);
        return level == 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
    }

    private void OnPowerChanged()
    {
        UpdateBattery();
        if (!HasBattery) return;

        int pct = PowerManager.RemainingChargePercent;
        if (IsPluggedIn)
            ShowActivity("\uE945", new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)), "Cargando", $"{pct}%");
        else
            ShowActivity("\uE83F", (Brush)FindResource("TextPrimary"), "Desconectado", $"{pct}%");
    }

    // =====================================================================
    //  Música
    // =====================================================================
    private async Task InitMediaAsync()
    {
        try
        {
            _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mediaManager.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(AttachSession);
            AttachSession();
        }
        catch
        {
            _hasMedia = false;
        }
    }

    private void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = _mediaManager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        _ = RefreshMediaAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => Dispatcher.InvokeAsync(() => { _ = RefreshMediaAsync(); });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => Dispatcher.InvokeAsync(() => { _ = RefreshMediaAsync(); });

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session == null)
        {
            SetNoMedia();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";

            if (string.IsNullOrWhiteSpace(title))
            {
                SetNoMedia();
                return;
            }

            _hasMedia = true;
            _isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TitleText.Text = title;
            ArtistText.Text = artist;
            PlayIcon.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;

            var art = props?.Thumbnail != null ? await LoadThumbnailAsync(props.Thumbnail) : null;
            if (art != null)
            {
                var brush = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                BigArt.Background = brush;
                MiniArt.Background = brush;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                BigArt.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ArtPlaceholderBg");
                MiniArt.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ArtPlaceholderBg");
                ArtPlaceholder.Visibility = Visibility.Visible;
            }

            if (title != _lastTitle)
            {
                if (!_firstMediaLoad && _isPlaying)
                    ShowActivity("\uE8D6", (Brush)FindResource("VolIconBrush"), title, artist);
                _lastTitle = title;
            }
            _firstMediaLoad = false;
        }
        catch
        {
            SetNoMedia();
            return;
        }

        UpdateCompactIndicators();
        UpdateState();
    }

    private void SetNoMedia()
    {
        _hasMedia = false;
        _isPlaying = false;
        _lastTitle = "";
        UpdateCompactIndicators();
        UpdateState();
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStream();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    // =====================================================================
    //  Indicadores de la vista compacta
    // =====================================================================
    private void UpdateCompactIndicators()
    {
        MiniArt.Visibility = _hasMedia ? Visibility.Visible : Visibility.Collapsed;

        bool showEq = _hasMedia && _isPlaying;
        EqBars.Visibility = showEq ? Visibility.Visible : Visibility.Collapsed;
        BatteryCompact.Visibility = !showEq && HasBattery ? Visibility.Visible : Visibility.Collapsed;

        var bars = new[] { Bar1, Bar2, Bar3, Bar4 };
        var speeds = new[] { 420, 300, 520, 360 };
        for (int i = 0; i < bars.Length; i++)
        {
            if (bars[i].RenderTransform is not ScaleTransform st) continue;
            if (showEq)
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(speeds[i]))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleY = 0.4;
            }
        }
    }

    // =====================================================================
    //  Temas (Automático / Oscuro / Claro)
    // =====================================================================
    private enum ThemeMode { Auto, Dark, Light }
    private bool _isLight;
    private ThemeMode _themeMode = ThemeMode.Auto;
    private const string SettingsKey = @"Software\DynamicIsland";

    private static ThemeMode LoadThemeMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey, false);
        return Enum.TryParse(key?.GetValue("Theme") as string, out ThemeMode m) ? m : ThemeMode.Auto;
    }

    private static void SaveThemeMode(ThemeMode m)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key?.SetValue("Theme", m.ToString());
    }

    private static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    private void ThemeAuto_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.Auto);
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.Dark);
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.Light);

    private void SetThemeMode(ThemeMode m)
    {
        _themeMode = m;
        SaveThemeMode(m);
        ApplyTheme(animate: true);
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static LinearGradientBrush Linear(Point a, Point b, params (string c, double o)[] stops)
    {
        var br = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        foreach (var (c, o) in stops) br.GradientStops.Add(new GradientStop(C(c), o));
        br.Freeze();
        return br;
    }

    private static RadialGradientBrush Glow(string center)
    {
        var br = new RadialGradientBrush
        {
            Center = new Point(0.75, 1.1), GradientOrigin = new Point(0.75, 1.1),
            RadiusX = 0.7, RadiusY = 0.9
        };
        br.GradientStops.Add(new GradientStop(C(center), 0));
        br.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        br.Freeze();
        return br;
    }

    private static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }

    private void ApplyTheme(bool animate)
    {
        bool light = _themeMode == ThemeMode.Light ||
                     (_themeMode == ThemeMode.Auto && WindowsUsesLightTheme());
        _isLight = light;

        ThemeAutoItem.IsChecked = _themeMode == ThemeMode.Auto;
        ThemeDarkItem.IsChecked = _themeMode == ThemeMode.Dark;
        ThemeLightItem.IsChecked = _themeMode == ThemeMode.Light;

        var R = Resources;
        var top = new Point(0, 0);
        var bottom = new Point(0, 1);
        var diag = new Point(1, 1);
        var right = new Point(1, 0);

        if (light)
        {
            // ---- TEMA CLARO: vidrio blanco esmerilado ----
            R["IslandBg"] = Linear(top, bottom, ("#FBFCFCFE", 0), ("#F8F3F4F8", 0.6), ("#F6ECEEF4", 1));
            R["IslandBorder"] = Linear(top, diag, ("#FFFFFFFF", 0), ("#60FFFFFF", 0.3), ("#18000000", 0.6), ("#30000000", 1));
            R["SheenBrush"] = Linear(top, bottom, ("#90FFFFFF", 0), ("#20FFFFFF", 0.35), ("#00FFFFFF", 0.6), ("#0A3A6FE0", 1));
            R["GlowBrush"] = Glow("#305A8CFF");
            R["VolumeBrush"] = Linear(top, right, ("#2F5FD0", 0), ("#4C7DF0", 0.6), ("#7FA8FF", 1));
            R["TextPrimary"] = Solid("#1C1C1E");
            R["TextSecondary"] = Solid("#48484D");
            R["TextTertiary"] = Solid("#5A5A5F");
            R["IconBrush"] = Solid("#1C1C1E");
            R["VolIconBrush"] = Solid("#2F5FD0");
            R["TrackBrush"] = Solid("#1F000000");
            R["ArtPlaceholderBg"] = Solid("#E3E5EA");
            R["ArtPlaceholderFg"] = Solid("#9A9AA0");
            R["ArtBorder"] = Solid("#26000000");
            R["ThumbBrush"] = Solid("#FFFFFF");
            IslandShadow.Color = C("#405070");
            IslandShadow.Opacity = 0.30;
            IslandShadow.BlurRadius = 20;
        }
        else
        {
            // ---- TEMA OSCURO (el actual) ----
            R["IslandBg"] = Linear(top, bottom, ("#F7202330", 0), ("#FA0E1018", 0.6), ("#FC080A10", 1));
            R["IslandBorder"] = Linear(top, diag, ("#B0FFFFFF", 0), ("#30FFFFFF", 0.25), ("#10FFFFFF", 0.55), ("#70FFFFFF", 1));
            R["SheenBrush"] = Linear(top, bottom, ("#26FFFFFF", 0), ("#08FFFFFF", 0.35), ("#00FFFFFF", 0.6), ("#0AA0C4FF", 1));
            R["GlowBrush"] = Glow("#403A7BFF");
            R["VolumeBrush"] = Linear(top, right, ("#7FA8FF", 0), ("#C9DBFF", 0.6), ("#FFFFFF", 1));
            R["TextPrimary"] = Solid("#FFFFFF");
            R["TextSecondary"] = Solid("#A8A8A8");
            R["TextTertiary"] = Solid("#9A9A9A");
            R["IconBrush"] = Solid("#FFFFFF");
            R["VolIconBrush"] = Solid("#C9DBFF");
            R["TrackBrush"] = Solid("#26FFFFFF");
            R["ArtPlaceholderBg"] = Solid("#333333");
            R["ArtPlaceholderFg"] = Solid("#777777");
            R["ArtBorder"] = Solid("#50FFFFFF");
            R["ThumbBrush"] = Solid("#FFFFFF");
            IslandShadow.Color = Colors.Black;
            IslandShadow.Opacity = 0.45;
            IslandShadow.BlurRadius = 16;
        }

        UpdateBattery();   // redibuja el rayo con el color del tema

        if (animate)
        {
            // Pequeño destello para que el cambio de tema se sienta suave
            Island.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    // =====================================================================
    //  Menú contextual
    // =====================================================================
    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    private void StartupItem_Click(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;

        if (StartupItem.IsChecked && Environment.ProcessPath is string exe)
            key.SetValue(AppName, $"\"{exe}\"");
        else
            key.DeleteValue(AppName, false);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // =====================================================================
    //  Win32 / DWM
    // =====================================================================
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    // ---------- Core Audio (volumen del sistema) ----------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static class AudioHelper
    {
        public static IAudioEndpointVolume? GetDefaultEndpointVolume()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                // 0 = eRender (salida de audio), 1 = eMultimedia
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device == null)
                    return null;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0)
                        return null;
                    return (IAudioEndpointVolume)o;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
}
