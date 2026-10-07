using System.Text.Json;
using GlanceNext.Core;

namespace GlanceNext.Desktop.Services;

public sealed class LocalStore
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlanceNext");
    private readonly object sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public AppSettings Load()
    {
        try
        {
            var path = Path.Combine(DirectoryPath, "settings.json");
            var result = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new() : new AppSettings();
            result.Sanitize();
            return result;
        }
        catch (Exception ex) { Log("settings-load", ex.GetType().Name); return new(); }
    }
    public void Save(AppSettings settings)
    {
        lock (sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "settings.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, Options));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void Log(string state, string detail)
    {
        // Only callers' state/error labels are written, never image bytes or landmarks.
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{state}] {detail.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
            catch { /* Logging must not interfere with cleanup or camera error handling. */ }
        }
    }
}
