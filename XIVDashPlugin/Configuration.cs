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

    // Optional telemetry for recommendation quality (session duration/playtime)
    public bool EnableSessionTelemetry { get; set; } = false;

    // Detailed gameplay signals for recommendation relevance (job/party/quest context)
    public bool EnableDetailedGameplaySignals { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
