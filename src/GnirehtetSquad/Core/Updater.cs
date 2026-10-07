using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace GnirehtetSquad.Core;

// OTA-обновления: последний релиз на GitHub → zip GnirehtetSquad-X.Y.Z-win64.zip →
// проверка SHA-256 → замена файлов рядом с программой (с откатом) → перезапуск.
// Формат архива тот же, что у 1.x: папка GnirehtetSquad/ с файлами верхнего уровня.
public sealed partial class Engine
{
    const string UpdateRepo = "JEFFRIPPER/GIHERNET-SQUAD";
    const string SelfName = "GnirehtetSquad.exe";

    static string UpdateApi =>
        Environment.GetEnvironmentVariable("GSQUAD_UPDATE_API") is { Length: > 0 } v
            ? v
            : $"https://api.github.com/repos/{UpdateRepo}/releases/latest";

    /// <summary>Новее ли версия a, чем b ("1.2.0" &gt; "1.1.9").</summary>
    public static bool NewerVersion(string a, string b)
    {
        var pa = SplitVer(a);
        var pb = SplitVer(b);
        for (int i = 0; i < 3; i++)
            if (pa[i] != pb[i]) return pa[i] > pb[i];
        return false;
    }

    static int[] SplitVer(string v)
    {
        var r = new int[3];
        v = v.Trim().TrimStart('v');
        int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) v = v[..cut];
        var parts = v.Split('.', 3);
        for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
        return r;
    }

    async Task UpdateLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            bool on;
            lock (_lock) on = _settings.CheckUpdates;
            if (on) await CheckUpdateAsync(false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    public void CheckUpdate() => Task.Run(() => CheckUpdateAsync(true));

    async Task CheckUpdateAsync(bool manual)
    {
        lock (_lock)
        {
            if (_update.Checking || _update.Installing) return;
            _update.Checking = true;
            _update.Error = "";
            ChangedLocked();
        }

        JsonElement rel = default;
        string? err = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UpdateApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if ((int)resp.StatusCode == 404) err = "релизов пока нет";
            else if (!resp.IsSuccessStatusCode) err = $"GitHub ответил {(int)resp.StatusCode}";
            else rel = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        catch (Exception ex) { err = ex.Message; }

        bool autoInstall = false;
        lock (_lock)
        {
            var u = _update;
            u.Checking = false;
            u.CheckedAt = DateTime.Now;
            if (err != null)
            {
                u.Error = "Проверка обновлений: " + err;
                if (manual) LogLocked("warn", u.Error);
                ChangedLocked();
                return;
            }
            var latest = Str(rel, "tag_name").TrimStart('v');
            u.Latest = latest;
            u.Notes = Str(rel, "body").Trim();
            u.Url = Str(rel, "html_url");
            u.AssetUrl = ""; u.Digest = ""; u.Size = 0;
            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name");
                    if (!name.StartsWith("GnirehtetSquad-") || !name.EndsWith("-win64.zip")) continue;
                    u.AssetUrl = Str(a, "browser_download_url");
                    u.Digest = Str(a, "digest");
                    u.Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                    break;
                }
            }
            bool was = u.Available;
            u.Available = NewerVersion(latest, AppVersion) && u.AssetUrl != "";
            if (u.Available && (!was || manual)) LogLocked("app", $"⬆ Доступно обновление {latest} (у вас {AppVersion})");
            else if (!u.Available && manual) LogLocked("app", "✓ Установлена последняя версия " + AppVersion);
            autoInstall = u.Available && _settings.AutoUpdate && !Background;
            ChangedLocked();
        }
        if (autoInstall)
        {
            await Task.Delay(3000);
            try { InstallUpdate(); }
            catch (Exception ex) { Log("error", "Автообновление: " + ex.Message); }
        }
    }

    static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    void SetStage(string stage, long done)
    {
        lock (_lock)
        {
            _update.Stage = stage;
            _update.Done = done;
            ChangedLocked();
        }
    }

    public void InstallUpdate()
    {
        string assetUrl, digest, latest;
        lock (_lock)
        {
            var u = _update;
            if (u.Installing) throw new InvalidOperationException("Обновление уже устанавливается");
            if (!u.Available || u.AssetUrl == "") throw new InvalidOperationException("Нет доступного обновления");
            if (!WritableDir(BaseDir)) throw new InvalidOperationException("Нет прав на запись в папку программы — переместите её, например, в Документы");
            u.Installing = true; u.Error = ""; u.Done = 0;
            assetUrl = u.AssetUrl; digest = u.Digest; latest = u.Latest;
            LogLocked("app", $"⬇ Скачивание обновления {latest}…");
            ChangedLocked();
        }
        Task.Run(async () =>
        {
            try { await DoUpdateAsync(assetUrl, digest, latest); }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _update.Installing = false;
                    _update.Stage = "";
                    _update.Error = "Обновление не установлено: " + ex.Message;
                    LogLocked("error", _update.Error);
                    ChangedLocked();
                }
            }
        });
    }

    async Task DoUpdateAsync(string assetUrl, string digest, string latest)
    {
        // 1. скачивание
        var tmp = Path.Combine(CfgDir, $"update-{Guid.NewGuid():N}.zip");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var resp = await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) throw new Exception($"скачивание: HTTP {(int)resp.StatusCode}");
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                await CopyWithProgress(src, dst, hash, n => SetStage("download", n));
            }

            // 2. контрольная сумма (GitHub отдаёт digest "sha256:…")
            SetStage("verify", 0);
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var got = Convert.ToHexString(hash.GetHashAndReset());
                if (!got.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new Exception("контрольная сумма не совпала — файл повреждён");
            }

            using var zip = ZipFile.OpenRead(tmp);
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                var name = e.FullName.Replace('\\', '/');
                int slash = name.IndexOf('/');
                if (slash >= 0) name = name[(slash + 1)..]; // убираем корневую папку GnirehtetSquad/
                if (name == "" || name.Contains("..") || name.Contains('/')) continue; // только верхний уровень
                files[name] = e;
            }
            if (!files.ContainsKey(SelfName)) throw new Exception("в архиве нет " + SelfName);

            // 3. остановка relay
            bool wasRunning;
            lock (_lock) wasRunning = _running || _starting;
            SetStage("install", 0);
            if (wasRunning)
            {
                Stop(false);
                await Task.Delay(800);
            }

            // 4. замена: старый файл → .old (работающий exe можно переименовать), новый — на его место
            var replaced = new List<string>();
            void Rollback()
            {
                foreach (var p in replaced)
                {
                    TryDelete(p);
                    try { if (File.Exists(p + ".old")) File.Move(p + ".old", p); } catch { }
                }
            }
            foreach (var (name, entry) in files)
            {
                var dst = Path.Combine(BaseDir, name);
                try
                {
                    if (File.Exists(dst))
                    {
                        TryDelete(dst + ".old");
                        File.Move(dst, dst + ".old");
                    }
                    replaced.Add(dst);
                    entry.ExtractToFile(dst, true);
                }
                catch (Exception ex)
                {
                    Rollback();
                    throw new Exception($"не удалось заменить {name}: {ex.Message}");
                }
            }

            // 5. перезапуск новой версии
            SetStage("restart", 0);
            Log("app", $"✓ Обновление {latest} установлено, перезапуск…");
            var psi = new ProcessStartInfo(Path.Combine(BaseDir, SelfName)) { UseShellExecute = false, WorkingDirectory = BaseDir };
            psi.ArgumentList.Add("--after-update");
            if (wasRunning) psi.ArgumentList.Add("--start-relay");
            if (Background) psi.ArgumentList.Add("--background");
            try { Process.Start(psi); }
            catch (Exception ex)
            {
                Rollback();
                throw new Exception("не удалось запустить новую версию: " + ex.Message);
            }
            ExitRequested?.Invoke();
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Удаляет *.old, оставшиеся после обновления.</summary>
    async Task CleanupOld()
    {
        // профиль браузера от окна версий 1.x больше не нужен
        try
        {
            var oldProfile = Path.Combine(CfgDir, "window");
            if (Directory.Exists(oldProfile)) Directory.Delete(oldProfile, true);
        }
        catch { }
        string[] old;
        try { old = Directory.GetFiles(BaseDir, "*.old"); } catch { return; }
        foreach (var f in old)
        {
            for (int i = 0; i < 10 && File.Exists(f); i++) // старый процесс может ещё завершаться
            {
                TryDelete(f);
                if (File.Exists(f)) await Task.Delay(500);
            }
        }
    }
}
