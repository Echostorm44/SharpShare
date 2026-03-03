using System.Text.Json.Serialization;

namespace SharpShare.Models;

public sealed class UserSettings
{
    public string SharedFolderPath { get; set; } = "";
    public bool IsDarkMode { get; set; } = false;
    public int ListeningPort { get; set; } = 9500;
    public bool EnableUpnp { get; set; } = true;
    public int MaxTransferSpeedMBps { get; set; } = 0; // 0 = unlimited
    public string LastConnectionAddress { get; set; } = "";
}

[JsonSerializable(typeof(UserSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext { }
