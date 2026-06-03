using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using XIVPathPlugin.Windows;

namespace XIVPathPlugin;

public sealed class Plugin : IDalamudPlugin
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly ICommandManager _commands;
    private readonly IClientState _clientState;
    private readonly IPartyList _partyList;
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
    private int _manualSyncCountThisSession;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IClientState clientState,
        IPartyList partyList,
        IPluginLog log,
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        _commands = commands;
        _clientState = clientState;
        _partyList = partyList;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _sync = new SyncService(dataManager);
        _windowSystem = new WindowSystem("XIVPathPlugin");
        _configWindow = new ConfigWindow(
            _config,
            _sync,
            () => BuildTelemetry("manual"),
            BuildGameplaySignals);
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

        if (_clientState.IsLoggedIn)
            StartSession();
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
        _manualSyncCountThisSession = 0;
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

        if (!_sessionStartedAtUtc.HasValue)
            StartSession();

        var now = DateTimeOffset.UtcNow;
        UpdateDailyPlaytime(now);
        if (string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase))
            _manualSyncCountThisSession++;
        var sessionDurationSec = _sessionStartedAtUtc.HasValue
            ? (int)Math.Max(0, (now - _sessionStartedAtUtc.Value).TotalSeconds)
            : 0;

        return new SessionTelemetry(
            Version: "v1-plugin-first",
            SyncReason: reason,
            PluginVersion: PluginInterface.Manifest.AssemblyVersion?.ToString(),
            SessionStartedAtUtc: _sessionStartedAtUtc?.UtcDateTime.ToString("O"),
            SessionDurationSec: Math.Min(86_400, sessionDurationSec),
            DailyPlaytimeSec: _dailyPlaytimeSec,
            ZoneChanges: _zoneChangesThisSession,
            ManualSyncCount: _manualSyncCountThisSession);
    }

    private GameplaySignals? BuildGameplaySignals()
    {
        if (!_config.EnableSessionTelemetry || !_config.EnableDetailedGameplaySignals)
            return null;

        var player = _clientState.LocalPlayer;
        var activeJobId = player?.ClassJob.RowId > 0 ? (int?)player.ClassJob.RowId : null;
        var activeRole = ResolveRole(activeJobId);
        var territoryId = _clientState.TerritoryType;
        var partySize = _partyList.Length;
        var inParty = partySize > 1;

        var trackedQuestId = GetTrackedQuestId();
        var questSeries = trackedQuestId.HasValue ? InferQuestSeriesFromTrackedQuest(trackedQuestId.Value) : null;

        // Roulette/duty signals can be added with explicit duty events in a next pass.
        bool? rouletteLevelingDoneToday = null;
        bool? rouletteTrialsDoneToday = null;
        bool? rouletteAllianceDoneToday = null;
        int? roulettesDoneToday = null;
        string? lastDutyType = null;

        return new GameplaySignals(
            ActiveJobId: activeJobId,
            ActiveRole: activeRole,
            TerritoryId: territoryId,
            InParty: inParty,
            PartySize: partySize,
            TrackedQuestId: trackedQuestId,
            QuestSeries: questSeries,
            RouletteLevelingDoneToday: rouletteLevelingDoneToday,
            RouletteTrialsDoneToday: rouletteTrialsDoneToday,
            RouletteAllianceDoneToday: rouletteAllianceDoneToday,
            RoulettesDoneToday: roulettesDoneToday,
            LastDutyType: lastDutyType
        );
    }

    private static string? ResolveRole(int? jobId) => jobId switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => "tank",
        6 or 24 or 28 or 33 or 40 => "healer",
        8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 => "crafter",
        16 or 17 or 18 => "gatherer",
        > 0 => "dps",
        _ => null,
    };

    private static unsafe uint? GetTrackedQuestId()
    {
        // TODO: replace with exact tracked-quest API when exposed reliably in Dalamud service layer.
        _ = QuestManager.Instance();
        return null;
    }

    private static string? InferQuestSeriesFromTrackedQuest(uint trackedQuestId) =>
        trackedQuestId switch
        {
            >= 65536 and <= 72000 => "msq_or_annex_arr",
            _ => null,
        };

    private async Task TriggerSync(string reason)
    {
        try
        {
            _log.Debug($"[XIVPath] Déclenchement synchro ({reason})");
            var telemetry = BuildTelemetry(reason);
            var gameplay = BuildGameplaySignals();
            var result = await _sync.SyncAsync(_config.ApiToken, _config.XIVPathUrl, telemetry, gameplay);
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
