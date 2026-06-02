using Dalamud.Configuration;

namespace XIVPathPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string XIVPathUrl { get; set; } = "https://xivpath.fr";
    public string ApiToken { get; set; } = string.Empty;

    // Auto-sync on zone change (recommended)
    public bool AutoSyncOnZoneChange { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
