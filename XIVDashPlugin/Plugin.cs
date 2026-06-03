using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
        if (!_config.EnableDetailedGameplaySignals)
            return null;

        var activeJobId = GetActiveJobId();
        var activeRole = ResolveRole(activeJobId);
        var territoryId = _clientState.TerritoryType;
        var partySize = _partyList.Length;
        var inParty = partySize > 1;

        var trackedQuestId = GetTrackedQuestId();
        // questSeries est résolu côté serveur (lookup BD par trackedQuestId).
        string? questSeries = null;

        // Roulettes : best-effort via UIState. Si l'API change/échoue, on renvoie
        // null et le serveur traite ça comme "inconnu" (pas "non fait").
        var (rLeveling, rTrials, rAlliance, rTotal) = GetDailyRouletteFlags();

        // `lastDutyType` : on n'a pas encore de hook duty fini → null. Sera renseigné
        // dans une prochaine itération avec IDutyState.
        string? lastDutyType = null;

        return new GameplaySignals(
            ActiveJobId: activeJobId,
            ActiveRole: activeRole,
            TerritoryId: territoryId,
            InParty: inParty,
            PartySize: partySize,
            TrackedQuestId: trackedQuestId,
            QuestSeries: questSeries,
            RouletteLevelingDoneToday: rLeveling,
            RouletteTrialsDoneToday: rTrials,
            RouletteAllianceDoneToday: rAlliance,
            RoulettesDoneToday: rTotal,
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

    /// <summary>
    /// Job actuellement équipé (RowId XIV Excel `ClassJob`).
    /// `PlayerState.Instance()->CurrentClassJob` est un byte, déjà au format RowId.
    /// </summary>
    private static unsafe int? GetActiveJobId()
    {
        try
        {
            var ps = PlayerState.Instance();
            if (ps == null) return null;
            var jobId = (int)ps->CurrentClassJob;
            return jobId > 0 ? jobId : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Quête actuellement suivie dans le journal (premier slot non vide).
    /// FFXIVClientStructs : `QuestManager.Instance()->TrackedQuests` (Span fixe).
    /// L'`Id` stocké est un ushort (= rowId & 0xFFFF) ; on remappe vers l'id XIVAPI
    /// complet en ré-ajoutant le préfixe 0x10000 (cohérent avec QuestIdMin=65536).
    /// </summary>
    private static unsafe uint? GetTrackedQuestId()
    {
        try
        {
            var qm = QuestManager.Instance();
            if (qm == null) return null;
            var tracked = qm->TrackedQuests;
            for (var i = 0; i < tracked.Length; i++)
            {
                var slot = tracked[i];
                var rawId = slot.QuestId;
                if (rawId == 0) continue;
                return ((uint)rawId) | 0x10000u;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lecture des compteurs de roulettes daily (best-effort).
    /// `PlayerState.Instance()->IsContentRouletteCompleted(byte)` selon
    /// FFXIVClientStructs récents — IDs roulettes : 1=Leveling, 6=Trials,
    /// 15=Alliance (table `ContentRoulette`). Si l'API n'est pas dispo, on
    /// renvoie (null, null, null, null) sans casser le sync.
    /// </summary>
    private static unsafe (bool? leveling, bool? trials, bool? alliance, int? total) GetDailyRouletteFlags()
    {
        try
        {
            var ps = PlayerState.Instance();
            if (ps == null) return (null, null, null, null);

            bool leveling = ps->IsContentRouletteCompleted(1);
            bool trials = ps->IsContentRouletteCompleted(6);
            bool alliance = ps->IsContentRouletteCompleted(15);
            int total = (leveling ? 1 : 0) + (trials ? 1 : 0) + (alliance ? 1 : 0);
            return (leveling, trials, alliance, total);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

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
