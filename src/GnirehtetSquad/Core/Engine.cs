using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GnirehtetSquad.Core;

/// <summary>
/// Ядро программы: relay gnirehtet, устройства adb, журнал, настройки, platform-tools.
/// Всё состояние под одним замком; окно читает снимки через <see cref="Snapshot"/>.
/// </summary>
public sealed partial class Engine
{
    public const string AppName = "Gnirehtet Squad";
    public const string AppVersion = "2.0.1";
    const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
    const string ApkPackage = "com.genymobile.gnirehtet";
    const int MaxLogLines = 3000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static readonly Regex LevelRe = new(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)? (ERROR|WARN|INFO|DEBUG|TRACE) (.*)$", RegexOptions.Compiled);
    static readonly Regex ClientRe = new(@"Client #(\d+) (connected|disconnected)", RegexOptions.Compiled);
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    readonly object _lock = new();

    public string BaseDir { get; }
    public string CfgDir { get; }
    public string ToolsDir { get; }
    public string ExePath { get; }
    public bool Background { get; }

    Settings _settings = new();

    Process? _proc;
    bool _running, _starting, _userStopped;
    DateTime _startedAt;
    string _runMode = "autorun", _runSerial = "";
    readonly HashSet<int> _clients = new();
    int _restarts, _restartGen;

    readonly Dictionary<string, Device> _devices = new();
    readonly HashSet<string> _installCheck = new();
    string _adbPath = "", _adbErr = "";
    string? _adbFound; // null — ещё не искали, чтобы первый результат попал в журнал
    DownloadState? _download;
    readonly UpdateInfo _update = new();

    readonly List<LogLine> _logs = new();
    long _logSeq;
    int _stateVersion;
    int _polling;
    Timer? _pollTimer;

    /// <summary>Просьба к окну завершить процесс (после установки обновления).</summary>
    public event Action? ExitRequested;

    public Engine(bool background)
    {
        Background = background;
        ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GnirehtetSquad.exe");
        BaseDir = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
        // настройки — в Roaming, исполняемые файлы — в Local (Roaming на рабочих ПК бывает сетевой папкой)
        CfgDir = DataDir(Environment.SpecialFolder.ApplicationData);
        ToolsDir = Path.Combine(DataDir(Environment.SpecialFolder.LocalApplicationData), "bin");
        LoadSettings();
        Log("app", $"{AppName} {AppVersion} · папка: {BaseDir}");
    }

    /// <summary>Папка GnirehtetSquad в указанной системной папке; если её нельзя создать — в Local или %TEMP%.</summary>
    static string DataDir(Environment.SpecialFolder folder)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(folder),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = Path.Combine(root, "GnirehtetSquad");
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
        }
        return Path.Combine(Path.GetTempPath(), "GnirehtetSquad");
    }

    public int StateVersion => Volatile.Read(ref _stateVersion);
    public string GnirehtetPath => Path.Combine(ToolsDir, "gnirehtet.exe");
    public string ApkPath => Path.Combine(ToolsDir, "gnirehtet.apk");
    string SettingsPath => Path.Combine(CfgDir, "settings.json");

    public Settings CurrentSettings { get { lock (_lock) return _settings.Clone(); } }

    // ---------- запуск ----------

    /// <summary>Распаковка инструментов, опрос adb, автозапуск relay. Вызывается в фоновом потоке.</summary>
    public void Init(bool afterUpdate, bool startRelay)
    {
        if (afterUpdate) Log("app", "✓ Программа обновлена до версии " + AppVersion);
        ExtractTools();
        Task.Run(CleanupOld);
        PollDevices();
        _pollTimer = new Timer(_ => PollDevices(), null, 2000, 2000);

        bool auto;
        lock (_lock) auto = _settings.AutoStart || Background || startRelay;
        if (auto)
        {
            try { Start(null, null); }
            catch (Exception ex) { Log("error", "Автозапуск relay: " + ex.Message); }
        }
        Task.Run(UpdateLoop);
    }

    /// <summary>Распаковывает вшитые gnirehtet.exe и gnirehtet.apk в %LOCALAPPDATA%\GnirehtetSquad\bin.</summary>
    void ExtractTools()
    {
        try { Directory.CreateDirectory(ToolsDir); }
        catch (Exception ex) { Log("error", "Не удалось создать папку " + ToolsDir + ": " + ex.Message); return; }
        foreach (var name in new[] { "gnirehtet.exe", "gnirehtet.apk" })
        {
            var dst = Path.Combine(ToolsDir, name);
            try
            {
                using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException("ресурс не найден в программе");
                var data = new byte[res.Length];
                res.ReadExactly(data);
                if (File.Exists(dst) && new FileInfo(dst).Length == data.Length &&
                    SHA256.HashData(File.ReadAllBytes(dst)).AsSpan().SequenceEqual(SHA256.HashData(data)))
                    continue;
                File.WriteAllBytes(dst, data);
            }
            catch (Exception ex)
            {
                if (!File.Exists(dst)) Log("error", $"Не удалось распаковать {name}: {ex.Message}");
            }
        }
    }

    // ---------- настройки ----------

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOpts);
            if (s == null) return;
            s.Normalize();
            _settings = s;
        }
        catch { /* битый файл — остаются значения по умолчанию */ }
    }

    void SaveSettingsLocked()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, JsonOpts)); }
        catch (Exception ex) { LogLocked("error", "Не удалось сохранить настройки: " + ex.Message); }
    }

    public void ApplySettings(Settings s)
    {
        if (s.Port <= 0 || s.Port > 65535) throw new ArgumentException("Порт должен быть 1–65535");
        s.Normalize();
        bool prevStartup;
        lock (_lock)
        {
            prevStartup = _settings.StartWithWindows;
            _settings = s.Clone();
            SaveSettingsLocked();
            ChangedLocked();
        }
        if (prevStartup != s.StartWithWindows)
        {
            try
            {
                SetStartup(s.StartWithWindows);
                Log("app", s.StartWithWindows ? "✓ Добавлено в автозагрузку Windows" : "Удалено из автозагрузки Windows");
            }
            catch (Exception ex) { Log("error", "Автозапуск Windows: " + ex.Message); }
        }
        Task.Run(PollDevices);
    }

    void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("GnirehtetSquad", $"\"{ExePath}\" --background");
        else key.DeleteValue("GnirehtetSquad", false);
    }

    // ---------- журнал и состояние ----------

    void ChangedLocked() => _stateVersion++;

    void LogLocked(string level, string text)
    {
        _logSeq++;
        _logs.Add(new LogLine(_logSeq, DateTime.Now.ToString("HH:mm:ss"), level, text));
        if (_logs.Count > MaxLogLines) _logs.RemoveRange(0, _logs.Count - MaxLogLines);
    }

    public void Log(string level, string text)
    {
        lock (_lock) LogLocked(level, text);
    }

    /// <summary>Строки журнала с номером больше <paramref name="afterId"/>.</summary>
    public List<LogLine> LogsSince(long afterId)
    {
        lock (_lock)
        {
            if (_logs.Count == 0 || _logs[^1].Id <= afterId) return new();
            int i = _logs.FindIndex(l => l.Id > afterId);
            return _logs.GetRange(i, _logs.Count - i);
        }
    }

    public void ClearLogs()
    {
        lock (_lock) _logs.Clear();
    }

    /// <summary>Разбирает строку вывода gnirehtet: уровень и подключение клиентов.</summary>
    void RelayLine(string line, bool stderr)
    {
        line = line.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(line)) return;
        string level = stderr ? "error" : "info", text = line;
        var m = LevelRe.Match(line);
        if (m.Success)
        {
            level = m.Groups[1].Value.ToLowerInvariant();
            if (level == "trace") level = "debug";
            text = m.Groups[2].Value;
        }
        lock (_lock)
        {
            var c = ClientRe.Match(text);
            if (c.Success)
            {
                int id = int.Parse(c.Groups[1].Value);
                if (c.Groups[2].Value == "connected") _clients.Add(id); else _clients.Remove(id);
                ChangedLocked();
            }
            LogLocked(level, text);
        }
    }

    public StateSnapshot Snapshot()
    {
        lock (_lock)
        {
            var devs = _devices.Values.Select(d => d.Clone()).OrderBy(d => d.Serial, StringComparer.Ordinal).ToList();
            return new StateSnapshot(
                _running, _starting, _runMode, _runSerial, _running ? _startedAt : null,
                _clients.Count, _restarts,
                _adbPath, _adbPath != "" && _adbErr == "", _adbErr,
                GnirehtetPath, File.Exists(GnirehtetPath), File.Exists(ApkPath),
                devs, _settings.Clone(), _download?.Clone(), _update.Clone());
        }
    }

    // ---------- внешние команды ----------

    ProcessStartInfo NewPsi(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolsDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        string adb;
        lock (_lock) adb = _adbPath;
        if (adb != "") psi.Environment["ADB"] = adb;
        return psi;
    }

    /// <summary>Выполняет короткую команду. Возвращает вывод и код завершения.</summary>
    async Task<(string Output, int Code)> RunToolAsync(TimeSpan timeout, bool logOutput, string file, params string[] args)
    {
        using var p = new Process { StartInfo = NewPsi(file, args) };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"превышено время ожидания ({timeout.TotalSeconds:0} с)");
        }
        // adb может запустить свой сервер, который унаследует каналы вывода, — не ждём их вечно
        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(2000)).ConfigureAwait(false);
        string output = (outTask.IsCompletedSuccessfully ? outTask.Result : "") +
                        (errTask.IsCompletedSuccessfully ? errTask.Result : "");
        if (logOutput)
            foreach (var l in output.Split('\n')) RelayLine(l, false);
        return (output, p.ExitCode);
    }

    async Task GnirehtetAsync(TimeSpan timeout, params string[] args)
    {
        var (_, code) = await RunToolAsync(timeout, true, GnirehtetPath, args).ConfigureAwait(false);
        if (code != 0) throw new Exception($"код завершения {code}");
    }

    async Task<(string Output, int Code)> AdbAsync(TimeSpan timeout, params string[] args)
    {
        string adb;
        lock (_lock) adb = _adbPath;
        if (adb == "") throw new Exception("adb не найден");
        return await RunToolAsync(timeout, false, adb, args).ConfigureAwait(false);
    }

    // ---------- relay ----------

    List<string> RelayArgsLocked(string mode, string serial)
    {
        var s = _settings;
        var args = new List<string>();
        if (mode == "run")
        {
            args.Add("run");
            if (serial != "") args.Add(serial);
        }
        else args.Add("autorun");
        var dns = s.Dns.Replace(" ", "").Trim();
        if (dns != "") { args.Add("-d"); args.Add(dns); }
        var routes = s.Routes.Replace(" ", "").Trim();
        if (routes != "") { args.Add("-r"); args.Add(routes); }
        if (s.Port != 0 && s.Port != 31416) { args.Add("-p"); args.Add(s.Port.ToString()); }
        return args;
    }

    static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    public void Start(string? mode, string? serial)
    {
        lock (_lock)
        {
            if (_running || _starting) throw new InvalidOperationException("Relay уже запущен");
            mode = string.IsNullOrEmpty(mode) ? _settings.Mode : mode;
            serial ??= "";
            if (mode == "run" && serial == "") serial = _settings.Serial;
            if (!File.Exists(GnirehtetPath)) throw new InvalidOperationException("Не найден gnirehtet.exe: " + GnirehtetPath);
            if (_adbPath == "") throw new InvalidOperationException("adb не найден — скачайте platform-tools или укажите путь в настройках");
            if (!PortFree(_settings.Port))
                throw new InvalidOperationException($"Порт {_settings.Port} занят — вероятно, уже работает другой gnirehtet. Нажмите «Завершить зависшие процессы»");
            _userStopped = false;
            _restarts = 0;
            _restartGen++;
            SpawnLocked(mode, serial, _restartGen);
        }
    }

    void SpawnLocked(string mode, string serial, int gen)
    {
        var args = RelayArgsLocked(mode, serial);
        var p = new Process { StartInfo = NewPsi(GnirehtetPath, args), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) RelayLine(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) RelayLine(e.Data, true); };
        p.Exited += (_, _) => OnRelayExited(p, mode, serial, gen);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        _running = true;
        _starting = false;
        _runMode = mode;
        _runSerial = serial;
        _startedAt = DateTime.Now;
        _clients.Clear();
        LogLocked("app", "▶ gnirehtet " + string.Join(" ", args));
        ChangedLocked();
    }

    void OnRelayExited(Process p, string mode, string serial, int gen)
    {
        Thread.Sleep(150); // дочитываем последние строки вывода
        lock (_lock)
        {
            if (_proc != p) return;
            _proc = null;
            _running = false;
            _clients.Clear();
            int code = 0;
            try { code = p.ExitCode; } catch { }
            if (_userStopped)
            {
                LogLocked("app", "■ Relay остановлен");
                ChangedLocked();
                return;
            }
            LogLocked("warn", $"Relay завершился (код {code})");
            if (_settings.AutoRestart && gen == _restartGen && _restarts < 50)
            {
                _restarts++;
                var delay = TimeSpan.FromSeconds(Math.Min(_restarts, 10) * 2);
                _starting = true;
                LogLocked("app", $"↻ Автоперезапуск через {delay.TotalSeconds:0} с (попытка {_restarts})");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (_userStopped || gen != _restartGen || _running)
                        {
                            _starting = false;
                            ChangedLocked();
                            return;
                        }
                        try { SpawnLocked(mode, serial, gen); }
                        catch (Exception ex)
                        {
                            _starting = false;
                            LogLocked("error", "Перезапуск не удался: " + ex.Message);
                            ChangedLocked();
                        }
                    }
                });
            }
            ChangedLocked();
        }
    }

    /// <summary>Останавливает relay; при stopDevices выключает VPN на телефонах.</summary>
    public void Stop(bool stopDevices)
    {
        Process? p;
        var serials = new List<string>();
        lock (_lock)
        {
            _userStopped = true;
            _starting = false;
            _restartGen++;
            p = _proc;
            if (stopDevices && _settings.StopDevicesOnOff)
                foreach (var d in _devices.Values)
                    if (d.State == "device" && (_runMode != "run" || _runSerial == "" || _runSerial == d.Serial))
                        serials.Add(d.Serial);
            ChangedLocked();
        }
        if (p != null)
        {
            try { p.Kill(true); p.WaitForExit(3000); } catch { }
        }
        var tasks = serials.Select(s => Task.Run(async () =>
        {
            try { await GnirehtetAsync(TimeSpan.FromSeconds(15), "stop", s); }
            catch (Exception ex) { Log("warn", $"stop {s}: {ex.Message}"); }
        })).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
    }

    public void Restart()
    {
        string mode, serial;
        lock (_lock) { mode = _runMode; serial = _runSerial; }
        Stop(false);
        Thread.Sleep(700);
        Start(mode, serial);
    }

    public void KillStale()
    {
        Stop(false);
        foreach (var p in Process.GetProcessesByName("gnirehtet"))
        {
            try { p.Kill(true); } catch { }
            p.Dispose();
        }
        Log("app", "✓ Все процессы gnirehtet завершены");
    }

    // ---------- устройства ----------

    string FindAdb()
    {
        string custom;
        lock (_lock) custom = _settings.AdbPath.Trim().Trim('"');
        var cand = new List<string>();
        if (custom != "")
            cand.Add(Directory.Exists(custom) ? Path.Combine(custom, "adb.exe") : custom);
        cand.Add(Path.Combine(BaseDir, "platform-tools", "adb.exe"));
        cand.Add(Path.Combine(BaseDir, "adb.exe"));
        cand.Add(Path.Combine(CfgDir, "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            cand.Add(Path.Combine(dir.Trim().Trim('"'), "adb.exe"));
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(v)) cand.Add(Path.Combine(v, "platform-tools", "adb.exe"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        cand.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (var c in cand)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
        }
        return "";
    }

    static List<Device> ParseDevices(string output)
    {
        var res = new List<Device>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith("List of devices") || line.StartsWith("*")) continue;
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2) continue;
            var d = new Device { Serial = f[0], State = f[1] };
            foreach (var kv in f.Skip(2))
            {
                int i = kv.IndexOf(':');
                if (i < 0) continue;
                var k = kv[..i];
                var v = kv[(i + 1)..];
                if (k == "model") d.Model = v.Replace('_', ' ');
                else if (k == "product") d.Product = v;
            }
            d.Wireless = d.Serial.Contains(':') || d.Serial.StartsWith("adb-");
            res.Add(d);
        }
        return res;
    }

    public void PollDevices()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        // только в пуле потоков: синхронное ожидание async-кода на UI-потоке WPF
        // даёт взаимоблокировку (из-за неё 2.0.0 зависала при старте, если adb найден)
        try { Task.Run(PollDevicesAsync).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("debug", "Опрос устройств: " + ex.Message); }
        finally { Volatile.Write(ref _polling, 0); }
    }

    async Task PollDevicesAsync()
    {
        var adbPath = FindAdb();
        bool changedAdb;
        lock (_lock)
        {
            changedAdb = adbPath != _adbFound;
            _adbFound = adbPath;
            _adbPath = adbPath;
            if (changedAdb)
            {
                if (adbPath != "") LogLocked("app", "adb: " + adbPath);
                else LogLocked("warn", "adb не найден. Нажмите «Скачать platform-tools» или укажите путь в настройках.");
            }
        }

        var list = new List<Device>();
        string adbErr = "";
        if (adbPath != "")
        {
            try
            {
                var (output, code) = await AdbAsync(TimeSpan.FromSeconds(8), "devices", "-l").ConfigureAwait(false);
                if (code != 0) adbErr = $"adb devices: код {code} {FirstLine(output)}".Trim();
                else list = ParseDevices(output);
            }
            catch (Exception ex) { adbErr = ex.Message; }
        }
        else adbErr = "adb не найден";

        var toCheck = new List<string>();
        lock (_lock)
        {
            bool changed = adbErr != _adbErr || changedAdb;
            _adbErr = adbErr;
            var seen = new HashSet<string>();
            foreach (var d in list)
            {
                seen.Add(d.Serial);
                if (!_devices.TryGetValue(d.Serial, out var old))
                {
                    _devices[d.Serial] = d;
                    LogLocked("app", $"＋ Устройство {d.Serial} ({Nz(d.Model, "?")}) — {StateRu(d.State)}");
                    changed = true;
                    continue;
                }
                if (old.State != d.State || old.Model != d.Model)
                {
                    if (old.State != d.State) LogLocked("app", $"● {d.Serial}: {StateRu(d.State)}");
                    old.State = d.State; old.Model = d.Model; old.Product = d.Product; old.Wireless = d.Wireless;
                    if (d.State != "device")
                    {
                        old.Installed = null;
                        _installCheck.Remove(d.Serial);
                    }
                    changed = true;
                }
            }
            foreach (var s in _devices.Keys.ToList())
            {
                if (seen.Contains(s)) continue;
                LogLocked("app", "－ Устройство отключено: " + s);
                _devices.Remove(s);
                _installCheck.Remove(s);
                changed = true;
            }
            foreach (var (s, d) in _devices)
                if (d.State == "device" && _installCheck.Add(s)) toCheck.Add(s);
            if (changed) ChangedLocked();
        }
        foreach (var s in toCheck) _ = Task.Run(() => CheckInstalledAsync(s));
    }

    async Task CheckInstalledAsync(string serial)
    {
        string output;
        try
        {
            var r = await AdbAsync(TimeSpan.FromSeconds(15), "-s", serial, "shell", "pm", "list", "packages", ApkPackage);
            if (r.Code != 0) throw new Exception("код " + r.Code);
            output = r.Output;
        }
        catch
        {
            lock (_lock) _installCheck.Remove(serial);
            return;
        }
        lock (_lock)
        {
            if (!_devices.TryGetValue(serial, out var d)) return;
            d.Installed = output.Contains("package:" + ApkPackage);
            ChangedLocked();
        }
    }

    static readonly HashSet<string> DeviceActions = new() { "start", "stop", "install", "reinstall", "uninstall", "tunnel" };

    public void DeviceAction(string serial, string action)
    {
        if (!DeviceActions.Contains(action)) throw new ArgumentException("Неизвестное действие");
        lock (_lock)
        {
            if (!_devices.TryGetValue(serial, out var d)) throw new InvalidOperationException("Устройство не найдено");
            if (d.Busy != "") throw new InvalidOperationException("Устройство занято: " + d.Busy);
            if (action == "start" && !_running) throw new InvalidOperationException("Сначала запустите relay");
            d.Busy = action;
            ChangedLocked();
        }
        Task.Run(async () =>
        {
            Exception? err = null;
            try { await GnirehtetAsync(TimeSpan.FromSeconds(90), action, serial); }
            catch (Exception ex) { err = ex; }
            lock (_lock)
            {
                if (_devices.TryGetValue(serial, out var d))
                {
                    d.Busy = "";
                    if (action is "install" or "reinstall" or "uninstall")
                    {
                        _installCheck.Remove(serial);
                        d.Installed = null;
                    }
                }
                LogLocked(err != null ? "error" : "app", err != null ? $"{action} {serial}: {err.Message}" : $"✓ {action} {serial} — готово");
                ChangedLocked();
            }
        });
    }

    public void AdbConnect(string addr)
    {
        addr = addr.Trim();
        if (addr == "" || addr.IndexOfAny(new[] { ' ', '\t', '"', '\'', '&', '|', ';' }) >= 0)
            throw new ArgumentException("Укажите адрес вида 192.168.1.10:5555");
        Task.Run(async () =>
        {
            try
            {
                var (output, _) = await AdbAsync(TimeSpan.FromSeconds(15), "connect", addr);
                Log("app", $"adb connect {addr}: {output.Trim()}");
            }
            catch (Exception ex) { Log("error", "adb connect: " + ex.Message); }
            PollDevices();
        });
    }

    public void AdbRestart()
    {
        Task.Run(async () =>
        {
            Log("app", "↻ Перезапуск adb-сервера…");
            try
            {
                await AdbAsync(TimeSpan.FromSeconds(10), "kill-server");
                var (output, code) = await AdbAsync(TimeSpan.FromSeconds(20), "start-server");
                if (code != 0) Log("error", "adb start-server: " + output.Trim());
                else Log("app", "✓ adb-сервер запущен");
            }
            catch (Exception ex) { Log("error", "adb: " + ex.Message); }
            PollDevices();
        });
    }

    // ---------- platform-tools ----------

    static bool WritableDir(string dir)
    {
        try
        {
            var f = Path.Combine(dir, ".wtest" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(f, "");
            File.Delete(f);
            return true;
        }
        catch { return false; }
    }

    public void DownloadPlatformTools()
    {
        lock (_lock)
        {
            if (_download != null && _download.Error == "") throw new InvalidOperationException("Загрузка уже идёт");
            _download = new DownloadState();
            LogLocked("app", "⇣ Скачивание platform-tools с dl.google.com…");
            ChangedLocked();
        }
        Task.Run(async () =>
        {
            try
            {
                await DownloadPlatformToolsAsync();
                lock (_lock)
                {
                    _download = null;
                    LogLocked("app", "✓ platform-tools установлены");
                    ChangedLocked();
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (_download != null) _download.Error = ex.Message;
                    LogLocked("error", "Не удалось скачать platform-tools: " + ex.Message);
                    ChangedLocked();
                }
            }
            PollDevices();
        });
    }

    async Task DownloadPlatformToolsAsync()
    {
        var target = WritableDir(BaseDir) ? BaseDir : CfgDir;
        using var resp = await Http.GetAsync(PlatformToolsUrl, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}");
        lock (_lock) if (_download != null) _download.Total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = Path.Combine(CfgDir, $"pt-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
                await CopyWithProgress(src, dst, null, n => { lock (_lock) { if (_download != null) _download.Done = n; ChangedLocked(); } });
            ZipFile.ExtractToDirectory(tmp, target, true);
        }
        finally { TryDelete(tmp); }
    }

    static async Task CopyWithProgress(Stream src, Stream dst, IncrementalHash? hash, Action<long> progress)
    {
        var buf = new byte[81920];
        long total = 0;
        var last = DateTime.MinValue;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            hash?.AppendData(buf, 0, n);
            total += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 200)
            {
                last = DateTime.UtcNow;
                progress(total);
            }
        }
        progress(total);
    }

    // ---------- выход ----------

    /// <summary>Полная остановка перед выходом: relay и VPN на телефонах.</summary>
    public void Shutdown()
    {
        _pollTimer?.Dispose();
        Log("app", "Выход…");
        Stop(true);
    }

    public void OpenFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BaseDir}\"") { UseShellExecute = true });

    // ---------- вспомогательное ----------

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GnirehtetSquad/" + AppVersion);
        return c;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static string FirstLine(string s)
    {
        s = s.Trim();
        int i = s.IndexOf('\n');
        return i >= 0 ? s[..i] : s;
    }

    static string Nz(string s, string d) => s == "" ? d : s;

    public static string StateRu(string s) => s switch
    {
        "device" => "готово",
        "unauthorized" => "нужно разрешить отладку на телефоне",
        "offline" => "offline",
        "authorizing" => "авторизация…",
        _ => s,
    };
}
