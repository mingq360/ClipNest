using System.IO;
using ClipNest.Services;
using Microsoft.Data.Sqlite;

namespace ClipNest.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略残留
        }
    }

    private SettingsService CreateService()
    {
        var service = new SettingsService(SettingsPath);
        service.Load();
        return service;
    }

    [Fact]
    public void Defaults_AreLightThemeAndTealAccent()
    {
        var settings = CreateService().Current;

        Assert.Equal(ThemeMode.Light, settings.Theme);
        Assert.Equal("Teal", settings.AccentColor);
    }

    [Fact]
    public void LegacyFileWithoutThemeFields_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        // 旧版本的 settings.json：没有 theme / accentColor
        File.WriteAllText(SettingsPath, """{ "maxItems": 1234, "enableMonitor": true }""");

        var settings = CreateService().Current;

        Assert.Equal(1234, settings.MaxItems);
        Assert.Equal(ThemeMode.Light, settings.Theme);
        Assert.Equal("Teal", settings.AccentColor);
    }

    [Fact]
    public void CorruptedThemeValue_FallsBackToLight()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "theme": 99, "accentColor": "" }""");

        var settings = CreateService().Current;

        Assert.Equal(ThemeMode.Light, settings.Theme);
        Assert.Equal("Teal", settings.AccentColor);
    }

    [Fact]
    public void Save_WritesThemeAsReadableString()
    {
        var service = CreateService();
        service.Update(s =>
        {
            s.Theme = ThemeMode.Dark;
            s.AccentColor = "Indigo";
        });

        var json = File.ReadAllText(SettingsPath);

        // 用字符串而不是数字，方便用户直接改文件
        Assert.Contains("\"theme\": \"Dark\"", json);
        Assert.Contains("\"accentColor\": \"Indigo\"", json);
    }

    [Fact]
    public void RoundTrip_KeepsThemeAndAccent()
    {
        var service = CreateService();
        service.Update(s =>
        {
            s.Theme = ThemeMode.System;
            s.AccentColor = "Rose";
        });

        var reloaded = CreateService().Current;

        Assert.Equal(ThemeMode.System, reloaded.Theme);
        Assert.Equal("Rose", reloaded.AccentColor);
    }

    [Fact]
    public void Update_ClampsMaxContentChars()
    {
        var service = CreateService();

        service.Update(s => s.MaxContentChars = 10);

        Assert.True(service.Current.MaxContentChars >= 1_000);
    }
}
