using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XIVDashPlugin;

public sealed class SyncService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // FFXIV quest IDs start at 65536 (0x10000). We scan up to ~72000 to cover all content.
    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    public event Action<string>? OnStatus;

    public unsafe Task<SyncResult> SyncAsync(string token, string baseUrl)
    {
        return Task.Run(async () =>
        {
            // ── Completed quest IDs ──────────────────────────────────────────
            var completedIds = new List<uint>();
            for (uint id = QuestIdMin; id <= QuestIdMax; id++)
            {
                if (QuestManager.IsQuestComplete((ushort)(id & 0xFFFF)))
                    completedIds.Add(id);
            }

            // ── Job levels via PlayerState ────────────────────────────────────
            var jobs = new List<object>();
            var playerState = PlayerState.Instance();
            if (playerState != null)
            {
                // ClassJob RowId → abbrev mapping (covers all 33 jobs)
                var classJobMap = GetClassJobMap();
                foreach (var (rowId, abbrev) in classJobMap)
                {
                    var level = playerState->ClassJobLevelArray[rowId];
                    if (level > 0)
                        jobs.Add(new { abbrev, level = (int)level });
                }
            }

            // ── POST to XIVDash ──────────────────────────────────────────────
            var payload = new
            {
                completedQuestIds = completedIds,
                jobs,
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = baseUrl.TrimEnd('/') + "/api/dalamud/sync";
            var response = await _http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new SyncResult(false, $"Erreur {(int)response.StatusCode}: {body}", completedIds.Count, jobs.Count);

            return new SyncResult(true, $"Synchro OK — {completedIds.Count} quêtes, {jobs.Count} jobs", completedIds.Count, jobs.Count);
        });
    }

    // ClassJob RowId (from the game's sheet) → abbreviation
    // Covers all 33 combat/crafting/gathering jobs as of Dawntrail
    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [1]  = "GLA", // Gladiateur (→PLD)
        [2]  = "PGL", // Pugiliste (→MNK)
        [3]  = "MRD", // Maraudeur (→WAR)
        [4]  = "LNC", // Lancier (→DRG)
        [5]  = "ARC", // Archer (→BRD)
        [6]  = "CNJ", // Élémentaliste (→WHM)
        [7]  = "THM", // Thaumaturge (→BLM)
        [8]  = "CRP",
        [9]  = "BSM",
        [10] = "ARM",
        [11] = "GSM",
        [12] = "LTW",
        [13] = "WVR",
        [14] = "ALC",
        [15] = "CUL",
        [16] = "MIN",
        [17] = "BTN",
        [18] = "FSH",
        [19] = "PLD",
        [20] = "MNK",
        [21] = "WAR",
        [22] = "DRG",
        [23] = "BRD",
        [24] = "WHM",
        [25] = "BLM",
        [26] = "SMN",
        [27] = "SCH",
        [28] = "ROG", // Surineur (→NIN)
        [29] = "NIN",
        [30] = "MCH",
        [31] = "DRK",
        [32] = "AST",
        [33] = "SAM",
        [34] = "RDM",
        [35] = "BLU",
        [36] = "GNB",
        [37] = "DNC",
        [38] = "RPR",
        [39] = "SGE",
        [40] = "VPR",
        [41] = "PCT",
    };

    public void Dispose() => _http.Dispose();
}

public record SyncResult(bool Success, string Message, int QuestCount, int JobCount);
