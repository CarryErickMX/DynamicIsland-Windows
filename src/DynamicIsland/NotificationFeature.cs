using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland;

// =====================================================================
//  Notificaciones de Windows en la isla (WhatsApp, Outlook, Teams, etc.)
//  Archivo independiente: no requiere cambios en MainWindow.xaml(.cs)
// =====================================================================
public partial class MainWindow
{
    private const double NotifW = 380, NotifH = 70, NotifR = 30;
    private static readonly TimeSpan NotifDuration = TimeSpan.FromSeconds(5);

    private UserNotificationListener? _nListener;
    private readonly DispatcherTimer _nPollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _nHideTimer = new();
    private readonly Queue<UserNotification> _nQueue = new();
    private HashSet<uint>? _nSeen;
    private bool _nPolling;
    private bool _nShowing;
    private bool _nEnabled = true;

    private Grid? _nView;
    private Border? _nIcon;
    private TextBlock? _nIconGlyph, _nApp, _nTitle, _nBody;
    private MenuItem? _nMenuItem;

    // Se ejecuta una vez, cuando la ventana ya se dibujó
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        BuildNotificationView();
        AddNotificationMenu();

        _nEnabled = LoadNotifSetting();
        _nMenuItem!.IsChecked = _nEnabled;

        _nHideTimer.Interval = NotifDuration;
        _nHideTimer.Tick += (_, _) => HideNotification();
        _nPollTimer.Tick += async (_, _) => await PollNotificationsAsync();

        try
        {
            _nListener = UserNotificationListener.Current;
            var access = await _nListener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _nListener = null;
                ShowActivity("\uE7BA", (Brush)FindResource("TextPrimary"),
                    "Activa el acceso a notificaciones en Windows", "");
                return;
            }
            _nPollTimer.Start();
        }
        catch
        {
            _nListener = null;   // API no disponible en este equipo
        }
    }

    // ---------- Lectura de notificaciones nuevas ----------
    private async Task PollNotificationsAsync()
    {
        if (_nPolling || _nListener == null) return;
        _nPolling = true;
        try
        {
            var list = await _nListener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>(list.Select(n => n.Id));

            if (_nSeen == null)
            {
                // Primera lectura: ignora las que ya estaban en el centro de notificaciones
                _nSeen = current;
            }
            else
            {
                if (_nEnabled)
                {
                    foreach (var n in list.Where(n => !_nSeen.Contains(n.Id)).OrderBy(n => n.CreationTime))
                        _nQueue.Enqueue(n);
                }
                _nSeen = current;
            }

            // Mostrar la siguiente si la isla está libre
            if (!_nShowing && _nQueue.Count > 0 && !_hover && !_activity)
                await ShowNotificationAsync(_nQueue.Dequeue());

            // Si otro evento cambió la isla mientras se mostraba, recuperarla
            if (_nShowing && !_hover && !_activity && _currentView != _nView)
                Morph(NotifW, NotifH, NotifR, _nView!);
        }
        catch { }
        finally
        {
            _nPolling = false;
        }
    }

    private async Task ShowNotificationAsync(UserNotification n)
    {
        string app = "", title = "", body = "";
        try
        {
            app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                        ?? new List<string>();
            title = texts.FirstOrDefault() ?? app;
            body = string.Join(" · ", texts.Skip(1));
        }
        catch { }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(app)) return;

        _nApp!.Text = app;
        _nTitle!.Text = title;
        _nBody!.Text = body;
        _nBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        // Ícono de la aplicación
        _nIcon!.SetResourceReference(Border.BackgroundProperty, "ArtPlaceholderBg");
        _nIconGlyph!.Visibility = Visibility.Visible;
        try
        {
            var logoRef = n.AppInfo?.DisplayInfo?.GetLogo(new global::Windows.Foundation.Size(64, 64));
            if (logoRef != null)
            {
                var img = await LoadThumbnailAsync(logoRef);
                if (img != null)
                {
                    _nIcon.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                    _nIconGlyph.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch { }

        _nShowing = true;
        Morph(NotifW, NotifH, NotifR, _nView!);
        _nHideTimer.Stop();
        _nHideTimer.Start();
    }

    private void HideNotification()
    {
        _nHideTimer.Stop();
        _nShowing = false;
        UpdateState();
    }

    // ---------- Vista de la notificación (creada por código) ----------
    private void BuildNotificationView()
    {
        _nView = new Grid { Margin = new Thickness(14, 0, 18, 0), Visibility = Visibility.Collapsed, Opacity = 0 };
        _nView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _nView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _nIcon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1)
        };
        _nIcon.SetResourceReference(Border.BackgroundProperty, "ArtPlaceholderBg");
        _nIcon.SetResourceReference(Border.BorderBrushProperty, "ArtBorder");

        _nIconGlyph = new TextBlock
        {
            Text = "\uEA8F",   // campana
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _nIconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "VolIconBrush");
        _nIcon.Child = _nIconGlyph;
        _nView.Children.Add(_nIcon);

        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texts, 1);

        var header = new DockPanel { LastChildFill = true };
        var now = new TextBlock { Text = "ahora", FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
        now.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        DockPanel.SetDock(now, Dock.Right);
        _nApp = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        _nApp.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        header.Children.Add(now);
        header.Children.Add(_nApp);

        _nTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        _nTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        _nBody = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        _nBody.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        texts.Children.Add(header);
        texts.Children.Add(_nTitle);
        texts.Children.Add(_nBody);
        _nView.Children.Add(texts);

        if (Island.Child is Grid content)
            content.Children.Add(_nView);
    }

    // ---------- Opción en el menú contextual ----------
    private void AddNotificationMenu()
    {
        _nMenuItem = new MenuItem { Header = "Mostrar notificaciones", IsCheckable = true };
        _nMenuItem.Click += (_, _) =>
        {
            _nEnabled = _nMenuItem.IsChecked;
            SaveNotifSetting(_nEnabled);
            if (!_nEnabled)
            {
                _nQueue.Clear();
                if (_nShowing) HideNotification();
            }
        };
        Island.ContextMenu?.Items.Insert(1, _nMenuItem);
    }

    private static bool LoadNotifSetting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey, false);
        return key?.GetValue("Notifications") is not int v || v == 1;
    }

    private static void SaveNotifSetting(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key?.SetValue("Notifications", on ? 1 : 0, RegistryValueKind.DWord);
    }
}
