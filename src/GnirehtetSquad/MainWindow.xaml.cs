using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GnirehtetSquad.Core;

namespace GnirehtetSquad;

public partial class MainWindow : Window
{
    const int MaxLogItems = 3000;
    const int MaxRecent = 80;

    readonly Engine _engine;
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _toastTimer;
    readonly ObservableCollection<LogItem> _logs = new();
    readonly ObservableCollection<LogItem> _recent = new();
    readonly ICollectionView _logView;
    StateSnapshot? _snap;
    int _lastVersion = -1;
    long _lastLogId;
    bool _loadingUi;
    string _devSig = "", _comboSig = "";

    public MainWindow()
    {
        InitializeComponent();
        _engine = App.Current.Engine;

        FullLog.ItemsSource = _logs;
        _logView = CollectionViewSource.GetDefaultView(_logs);
        _logView.Filter = LogFilter;
        RecentLog.ItemsSource = _recent;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        LoadSettingsToForm();
        AboutText.Text = $"{Engine.AppName} {Engine.AppVersion} · программа: {_engine.ExePath} · данные: {_engine.CfgDir}";

        // окно не больше рабочей области (ноутбуки с масштабом 150%)
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.94);
        Height = Math.Min(Height, area.Height * 0.94);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => DarkTitleBar();
        Closing += (_, e) => e.Cancel = !App.Current.OnWindowClosing();
        Closed += (_, _) => _timer.Stop();

        Tick();
        _timer.Start();
    }

    /// <summary>Прокрутка списка в конец без прокрутки всей страницы (в отличие от ScrollIntoView).</summary>
    static void ScrollToEnd(DependencyObject list)
    {
        if (FindScroll(list) is { } sv) sv.ScrollToEnd();
    }

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    // ---------- тёмный заголовок окна ----------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // старые сборки Windows 10
        int caption = 0x00020103; // COLORREF 0x00BBGGRR: #030102
        int text = 0x00E4E2F7;
        int border = 0x0020185C;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // Windows 11
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    // ---------- обновление экрана ----------

    void Tick()
    {
        var fresh = _engine.LogsSince(_lastLogId);
        if (fresh.Count > 0)
        {
            _lastLogId = fresh[^1].Id;
            foreach (var l in fresh)
            {
                var item = new LogItem(l);
                _logs.Add(item);
                _recent.Add(item);
            }
            while (_logs.Count > MaxLogItems) _logs.RemoveAt(0);
            while (_recent.Count > MaxRecent) _recent.RemoveAt(0);
            ScrollToEnd(RecentLog);
            if (AutoScroll.IsChecked == true && PageLog.Visibility == Visibility.Visible) ScrollToEnd(FullLog);
        }

        int v = _engine.StateVersion;
        if (v != _lastVersion || _snap == null)
        {
            _lastVersion = v;
            _snap = _engine.Snapshot();
            Render(_snap);
        }
        StatUptime.Text = _snap!.StartedAt is { } t ? FormatUptime(DateTime.Now - t) : "—";
    }

    static string FormatUptime(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    Brush B(string key) => (Brush)FindResource(key);

    void Render(StateSnapshot s)
    {
        int ready = s.Devices.Count(d => d.State == "device");

        // статус
        if (s.Running)
        {
            StatusText.Text = "Работает";
            StatusDot.Fill = B("PrimaryHi");
            StatusDot.Effect = (System.Windows.Media.Effects.Effect)FindResource("RedGlow");
            StatusChip.BorderBrush = B("Primary");
        }
        else if (s.Starting)
        {
            StatusText.Text = "Запуск…";
            StatusDot.Fill = B("Warn");
            StatusDot.Effect = null;
            StatusChip.BorderBrush = B("Outline");
        }
        else
        {
            StatusText.Text = "Остановлен";
            StatusDot.Fill = B("OnSurfaceVar");
            StatusDot.Effect = null;
            StatusChip.BorderBrush = B("Outline");
        }

        // главная карточка
        if (s.Running)
        {
            HeroTitle.Text = "Интернет раздаётся";
            HeroSub.Text = s.Clients > 0
                ? $"Подключено клиентов: {s.Clients}. Телефон выходит в сеть через этот ПК."
                : "Relay запущен. Если на телефоне появился запрос VPN — подтвердите его.";
        }
        else if (s.Starting)
        {
            HeroTitle.Text = s.Restarts > 0 ? "Перезапуск relay…" : "Запуск…";
            HeroSub.Text = "Relay завершился и сейчас будет запущен снова.";
        }
        else
        {
            HeroTitle.Text = "Раздача выключена";
            HeroSub.Text = !s.AdbOk && s.AdbPath == ""
                ? "adb не найден — нажмите «Скачать platform-tools» в карточке «Готовность»."
                : ready == 0
                    ? "Подключите телефон по USB и включите «Отладку по USB»."
                    : "Нажмите кнопку питания, чтобы раздать интернет на телефон.";
        }
        bool on = s.Running;
        PowerStop1.Color = on ? (Color)ColorConverter.ConvertFromString("#FF2440") : (Color)ColorConverter.ConvertFromString("#2A0D10");
        PowerStop2.Color = on ? (Color)ColorConverter.ConvertFromString("#7A000E") : (Color)ColorConverter.ConvertFromString("#100405");
        PowerDisc.Stroke = on ? B("PrimaryHi") : B("Outline");
        PowerIcon.Foreground = on ? Brushes.White : B("PrimaryHi");
        PowerGlow.Opacity = on ? 0.85 : s.Starting ? 0.35 : 0.0;

        StatClients.Text = s.Clients.ToString();
        StatDevices.Text = ready.ToString();
        StatRestarts.Text = s.Restarts.ToString();
        RestartBtn.IsEnabled = s.Running;

        // режим
        _loadingUi = true;
        bool runMode = s.Settings.Mode == "run";
        ModeAll.IsChecked = !runMode;
        ModeOne.IsChecked = runMode;
        SerialCombo.Visibility = runMode ? Visibility.Visible : Visibility.Collapsed;
        var choices = s.Devices.Select(d => new SerialChoice(d.Serial, d.Model == "" ? d.Serial : $"{d.Model} · {d.Serial}")).ToList();
        if (s.Settings.Serial != "" && choices.All(c => c.Serial != s.Settings.Serial))
            choices.Add(new SerialChoice(s.Settings.Serial, s.Settings.Serial + " (не подключено)"));
        var comboSig = string.Join("|", choices.Select(c => c.Label)) + "#" + s.Settings.Serial;
        if (comboSig != _comboSig)
        {
            _comboSig = comboSig;
            SerialCombo.ItemsSource = choices;
            SerialCombo.SelectedItem = choices.FirstOrDefault(c => c.Serial == s.Settings.Serial);
        }
        _loadingUi = false;

        // устройства
        var vms = s.Devices.Select(d => new DeviceVM(d, s.Running, this)).ToList();
        var sig = string.Join("|", vms.Select(x => x.Sig)) + s.Running;
        if (sig != _devSig)
        {
            _devSig = sig;
            HomeDevices.ItemsSource = vms;
            AllDevices.ItemsSource = vms;
        }
        var empty = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeNoDevices.Visibility = empty;
        AllNoDevices.Visibility = empty;
        DeviceBadge.Visibility = ready > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceBadgeText.Text = ready.ToString();

        // готовность
        SetReady(ReadyRelayIcon, s.GnirehtetOk);
        SetReady(ReadyApkIcon, s.ApkOk);
        SetReady(ReadyAdbIcon, s.AdbOk);
        ReadyAdbText.Text = s.AdbOk ? s.AdbPath : (s.AdbError == "" ? "Поиск…" : s.AdbError);
        SetAdbFound.Text = s.AdbPath != "" ? "Используется: " + s.AdbPath : "adb не найден";
        bool downloading = s.Download != null && s.Download.Error == "";
        HomeDownloadPt.Visibility = !s.AdbOk && s.AdbPath == "" && !downloading ? Visibility.Visible : Visibility.Collapsed;
        foreach (var bar in new[] { HomePtProgress, SetPtProgress })
        {
            bar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            if (downloading)
            {
                bar.IsIndeterminate = s.Download!.Total <= 0;
                if (s.Download.Total > 0) bar.Value = s.Download.Done * 100.0 / s.Download.Total;
            }
        }

        // обновления
        var u = s.Update;
        UpdateBanner.Visibility = u.Available || u.Installing ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle.Text = $"Доступно обновление {u.Latest}";
        UpdateInstallBtn.IsEnabled = !u.Installing;
        UpdateProgress.Visibility = u.Installing ? Visibility.Visible : Visibility.Collapsed;
        if (u.Installing)
        {
            UpdateSub.Text = u.Stage switch
            {
                "download" => u.Size > 0 ? $"Скачивание… {u.Done * 100 / u.Size}%" : "Скачивание…",
                "verify" => "Проверка контрольной суммы…",
                "install" => "Установка…",
                "restart" => "Перезапуск…",
                _ => "Подготовка…",
            };
            UpdateProgress.IsIndeterminate = u.Stage != "download" || u.Size <= 0;
            if (u.Stage == "download" && u.Size > 0) UpdateProgress.Value = u.Done * 100.0 / u.Size;
        }
        else
        {
            var note = u.Notes.Split('\n').Select(x => x.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(x => x != "") ?? "";
            UpdateSub.Text = $"У вас {Engine.AppVersion}." + (note != "" ? " " + note : "") + (u.Error != "" ? " " + u.Error : "");
        }
        CheckUpdBtn.IsEnabled = !u.Checking && !u.Installing;
        UpdStatus.Text = u.Checking ? "Проверка…"
            : u.Error != "" ? u.Error
            : u.Available ? $"Доступна версия {u.Latest}"
            : u.CheckedAt != null ? $"Установлена последняя версия ({Engine.AppVersion})"
            : $"Версия {Engine.AppVersion}";

        App.Current.UpdateTrayText($"{Engine.AppName} — {(s.Running ? $"работает, клиентов: {s.Clients}" : s.Starting ? "запуск…" : "остановлен")}");
    }

    void SetReady(TextBlock icon, bool ok)
    {
        icon.Text = ok ? "" : "";
        icon.Foreground = ok ? B("Success") : B("Error");
    }

    // ---------- журнал ----------

    bool LogFilter(object o)
    {
        if (o is not LogItem l) return false;
        bool level = l.Level switch
        {
            "app" => FltApp.IsChecked == true,
            "info" => FltInfo.IsChecked == true,
            "warn" => FltWarn.IsChecked == true,
            "error" => FltError.IsChecked == true,
            _ => FltDebug.IsChecked == true,
        };
        if (!level) return false;
        var q = LogSearch.Text.Trim();
        return q == "" || l.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => _logView.Refresh();
    void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();

    IEnumerable<LogItem> VisibleOrSelected() =>
        FullLog.SelectedItems.Count > 0
            ? FullLog.SelectedItems.Cast<LogItem>().OrderBy(l => l.Id)
            : _logView.Cast<LogItem>();

    static string LogText(IEnumerable<LogItem> items)
    {
        var sb = new StringBuilder();
        foreach (var l in items) sb.Append(l.Time).Append(' ').Append(l.LevelText.PadRight(5)).Append(' ').AppendLine(l.Text);
        return sb.ToString();
    }

    void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogText(VisibleOrSelected()));
            ShowToast("Журнал скопирован в буфер обмена");
        }
        catch (Exception ex) { ShowToast("Не удалось скопировать: " + ex.Message); }
    }

    void LogSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"gnirehtet-squad-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Журнал (*.log;*.txt)|*.log;*.txt|Все файлы|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, LogText(_logView.Cast<LogItem>()), Encoding.UTF8);
            ShowToast("Сохранено: " + dlg.FileName);
        }
        catch (Exception ex) { ShowToast("Не удалось сохранить: " + ex.Message); }
    }

    void LogClear_Click(object sender, RoutedEventArgs e)
    {
        _engine.ClearLogs();
        _logs.Clear();
        _recent.Clear();
    }

    // ---------- навигация ----------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return;
        PageHome.Visibility = sender == NavHome ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = sender == NavDevices ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = sender == NavLog ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        if (sender == NavLog)
            Dispatcher.BeginInvoke(() => ScrollToEnd(FullLog), DispatcherPriority.Loaded);
        if (sender == NavSettings) LoadSettingsToForm();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e) => NavLog.IsChecked = true;

    // ---------- relay ----------

    void RunBg(Action action)
    {
        Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => ShowToast(ex.Message)); }
        });
    }

    void Power_Click(object sender, RoutedEventArgs e)
    {
        var s = _engine.Snapshot();
        if (s.Running || s.Starting) RunBg(() => _engine.Stop(true));
        else RunBg(() => _engine.Start(null, null));
    }

    void Restart_Click(object sender, RoutedEventArgs e) => RunBg(_engine.Restart);

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || ModeAll == null) return;
        var s = _engine.CurrentSettings;
        s.Mode = ModeOne.IsChecked == true ? "run" : "autorun";
        if (s.Mode == "run" && s.Serial == "")
            s.Serial = _snap?.Devices.FirstOrDefault(d => d.State == "device")?.Serial ?? "";
        Apply(s);
        if (_snap?.Running == true) ShowToast("Режим применится после перезапуска relay");
    }

    void SerialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SerialCombo.SelectedItem is not SerialChoice c) return;
        var s = _engine.CurrentSettings;
        s.Serial = c.Serial;
        Apply(s);
    }

    void DeviceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action, CommandParameter: string serial }) return;
        try { _engine.DeviceAction(serial, action); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    void WifiConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _engine.AdbConnect(WifiAddr.Text);
            ShowToast("Подключение к " + WifiAddr.Text.Trim() + "…");
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    void WifiAddr_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WifiConnect_Click(sender, e);
    }

    void AdbRestart_Click(object sender, RoutedEventArgs e) => _engine.AdbRestart();

    void DownloadPt_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.DownloadPlatformTools(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    void KillStale_Click(object sender, RoutedEventArgs e) => RunBg(_engine.KillStale);

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.OpenFolder(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    void Quit_Click(object sender, RoutedEventArgs e) => App.Current.Quit();

    // ---------- обновления ----------

    void CheckUpdate_Click(object sender, RoutedEventArgs e) => _engine.CheckUpdate();

    void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.InstallUpdate(); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    // ---------- настройки ----------

    void LoadSettingsToForm()
    {
        _loadingUi = true;
        var s = _engine.CurrentSettings;
        SetDns.Text = s.Dns;
        SetRoutes.Text = s.Routes;
        SetPort.Text = s.Port.ToString();
        SetAdbPath.Text = s.AdbPath;
        SetSwitches(s);
        SaveStatus.Text = "";
        _loadingUi = false;
    }

    void SetSwitches(Settings s)
    {
        SetAutoStart.IsChecked = s.AutoStart;
        SetAutoRestart.IsChecked = s.AutoRestart;
        SetStopDevices.IsChecked = s.StopDevicesOnOff;
        SetKeepBg.IsChecked = s.KeepBackground;
        SetStartup.IsChecked = s.StartWithWindows;
        SetCheckUpd.IsChecked = s.CheckUpdates;
        SetAutoUpd.IsChecked = s.AutoUpdate;
    }

    void ReadSwitches(Settings s)
    {
        s.AutoStart = SetAutoStart.IsChecked == true;
        s.AutoRestart = SetAutoRestart.IsChecked == true;
        s.StopDevicesOnOff = SetStopDevices.IsChecked == true;
        s.KeepBackground = SetKeepBg.IsChecked == true;
        s.StartWithWindows = SetStartup.IsChecked == true;
        s.CheckUpdates = SetCheckUpd.IsChecked == true;
        s.AutoUpdate = SetAutoUpd.IsChecked == true;
    }

    bool Apply(Settings s)
    {
        try
        {
            _engine.ApplySettings(s);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return false;
        }
    }

    void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var s = _engine.CurrentSettings;
        ReadSwitches(s);
        Apply(s);
    }

    void DnsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dns }) SetDns.Text = dns;
    }

    void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "adb.exe|adb.exe|Программы (*.exe)|*.exe", Title = "Где находится adb.exe?" };
        if (dlg.ShowDialog(this) == true) SetAdbPath.Text = dlg.FileName;
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SetPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
        {
            ShowToast("Порт должен быть числом 1–65535");
            return;
        }
        if (Settings.ValidateNetwork(SetDns.Text, SetRoutes.Text) is { } err)
        {
            ShowToast(err);
            return;
        }
        var s = _engine.CurrentSettings;
        s.Dns = SetDns.Text.Trim();
        s.Routes = SetRoutes.Text.Trim();
        s.Port = port;
        s.AdbPath = SetAdbPath.Text.Trim();
        ReadSwitches(s);
        if (Apply(s))
        {
            SaveStatus.Text = _snap?.Running == true ? "Сохранено ✓ Сетевые настройки применятся после перезапуска relay." : "Сохранено ✓";
        }
    }

    void RevertSettings_Click(object sender, RoutedEventArgs e) => LoadSettingsToForm();

    // ---------- сообщения ----------

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- модели для привязки ----------

    sealed record SerialChoice(string Serial, string Label);

    public sealed class LogItem
    {
        static readonly Dictionary<string, (string Label, string Level, string Text)> Styles = new()
        {
            ["app"] = ("APP", "PrimaryHi", "OnSurface"),
            ["info"] = ("INFO", "Success", "OnSurface"),
            ["warn"] = ("WARN", "Warn", "Warn"),
            ["error"] = ("ERROR", "Error", "Error"),
            ["debug"] = ("DEBUG", "OnSurfaceVar", "OnSurfaceVar"),
        };

        public LogItem(LogLine l)
        {
            Id = l.Id;
            Time = l.Time;
            Level = l.Level;
            Text = l.Text;
            var st = Styles.TryGetValue(l.Level, out var x) ? x : Styles["debug"];
            LevelText = st.Label;
            LevelBrush = (Brush)Application.Current.FindResource(st.Level);
            TextBrush = (Brush)Application.Current.FindResource(st.Text);
        }

        public long Id { get; }
        public string Time { get; }
        public string Level { get; }
        public string Text { get; }
        public string LevelText { get; }
        public Brush LevelBrush { get; }
        public Brush TextBrush { get; }
    }

    public sealed class DeviceVM
    {
        public DeviceVM(Device d, bool running, MainWindow w)
        {
            Serial = d.Serial;
            Title = d.Model != "" ? d.Model : d.Serial;
            Glyph = d.Wireless ? "" : "";
            bool ok = d.State == "device";
            IconBg = w.B(ok ? "PrimaryContainer" : "ScHighest");
            IconFg = w.B(ok ? "PrimaryHi" : "OnSurfaceVar");
            (StateText, StateBg, StateFg) = d.State switch
            {
                "device" => ("Готово", w.B("SuccessContainer"), w.B("Success")),
                "unauthorized" => ("Нужно разрешить отладку", w.B("WarnContainer"), w.B("Warn")),
                "offline" => ("Offline", w.B("ErrorContainer"), w.B("Error")),
                _ => (d.State, w.B("WarnContainer"), w.B("Warn")),
            };
            ApkText = d.Installed switch { true => "APK установлен", false => "APK не установлен", _ => ok ? "Проверка APK…" : "" };
            ApkVisibility = ApkText == "" ? Visibility.Collapsed : Visibility.Visible;
            BusyText = d.Busy switch
            {
                "start" => "Включение VPN…",
                "stop" => "Выключение VPN…",
                "install" => "Установка APK…",
                "reinstall" => "Переустановка APK…",
                "uninstall" => "Удаление APK…",
                "" => "",
                var b => b + "…",
            };
            BusyVisibility = BusyText == "" ? Visibility.Collapsed : Visibility.Visible;
            CanAct = ok && d.Busy == "";
            CanStart = CanAct && running;
            InstallAction = d.Installed == true ? "reinstall" : "install";
            InstallText = d.Installed == true ? "Переустановить" : "Установить APK";
            Sig = $"{d.Serial};{d.State};{d.Model};{d.Installed};{d.Busy};{d.Wireless}";
        }

        public string Serial { get; }
        public string Title { get; }
        public string Glyph { get; }
        public Brush IconBg { get; }
        public Brush IconFg { get; }
        public string StateText { get; }
        public Brush StateBg { get; }
        public Brush StateFg { get; }
        public string ApkText { get; }
        public Visibility ApkVisibility { get; }
        public string BusyText { get; }
        public Visibility BusyVisibility { get; }
        public bool CanAct { get; }
        public bool CanStart { get; }
        public string InstallAction { get; }
        public string InstallText { get; }
        public string Sig { get; }
    }
}
