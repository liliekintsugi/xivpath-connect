using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace XIVPathPlugin;

public sealed class SyncService : IDisposable
{
    private readonly HttpClient  _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly IDataManager _dataManager;

    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    // ContentType IDs we care about for completion tracking
    // 2=Dungeons  3=Guildhests  4=Trials  5=Raids (normal + alliance)  28=Ultimate
    private static readonly HashSet<uint> RelevantContentTypes = [2, 3, 4, 5, 28];

    public SyncService(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public Task<SyncResult>(
        string token,
        string baseUrl,
        SessionTelemetry? telemetry = null,
        GameplaySignals? gameplay = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(new SyncResult(false, "URL invalide (HTTPS requis)", 0, 0, 0, 0));

        var completedQuests = GetCompletedQuestIds();
        var jobs = GetJobLevels();
        var completedContent = GetCompletedContentIdsByType();

        return Task.Run(async () =>
        {
            if (!await _syncLock.WaitAsync(0))
                return new SyncResult(false, "Synchro déjà en cours", 0, 0, 0, 0);

            try
            {
                var syncKey = BuildSyncKey(telemetry?.SyncReason);
                var payload = new
                {
                    syncKey,
                    completedQuests = completedQuests,
                    jobs,
                    completedDungeons = completedContent.Dungeons,
                    completedTrials = completedContent.Trials,
                    completedRaids = completedContent.Raids,
                    completedGuildhests = completedContent.Guildhests,
                    telemetry,
                    gameplay,
                };
                var json = JsonSerializer.Serialize(payload);
                using var bodyContent = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl.TrimEnd('/') + "/api/sync/dalamud");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = bodyContent;

                var response = await _http.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return new SyncResult(false, BuildHttpErrorMessage(response, responseText),
                        completedQuests.Count, jobs.Count,
                        completedContent.Dungeons.Count, completedContent.Trials.Count, completedContent.Raids.Count, completedContent.Guildhests.Count);

                var parsed = ParseSyncSummary(responseText);
                if (parsed == null)
                {
                    var sentContentCount = completedContent.Dungeons.Count + completedContent.Trials.Count + completedContent.Raids.Count + completedContent.Guildhests.Count;
                    return new SyncResult(true,
                        $"Synchro OK (résumé serveur indisponible) — envoyé: {completedQuests.Count} quêtes, {jobs.Count} jobs, {sentContentCount} contenus",
                        completedQuests.Count, jobs.Count,
                        completedContent.Dungeons.Count, completedContent.Trials.Count, completedContent.Raids.Count, completedContent.Guildhests.Count);
                }

                var effectiveContentCount = parsed.DungeonsMarked + parsed.TrialsMarked + parsed.RaidsMarked + parsed.GuildhestsMarked;
                var qWord = parsed.QuestsMarked == 1 ? "quête" : "quêtes";
                var jWord = parsed.JobsUpdated == 1 ? "job" : "jobs";
                var cWord = effectiveContentCount == 1 ? "contenu" : "contenus";
                var ignoredInfo = parsed.SkippedUnknown > 0 ? $" (ignorés: {parsed.SkippedUnknown})" : string.Empty;
                return new SyncResult(true,
                    $"Synchro OK — pris en compte: {parsed.QuestsMarked} {qWord}, {parsed.JobsUpdated} {jWord}, {effectiveContentCount} {cWord}{ignoredInfo}",
                    parsed.QuestsMarked, parsed.JobsUpdated,
                    parsed.DungeonsMarked, parsed.TrialsMarked, parsed.RaidsMarked, parsed.GuildhestsMarked);
            }
            finally
            {
                _syncLock.Release();
            }
        });
    }

    private static unsafe List<uint> GetCompletedQuestIds()
    {
        var ids = new List<uint>();
        for (uint id = QuestIdMin; id <= QuestIdMax; id++)
            if (QuestManager.IsQuestComplete((ushort)(id & 0xFFFF)))
                ids.Add(id);
        return ids;
    }

    private static unsafe List<object> GetJobLevels()
    {
        var jobs        = new List<object>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return jobs;

        var levels = playerState->ClassJobLevels;
        foreach (var (expIdx, abbrev) in GetClassJobMap())
        {
            if (expIdx < levels.Length)
            {
                var level = levels[expIdx];
                if (level > 0)
                {
                    var jobId = ResolveLegacyAbbrevToJobId(abbrev);
                    if (jobId > 0) jobs.Add(new { id = jobId, level = (int)level });
                }
            }
        }
        return jobs;
    }

    /// <summary>
    /// Retourne les ContentFinderCondition row IDs (= XIVAPI IDs) des instances
    /// complétées au moins une fois.
    ///
    /// Utilise UIState.IsInstanceContentCompleted(instanceContentId) — la méthode
    /// correcte, vérifiée dans FFXIVClientStructs. Le paramètre attendu est l'ID
    /// de la feuille InstanceContent, qui diffère du CFC row ID (ex. : Labyrinth
    /// of the Ancients a CFC=92 mais InstanceContent=30001). On obtient ce mapping
    /// via Lumina : ContentFinderCondition.Content.RowId.
    /// </summary>
    private unsafe CompletedContentIds GetCompletedContentIdsByType()
    {
        var dungeons = new List<uint>();
        var trials = new List<uint>();
        var raids = new List<uint>();
        var guildhests = new List<uint>();

        var cfcSheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null) return new CompletedContentIds(dungeons, trials, raids, guildhests);

        foreach (var row in cfcSheet)
        {
            // Filtrer sur les types pertinents
            if (!RelevantContentTypes.Contains(row.ContentType.RowId)) continue;

            // N'inclure que le contenu visible dans le Duty Finder
            if (!row.IsInDutyFinder) continue;

            // Récupérer l'InstanceContent ID (≠ CFC row ID dans le cas général)
            var instanceContentId = row.Content.RowId;
            if (instanceContentId == 0) continue;

            if (!UIState.IsInstanceContentCompleted(instanceContentId)) continue;

            // on envoie le CFC row ID = xivapi_id
            switch (row.ContentType.RowId)
            {
                case 2:
                    dungeons.Add(row.RowId);
                    break;
                case 3:
                    guildhests.Add(row.RowId);
                    break;
                case 4:
                    trials.Add(row.RowId);
                    break;
                case 5:
                case 28:
                    raids.Add(row.RowId);
                    break;
            }
        }
        return new CompletedContentIds(dungeons, trials, raids, guildhests);
    }

    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [0]  = "MNK", [1]  = "PLD", [2]  = "WAR", [3]  = "BRD",
        [4]  = "DRG", [5]  = "BLM", [6]  = "WHM", [7]  = "CRP",
        [8]  = "BSM", [9]  = "ARM", [10] = "GSM", [11] = "LTW",
        [12] = "WVR", [13] = "ALC", [14] = "CUL", [15] = "MIN",
        [16] = "BTN", [17] = "FSH", [18] = "SMN", [19] = "NIN",
        [20] = "MCH", [21] = "DRK", [22] = "AST", [23] = "SAM",
        [24] = "RDM", [25] = "BLU", [26] = "GNB", [27] = "DNC",
        [28] = "RPR", [29] = "SGE", [30] = "VPR", [31] = "PCT",
    };

    private static int ResolveLegacyAbbrevToJobId(string abbrev) => abbrev switch
    {
        "GLA" => 1,
        "PGL" => 2,
        "MRD" => 3,
        "LNC" => 4,
        "ARC" => 5,
        "CNJ" => 6,
        "THM" => 7,
        "CRP" => 8,
        "BSM" => 9,
        "ARM" => 10,
        "GSM" => 11,
        "LTW" => 12,
        "WVR" => 13,
        "ALC" => 14,
        "CUL" => 15,
        "MIN" => 16,
        "BTN" => 17,
        "FSH" => 18,
        "PLD" => 19,
        "MNK" => 20,
        "WAR" => 21,
        "DRG" => 22,
        "BRD" => 23,
        "WHM" => 24,
        "BLM" => 25,
        "ACN" => 26,
        "SMN" => 27,
        "SCH" => 28,
        "ROG" => 29,
        "NIN" => 30,
        "MCH" => 31,
        "DRK" => 32,
        "AST" => 33,
        "SAM" => 34,
        "RDM" => 35,
        "BLU" => 36,
        "GNB" => 37,
        "DNC" => 38,
        "RPR" => 39,
        "SGE" => 40,
        "VPR" => 41,
        "PCT" => 42,
        _ => 0,
    };

    private static string BuildHttpErrorMessage(HttpResponseMessage response, string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return $"Erreur {(int)response.StatusCode}";

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
                return $"Erreur {(int)response.StatusCode} — {error.GetString()}";
        }
        catch (JsonException)
        {
            // Ignore parse errors and fallback to status code only.
        }

        return $"Erreur {(int)response.StatusCode}";
    }

    private static string BuildSyncKey(string? reason)
    {
        Span<byte> randomBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(randomBytes);
        var entropy = Convert.ToHexString(randomBytes).ToLowerInvariant();
        var safeReason = string.IsNullOrWhiteSpace(reason) ? "sync" : reason.Trim().ToLowerInvariant();
        return $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{safeReason}:{entropy}";
    }

    private static SyncSummary? ParseSyncSummary(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<SyncApiResponse>(responseText);
            if (payload?.Summary == null)
                return null;
            return payload.Summary;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _syncLock.Dispose();
    }
}

public record CompletedContentIds(
    List<uint> Dungeons,
    List<uint> Trials,
    List<uint> Raids,
    List<uint> Guildhests
);

public record SyncResult(
    bool   Success,
    string Message,
    int    QuestCount,
    int    JobCount,
    int    DungeonCount = 0,
    int    TrialCount = 0,
    int    RaidCount = 0,
    int    GuildhestCount = 0);

public record SessionTelemetry(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("syncReason")] string SyncReason,
    [property: JsonPropertyName("pluginVersion")] string? PluginVersion,
    [property: JsonPropertyName("sessionStartedAtUtc")] string? SessionStartedAtUtc,
    [property: JsonPropertyName("sessionDurationSec")] int SessionDurationSec,
    [property: JsonPropertyName("dailyPlaytimeSec")] int DailyPlaytimeSec,
    [property: JsonPropertyName("zoneChanges")] int ZoneChanges,
    [property: JsonPropertyName("manualSyncCount")] int ManualSyncCount
);

public record GameplaySignals(
    [property: JsonPropertyName("activeJobId")] int? ActiveJobId,
    [property: JsonPropertyName("activeRole")] string? ActiveRole,
    [property: JsonPropertyName("territoryId")] uint TerritoryId,
    [property: JsonPropertyName("inParty")] bool InParty,
    [property: JsonPropertyName("partySize")] int PartySize,
    [property: JsonPropertyName("trackedQuestId")] uint? TrackedQuestId,
    [property: JsonPropertyName("questSeries")] string? QuestSeries,
    [property: JsonPropertyName("rouletteLevelingDoneToday")] bool? RouletteLevelingDoneToday,
    [property: JsonPropertyName("rouletteTrialsDoneToday")] bool? RouletteTrialsDoneToday,
    [property: JsonPropertyName("rouletteAllianceDoneToday")] bool? RouletteAllianceDoneToday,
    [property: JsonPropertyName("roulettesDoneToday")] int? RoulettesDoneToday,
    [property: JsonPropertyName("lastDutyType")] string? LastDutyType
);

public sealed class SyncApiResponse
{
    [JsonPropertyName("summary")]
    public SyncSummary? Summary { get; init; }
}

public sealed class SyncSummary
{
    [JsonPropertyName("jobsUpdated")]
    public int JobsUpdated { get; init; }

    [JsonPropertyName("questsMarked")]
    public int QuestsMarked { get; init; }

    [JsonPropertyName("dungeonsMarked")]
    public int DungeonsMarked { get; init; }

    [JsonPropertyName("trialsMarked")]
    public int TrialsMarked { get; init; }

    [JsonPropertyName("raidsMarked")]
    public int RaidsMarked { get; init; }

    [JsonPropertyName("guildhestsMarked")]
    public int GuildhestsMarked { get; init; }

    [JsonPropertyName("skippedUnknown")]
    public int SkippedUnknown { get; init; }
}
