using System.Diagnostics;
using System.Windows;
using GnirehtetSquad.Core;
using Forms = System.Windows.Forms;

namespace GnirehtetSquad;

public partial class App : Application
{
    const string MutexName = @"Local\GnirehtetSquad.Instance";
    const string ShowEventName = @"Local\GnirehtetSquad.Show";
    // ответ работающей копии на Show: окно показано, UI-поток жив (с 2.0.1)
    const string AliveEventName = @"Local\GnirehtetSquad.Alive";

    Mutex? _mutex;
    bool _ownsMutex;
    EventWaitHandle? _showEvent;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayRelayItem;
    MainWindow? _window;
    bool _quitting, _trayHintShown;
    string? _takeoverNote;

    public Engine Engine { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception err) Crash.Report("Критическая ошибка", err, ex.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Engine?.Log("debug", "Фоновая задача: " + ex.Exception.GetBaseException().Message);
            ex.SetObserved();
        };
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            Crash.Write("Ошибка интерфейса", ex.Exception);
            Engine?.Log("error", "Ошибка интерфейса: " + ex.Exception.Message);
        };

        try { StartApp(e.Args); }
        catch (Exception ex)
        {
            // без этого исключение гасится обработчиком выше и процесс висит без окна
            Crash.Report("Программа не запустилась", ex, true);
            ExitNow();
        }
    }

    void StartApp(string[] rawArgs)
    {
        var args = rawArgs.Select(a => a.ToLowerInvariant()).ToHashSet();
        bool background = args.Contains("--background");
        bool afterUpdate = args.Contains("--after-update");
        bool startRelay = args.Contains("--start-relay");

        // один экземпляр: повторный запуск показывает окно уже работающей программы
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex) _ownsMutex = TakeOver(background, afterUpdate);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    ShowMain();
                    SignalAlive();
                });
        }) { IsBackground = true, Name = "show-signal" }.Start();

        Engine = new Engine(background);
        Crash.SetDir(Engine.CfgDir);
        Engine.ExitRequested += () => Dispatcher.BeginInvoke(ExitNow);
        if (_takeoverNote != null) Engine.Log("warn", _takeoverNote);

        // окно и значок — сразу; adb, распаковка gnirehtet и автозапуск relay — в фоне,
        // чтобы медленный или зависший adb не мешал программе открыться
        CreateTray();
        if (!background) ShowMain();
        Task.Run(() =>
        {
            try { Engine.Init(afterUpdate, startRelay); }
            catch (Exception ex)
            {
                Crash.Write("Инициализация", ex);
                Engine.Log("error", "Ошибка запуска: " + ex.Message);
            }
        });
    }

    // ---------- второй запуск ----------

    enum OtherInstance { Alive, Gone, Hung }

    bool WaitMutex(TimeSpan timeout)
    {
        try { return _mutex!.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; } // прежняя копия упала — мьютекс наш
    }

    /// <summary>
    /// Мьютекс занят другой копией. Живую просим показать окно и выходим;
    /// зависшую (например, 2.0.0, которая блокировалась при старте) завершаем и запускаемся сами.
    /// </summary>
    bool TakeOver(bool background, bool afterUpdate)
    {
        // после обновления старая версия ещё завершается — ждём её
        if (afterUpdate && WaitMutex(TimeSpan.FromSeconds(20))) return true;
        // автозагрузка Windows при уже работающей программе
        if (background && !afterUpdate) return false;

        using var alive = new EventWaitHandle(false, EventResetMode.AutoReset, AliveEventName);
        switch (PingOther(alive, TimeSpan.FromSeconds(7)))
        {
            case OtherInstance.Alive: return false;
            case OtherInstance.Gone: return true;
        }

        var killed = KillOthers();
        if (WaitMutex(TimeSpan.FromSeconds(5)))
        {
            if (killed.Count > 0)
                _takeoverNote = "Завершена зависшая копия программы (PID " + string.Join(", ", killed) + ")";
            return true;
        }
        // мьютекс успела занять копия, запущенная одновременно с нами
        var again = PingOther(alive, TimeSpan.FromSeconds(6));
        if (again != OtherInstance.Hung) return again == OtherInstance.Gone;

        MessageBox.Show(
            "Программа уже запущена, но не отвечает, и её не удалось завершить.\n\n" +
            "Завершите GnirehtetSquad.exe в диспетчере задач или перезагрузите компьютер.",
            Engine.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Просит работающую копию показать окно и ждёт подтверждения.</summary>
    OtherInstance PingOther(EventWaitHandle alive, TimeSpan timeout)
    {
        try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (WaitMutex(TimeSpan.Zero)) return OtherInstance.Gone;
            // 2.0.1+ отвечает событием, 2.0.0 просто показывает окно
            if (alive.WaitOne(300) || AnyLiveWindow()) return OtherInstance.Alive;
        } while (DateTime.UtcNow < deadline);
        return OtherInstance.Hung;
    }

    static void SignalAlive()
    {
        try { EventWaitHandle.OpenExisting(AliveEventName).Set(); } catch { }
    }

    static List<Process> OtherInstances()
    {
        using var self = Process.GetCurrentProcess();
        var res = new List<Process>();
        foreach (var name in new[] { "GnirehtetSquad", self.ProcessName }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id != self.Id && p.SessionId == self.SessionId) res.Add(p);
                else p.Dispose();
            }
        return res;
    }

    static bool AnyLiveWindow()
    {
        bool live = false;
        foreach (var p in OtherInstances())
            using (p)
            {
                try
                {
                    p.Refresh();
                    if (!live && p.MainWindowHandle != IntPtr.Zero && p.Responding) live = true;
                }
                catch { }
            }
        return live;
    }

    static List<int> KillOthers()
    {
        var killed = new List<int>();
        foreach (var p in OtherInstances())
            using (p)
            {
                try
                {
                    p.Kill(true);
                    p.WaitForExit(5000);
                    killed.Add(p.Id);
                }
                catch { }
            }
        return killed;
    }

    // ---------- трей и окно ----------

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
        if (_quitting || Engine == null) return;
        try
        {
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
        catch (Exception ex)
        {
            _window = null;
            Crash.Report("Не удалось открыть окно", ex, false);
        }
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
