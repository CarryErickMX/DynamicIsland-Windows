using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

// =====================================================================
//  Ocultar la isla cuando hay una app en pantalla completa
//  (videos, presentaciones, juegos). Archivo independiente.
// =====================================================================
public partial class MainWindow
{
    private readonly DispatcherTimer _fsTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _fsHidden;
    private bool _fsEnabled = true;
    private MenuItem? _fsMenuItem;

    // Punto de arranque de las funciones de la v1.3 (pantalla completa y progreso)
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) =>
        {
            InitFullscreenWatcher();
            InitMediaProgress();
        };
    }

    private void InitFullscreenWatcher()
    {
        _fsEnabled = LoadFlag("HideFullscreen", true);

        _fsMenuItem = new MenuItem
        {
            Header = "Ocultar en pantalla completa",
            IsCheckable = true,
            IsChecked = _fsEnabled
        };
        _fsMenuItem.Click += (_, _) =>
        {
            _fsEnabled = _fsMenuItem.IsChecked;
            SaveFlag("HideFullscreen", _fsEnabled);
            CheckFullscreen();
        };
        Island.ContextMenu?.Items.Insert(1, _fsMenuItem);

        _fsTimer.Tick += (_, _) => CheckFullscreen();
        _fsTimer.Start();
    }

    private void CheckFullscreen()
    {
        bool fullscreen = _fsEnabled && IsForegroundFullscreen();
        if (fullscreen == _fsHidden) return;
        _fsHidden = fullscreen;

        if (Island.Parent is not FrameworkElement root) return;
        if (root.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            root.RenderTransform = tt;
        }

        var dur = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (fullscreen)
        {
            // Sube y se desvanece; luego deja pasar los clics
            var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (_fsHidden) root.Visibility = Visibility.Hidden; };
            root.BeginAnimation(OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-24, dur) { EasingFunction = ease });
        }
        else
        {
            root.Visibility = Visibility.Visible;
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });

            // Recupera la posición "siempre visible" por si otra ventana la tomó
            Topmost = false;
            Topmost = true;
        }
    }

    private bool IsForegroundFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        if (!IsWindowVisible(fg) || IsIconic(fg)) return false;

        // El escritorio y la barra de tareas no cuentan
        var cls = new StringBuilder(256);
        GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        // Solo importa si está en el mismo monitor que la isla
        IntPtr monFg = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monFg != MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST)) return false;

        var mi = new FsMonitorInfo { cbSize = Marshal.SizeOf<FsMonitorInfo>() };
        if (!GetMonitorInfo(monFg, ref mi)) return false;

        // Límites reales de la ventana (sin la sombra invisible de Windows)
        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out FsRect r, Marshal.SizeOf<FsRect>()) != 0)
            GetWindowRect(fg, out r);

        // Una ventana maximizada no tapa la barra de tareas; una en pantalla completa sí cubre todo el monitor
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---------- Preferencias ----------
    private static bool LoadFlag(string name, bool defaultValue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey, false);
        return key?.GetValue(name) is int v ? v == 1 : defaultValue;
    }

    private static void SaveFlag(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }

    // ---------- Win32 ----------
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FsRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FsMonitorInfo
    {
        public int cbSize;
        public FsRect rcMonitor;
        public FsRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref FsMonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out FsRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out FsRect value, int size);
}
