using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static EkispertMcp.EkispertTools;

namespace EkispertMcp;

/// <summary>駅のまわりの情報（周辺駅・駅の付加情報・範囲探索・時刻表・運行情報）を調べるツール。</summary>
[McpServerToolType]
public sealed class StationTools(EkispertClient client)
{
    [McpServerTool(Name = "search_nearby_stations", ReadOnly = true, Idempotent = true)]
    [Description("""
        住所または緯度経度から、近くの駅・バス停・空港・港を近い順に探す。
        「渋谷区神南1丁目の最寄り駅は？」「この座標から歩いて行ける駅」のようなときに使う。
        address か latitude/longitude のどちらかを渡す。返した name はそのまま search_route の from / to に使える。
        """)]
    public async Task<object> SearchNearbyStations(
        [Description("住所（例: 東京都千代田区丸の内1丁目）。latitude/longitude と同時には使わない")] string? address = null,
        [Description("緯度（世界測地系の十進数。例: 35.6812）")] double? latitude = null,
        [Description("経度（世界測地系の十進数。例: 139.7671）")] double? longitude = null,
        [Description("探す半径（メートル、1〜10000）。既定 1000")] int radius = 1000,
        [Description("交通種別で絞る: train=鉄道, plane=空港, ship=港, bus=バス停。省略時はすべて")] string[]? types = null,
        [Description("返す件数 (1〜50)。既定 10")] int count = 10,
        CancellationToken ct = default)
    {
        var hasAddress = !string.IsNullOrWhiteSpace(address);
        var hasPoint = latitude is not null || longitude is not null;
        if (hasAddress == hasPoint) throw new McpException("address か latitude/longitude のどちらか一方を指定してください。");
        if (hasPoint && (latitude is not { } lat || longitude is not { } lng || lat is < 20 or > 46 || lng is < 122 or > 154))
            throw new McpException("latitude と longitude は両方、日本国内の十進数の座標で指定してください（例: 35.6812, 139.7671）。");
        if (radius is < 1 or > 10000) throw new McpException("radius は 1〜10000（メートル）で指定してください。");
        if (count is < 1 or > 50) throw new McpException("count は 1〜50 で指定してください。");
        var typeList = ParseTypes(types);

        var result = hasAddress
            ? await client.AddressStationAsync(address!.Trim(), radius, typeList, count, ct)
            : await client.GeoStationAsync(latitude!.Value, longitude!.Value, radius, typeList, count, ct);
        var stations = Json.Items(result, "Point").Take(count).Select(point =>
        {
            var station = Json.Child(point, "Station");
            return new
            {
                name = Json.Str(station, "Name"),
                code = Json.Str(station, "code"),
                type = Json.Str(station, "Type"),
                type_detail = station is { } s ? Json.Attr(s, "Type", "detail") : null,
                distance_m = Json.Int(point, "Distance"),
                prefecture = Json.Str(Json.Child(point, "Prefecture"), "Name"),
            };
        }).ToList();
        return new
        {
            address = hasAddress ? address!.Trim() : null,
            stations,
            note = stations.Count == 0
                ? "範囲内に駅がありません。radius を広げてください。"
                : "distance_m は直線距離（メートル）で、歩く道のりではない。type は train=鉄道, plane=空港, ship=港, bus=バス停。",
        };
    }

    [McpServerTool(Name = "get_station_info", ReadOnly = true, Idempotent = true)]
    [Description("""
        駅の付加情報を調べる: 乗り入れている路線、最寄りの路線（バスなど駅の周りの路線）、出口案内、
        バリアフリー設備（段差のない経路・エレベーター・エスカレーター・多機能トイレなど）。
        「東京駅に乗り入れている路線は？」「新宿駅のエレベーターはどこ？」「丸ビルに近い出口は？」のようなときに使う。
        """)]
    public async Task<object> GetStationInfo(
        [Description("駅名（駅すぱあとの正式名称。例: 東京, 府中(東京都)）または駅コード")] string station,
        [Description("調べる情報（複数可）: lines=乗り入れ路線（既定）, nearby_lines=最寄りの路線（バス路線など）, exits=出口案内（鉄道のみ）, accessibility=バリアフリー設備")] string[]? kinds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(station)) throw new McpException("station を指定してください。");
        var kindMap = new Dictionary<string, string>
        {
            ["lines"] = "operationLine",
            ["nearby_lines"] = "nearrail",
            ["exits"] = "exit",
            ["accessibility"] = "welfare",
        };
        var kindList = (kinds is { Length: > 0 } ? kinds : ["lines"])
            .Select(k => ParseOption(k, nameof(kinds), kindMap)).Distinct().ToList();
        var (code, name) = await ResolveStationAsync(station.Trim(), ct);

        var result = await client.StationInfoAsync(code, kindList, ct);
        object? lines = null, nearbyLines = null, exits = null, accessibility = null;
        foreach (var info in Json.Items(result, "Information"))
        {
            var corporations = Json.Items(info, "Corporation").Select(c => Json.Str(c, "Name")).ToList();
            object[] Lines() => Json.Items(info, "Line").Select(l => (object)new
            {
                name = Json.Str(l, "Name"),
                type = Json.Str(l, "Type"),
                type_detail = Json.Attr(l, "Type", "detail"),
                // corporationIndex は 1 始まり
                company = Json.Int(l, "corporationIndex") is { } i ? corporations.ElementAtOrDefault(i - 1) : null,
            }).ToArray();
            object[] Comments(string name) => Json.Items(info, name).Select(e => (object)new
            {
                name = Json.Str(e, "Name"),
                detail = Json.Str(e, "Comment"),
            }).ToArray();

            switch (Json.Str(info, "Type"))
            {
                case "operationLine": lines = Lines(); break;
                case "nearrail": nearbyLines = Lines(); break;
                case "exit": exits = Comments("Exit"); break;
                case "welfare": accessibility = Comments("WelfareFacilities"); break;
            }
        }
        return new
        {
            station = name,
            code,
            lines,
            nearby_lines = nearbyLines,
            exits,
            accessibility,
            note = "指定した種類の情報がないときは、その項目を省く。lines は乗り入れている鉄道の運行路線、nearby_lines は駅の周りのバス路線なども含む。",
        };
    }

    [McpServerTool(Name = "search_reachable_stations", ReadOnly = true, Idempotent = true)]
    [Description("""
        起点の鉄道駅（最大 5 駅）から、指定した時間内に電車で行ける駅を探す（範囲探索）。
        起点を複数渡すと、すべての起点から時間内に行ける駅だけを返すので、
        「渋谷と新宿のどちらにも 20 分で行ける駅（住む場所探し）」「3 人が集まりやすい駅」のようなときに使える。
        時間は平均待ち時間を含む目安で、時刻表は使わない。
        """)]
    public async Task<object> SearchReachableStations(
        [Description("起点の鉄道駅名（最大 5 駅。例: [\"渋谷\", \"新宿\"]）または駅コード")] string[] from,
        [Description("各起点からの所要時間の上限（分、10〜200）")] int minutes,
        [Description("各起点からの乗換回数の上限。省略時は制限なし")] int? max_transfers = null,
        [Description("返す駅の数の上限 (1〜200)。既定 50。所要時間の短い駅から返す")] int limit = 50,
        [Description("新幹線を使う")] bool use_shinkansen = false,
        [Description("有料特急を使う")] bool use_express = false,
        [Description("飛行機を使う")] bool use_airline = false,
        CancellationToken ct = default)
    {
        var bases = (from ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct().ToList();
        if (bases.Count is < 1 or > 5) throw new McpException("from には起点の駅を 1〜5 駅指定してください。");
        if (minutes is < 10 or > 200) throw new McpException("minutes は 10〜200 で指定してください。");
        if (max_transfers is < 0) throw new McpException("max_transfers は 0 以上で指定してください。");
        if (limit is < 1 or > 200) throw new McpException("limit は 1〜200 で指定してください。");

        JsonElement result;
        try
        {
            result = await client.MultipleRangeAsync(bases, minutes, max_transfers, use_airline, use_shinkansen, use_express, limit, ct);
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new McpException(ex.Message + "\n起点は鉄道駅の正式名称（例: 府中(東京都)）で指定してください。分からないときは search_stations で調べてください。");
        }

        var stations = Json.Items(result, "Point").Select(point =>
        {
            var costs = Json.Items(point, "Cost").ToList();
            return new
            {
                name = StationName(point),
                code = Json.Str(Json.Child(point, "Station"), "code"),
                prefecture = Json.Str(Json.Child(point, "Prefecture"), "Name"),
                // baseIndex は 1 始まりで from の順に対応する
                from = costs.Select(c => new
                {
                    station = Json.Int(c, "baseIndex") is { } i ? bases.ElementAtOrDefault(i - 1) : null,
                    minutes = Json.Int(c, "Minute"),
                    transfers = Json.Int(c, "TransferCount"),
                }).ToArray(),
            };
        })
        .OrderBy(s => s.from.Max(c => c.minutes ?? 0))
        .ThenBy(s => s.from.Sum(c => c.minutes ?? 0))
        .ToList();

        return new
        {
            from = bases,
            minutes,
            stations,
            note = stations.Count == 0
                ? "条件に合う駅がありません。minutes を増やすか、起点を減らしてください。"
                : "stations は各起点からの所要時間（分、平均待ち時間を含む目安）と乗換回数で、いちばん遠い起点からの時間が短い順。起点の駅自身も含む。",
        };
    }

    [McpServerTool(Name = "get_station_timetable", ReadOnly = true, Idempotent = true)]
    [Description("""
        鉄道駅の時刻表を調べる。まず station だけで呼ぶと、その駅の路線・方面ごとの時刻表の一覧（timetable_code 付き）を返す。
        line で路線名・方面を絞ると、合う時刻表（最大 4 つ）の発車時刻を続けて返す。timetable_code を渡すとその時刻表を返す。
        発車時刻ごとに種別（快速など）・行き先・番線・列車名を返す。from_time / to_time で時間帯を絞れる。
        例: 「高円寺から東京方面の中央線、朝 8 時台」→ station="高円寺", line="中央線快速 東京", from_time="08:00", to_time="08:59"
        """)]
    public async Task<object> GetStationTimetable(
        [Description("駅名（例: 高円寺）または駅コード。鉄道駅のみ")] string station,
        [Description("路線名・方面で絞る語（空白区切りで複数語、すべてを含むものに絞る。例: \"中央線快速 東京\", \"山手線 渋谷\"）")] string? line = null,
        [Description("一覧で返した timetable_code。指定すると line より優先する")] string? timetable_code = null,
        [Description("日付 (YYYY-MM-DD)。その日のダイヤの時刻表になる。省略時は今日（日本時間）")] string? date = null,
        [Description("曜日区分: weekday=平日, saturday=土曜, holiday=日曜・祝日。指定すると date より優先し、その区分の直近の日の時刻表になる")] string? day_type = null,
        [Description("この時刻 (HH:MM) 以降の発車だけ返す")] string? from_time = null,
        [Description("この時刻 (HH:MM) 以前の発車だけ返す")] string? to_time = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(station)) throw new McpException("station を指定してください。");
        var stationName = station.Trim();
        var dateGroup = day_type is null ? null : ParseOption(day_type, nameof(day_type), new Dictionary<string, string>
        {
            ["weekday"] = "weekday",
            ["saturday"] = "saturday",
            ["holiday"] = "holiday",
        });
        DateOnly? day = dateGroup is null ? DateOnly.FromDateTime(ParseDateTime(date, null)) : null;
        var from = ParseTime(from_time, nameof(from_time));
        var to = ParseTime(to_time, nameof(to_time));

        // 一覧を取る（timetable_code の指定があればその時刻表だけ）
        var codes = new List<string>();
        object[] directions = [];
        if (!string.IsNullOrWhiteSpace(timetable_code))
        {
            codes.Add(timetable_code.Trim());
        }
        else
        {
            var list = await GetTimetableAsync(stationName, null, day, dateGroup, ct);
            var tables = Json.Items(list, "TimeTable").Select(t =>
            {
                var l = Json.Child(t, "Line");
                return new
                {
                    timetable_code = Json.Str(t, "code"),
                    line = Json.Str(l, "Name"),
                    direction = Json.Str(l, "Direction"),
                    from = Json.Str(l, "Source"),
                    day_type = Json.Str(t, "dateGroup"),
                    trains = Json.Int(t, "trainCount"),
                };
            }).ToList();
            directions = tables.Cast<object>().ToArray();
            if (!string.IsNullOrWhiteSpace(line))
            {
                var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToList();
                var matched = tables.Where(t => words.All(w => Normalize($"{t.line} {t.direction}").Contains(w))).ToList();
                if (matched.Count is > 0 and <= 4) codes.AddRange(matched.Select(t => t.timetable_code!));
                else if (matched.Count > 4) directions = matched.Cast<object>().ToArray();
            }
            if (codes.Count == 0)
            {
                return new
                {
                    station = stationName,
                    timetables = directions,
                    note = directions.Length == 0
                        ? "この駅の時刻表がありません。鉄道駅の正式名称か確かめてください。"
                        : (string.IsNullOrWhiteSpace(line) ? "" : $"line「{line}」に合う時刻表を 1〜4 つに絞れませんでした。")
                          + "路線・方面ごとの時刻表の一覧。direction は行き先の方面、from は始発の方面。trains が 0 ならその曜日区分ではこの駅に停まらない。"
                          + "line で絞るか timetable_code を指定して、もう一度呼ぶと発車時刻が分かる。",
                };
            }
        }

        var timetables = new List<object>();
        foreach (var code in codes)
        {
            var table = Json.Items(await GetTimetableAsync(stationName, code, day, dateGroup, ct), "TimeTable").FirstOrDefault();
            if (table.ValueKind != JsonValueKind.Object) continue;
            timetables.Add(DescribeTimetable(table, from, to));
        }
        return new
        {
            station = stationName,
            date = day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            timetables,
            note = "departures は「時刻 種別 列車名 行き先 番線」。時刻は日本時間で、24 時以降は 24:xx のように表す。"
                   + "「臨時・運転日注意」の列車は日によって走らないことがある。departures が空なら、その時間帯・曜日区分にこの駅を発車する列車はない。",
        };
    }

    [McpServerTool(Name = "get_train_status", ReadOnly = true, Idempotent = true)]
    [Description("""
        鉄道の運行情報（遅延・運転見合わせ・ダイヤ乱れなど）を調べる。情報はレスキューナウの提供。
        「今、中央線は遅れている？」「東京の電車の運行状況」のようなときに使う。
        prefectures で地域を、line で路線名を絞る。運行情報がない路線は平常運転と考えてよい。
        """)]
    public async Task<object> GetTrainStatus(
        [Description("都道府県コード（1〜47。例: 13=東京都, 27=大阪府）で絞る。省略時は全国")] int[]? prefectures = null,
        [Description("路線名・会社名に含まれる語で絞る（例: 中央線, 東急）")] string? line = null,
        [Description("災害による長期の運転見合わせ・ダイヤ変更も含める。既定 false（当日の遅延・見合わせなどだけ）")] bool include_long_term = false,
        CancellationToken ct = default)
    {
        var prefList = (prefectures ?? []).Distinct().ToList();
        if (prefList.Any(p => p is < 1 or > 47)) throw new McpException("prefectures は 1〜47 の都道府県コードで指定してください。");

        var result = await client.RescueNowInformationAsync(prefList, ct);
        var corporations = Json.Items(result, "Corporation").Select(c => Json.Str(c, "Name")).ToList();
        var word = string.IsNullOrWhiteSpace(line) ? null : Normalize(line.Trim());
        var items = Json.Items(result, "Information").Select(info =>
        {
            var l = Json.Child(info, "Line");
            var section = Json.Child(info, "Section") is { } s ? Json.Items(s, "Point").Select(p => StationName(p)).ToList() : [];
            var comments = Json.Items(info, "Comment").ToList();
            return new
            {
                line = Json.Str(l, "Name"),
                company = Json.Int(l, "corporationIndex") is { } i ? corporations.ElementAtOrDefault(i - 1) : null,
                status = Json.Str(info, "status"),
                title = Json.Str(info, "Title"),
                detail = (comments.FirstOrDefault(c => Json.Str(c, "status") == "long") is { ValueKind: JsonValueKind.Object } longComment
                    ? Json.Text(longComment)
                    : comments.Select(Json.Text).FirstOrDefault()),
                section = section.Count >= 2 ? $"{section[0]}〜{section[^1]}" : null,
                updated = FormatDateTime(Json.Str(info, "Datetime")),
                prefectures = Json.Items(info, "Prefecture").Select(p => Json.Str(p, "Name")).ToArray(),
            };
        })
        .Where(i => include_long_term || i.title?.Contains("長期") != true)
        .Where(i => word is null || Normalize($"{i.line} {i.company} {i.title}").Contains(word))
        .ToList();

        return new
        {
            information = items,
            note = items.Count == 0
                ? "該当する運行情報はありません（平常運転と考えてよい）。"
                : "status は運転見合わせ・列車遅延・運転状況など。updated は情報の配信日時（日本時間）。",
        };
    }

    // ---- 補助 ----

    private async Task<JsonElement> GetTimetableAsync(string station, string? code, DateOnly? date, string? dateGroup, CancellationToken ct)
    {
        try
        {
            return await client.StationTimetableAsync(station, code, date, dateGroup, ct);
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new McpException(ex.Message + await DescribeCandidatesAsync(station, ct));
        }
    }

    private static object DescribeTimetable(JsonElement table, TimeOnly? from, TimeOnly? to)
    {
        // 種別・列車名・行き先はコードで返り、名前は時刻表ごとの一覧から引く
        Dictionary<string, string?> Master(string name) =>
            Json.Items(table, name).Where(e => Json.Str(e, "code") is not null)
                .GroupBy(e => Json.Str(e, "code")!).ToDictionary(g => g.Key, g => Json.Text(g.First()));
        var kinds = Master("LineKind");
        var names = Master("LineName");
        var destinations = Master("LineDestination");
        var line = Json.Child(table, "Line");

        var departures = new List<string>();
        var seen = new HashSet<string>();
        foreach (var hour in Json.Items(table, "HourTable"))
        {
            var h = Json.Int(hour, "Hour") ?? 0;
            foreach (var minute in Json.Items(hour, "MinuteTable"))
            {
                var m = Json.Int(minute, "Minute") ?? 0;
                // 24 時以降（深夜）は TimeOnly で比べられないので 0 時台以降として扱う
                var time = new TimeOnly(h % 24, m);
                if (h < 24 && from is { } f && time < f) continue;
                if (h < 24 && to is { } t && time > t) continue;
                if (h >= 24 && to is not null) continue;
                var stop = Json.Child(minute, "Stop") ?? default;
                // 運転日の違う同じ列車（全席指定の日とそうでない日など）が別々に入るので、時刻と列車番号で 1 本にまとめる
                if (!seen.Add($"{h}:{m}/{Json.Str(stop, "trainID")}/{Json.Str(stop, "nameCode")}")) continue;

                var kind = kinds.GetValueOrDefault(Json.Str(stop, "kindCode") ?? "");
                var name = names.GetValueOrDefault(Json.Str(stop, "nameCode") ?? "");
                if (name == "無し") name = null;
                // 列車名に号数を付ける（のぞみ289号）。種別と列車名が同じ（のぞみ・のぞみ）なら種別は省く
                if (name is not null && Json.Str(stop, "Number") is { } number) name += number + "号";
                var parts = new[]
                {
                    $"{h:00}:{m:00}",
                    name is not null && kind is not null && name.StartsWith(kind, StringComparison.Ordinal) ? null : kind,
                    name,
                    destinations.GetValueOrDefault(Json.Str(stop, "destinationCode") ?? "") is { } d ? d + "行" : null,
                    Json.Str(stop, "platformNo") is { } no ? no + "番線" : null,
                    Json.Str(stop, "isStarting") == "True" ? "当駅始発" : null,
                    Json.Str(stop, "extra") == "True" ? "臨時・運転日注意" : null,
                };
                departures.Add(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))));
            }
        }
        return new
        {
            timetable_code = Json.Str(table, "code"),
            line = Json.Str(line, "Name"),
            direction = Json.Str(line, "Direction"),
            day_type = Json.Str(table, "dateGroup"),
            departures,
        };
    }

    /// <summary>駅名または駅コードから、駅コードと正式名称を得る。駅名があいまいなら候補を添えてエラーにする。</summary>
    private async Task<(string Code, string Name)> ResolveStationAsync(string station, CancellationToken ct)
    {
        if (station.All(char.IsAsciiDigit)) return (station, station);
        var result = await client.StationLightAsync(new StationQuery(station, false, [], []), ct);
        var candidates = Json.Items(result, "Point")
            .Select(p => (Code: Json.Str(Json.Child(p, "Station"), "code"), Name: StationName(p)))
            .Where(c => c.Code is not null && c.Name is not null)
            .ToList();
        if (candidates.FirstOrDefault(c => c.Name == station) is { Code: not null } exact) return (exact.Code!, exact.Name!);
        if (candidates.Count == 1) return (candidates[0].Code!, candidates[0].Name!);
        throw new McpException(candidates.Count == 0
            ? $"「{station}」に一致する駅がありません。search_stations で正確な駅名を調べてください。"
            : $"「{station}」に当てはまる駅が複数あります: {string.Join(", ", candidates.Take(8).Select(c => c.Name))}。正式名称か駅コードで指定してください。");
    }

    private async Task<string> DescribeCandidatesAsync(string station, CancellationToken ct)
    {
        if (station.All(char.IsAsciiDigit)) return "";
        try
        {
            var result = await client.StationLightAsync(new StationQuery(station, false, ["train"], []), ct);
            var candidates = Json.Items(result, "Point").Select(p => StationName(p)).Where(n => n is not null).Take(8).ToList();
            return candidates.Count == 0
                ? $"\n「{station}」に一致する鉄道駅がありません。"
                : $"\n「{station}」の候補: {string.Join(", ", candidates)}";
        }
        catch (McpException)
        {
            return "";
        }
    }

    private static List<string> ParseTypes(string[]? types)
    {
        var typeList = (types ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var allowed = new[] { "train", "plane", "ship", "bus" };
        if (typeList.FirstOrDefault(t => !allowed.Contains(t)) is { } bad)
            throw new McpException($"types に {bad} は使えません。{string.Join(" / ", allowed)} のいずれかを指定してください。");
        return typeList;
    }

    private static TimeOnly? ParseTime(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeOnly.TryParseExact(value.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        throw new McpException($"{name} は HH:MM 形式（24 時間制）で指定してください。");
    }

    /// <summary>全角英数字（ＪＲ など）を半角にそろえて比べる。</summary>
    private static string Normalize(string value) => value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
}
