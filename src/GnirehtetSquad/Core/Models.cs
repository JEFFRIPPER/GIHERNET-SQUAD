using System.Net;
using System.Net.Sockets;

namespace GnirehtetSquad.Core;

/// <summary>Настройки; формат settings.json совместим с версиями 1.x.</summary>
public sealed class Settings
{
    public string Mode { get; set; } = "autorun"; // autorun — все устройства, run — одно выбранное
    public string Serial { get; set; } = "";
    public string Dns { get; set; } = "1.1.1.1,8.8.8.8";
    public string Routes { get; set; } = "";
    public int Port { get; set; } = 31416;
    public string AdbPath { get; set; } = "";
    public bool AutoStart { get; set; }
    public bool AutoRestart { get; set; } = true;
    public bool StopDevicesOnOff { get; set; } = true;
    public bool KeepBackground { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; }

    public Settings Clone() => (Settings)MemberwiseClone();

    public void Normalize()
    {
        Mode = Mode == "run" ? "run" : "autorun";
        Serial ??= "";
        Dns ??= "";
        Routes ??= "";
        AdbPath ??= "";
        if (Port <= 0 || Port > 65535) Port = 31416;
    }

    /// <summary>Проверка DNS и маршрутов до запуска relay; null — всё верно, иначе текст ошибки.</summary>
    public static string? ValidateNetwork(string dns, string routes)
    {
        foreach (var d in SplitList(dns))
            if (!IPAddress.TryParse(d, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return $"DNS «{d}»: нужен IPv4-адрес, например 1.1.1.1";
        foreach (var r in SplitList(routes))
        {
            var parts = r.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                !int.TryParse(parts[1], out int bits) || bits < 0 || bits > 32)
                return $"Маршрут «{r}»: нужен вид 10.0.0.0/8";
        }
        return null;
    }

    static IEnumerable<string> SplitList(string s) =>
        s.Replace(" ", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
}

public sealed class Device
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";
    public string Model { get; set; } = "";
    public string Product { get; set; } = "";
    public bool Wireless { get; set; }
    public bool? Installed { get; set; }
    public string Busy { get; set; } = "";

    public Device Clone() => (Device)MemberwiseClone();
}

public sealed record LogLine(long Id, string Time, string Level, string Text);

public sealed class DownloadState
{
    public long Done { get; set; }
    public long Total { get; set; }
    public string Error { get; set; } = "";
    public DownloadState Clone() => (DownloadState)MemberwiseClone();
}

public sealed class UpdateInfo
{
    public bool Checking { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string Latest { get; set; } = "";
    public bool Available { get; set; }
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public bool Installing { get; set; }
    public long Done { get; set; }
    public string Stage { get; set; } = "";
    public string Error { get; set; } = "";
    public string AssetUrl { get; set; } = "";
    public string Digest { get; set; } = "";
    public UpdateInfo Clone() => (UpdateInfo)MemberwiseClone();
}

public sealed record StateSnapshot(
    bool Running,
    bool Starting,
    string Mode,
    string Serial,
    DateTime? StartedAt,
    int Clients,
    int Restarts,
    string AdbPath,
    bool AdbOk,
    string AdbError,
    string GnirehtetPath,
    bool GnirehtetOk,
    bool ApkOk,
    IReadOnlyList<Device> Devices,
    Settings Settings,
    DownloadState? Download,
    UpdateInfo Update);
