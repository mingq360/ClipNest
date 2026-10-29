using System.IO;
using System.Text;

namespace ClipNest.Infrastructure.Diagnostics;

/// <summary>
/// 极简文件日志。日志本身绝不能成为崩溃源，所有写入失败都静默吞掉。
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _filePath;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "clipnest.log");
        }
        catch (Exception)
        {
            _filePath = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message) => Write("WARN ", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (_filePath is null) return;

        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            builder.AppendLine();

            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 磁盘满、权限不足等：放弃这条日志，不影响主流程
        }
    }

    private static void RotateIfNeeded()
    {
        if (_filePath is null) return;

        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var backup = _filePath + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(_filePath, backup);
    }
}
