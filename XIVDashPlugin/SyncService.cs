using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace XIVDashPlugin;

public sealed class SyncService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IDataManager _dataManager;

    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    // ContentType IDs for duties we care about (from ContentType sheet)
    // 2 = Dungeon, 4 = Trial, 5 = Raid, 9 = Alliance Raid, 28 = Ultimate
    private static readonly HashSet<uint> TrackedContentTypes = [2, 4, 5, 9, 28];

    public event Action<string>? OnStatus;

    public SyncService(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public Task<SyncResult> SyncAsync(string token, string baseUrl)
    {
        // Read game data synchronously on the calling (game) thread before going async
        var completedIds    = GetCompletedQuestIds();
        var jobs            = GetJobLevels();
        var unlockedContent = GetUnlockedContentNames();

        return Task.Run(async () =>
        {
            var payload = new
            {
                completedQuestIds = completedIds,
                jobs,
                unlockedContent,
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = baseUrl.TrimEnd('/') + "/api/dalamud/sync";
            var response = await _http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new SyncResult(false, $"Erreur {(int)response.StatusCode}", completedIds.Count, jobs.Count, unlockedContent.Count);

            var questWord   = completedIds.Count == 1 ? "quête" : "quêtes";
            var jobWord     = jobs.Count == 1 ? "job" : "jobs";
            var contentWord = unlockedContent.Count == 1 ? "activité" : "activités";
            return new SyncResult(
                true,
                $"Synchro OK — {completedIds.Count} {questWord}, {jobs.Count} {jobWord}, {unlockedContent.Count} {contentWord}",
                completedIds.Count, jobs.Count, unlockedContent.Count);
        });
    }

    private static unsafe List<uint> GetCompletedQuestIds()
    {
        var completedIds = new List<uint>();
        for (uint id = QuestIdMin; id <= QuestIdMax; id++)
        {
            if (QuestManager.IsQuestComplete((ushort)(id & 0xFFFF)))
                completedIds.Add(id);
        }
        return completedIds;
    }

    private static unsafe List<object> GetJobLevels()
    {
        var jobs = new List<object>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return jobs;

        var levels = playerState->ClassJobLevels;
        foreach (var (expIdx, abbrev) in GetClassJobMap())
        {
            if (expIdx < levels.Length)
            {
                var level = levels[expIdx];
                if (level > 0)
                    jobs.Add(new { abbrev, level = (int)level });
            }
        }
        return jobs;
    }

    /// <summary>
    /// Lit les ContentFinderCondition débloquées par le joueur.
    /// Filtre sur les types Donjon, Défi, Raid, Alliance, Ultimate.
    /// Utilise UIState.IsUnlockLinkUnlocked pour vérifier l'accès.
    /// </summary>
    private unsafe List<string> GetUnlockedContentNames()
    {
        var result = new List<string>();
        try
        {
            var sheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return result;

            var uiState = UIState.Instance();
            if (uiState == null) return result;

            foreach (var row in sheet)
            {
                // Skip rows without a name or unlock link
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Filter to duty types we track
                if (!TrackedContentTypes.Contains(row.ContentType.RowId)) continue;

                // Check if unlocked — UnlockLink.RowId == 0 means always available
                var unlinkId = row.UnlockLink.RowId;
                if (unlinkId == 0 || uiState->IsUnlockLinkUnlocked(unlinkId))
                {
                    result.Add(name);
                }
            }
        }
        catch
        {
            // Non-fatal — sync continues without duty data
        }
        return result;
    }

    // ExpArrayIndex → job abbreviation (verified via XIVAPI)
    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [0]  = "MNK",  [1]  = "PLD",  [2]  = "WAR",  [3]  = "BRD",
        [4]  = "DRG",  [5]  = "BLM",  [6]  = "WHM",  [7]  = "CRP",
        [8]  = "BSM",  [9]  = "ARM",  [10] = "GSM",  [11] = "LTW",
        [12] = "WVR",  [13] = "ALC",  [14] = "CUL",  [15] = "MIN",
        [16] = "BTN",  [17] = "FSH",  [18] = "SMN",  [19] = "NIN",
        [20] = "MCH",  [21] = "DRK",  [22] = "AST",  [23] = "SAM",
        [24] = "RDM",  [25] = "BLU",  [26] = "GNB",  [27] = "DNC",
        [28] = "RPR",  [29] = "SGE",  [30] = "VPR",  [31] = "PCT",
    };

    public void Dispose() => _http.Dispose();
}

public record SyncResult(bool Success, string Message, int QuestCount, int JobCount, int ContentCount);
