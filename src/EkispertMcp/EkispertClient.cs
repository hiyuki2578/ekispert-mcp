using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace EkispertMcp;

/// <summary>
/// 駅すぱあと API（api.ekispert.jp）を呼ぶクライアント。アクセスキーは環境変数 EKISPERT_API_KEY から読む。
/// 応答は JSON のまま返し、解析は呼び出し側で <see cref="Json"/> の補助関数を使って行う。
/// </summary>
public sealed class EkispertClient(IHttpClientFactory httpClientFactory, ILogger<EkispertClient> logger)
{
    public const string HttpClientName = "ekispert";
    public const string BaseUrl = "https://api.ekispert.jp/v1/json/";
    public const string ApiKeyVariable = "EKISPERT_API_KEY";

    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // 探索条件の生成は同じ組み合わせが繰り返し来るので、結果を覚えておく（条件の書式はデータ更新では変わらない）
    private readonly ConcurrentDictionary<string, string> _conditionCache = new();

    public static DateTime NowJst() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).DateTime;

    // ---- 駅簡易情報（課金対象外） ----

    public Task<JsonElement> StationLightAsync(StationQuery query, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("name", query.Name), ("nameMatchType", query.Partial ? "partial" : "forward") };
        if (query.Types.Count > 0) p.Add(("type", string.Join(":", query.Types)));
        if (query.PrefectureCodes.Count > 0) p.Add(("prefectureCode", string.Join(":", query.PrefectureCodes)));
        return GetAsync("station/light", p, ct);
    }

    // ---- 経路探索 ----

    public async Task<JsonElement> SearchCourseAsync(RouteQuery query, CancellationToken ct)
    {
        var p = new List<(string, string)>
        {
            ("viaList", string.Join(":", new[] { query.From }.Concat(query.Via).Append(query.To))),
            ("date", query.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            ("searchType", query.SearchType),
            ("sort", query.Sort),
            ("answerCount", query.AnswerCount.ToString(CultureInfo.InvariantCulture)),
            ("conditionDetail", await GetConditionAsync(query, ct)),
        };
        // 平均待ち時間探索（plain）では時刻を使わない
        if (query.SearchType != "plain") p.Add(("time", query.DateTime.ToString("HHmm", CultureInfo.InvariantCulture)));
        if (query.IncludeStops && query.SearchType != "plain") p.Add(("addStop", "true"));
        return await GetAsync("search/course/extreme", p, ct);
    }

    /// <summary>交通手段・座席・乗車券の指定を、経路探索の conditionDetail に変換する（探索条件生成 API は課金対象外）。</summary>
    private async Task<string> GetConditionAsync(RouteQuery q, CancellationToken ct)
    {
        static string Use(bool use) => use ? "normal" : "never";
        var p = new List<(string, string)>
        {
            ("plane", Use(q.UseAirline)),
            ("shinkansen", Use(q.UseShinkansen)),
            ("shinkansenNozomi", Use(q.UseShinkansen && q.UseNozomi)),
            ("limitedExpress", Use(q.UseExpress)),
            ("liner", Use(q.UseExpress)),
            ("highwayBus", Use(q.UseHighwayBus)),
            ("connectionBus", Use(q.UseLocalBus)),
            ("localBus", Use(q.UseLocalBus)),
            ("ship", Use(q.UseFerry)),
            ("surchargeKind", q.Seat),
            ("teikiKind", q.TeikiKind),
            // IC 運賃も計算させたうえで、どちらを経路の運賃にするかを選ぶ
            ("ticketSystemType", "ic"),
            ("preferredTicketOrder", q.Ticket == "ic" ? "ic" : "normal"),
        };
        var cacheKey = string.Join("&", p.Select(x => $"{x.Item1}={x.Item2}"));
        if (_conditionCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = await GetAsync("toolbox/course/condition", p, ct);
        var condition = Json.Str(result, "Condition")
            ?? throw new McpException("駅すぱあと API から探索条件を取得できませんでした。");
        _conditionCache[cacheKey] = condition;
        return condition;
    }

    // ---- 共通 ----

    private async Task<JsonElement> GetAsync(string path, IEnumerable<(string Key, string Value)> parameters, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(key))
            throw new McpException($"駅すぱあと API のアクセスキーが設定されていません。環境変数 {ApiKeyVariable} にアクセスキーを入れて MCP サーバーを起動してください。");

        var url = path + "?" + BuildQuery(parameters.Prepend(("key", key.Trim())));
        var http = httpClientFactory.CreateClient(HttpClientName);
        logger.LogInformation("GET {Path}", path);

        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonElement resultSet;
        try
        {
            using var doc = JsonDocument.Parse(body);
            resultSet = doc.RootElement.GetProperty("ResultSet").Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            throw new McpException($"駅すぱあと API の応答を読めませんでした（HTTP {(int)response.StatusCode}）。");
        }

        // エラーは HTTP ステータスで判断し、内容は ResultSet.Error に入る
        if (!response.IsSuccessStatusCode || resultSet.TryGetProperty("Error", out _))
        {
            var error = Json.Items(resultSet, "Error").FirstOrDefault();
            var code = Json.Str(error, "code");
            var message = Json.Str(error, "Message");
            throw new EkispertApiException(response.StatusCode, code, message);
        }
        return resultSet;
    }

    /// <summary>
    /// 日本語とパイプは UTF-8 でパーセントエンコードし、コロンなどの予約文字はエンコードしない（公式の「URLエンコードに関する注意」）。
    /// viaList などでは区切りのコロンをエンコードすると別の値として扱われてしまう。
    /// </summary>
    internal static string BuildQuery(IEnumerable<(string Key, string Value)> parameters)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parameters)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(k).Append('=').Append(string.Join(":", v.Split(':').Select(Uri.EscapeDataString)));
        }
        return sb.ToString();
    }
}

/// <summary>駅すぱあと API がエラーを返したとき。code は W400 / W403 / E… などの内部エラーコード。</summary>
public sealed class EkispertApiException(HttpStatusCode status, string? code, string? apiMessage)
    : McpException(Describe(status, code, apiMessage))
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;
    public string? ApiMessage { get; } = apiMessage;

    private static string Describe(HttpStatusCode status, string? code, string? message)
    {
        var detail = $"{message ?? "エラー"}（HTTP {(int)status}{(code is null ? "" : ", " + code)}）";
        return status switch
        {
            HttpStatusCode.Forbidden => $"駅すぱあと API の認証に失敗しました: {detail}。{EkispertClient.ApiKeyVariable} のアクセスキーと、そのキーでこの API を使えるプラン・期限かを確かめてください。",
            HttpStatusCode.TooManyRequests => $"駅すぱあと API のリクエスト数の上限に達しました: {detail}。少し待ってから再試行してください。",
            _ => $"駅すぱあと API がエラーを返しました: {detail}",
        };
    }
}
