using System.IO;
using System.Windows;
using GnirehtetSquad.Core;

namespace GnirehtetSquad;

/// <summary>Журнал сбоев crash.log и сообщение об ошибке, чтобы программа не пропадала молча.</summary>
static class Crash
{
    static int _shown;

    public static string LogPath { get; private set; } = Path.Combine(Path.GetTempPath(), "GnirehtetSquad-crash.log");

    public static void SetDir(string dir) => LogPath = Path.Combine(dir, "crash.log");

    public static void Write(string where, Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Engine.AppName} {Engine.AppVersion} · Windows {Environment.OSVersion.Version} · {where}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Записывает ошибку в crash.log и показывает её (одно окно за запуск).</summary>
    public static void Report(string where, Exception ex, bool fatal)
    {
        Write(where, ex);
        if (Interlocked.Exchange(ref _shown, 1) == 1) return;
        try
        {
            MessageBox.Show(
                $"{where}:\n{ex.GetBaseException().Message}\n\nПодробности: {LogPath}" + (fatal ? "\n\nПрограмма будет закрыта." : ""),
                Engine.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
