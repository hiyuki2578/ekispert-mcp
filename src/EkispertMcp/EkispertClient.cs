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

    // ---- 周辺駅・駅の情報 ----

    /// <summary>座標から半径 radius メートル以内の駅を近い順に取得する。座標は世界測地系。</summary>
    public Task<JsonElement> GeoStationAsync(double latitude, double longitude, int radius, IReadOnlyList<string> types, int count, CancellationToken ct)
    {
        var p = new List<(string, string)>
        {
            ("geoPoint", string.Create(CultureInfo.InvariantCulture, $"{latitude},{longitude},wgs84,{radius}")),
            ("stationCount", count.ToString(CultureInfo.InvariantCulture)),
            ("gcs", "wgs84"),
        };
        if (types.Count > 0) p.Add(("type", string.Join(":", types)));
        return GetAsync("geo/station", p, ct);
    }

    /// <summary>住所から半径 radius メートル以内の駅を近い順に取得する。住所の後ろに「,半径」を付けると半径指定になる。</summary>
    public Task<JsonElement> AddressStationAsync(string address, int radius, IReadOnlyList<string> types, int count, CancellationToken ct)
    {
        var p = new List<(string, string)>
        {
            ("address", string.Create(CultureInfo.InvariantCulture, $"{address},{radius}")),
            ("stationCount", count.ToString(CultureInfo.InvariantCulture)),
            ("gcs", "wgs84"),
        };
        if (types.Count > 0) p.Add(("type", string.Join(":", types)));
        return GetAsync("address/station", p, ct);
    }

    /// <summary>駅付加情報。kinds は operationLine / rail / nearrail / welfare / exit。</summary>
    public Task<JsonElement> StationInfoAsync(string code, IReadOnlyList<string> kinds, CancellationToken ct) =>
        GetAsync("station/info", [("code", code), ("type", string.Join(":", kinds))], ct);

    /// <summary>複数の起点駅から、それぞれの上限時間（分）以内に行ける鉄道駅を探す（範囲探索）。</summary>
    public Task<JsonElement> MultipleRangeAsync(
        IReadOnlyList<string> bases, int upperMinute, int? upperTransferCount, bool plane, bool shinkansen, bool limitedExpress, int? limit, CancellationToken ct)
    {
        // upperMinute などは起点と同じ数だけ並べる必要がある
        string Repeat(int value) => string.Join(":", Enumerable.Repeat(value.ToString(CultureInfo.InvariantCulture), bases.Count));
        var p = new List<(string, string)>
        {
            ("baseList", string.Join(":", bases)),
            ("upperMinute", Repeat(upperMinute)),
            ("plane", plane ? "true" : "false"),
            ("shinkansen", shinkansen ? "true" : "false"),
            ("limitedExpress", limitedExpress ? "true" : "false"),
            ("includeBaseStation", "true"),
        };
        if (upperTransferCount is { } transfer) p.Add(("upperTransferCount", Repeat(transfer)));
        if (limit is { } l) p.Add(("limit", l.ToString(CultureInfo.InvariantCulture)));
        return GetAsync("search/multipleRange", p, ct);
    }

    // ---- 時刻表・運行情報 ----

    /// <summary>
    /// 鉄道駅時刻表。code を省略すると方面別の時刻表の一覧、指定するとその時刻表を返す。
    /// date と dateGroup（weekday / saturday / holiday）はどちらか一方を渡す。
    /// </summary>
    public Task<JsonElement> StationTimetableAsync(string station, string? code, DateOnly? date, string? dateGroup, CancellationToken ct)
    {
        var p = new List<(string, string)> { station.All(char.IsAsciiDigit) ? ("stationCode", station) : ("stationName", station) };
        if (code is not null) p.Add(("code", code));
        if (date is { } d) p.Add(("date", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        if (dateGroup is not null) p.Add(("dateGroup", dateGroup));
        return GetAsync("operationLine/timetable", p, ct);
    }

    /// <summary>レスキューナウ提供の鉄道運行情報。都道府県で絞れる。</summary>
    public Task<JsonElement> RescueNowInformationAsync(IReadOnlyList<int> prefectureCodes, CancellationToken ct)
    {
        var p = new List<(string, string)>();
        if (prefectureCodes.Count > 0) p.Add(("prefectureCode", string.Join(":", prefectureCodes)));
        return GetAsync("operationLine/service/rescuenow/information", p, ct);
    }

    // ---- 経路シリアライズデータを使う操作 ----

    /// <summary>経路の sectionIndex（1 始まり）の区間以降について、前後のダイヤの経路を探す。</summary>
    public Task<JsonElement> SearchCoursePatternAsync(string serializeData, int sectionIndex, int answerCount, int previousCount, CancellationToken ct) =>
        GetAsync("search/course/pattern",
        [
            ("serializeData", serializeData),
            ("sectionIndex", sectionIndex.ToString(CultureInfo.InvariantCulture)),
            ("answerCount", answerCount.ToString(CultureInfo.InvariantCulture)),
            ("previousCount", previousCount.ToString(CultureInfo.InvariantCulture)),
        ], ct);

    /// <summary>経路の運賃（fare）または定期代（teiki）を、乗車券を分けて買う場合で計算する。</summary>
    public Task<JsonElement> DividedPriceAsync(string serializeData, bool teiki, CancellationToken ct) =>
        GetAsync(teiki ? "course/teiki/divided" : "course/fare/divided", [("serializeData", serializeData)], ct);

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
