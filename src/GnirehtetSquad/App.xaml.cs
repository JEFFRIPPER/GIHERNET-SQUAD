using System.Windows;
using GnirehtetSquad.Core;
using Forms = System.Windows.Forms;

namespace GnirehtetSquad;

public partial class App : Application
{
    const string MutexName = @"Local\GnirehtetSquad.Instance";
    const string ShowEventName = @"Local\GnirehtetSquad.Show";

    Mutex? _mutex;
    bool _ownsMutex;
    EventWaitHandle? _showEvent;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayRelayItem;
    MainWindow? _window;
    bool _quitting, _trayHintShown;

    public Engine Engine { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        bool background = args.Contains("--background");
        bool afterUpdate = args.Contains("--after-update");
        bool startRelay = args.Contains("--start-relay");

        // один экземпляр: повторный запуск показывает окно уже работающей программы
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        for (int i = 0; !_ownsMutex && afterUpdate && i < 40; i++)
        {
            try { _ownsMutex = _mutex.WaitOne(500); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }
        if (!_ownsMutex)
        {
            if (!background)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true, Name = "show-signal" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Engine?.Log("error", "Ошибка интерфейса: " + ex.Exception.Message);
            ex.Handled = true;
        };

        Engine = new Engine(background);
        Engine.ExitRequested += () => Dispatcher.BeginInvoke(ExitNow);
        Engine.Init(afterUpdate, startRelay);

        CreateTray();
        if (!background) ShowMain();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowMain());
        _trayRelayItem = new Forms.ToolStripMenuItem("Запустить relay", null, (_, _) => ToggleRelay());
        menu.Items.Add(_trayRelayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            var s = Engine.Snapshot();
            _trayRelayItem.Text = s.Running || s.Starting ? "Остановить relay" : "Запустить relay";
        };

        System.Drawing.Icon? icon = null;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Engine.ExePath); } catch { }
        _tray = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = Engine.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(); };
    }

    void ToggleRelay()
    {
        var s = Engine.Snapshot();
        Task.Run(() =>
        {
            try
            {
                if (s.Running || s.Starting) Engine.Stop(true);
                else Engine.Start(null, null);
            }
            catch (Exception ex) { Engine.Log("error", ex.Message); }
        });
    }

    public void UpdateTrayText(string text)
    {
        if (_tray != null) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowMain()
    {
        if (_quitting) return;
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>Закрытие окна: в фон (если включено) или полный выход.</summary>
    public bool OnWindowClosing()
    {
        if (_quitting) return true;
        if (Engine.Background || Engine.CurrentSettings.KeepBackground)
        {
            _window?.Hide();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, Engine.AppName, "Программа работает в фоне. Значок — в области уведомлений.", Forms.ToolTipIcon.Info);
            }
            return false;
        }
        Quit();
        return false;
    }

    public async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _window?.Hide();
        if (_tray != null) _tray.Visible = false;
        await Task.WhenAny(Task.Run(Engine.Shutdown), Task.Delay(TimeSpan.FromSeconds(25)));
        ExitNow();
    }

    /// <summary>Выход без остановки VPN на телефонах (также после установки обновления).</summary>
    void ExitNow()
    {
        _quitting = true;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
