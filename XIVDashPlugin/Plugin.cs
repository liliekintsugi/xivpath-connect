using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVPathPlugin.Windows;

namespace XIVPathPlugin;

public sealed class Plugin : IDalamudPlugin
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly ICommandManager _commands;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly SyncService _sync;
    private readonly WindowSystem _windowSystem;
    private readonly ConfigWindow _configWindow;
    private DateTimeOffset? _sessionStartedAtUtc;
    private DateTimeOffset _lastPlaytimeTickUtc;
    private DateOnly _playtimeDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _dailyPlaytimeSec;
    private int _zoneChangesThisSession;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IClientState clientState,
        IPluginLog log,
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        _commands = commands;
        _clientState = clientState;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _sync = new SyncService(dataManager);
        _windowSystem = new WindowSystem("XIVPathPlugin");
        _configWindow = new ConfigWindow(_config, _sync, () => BuildTelemetry("manual"));
        _windowSystem.AddWindow(_configWindow);

        _commands.AddHandler("/xivpath", new CommandInfo(OnCommand)
        {
            HelpMessage = "Ouvre la fenêtre de configuration XIVPath Connect",
        });

        pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Login += OnLogin;
    }

    private void OnCommand(string command, string args) => OpenConfig();
    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OnLogin()
    {
        StartSession();
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync("login");
    }

    private void OnTerritoryChanged(uint territory)
    {
        _zoneChangesThisSession++;
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync($"zone:{territory}");
    }

    private void StartSession()
    {
        var now = DateTimeOffset.UtcNow;
        EnsureDailyCounter(now);
        _sessionStartedAtUtc = now;
        _lastPlaytimeTickUtc = now;
        _zoneChangesThisSession = 0;
    }

    private void EnsureDailyCounter(DateTimeOffset now)
    {
        var currentDay = DateOnly.FromDateTime(now.UtcDateTime);
        if (currentDay == _playtimeDay) return;
        _playtimeDay = currentDay;
        _dailyPlaytimeSec = 0;
    }

    private void UpdateDailyPlaytime(DateTimeOffset now)
    {
        EnsureDailyCounter(now);
        if (_lastPlaytimeTickUtc == default)
        {
            _lastPlaytimeTickUtc = now;
            return;
        }

        var deltaSec = (int)Math.Max(0, (now - _lastPlaytimeTickUtc).TotalSeconds);
        if (deltaSec > 0)
            _dailyPlaytimeSec = Math.Min(86_400, _dailyPlaytimeSec + deltaSec);
        _lastPlaytimeTickUtc = now;
    }

    private SessionTelemetry? BuildTelemetry(string reason)
    {
        if (!_config.EnableSessionTelemetry) return null;

        var now = DateTimeOffset.UtcNow;
        UpdateDailyPlaytime(now);
        var sessionDurationSec = _sessionStartedAtUtc.HasValue
            ? (int)Math.Max(0, (now - _sessionStartedAtUtc.Value).TotalSeconds)
            : 0;

        return new SessionTelemetry(
            SyncReason: reason,
            PluginVersion: PluginInterface.Manifest.AssemblyVersion,
            SessionStartedAtUtc: _sessionStartedAtUtc?.UtcDateTime.ToString("O"),
            SessionDurationSec: Math.Min(86_400, sessionDurationSec),
            DailyPlaytimeSec: _dailyPlaytimeSec,
            ZoneChanges: _zoneChangesThisSession);
    }

    private async Task TriggerSync(string reason)
    {
        try
        {
            _log.Debug($"[XIVPath] Déclenchement synchro ({reason})");
            var telemetry = BuildTelemetry(reason);
            var result = await _sync.SyncAsync(_config.ApiToken, _config.XIVPathUrl, telemetry);
            _log.Information($"[XIVPath] {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[XIVPath] Erreur de synchro");
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Login -= OnLogin;
        _commands.RemoveHandler("/xivpath");
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        _sync.Dispose();
    }
}
