using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Today;
using LiveWallpaper.Native;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Today;

public sealed class WeeekClient
{
    private const string ApiBase = "https://api.weeek.net/public/v1/";

    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    public WeeekClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public static bool HasToken() => CredentialManager.Exists(CredentialManager.WeeekTarget);

    private sealed record ColumnInfo(string Name, string Board, int Order);

    private Dictionary<long, ColumnInfo>? _columns;
    private DateTime _columnsLoadedAt = DateTime.MinValue;
    private long? _workspaceId;

    /// <summary>
    /// Workspace id for the current token (<c>GET /ws</c>); needed to build task links
    /// (<c>https://app.weeek.net/ws/{workspaceId}/task/{taskId}</c>). Cached for the process lifetime.
    /// </summary>
    public async Task<long?> GetWorkspaceIdAsync(CancellationToken ct)
    {
        if (_workspaceId.HasValue) return _workspaceId;
        var token = CredentialManager.ReadSecret(CredentialManager.WeeekTarget)?.Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        using var doc = await GetAsync(token, "ws", ct);
        if (doc.RootElement.TryGetProperty("workspace", out var ws) &&
            ws.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            _workspaceId = id.GetInt64();
        }
        return _workspaceId;
    }

    /// <summary>Task page in the WEEEK web app, or null when the workspace is unknown.</summary>
    public static Uri? BuildTaskUri(long workspaceId, string taskId)
    {
        // Ids are numeric in the public API; refuse anything else so no arbitrary text ends up in a URL.
        if (!long.TryParse(taskId, out var n) || n <= 0) return null;
        return new Uri($"https://app.weeek.net/ws/{workspaceId}/task/{n}");
    }

    /// <summary>
    /// Open tasks across all visible projects. Tasks in the client's workspace usually have no
    /// assignee or due date, so "my tasks for today" is empty — we take what's on the boards instead,
    /// ordered by workflow column ("В работе" → "На проверку" → "К работе" → rest).
    /// Kept under the old name for TodayService.
    /// </summary>
    public async Task<IReadOnlyList<TodayTask>> GetTodaysTasksAsync(int maxTasks, CancellationToken ct)
    {
        var token = CredentialManager.ReadSecret(CredentialManager.WeeekTarget)?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return Array.Empty<TodayTask>();

        maxTasks = Math.Clamp(maxTasks, 1, 50);
        var columns = await GetColumnsAsync(token, ct);

        var projects = await GetArrayAsync(token, "tm/projects", "projects", ct);
        var all = new List<(TodayTask Task, int Rank, int Order, DateTime? Due, long Id)>();

        foreach (var project in projects)
        {
            if (!project.TryGetProperty("id", out var pid)) continue;
            if (project.TryGetProperty("isArchived", out var arch) && arch.ValueKind == JsonValueKind.True) continue;

            for (var offset = 0; offset < 500; offset += 100)
            {
                using var doc = await GetAsync(token, $"tm/tasks?projectId={pid}&completed=0&perPage=100&offset={offset}", ct);
                if (!doc.RootElement.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array) break;

                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("isCompleted", out var c) && c.ValueKind == JsonValueKind.True) continue;
                    if (item.TryGetProperty("isDeleted", out var del) && del.ValueKind == JsonValueKind.True) continue;
                    if (item.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.Number) continue; // subtasks
                    var title = item.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    var colId = ReadColumnId(item);
                    ColumnInfo? col = null;
                    if (colId.HasValue) columns.TryGetValue(colId.Value, out col);
                    var rank = Rank(col?.Name);
                    if (rank >= 90) continue; // "Готово", "Сложно/Невозможно", notes

                    int? priority = item.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
                    var due = ReadDue(item);
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : Guid.NewGuid().ToString("N");

                    all.Add((new TodayTask
                    {
                        Id = id,
                        Title = StripHtml(title).Trim(),
                        Priority = priority,
                        IsCompleted = false,
                        Board = col?.Board,
                        Column = col?.Name,
                        // Board / column are sent separately now; the label is a real due date only.
                        DueLabel = due.HasValue
                            ? due.Value.ToString("d MMM", new CultureInfo("ru-RU")).Replace(".", string.Empty)
                            : null,
                    }, rank, col?.Order ?? 0, due, long.TryParse(id, out var n) ? n : 0));
                }

                if (!(doc.RootElement.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True)) break;
            }
        }

        return all
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Due ?? DateTime.MaxValue)
            .ThenByDescending(t => t.Task.Priority ?? -1)
            .ThenByDescending(t => t.Id)
            .Take(maxTasks)
            .Select(t => t.Task)
            .ToList();
    }

    /// <summary>Lower is more urgent. ≥ 90 means "don't show".</summary>
    private static int Rank(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return 50;
        var c = column.ToLowerInvariant();
        if (c.Contains("готов") || c.Contains("done") || c.Contains("невозможно") || c.Contains("архив")) return 99;
        if (c.Contains("заметк") || c.Contains("информац")) return 95;
        if (c.Contains("в работе") || c.Contains("in progress")) return 0;
        if (c.Contains("провер") || c.Contains("review")) return 10;
        if (c.Contains("к работе") || c.Contains("todo") || c.Contains("to do")) return 20;
        return 40;
    }

    private static long? ReadColumnId(JsonElement item)
    {
        if (item.TryGetProperty("boardColumnId", out var b) && b.ValueKind == JsonValueKind.Number) return b.GetInt64();
        if (item.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
            foreach (var l in locs.EnumerateArray())
                if (l.TryGetProperty("boardColumnId", out var lc) && lc.ValueKind == JsonValueKind.Number) return lc.GetInt64();
        return null;
    }

    private static DateTime? ReadDue(JsonElement item)
    {
        foreach (var key in new[] { "dueDate", "date", "dateEnd" })
        {
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                return d;
        }
        return null;
    }

    private async Task<Dictionary<long, ColumnInfo>> GetColumnsAsync(string token, CancellationToken ct)
    {
        if (_columns != null && DateTime.UtcNow - _columnsLoadedAt < TimeSpan.FromHours(1)) return _columns;
        var map = new Dictionary<long, ColumnInfo>();
        foreach (var project in await GetArrayAsync(token, "tm/projects", "projects", ct))
        {
            if (!project.TryGetProperty("id", out var pid)) continue;
            foreach (var board in await GetArrayAsync(token, $"tm/boards?projectId={pid}", "boards", ct))
            {
                if (!board.TryGetProperty("id", out var bid)) continue;
                var boardName = board.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";
                var order = 0;
                foreach (var col in await GetArrayAsync(token, $"tm/board-columns?boardId={bid}", "boardColumns", ct))
                {
                    if (col.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.Number)
                        map[cid.GetInt64()] = new ColumnInfo(col.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "", boardName, order++);
                }
            }
        }
        _columns = map;
        _columnsLoadedAt = DateTime.UtcNow;
        return map;
    }

    private async Task<List<JsonElement>> GetArrayAsync(string token, string relativeUrl, string prop, CancellationToken ct)
    {
        using var doc = await GetAsync(token, relativeUrl, ct);
        var list = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray()) list.Add(x.Clone());
        return list;
    }

    private async Task<JsonDocument> GetAsync(string token, string relativeUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("WEEEK {Url} HTTP {Status}: {Body}", relativeUrl.Split('?')[0], (int)resp.StatusCode, Trim(body));
            throw new InvalidOperationException($"WEEEK HTTP {(int)resp.StatusCode}");
        }
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            doc.Dispose();
            throw new InvalidOperationException("WEEEK returned success=false");
        }
        return doc;
    }

    private static string StripHtml(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", string.Empty);

    private static string Trim(string s) =>
        s.Length <= 180 ? s : s[..180] + "…";
}
