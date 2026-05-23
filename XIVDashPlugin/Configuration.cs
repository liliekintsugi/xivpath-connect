using Dalamud.Configuration;

namespace XIVDashPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string XIVDashUrl { get; set; } = "https://xivdash.app";
    public string ApiToken { get; set; } = string.Empty;

    // Auto-sync on zone change (recommended)
    public bool AutoSyncOnZoneChange { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
