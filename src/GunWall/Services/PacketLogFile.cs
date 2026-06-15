using System.IO;
using System.Text;

namespace GunWall.Services;

/// <summary>
/// Appends packet-log entries to a CSV file in the profile folder, with simple
/// size-based rotation (keeps the current file and one ".1" backup). Thread-safe
/// and best-effort — logging never throws into the app.
/// </summary>
public sealed class PacketLogFile
{
    private readonly string _path;
    private readonly string _backup;
    private readonly object _lock = new();
    private const long MaxBytes = 5 * 1024 * 1024; // 5 MB, then rotate

    public PacketLogFile(string profileFolder)
    {
        _path = Path.Combine(profileFolder, "packets.csv");
        _backup = Path.Combine(profileFolder, "packets.1.csv");
    }

    public string Path_ => _path;

    public void Append(DateTime time, string action, string app, string protocol,
                       string direction, string remote, string exePath)
    {
        try
        {
            lock (_lock)
            {
                bool newFile = !File.Exists(_path);
                if (!newFile && new FileInfo(_path).Length > MaxBytes) Rotate();
                newFile = !File.Exists(_path);

                var sb = new StringBuilder();
                if (newFile)
                    sb.AppendLine("Time,Action,App,Protocol,Direction,Remote,Path");
                sb.Append(Csv(time.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                  .Append(Csv(action)).Append(',')
                  .Append(Csv(app)).Append(',')
                  .Append(Csv(protocol)).Append(',')
                  .Append(Csv(direction)).Append(',')
                  .Append(Csv(remote)).Append(',')
                  .Append(Csv(exePath)).Append('\n');

                File.AppendAllText(_path, sb.ToString());
            }
        }
        catch { /* never break the app over logging */ }
    }

    private void Rotate()
    {
        try
        {
            if (File.Exists(_backup)) File.Delete(_backup);
            File.Move(_path, _backup);
        }
        catch { }
    }

    private static string Csv(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }
}
