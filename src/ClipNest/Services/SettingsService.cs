using System.IO;
using System.Text.Json;
using ClipNest.Infrastructure.Data;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.Services;

/// <summary>settings.json 读写。写盘用临时文件 + 替换，避免断电留下半截 JSON。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Db.DataDirectory, "settings.json");
        Current = new AppSettings();
    }

    public string FilePath { get; }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                return Current;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            Current = loaded ?? new AppSettings();
            Current.Normalize();
            return Current;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置读坏了不能让应用起不来：备份坏文件，用默认值继续
            Log.Error($"读取设置失败，回退默认值：{FilePath}", ex);
            TryBackupCorruptedFile();
            Current = new AppSettings();
            return Current;
        }
    }

    /// <summary>修改 + 落盘 + 通知。所有设置变更都应走这里。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        var draft = Current.Clone();
        mutate(draft);
        draft.Normalize();

        Current = draft;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
            return false;
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Move(FilePath, FilePath + ".corrupted", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("备份损坏的配置文件失败", ex);
        }
    }
}
