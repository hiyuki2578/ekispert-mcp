using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EkispertMcp;

[McpServerToolType]
public sealed class EkispertTools(EkispertClient client)
{
    [McpServerTool(Name = "search_route", ReadOnly = true, Idempotent = true)]
    [Description("""
        駅すぱあと API で、駅から駅への公共交通機関の経路（電車・バス・飛行機・船・徒歩）を探索する。
        各経路の出発/到着時刻、所要時間、乗換回数、運賃（片道・往復、IC/きっぷ）、特急料金などの料金、
        1・3・6 か月の定期代と、区間ごとの路線・行き先・発着番線を返す。交通費精算や定期代の確認にも使える。

        駅名は駅すぱあとの正式名称を日本語で渡すこと（例: 東京, 新宿, 大阪, 羽田空港(第1ターミナル)）。
        英語・中国語の駅名は日本語に直してから呼ぶ（Tokyo → 東京, Yokohama → 横浜, 涩谷 → 渋谷）。
        同名の駅は「府中(東京都)」「中野(東京都)」のように都道府県が付くので、名前が分からない・見つからないときは
        先に search_stations で正確な名前を調べる。駅コード（数字）も使える。

        例: 「東京から大阪の往復の交通費、いちばん安い経路で」→ from="東京", to="大阪", sort="fare" で round_trip を見る
            「明日 9 時までに横浜に着きたい」→ to="横浜", date=明日, time="09:00", time_type="arrival"
            「新幹線を使わずに」→ use_shinkansen=false
        """)]
    public async Task<object> SearchRoute(
        [Description("出発駅名（例: 東京, 新宿）または駅コード")] string from,
        [Description("到着駅名（例: 新大阪, 横浜）または駅コード")] string to,
        [Description("経由駅名（最大 3 駅）")] string[]? via = null,
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        [Description("時刻 (HH:MM, 24 時間制)。省略時は現在時刻（日本時間）。time_type=average では使わない")] string? time = null,
        [Description("時刻の意味: departure=出発（既定）, arrival=到着, first_train=始発, last_train=終電, average=時刻表を使わず平均待ち時間で探索（運賃・定期代だけ知りたいとき向き）")] string time_type = "departure",
        [Description("並び順: recommended=駅すぱあとのおすすめ順（既定）, time=所要時間が短い順, fare=運賃が安い順, transfer=乗換回数が少ない順, commuter_pass=定期代が安い順")] string sort = "recommended",
        [Description("返す経路の数 (1〜20)。既定 5")] int count = 5,
        [Description("運賃: ic=IC カード（既定）, cash=きっぷ")] string ticket = "ic",
        [Description("特急・新幹線の座席: non_reserved=自由席（既定）, reserved=指定席, green=グリーン車")] string seat = "non_reserved",
        [Description("定期の種類: commute=通勤（既定）, university=通学(大学), high_school=通学(高校), junior_high_school=通学(中学)")] string commuter_pass = "commute",
        [Description("飛行機を使う")] bool use_airline = true,
        [Description("新幹線を使う")] bool use_shinkansen = true,
        [Description("のぞみ・みずほ・はやぶさ・こまちを使う（use_shinkansen=true のときだけ意味がある）")] bool use_nozomi = true,
        [Description("有料特急・ライナーを使う")] bool use_express = true,
        [Description("高速バスを使う")] bool use_highway_bus = true,
        [Description("路線バス・連絡バスを使う")] bool use_local_bus = true,
        [Description("船を使う")] bool use_ferry = true,
        [Description("true なら区間ごとの途中停車駅と時刻も返す（time_type=average では返らない）")] bool include_stops = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new McpException("from（出発駅）を指定してください。");
        if (string.IsNullOrWhiteSpace(to)) throw new McpException("to（到着駅）を指定してください。");
        var viaList = (via ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (viaList.Count > 3) throw new McpException("経由駅は 3 駅までです。");
        if (count is < 1 or > 20) throw new McpException("count は 1〜20 で指定してください。");

        var query = new RouteQuery(
            from.Trim(),
            to.Trim(),
            viaList,
            ParseDateTime(date, time),
            SearchType: ParseOption(time_type, nameof(time_type), new Dictionary<string, string>
            {
                ["departure"] = "departure",
                ["arrival"] = "arrival",
                ["first_train"] = "firstTrain",
                ["last_train"] = "lastTrain",
                ["average"] = "plain",
            }),
            Sort: ParseOption(sort, nameof(sort), new Dictionary<string, string>
            {
                ["recommended"] = "ekispert",
                ["time"] = "time",
                ["fare"] = "price",
                ["transfer"] = "transfer",
                ["commuter_pass"] = "teiki",
            }),
            AnswerCount: count,
            Seat: ParseOption(seat, nameof(seat), new Dictionary<string, string>
            {
                ["non_reserved"] = "free",
                ["reserved"] = "reserved",
                ["green"] = "green",
            }),
            Ticket: ParseOption(ticket, nameof(ticket), new Dictionary<string, string>
            {
                ["ic"] = "ic",
                ["cash"] = "cash",
            }),
            TeikiKind: ParseOption(commuter_pass, nameof(commuter_pass), new Dictionary<string, string>
            {
                // API の値の綴りは bussiness（原文ママ）
                ["commute"] = "bussiness",
                ["university"] = "university",
                ["high_school"] = "highSchool",
                ["junior_high_school"] = "juniorHighSchool",
            }),
            UseAirline: use_airline,
            UseShinkansen: use_shinkansen,
            UseNozomi: use_nozomi,
            UseExpress: use_express,
            UseHighwayBus: use_highway_bus,
            UseLocalBus: use_local_bus,
            UseFerry: use_ferry,
            IncludeStops: include_stops);

        JsonElement result;
        try
        {
            result = await client.SearchCourseAsync(query, ct);
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // 駅名の誤りがいちばん多いので、駅名ごとの候補を添えて返す
            throw new McpException(ex.Message + await DescribeStationCandidatesAsync([query.From, .. query.Via, query.To], ct));
        }

        var courses = Json.Items(result, "Course").ToList();
        var routes = courses.Select(c => DescribeCourse(c, include_stops)).ToList();
        return new
        {
            from = query.From,
            to = query.To,
            via = viaList.Count > 0 ? viaList : null,
            search = query.SearchType == "plain"
                ? $"{query.DateTime:yyyy-MM-dd} 平均待ち時間"
                : $"{query.DateTime:yyyy-MM-dd HH:mm} {time_type}",
            routes,
            note = routes.Count == 0
                ? "経路が見つかりませんでした。日時や交通手段の条件を変えて探索してください。"
                : "金額は円。fare は乗車券（ticket の指定に従い IC またはきっぷ）、charge は特急券・指定席券などの料金、total はその合計。"
                  + "round_trip は往復の金額（往復割引などを含む）。commuter_pass は定期代（commuter_pass の種類）。"
                  + "fares は区間ごとの内訳で、lines はその金額がかかる区間の路線名。"
                  + "時刻は日本時間。time_type=average の結果には時刻がなく、duration は平均的な所要時間。"
                  + (routes.Any(r => r.HasForecast) ? "forecast=true の金額は運賃改定後の見込額で、確定ではない。" : ""),
        };
    }

    [McpServerTool(Name = "search_stations", ReadOnly = true, Idempotent = true)]
    [Description("""
        駅すぱあと API の駅簡易情報で、駅・空港・港・バス停を名前で検索する（最大 30 件）。
        search_route に渡す正式な駅名を調べるとき（例: 府中 → 府中(東京都)、羽田 → 羽田空港(第1ターミナル)）や、
        駅名があいまいなときに使う。返した name はそのまま search_route の from / to / via に使える。
        ひらがなの読みでも引ける（例: しんじゅく）。この API は課金対象外。
        """)]
    public async Task<object> SearchStations(
        [Description("駅名・バス停名などの先頭部分（例: 府中, 羽田, しんじゅく）")] string query,
        [Description("true なら部分一致、false なら前方一致（既定）")] bool partial = false,
        [Description("交通種別で絞る: train=鉄道, plane=空港, ship=港, bus=バス停。省略時はすべて")] string[]? types = null,
        [Description("都道府県コード（1〜47。例: 13=東京都, 27=大阪府）で絞る。省略時は全国")] int[]? prefectures = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query を指定してください。");
        var typeList = (types ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var allowed = new[] { "train", "plane", "ship", "bus" };
        if (typeList.FirstOrDefault(t => !allowed.Contains(t)) is { } bad)
            throw new McpException($"types に {bad} は使えません。{string.Join(" / ", allowed)} のいずれかを指定してください。");
        var prefList = (prefectures ?? []).Distinct().ToList();
        if (prefList.Any(p => p is < 1 or > 47)) throw new McpException("prefectures は 1〜47 の都道府県コードで指定してください。");

        var result = await client.StationLightAsync(new StationQuery(query.Trim(), partial, typeList, prefList), ct);
        var stations = Json.Items(result, "Point").Select(DescribeStation).ToList();
        return new
        {
            stations,
            note = stations.Count == 0
                ? "一致する駅がありません。表記を変えるか partial=true で検索してください。"
                : "type は train=鉄道, plane=空港, ship=港, bus=バス停（type_detail に local=路線バス, highway=高速バス など）。code は駅コードで、search_route の駅名の代わりにも使える。",
        };
    }

    // ---- 応答の整形 ----

    private static object DescribeStation(JsonElement point)
    {
        var station = Json.Child(point, "Station");
        return new
        {
            name = Json.Str(station, "Name"),
            yomi = Json.Str(station, "Yomi"),
            code = Json.Str(station, "code"),
            type = Json.Str(station, "Type"),
            type_detail = station is { } s ? Json.Attr(s, "Type", "detail") : null,
            prefecture = Json.Str(Json.Child(point, "Prefecture"), "Name"),
        };
    }

    private sealed record RouteSummary(
        string? Departure,
        string? Arrival,
        int? Duration,
        int? TimeOnBoard,
        int? TimeWalk,
        int? Transfers,
        double? DistanceKm,
        int? Fare,
        int? Charge,
        int? Total,
        object? RoundTrip,
        object? CommuterPass,
        object[] Fares,
        object[] Segments,
        string? CommuterPassRoute,
        [property: System.Text.Json.Serialization.JsonIgnore] bool HasForecast);

    private static RouteSummary DescribeCourse(JsonElement course, bool includeStops)
    {
        var route = Json.Child(course, "Route") ?? default;
        var points = Json.Items(route, "Point").ToList();
        var lines = Json.Items(route, "Line").ToList();
        var prices = Json.Items(course, "Price").ToList();

        int? Summary(string kind, string field = "Oneway") =>
            prices.Where(p => Json.Str(p, "kind") == kind).Select(p => Json.Int(p, field)).FirstOrDefault();

        string LineNames(JsonElement price)
        {
            // fromLineIndex / toLineIndex は 1 始まり（XML の index 基準）
            var fromIndex = Json.Int(price, "fromLineIndex") ?? 1;
            var toIndex = Json.Int(price, "toLineIndex") ?? fromIndex;
            return string.Join(" → ", lines.Skip(fromIndex - 1).Take(toIndex - fromIndex + 1).Select(l => Json.Str(l, "Name")));
        }

        var fare = Summary("FareSummary");
        var charge = Summary("ChargeSummary");
        var fareRound = Summary("FareSummary", "Round");
        var chargeRound = Summary("ChargeSummary", "Round");

        var fares = prices
            .Where(p => Json.Str(p, "kind") is "Fare" or "Charge" && Json.Str(p, "selected") != "false")
            // 料金のいらない区間にも 0 円の「自由席」が付くので除く
            .Where(p => !(Json.Str(p, "kind") == "Charge" && Json.Int(p, "Oneway") == 0))
            .OrderBy(p => Json.Str(p, "kind") == "Fare" ? 0 : 1)
            .ThenBy(p => Json.Int(p, "fromLineIndex") ?? 0)
            .Select(p => (object)new
            {
                kind = Json.Str(p, "kind") == "Fare" ? "fare" : "charge",
                type = Json.Str(p, "Type"),
                name = Json.Str(p, "Name"),
                lines = LineNames(p),
                oneway = Json.Int(p, "Oneway"),
                round_trip = Json.Int(p, "Round"),
                forecast = Json.Str(p, "RevisionStatus") == "forecast" ? true : (bool?)null,
            })
            .ToArray();

        var segments = lines.Select((line, i) =>
        {
            var dep = Json.Child(line, "DepartureState");
            var arr = Json.Child(line, "ArrivalState");
            var type = Json.Str(line, "Type");
            return (object)new
            {
                kind = type,
                kind_detail = Json.Attr(line, "Type", "detail"),
                line = Json.Str(line, "Name"),
                symbol = Json.Str(Json.Child(line, "LineSymbol"), "Name"),
                destination = Json.Str(line, "Destination"),
                from = StationName(points.ElementAtOrDefault(i)),
                to = StationName(points.ElementAtOrDefault(i + 1)),
                departure = FormatTime(Json.Str(dep, "Datetime")),
                arrival = FormatTime(Json.Str(arr, "Datetime")),
                departure_track = Json.Str(dep, "no"),
                arrival_track = Json.Str(arr, "no"),
                minutes = Json.Int(line, "timeOnBoard"),
                stop_count = type == "walk" ? null : Json.Int(line, "stopStationCount"),
                stops = includeStops
                    ? Json.Items(line, "Stop").Select(s => new
                    {
                        name = StationName(Json.Child(s, "Point")),
                        time = FormatTime(Json.Str(Json.Child(s, "ArrivalState"), "Datetime") ?? Json.Str(Json.Child(s, "DepartureState"), "Datetime")),
                    }).ToArray() is { Length: > 0 } st ? st : null
                    : null,
                comment = Json.Items(line, "Comment").Select(Json.Text).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            };
        }).ToArray();

        var onBoard = Json.Int(route, "timeOnBoard");
        var other = Json.Int(route, "timeOther");
        var walk = Json.Int(route, "timeWalk");
        var teiki = Json.Child(course, "Teiki");

        return new RouteSummary(
            Departure: FormatDateTime(lines.Select(l => Json.Str(Json.Child(l, "DepartureState"), "Datetime")).FirstOrDefault(d => d is not null)),
            Arrival: FormatDateTime(lines.Select(l => Json.Str(Json.Child(l, "ArrivalState"), "Datetime")).LastOrDefault(d => d is not null)),
            Duration: (onBoard ?? 0) + (other ?? 0) + (walk ?? 0),
            TimeOnBoard: onBoard,
            TimeWalk: walk is > 0 ? walk : null,
            Transfers: Json.Int(route, "transferCount"),
            DistanceKm: Json.Int(route, "distance") is { } d100 ? d100 / 10.0 : null,
            Fare: fare,
            Charge: charge is 0 ? null : charge,
            Total: fare is null && charge is null ? null : (fare ?? 0) + (charge ?? 0),
            RoundTrip: fareRound is null && chargeRound is null ? null : new { fare = fareRound, charge = chargeRound is 0 ? null : chargeRound, total = (fareRound ?? 0) + (chargeRound ?? 0) },
            CommuterPass: Summary("Teiki1Summary") is null ? null : new
            {
                one_month = Summary("Teiki1Summary"),
                three_months = Summary("Teiki3Summary"),
                six_months = Summary("Teiki6Summary"),
                twelve_months = Summary("Teiki12Summary"),
            },
            Fares: fares,
            Segments: segments,
            CommuterPassRoute: Json.Str(teiki, "DisplayRoute"),
            HasForecast: prices.Any(p => Json.Str(p, "RevisionStatus") == "forecast"));
    }

    private static string? StationName(JsonElement? point) =>
        point is { } p ? Json.Str(Json.Child(p, "Station"), "Name") ?? Json.Str(p, "Name") : null;

    /// <summary>2020-08-13T18:41:00+09:00 や 18:41:00+09:00 を 18:41 にする。</summary>
    /// <remarks>平均待ち時間探索では Datetime が日付だけ（2026-10-23）になるので、時刻がなければ null を返す。</remarks>
    private static string? FormatTime(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var t = value.Contains('T') ? value[(value.IndexOf('T') + 1)..] : value;
        return t.Length >= 5 && t[2] == ':' ? t[..5] : null;
    }

    /// <summary>2020-08-13T18:41:00+09:00 を 2020-08-13 18:41 にする。時刻がなければ null。</summary>
    private static string? FormatDateTime(string? value) =>
        value is not null && value.Contains('T') &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : FormatTime(value);

    private async Task<string> DescribeStationCandidatesAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var name in names.Distinct())
        {
            if (name.All(char.IsAsciiDigit)) continue; // 駅コード
            try
            {
                var result = await client.StationLightAsync(new StationQuery(name, false, [], []), ct);
                var candidates = Json.Items(result, "Point").Select(p => StationName(p)).Where(n => n is not null).ToList();
                if (candidates.Contains(name)) continue;
                lines.Add(candidates.Count == 0
                    ? $"「{name}」に一致する駅がありません。"
                    : $"「{name}」の候補: {string.Join(", ", candidates.Take(8))}");
            }
            catch (McpException)
            {
                // 候補の取得に失敗しても元のエラーを返す
            }
        }
        return lines.Count == 0 ? "" : "\n" + string.Join("\n", lines) + "\n候補の名前で探索し直すか、search_stations で調べてください。";
    }

    private static DateTime ParseDateTime(string? date, string? time)
    {
        var now = EkispertClient.NowJst();
        var d = DateOnly.FromDateTime(now);
        var t = new TimeOnly(now.Hour, now.Minute);
        if (!string.IsNullOrWhiteSpace(date) &&
            !DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            throw new McpException("date は YYYY-MM-DD 形式で指定してください。");
        if (!string.IsNullOrWhiteSpace(time) &&
            !TimeOnly.TryParseExact(time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            throw new McpException("time は HH:MM 形式（24 時間制）で指定してください。");
        return d.ToDateTime(t);
    }

    private static T ParseOption<T>(string value, string name, Dictionary<string, T> options)
    {
        if (options.TryGetValue(value.Trim().ToLowerInvariant(), out var v)) return v;
        throw new McpException($"{name} は {string.Join(" / ", options.Keys)} のいずれかで指定してください。");
    }
}
