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
        _configWindow = new ConfigWindow(_config, _sync, BuildGameplaySignals);
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
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync("login");
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync($"zone:{territory}");
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
            RoulettesDoneToday: rTotal
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
    /// FFXIVClientStructs: `PlayerState.CurrentClassJobId` (byte @ 0x7E).
    /// </summary>
    private static unsafe int? GetActiveJobId()
    {
        try
        {
            var ps = PlayerState.Instance();
            if (ps == null) return null;
            var jobId = (int)ps->CurrentClassJobId;
            return jobId > 0 ? jobId : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Quête actuellement suivie dans le journal (premier slot non vide).
    /// FFXIVClientStructs: <c>QuestManager.TrackedQuests</c> contient des
    /// <c>TrackingWork</c> (QuestType + Index). Index pointe dans
    /// <c>NormalQuests</c> (pour QuestType=0) qui contient l'`QuestId` (ushort).
    /// On remappe vers l'id XIVAPI complet en ajoutant 0x10000 (cohérent avec
    /// le rang [65536..72000] utilisé dans GetCompletedQuestIds).
    /// </summary>
    private static unsafe uint? GetTrackedQuestId()
    {
        try
        {
            var qm = QuestManager.Instance();
            if (qm == null) return null;
            var tracked = qm->TrackedQuests;
            var normal = qm->NormalQuests;
            for (var i = 0; i < tracked.Length; i++)
            {
                var slot = tracked[i];
                // QuestType 0 = quête normale (les autres = daily/leve, ignorés).
                if (slot.QuestType != 0) continue;
                var idx = slot.Index;
                if (idx >= normal.Length) continue;
                var rawId = normal[idx].QuestId;
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
    /// Roulettes daily complétées. FFXIVClientStructs commente PlayerState :
    /// "Use InstanceContent.IsRouletteComplete". IDs ContentRoulette :
    /// 1=Leveling, 6=Trials, 15=Alliance (sheet `ContentRoulette`).
    /// </summary>
    private static unsafe (bool? leveling, bool? trials, bool? alliance, int? total) GetDailyRouletteFlags()
    {
        try
        {
            var ic = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent.Instance();
            if (ic == null) return (null, null, null, null);

            bool leveling = ic->IsRouletteComplete(1);
            bool trials = ic->IsRouletteComplete(6);
            bool alliance = ic->IsRouletteComplete(15);
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
            var gameplay = BuildGameplaySignals();
            var result = await _sync.SyncAsync(_config.ApiToken, _config.XIVPathUrl, reason, gameplay);
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
