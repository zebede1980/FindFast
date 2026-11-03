using System.IO;

namespace FastFind.App.Services;

public static class Logger
{
    private static readonly object _gate = new();
    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastFind");
    public static string FilePath { get; } = Path.Combine(Directory, "fastfind.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
