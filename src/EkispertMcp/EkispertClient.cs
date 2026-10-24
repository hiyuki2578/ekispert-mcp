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

    // 飛行機・船の路線名の一覧（時刻表を引くときの路線名の解決に使う）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _railNameCache = new();

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

    /// <summary>
    /// 経路の利用運賃・利用料金（Price の index）や定期券の種別を切り替えて再計算する。
    /// addRouteData=true で、経路探索と同じ形の Course を返させる。
    /// </summary>
    public Task<JsonElement> RecalculateAsync(
        string serializeData, IReadOnlyList<string> fareIndexes, IReadOnlyList<string> chargeIndexes, string? teikiKind, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("serializeData", serializeData), ("addRouteData", "true") };
        if (fareIndexes.Count > 0) p.Add(("fareIndex", string.Join(":", fareIndexes)));
        if (chargeIndexes.Count > 0) p.Add(("chargeIndex", string.Join(":", chargeIndexes)));
        if (teikiKind is not null) p.Add(("teikiKind", teikiKind));
        return GetAsync("course/recalculate", p, ct);
    }

    /// <summary>
    /// 持っている定期券を経路に当てはめて運賃を計算し直す。定期券は定期経路シリアライズデータか、
    /// 「駅:路線:駅…」の定期経路文字列（assignRoute）で渡す。
    /// </summary>
    public Task<JsonElement> AssignTeikiAsync(string serializeData, string? teikiSerializeData, string? teikiRoute, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("serializeData", serializeData) };
        if (teikiSerializeData is not null) p.Add(("assignTeikiSerializeData", teikiSerializeData));
        if (teikiRoute is not null) p.Add(("assignRoute", teikiRoute));
        return GetAsync("course/edit", p, ct);
    }

    /// <summary>経路の定期券を払い戻したときの金額を計算する。months は 1 / 3 / 6 / 12。</summary>
    public Task<JsonElement> RepaymentAsync(
        string serializeData, DateOnly startDate, DateOnly? buyDate, DateOnly repaymentDate, int months, bool changeSection, CancellationToken ct)
    {
        static string D(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var p = new List<(string, string)>
        {
            ("serializeData", serializeData),
            ("startDate", D(startDate)),
            ("repaymentDate", D(repaymentDate)),
            ("validityPeriod", months.ToString(CultureInfo.InvariantCulture)),
            ("changeSection", changeSection ? "true" : "false"),
        };
        if (buyDate is { } b) p.Add(("buyDate", D(b)));
        return GetAsync("course/repayment", p, ct);
    }

    /// <summary>経路の区間の停車駅と時刻。sectionOnly=false なら、その列車の始発から終点まで。</summary>
    public Task<JsonElement> CourseStationAsync(string serializeData, int sectionIndex, bool sectionOnly, CancellationToken ct) =>
        GetAsync("course/station",
        [
            ("serializeData", serializeData),
            ("sectionIndex", sectionIndex.ToString(CultureInfo.InvariantCulture)),
            ("sectionOnly", sectionOnly ? "true" : "false"),
        ], ct);

    /// <summary>経路（sectionIndex を指定すればその区間）で通る駅を、通過駅も含めて返す。</summary>
    public Task<JsonElement> PassStationAsync(string serializeData, int? sectionIndex, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("serializeData", serializeData) };
        if (sectionIndex is { } s) p.Add(("sectionIndex", s.ToString(CultureInfo.InvariantCulture)));
        return GetAsync("course/passStation", p, ct);
    }

    /// <summary>経路の区間（鉄道のみ）の時刻表。</summary>
    public Task<JsonElement> CourseTimetableAsync(string serializeData, int sectionIndex, CancellationToken ct) =>
        GetAsync("course/timetable",
            [("serializeData", serializeData), ("sectionIndex", sectionIndex.ToString(CultureInfo.InvariantCulture))], ct);

    /// <summary>時刻表の結果にある「列車等を特定するコード」から、その列車の停車駅と時刻を得る。</summary>
    public Task<JsonElement> TrainAsync(string code, CancellationToken ct) =>
        GetAsync("station/timetable/train", [("code", code)], ct);

    /// <summary>
    /// 特急列車時刻表。引数なしで運行区間の一覧、station でその駅を通る列車の運行区間、
    /// serviceSectionCode（と号数）でその運行区間の時刻表を返す。
    /// </summary>
    public Task<JsonElement> ExpressTimetableAsync(string? station, string? serviceSectionCode, IReadOnlyList<int> numbers, DateOnly date, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("date", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) };
        if (serviceSectionCode is not null)
        {
            p.Add(("serviceSectionCode", serviceSectionCode));
            if (numbers.Count > 0) p.Add(("number", string.Join(":", numbers)));
        }
        else if (station is not null)
        {
            p.Add(station.All(char.IsAsciiDigit) ? ("stationCode", station) : ("stationName", station));
        }
        return GetAsync("train/timetable", p, ct);
    }

    /// <summary>平均路線の一覧（名前の前方一致・交通種別で絞る）。offset は 1 始まりで、1 回に最大 100 件。</summary>
    public Task<JsonElement> RailAsync(string? name, string? type, int offset, CancellationToken ct)
    {
        var p = new List<(string, string)> { ("offset", offset.ToString(CultureInfo.InvariantCulture)), ("limit", "100") };
        if (name is not null) p.Add(("name", name));
        if (type is not null) p.Add(("type", type));
        return GetAsync("rail", p, ct);
    }

    /// <summary>交通種別（plane / ship）の平均路線名をすべて取る。数百件を 100 件ずつ取るので、プロセス内で覚えておく。</summary>
    public async Task<IReadOnlyList<string>> GetRailNamesAsync(string type, CancellationToken ct)
    {
        if (_railNameCache.TryGetValue(type, out var cached)) return cached;
        var names = new List<string>();
        for (var offset = 1; ; offset += 100)
        {
            var result = await RailAsync(null, type, offset, ct);
            var page = Json.Items(result, "Line").Select(l => Json.Str(l, "Name")).OfType<string>().ToList();
            names.AddRange(page);
            if (page.Count < 100 || offset > 2000) break;
        }
        _railNameCache[type] = names;
        return names;
    }

    /// <summary>
    /// 飛行機（plane）・船（ship）の路線の時刻表。railName は「空路(東京－大阪)」のような平均路線名。
    /// up=true で括弧の右から左（大阪→東京）、false で左から右の向き。
    /// </summary>
    public Task<JsonElement> PlaneShipTimetableAsync(string kind, string railName, bool up, DateOnly date, CancellationToken ct) =>
        GetAsync($"{kind}/timetable",
        [
            ("railName", railName),
            ("direction", up ? "true" : "false"),
            ("date", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
        ], ct);

    /// <summary>2 つのバス停の間のバス路線時刻表。</summary>
    public Task<JsonElement> BusTimetableAsync(string from, string to, DateOnly date, CancellationToken ct) =>
        GetAsync("bus/timetable", [("from", from), ("to", to), ("date", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture))], ct);

    /// <summary>バスだけを使う、平均待ち時間による経路探索。</summary>
    public Task<JsonElement> SearchBusCourseAsync(string from, string to, DateOnly date, CancellationToken ct) =>
        GetAsync("search/course/bus", [("from", from), ("to", to), ("date", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture))], ct);

    /// <summary>回数券の一覧（名前で絞る）。</summary>
    public Task<JsonElement> CouponListAsync(string? name, CancellationToken ct) =>
        GetAsync("coupon/list", name is null ? [] : [("name", name)], ct);

    /// <summary>回数券の詳細。回数券の名前か、経路シリアライズデータ（その経路で使える回数券）のどちらかを渡す。</summary>
    public Task<JsonElement> CouponDetailAsync(string? name, string? serializeData, CancellationToken ct) =>
        GetAsync("coupon/detail", name is not null ? [("name", name)] : [("serializeData", serializeData ?? "")], ct);

    /// <summary>駅情報。railName でその路線の駅を順に、oldName で旧駅名から今の駅を返す。</summary>
    public Task<JsonElement> StationAsync(string? railName, string? oldName, CancellationToken ct)
    {
        var p = new List<(string, string)>();
        if (railName is not null) p.Add(("railName", railName));
        if (oldName is not null) p.Add(("oldName", oldName));
        return GetAsync("station", p, ct);
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
