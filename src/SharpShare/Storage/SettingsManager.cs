using SharpShare.Models;
using System.Text.Json;

namespace SharpShare.Storage;

public static class SettingsManager
{
    private static string settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SharpShare");

    private static string SettingsFilePath => Path.Combine(settingsDirectory, "settings.json");

    internal static void SetSettingsDirectory(string path)
    {
        settingsDirectory = path;
    }

    public static UserSettings Load()
    {
        string filePath = SettingsFilePath;

        if (!File.Exists(filePath))
        {
            return new UserSettings();
        }

        try
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            var settings = JsonSerializer.Deserialize(fileBytes, SettingsJsonContext.Default.UserSettings);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        string filePath = SettingsFilePath;
        string directory = Path.GetDirectoryName(filePath)!;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempFilePath = $"{filePath}.tmp";
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.UserSettings);
        File.WriteAllBytes(tempFilePath, jsonBytes);
        File.Move(tempFilePath, filePath, overwrite: true);
    }
}
