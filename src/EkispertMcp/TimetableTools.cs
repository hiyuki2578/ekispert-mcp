using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static EkispertMcp.EkispertTools;
using static EkispertMcp.StationTools;

namespace EkispertMcp;

/// <summary>列車・特急・飛行機・船・バスの時刻表と、路線の駅の一覧を調べるツール。</summary>
[McpServerToolType]
public sealed class TimetableTools(EkispertClient client)
{
    [McpServerTool(Name = "get_train_detail", ReadOnly = true, Idempotent = true)]
    [Description("""
        列車 1 本の停車駅と各駅の時刻を調べる。train_code は get_station_timetable・get_route_section_timetable の
        発車時刻の後ろの #番号、または get_express_timetable の code を渡す。
        「この のぞみは名古屋に何時に着く？」「この快速は○○駅に停まる？」のようなときに使う。
        """)]
    public async Task<object> GetTrainDetail(
        [Description("列車コード（例: 20283。# は付けても付けなくてもよい）")] string train_code,
        CancellationToken ct = default)
    {
        var code = (train_code ?? "").Trim().TrimStart('#');
        if (code.Length == 0 || !code.All(char.IsAsciiDigit)) throw new McpException("train_code には時刻表の #番号（数字）を指定してください。");
        var result = await client.TrainAsync(code, ct);
        var line = Json.Items(result, "Line").FirstOrDefault();
        if (line.ValueKind != JsonValueKind.Object) throw new McpException("この列車コードの列車が見つかりません。");
        return new
        {
            train = Json.Str(line, "Name"),
            comment = Json.Str(line, "GuideComment"),
            operating_days = Json.Str(line, "DriveComment"),
            stops = DescribeStops(line),
            note = "stops は停車駅と時刻（日本時間）。始発駅は発車時刻だけ、終点は到着時刻だけ。",
        };
    }

    [McpServerTool(Name = "get_express_timetable", ReadOnly = true, Idempotent = true)]
    [Description("""
        有料特急・新幹線・寝台列車の時刻表を調べる。
        まず name（列車名。例: あずさ, のぞみ, サンライズ）か station（例: 新宿）で呼ぶと、運行区間の一覧（section_code 付き）を返す。
        section_code を渡すと、その運行区間を走る列車ごとの停車駅と時刻を返す（numbers で号数を絞れる）。
        例: 「あずさ 5 号の時刻」→ name="あずさ" で section_code を調べ、section_code と numbers=[5] で呼ぶ
        """)]
    public async Task<object> GetExpressTimetable(
        [Description("列車名で絞る（例: あずさ, のぞみ）")] string? name = null,
        [Description("その駅に停まる列車に絞る（例: 新宿）")] string? station = null,
        [Description("運行区間の一覧で返した section_code（例: 31:1）")] string? section_code = null,
        [Description("号数で絞る（section_code と一緒に使う。例: [5, 7]）")] int[]? numbers = null,
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        CancellationToken ct = default)
    {
        var day = DateOnly.FromDateTime(ParseDateTime(date, null));
        if (!string.IsNullOrWhiteSpace(section_code))
        {
            var detail = await client.ExpressTimetableAsync(null, section_code.Trim(), numbers ?? [], day, ct);
            var trains = Json.Items(detail, "Line").Select(l => new
            {
                train = Json.Str(l, "Name"),
                code = Json.Str(l, "code"),
                comment = Json.Str(l, "GuideComment"),
                operating_days = Json.Str(l, "DriveComment"),
                stops = string.Join(" → ", DescribeStops(l)),
            }).ToList();
            return new
            {
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                trains,
                note = trains.Count == 0
                    ? "この運行区間・号数の列車がありません。同じ列車名の別の section_code（始発駅の違う区間）にあるかもしれません。"
                    : "stops は停車駅と時刻（日本時間）。code は get_train_detail の train_code に使える。operating_days があれば運転日に注意。",
            };
        }

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(station))
            throw new McpException("name（列車名）か station（駅名）を指定してください。運行区間が分かれば section_code を指定します。");
        JsonElement list;
        try
        {
            list = await client.ExpressTimetableAsync(string.IsNullOrWhiteSpace(station) ? null : station.Trim(), null, [], day, ct);
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new McpException(ex.Message + "\nstation は駅すぱあとの正式名称で指定してください。分からないときは search_stations で調べてください。");
        }
        var word = string.IsNullOrWhiteSpace(name) ? null : Normalize(name.Trim());
        var lines = Json.Items(list, "Line")
            .Where(l => word is null || Normalize(Json.Str(l, "Name") ?? "").Contains(word))
            .Select(l => new
            {
                train = Json.Str(l, "Name"),
                sections = Json.Items(l, "ServiceSection").Select(s => new
                {
                    section_code = Json.Str(s, "code"),
                    section = Json.Text(s),
                }).ToArray(),
            }).ToList();
        return new
        {
            trains = lines,
            note = lines.Count == 0
                ? "該当する特急列車がありません。"
                : "列車ごとの運行区間（出発－到着）。section_code を指定してもう一度呼ぶと、その区間の列車の停車駅と時刻が分かる。",
        };
    }

    [McpServerTool(Name = "get_flight_or_ferry_timetable", ReadOnly = true, Idempotent = true)]
    [Description("""
        飛行機（国内線）またはフェリー・高速船の路線の時刻表を、出発地と到着地で調べる。
        飛行機の from / to は空港のある都市名（例: 東京, 大阪, 札幌, 福岡, 那覇。東京は羽田、大阪は伊丹の便）、
        船は港の名前（例: 久里浜, 金谷, 竹芝）で渡す。路線が見つからないときは候補の路線名を返す。
        便ごとの出発・到着時刻と航空会社・便名を返す。
        """)]
    public async Task<object> GetFlightOrFerryTimetable(
        [Description("出発地（飛行機は都市名、船は港名）")] string from,
        [Description("到着地（飛行機は都市名、船は港名）")] string to,
        [Description("plane=飛行機（既定）, ship=フェリー・高速船")] string kind = "plane",
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new McpException("from と to を指定してください。");
        var type = ParseOption(kind, nameof(kind), new Dictionary<string, string> { ["plane"] = "plane", ["ship"] = "ship" });
        var day = DateOnly.FromDateTime(ParseDateTime(date, null));
        var f = PlaceName(from);
        var t = PlaceName(to);

        // 路線名は「空路(東京－大阪)」「フェリー(久里浜－金谷)」の形。括弧の中の 2 地点で向きを決める
        var routes = new List<(string Name, bool Up)>();
        var candidates = new List<string>();
        foreach (var railName in await client.GetRailNamesAsync(type, ct))
        {
            var open = railName.IndexOf('(');
            var close = railName.LastIndexOf(')');
            if (open < 0 || close <= open) continue;
            var ends = railName[(open + 1)..close].Split('－');
            if (ends.Length != 2) continue;
            var (a, b) = (Normalize(ends[0]), Normalize(ends[1]));
            if (a.Contains(f) && b.Contains(t)) routes.Add((railName, false));
            else if (a.Contains(t) && b.Contains(f)) routes.Add((railName, true));
            else if (a.Contains(f) || b.Contains(f) || a.Contains(t) || b.Contains(t)) candidates.Add(railName);
        }
        if (routes.Count == 0)
        {
            return new
            {
                timetables = Array.Empty<object>(),
                candidates = candidates.Take(30).ToList(),
                note = candidates.Count == 0
                    ? $"「{from}」「{to}」を含む{(type == "plane" ? "空路" : "航路")}がありません。飛行機は都市名（東京, 大阪 など）、船は港名で指定してください。"
                    : "from と to を結ぶ路線が見つかりませんでした。candidates は from か to を含む路線名なので、括弧の中の地名で指定し直してください。",
            };
        }

        var timetables = new List<object>();
        foreach (var (railName, up) in routes.Take(4))
        {
            var result = await client.PlaneShipTimetableAsync(type, railName, up, day, ct);
            var table = Json.Child(result, "TimeTable") ?? default;
            var trips = Json.Items(table, "Line").Select(l =>
            {
                var carrier = Json.Child(l, "Name") is { } n ? Json.Attr(l, "Name", "abbreviation") ?? Json.Text(n) : null;
                var number = Json.Str(l, "Number");
                var parts = new[]
                {
                    FormatTime(Json.Str(Json.Child(l, "DepartureState"), "Datetime")) + "発",
                    FormatTime(Json.Str(Json.Child(l, "ArrivalState"), "Datetime")) + "着",
                    carrier is null ? (number is null ? null : number + "便") : $"{carrier}{number}便",
                };
                return string.Join(" ", parts.Where(p => p is not null));
            }).ToList();
            timetables.Add(new
            {
                route = railName,
                from = Json.Str(Json.Child(table, "Station"), "Name"),
                to = Json.Str(table, "Direction"),
                trips,
            });
        }
        return new
        {
            date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            timetables,
            note = "trips は便ごとの出発・到着時刻（日本時間）と航空会社・便名。時刻表の日付の運航便で、欠航や臨時便は含まない。",
        };
    }

    [McpServerTool(Name = "get_bus_timetable", ReadOnly = true, Idempotent = true)]
    [Description("""
        2 つのバス停の間を走る路線バスの時刻表を調べる。発車時刻・到着時刻・系統名・行き先・乗り場を返す。
        バス停名は「渋谷駅／京王バス」のように会社名が付いた正式名称で渡す（search_stations で types=["bus"] を指定して調べる）。
        2 つのバス停は同じ会社の、同じ系統で結ばれている必要がある。
        """)]
    public async Task<object> GetBusTimetable(
        [Description("乗るバス停（例: 渋谷駅／京王バス）またはバス停のコード")] string from,
        [Description("降りるバス停（例: 新宿駅西口／京王バス）またはバス停のコード")] string to,
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        [Description("この時刻 (HH:MM) 以降の発車だけ返す")] string? from_time = null,
        [Description("この時刻 (HH:MM) 以前の発車だけ返す")] string? to_time = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new McpException("from と to を指定してください。");
        var day = DateOnly.FromDateTime(ParseDateTime(date, null));
        var start = ParseTime(from_time, nameof(from_time));
        var end = ParseTime(to_time, nameof(to_time));

        JsonElement result;
        try
        {
            result = await client.BusTimetableAsync(from.Trim(), to.Trim(), day, ct);
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            throw new McpException(ex.Message + await DescribeBusStopCandidatesAsync([from.Trim(), to.Trim()], ct));
        }

        var table = Json.Child(result, "TimeTable") ?? default;
        var buses = Json.Items(table, "Line").Select(l =>
        {
            var dep = Json.Child(l, "DepartureState");
            var arr = Json.Child(l, "ArrivalState");
            return (Departure: FormatTime(Json.Str(dep, "Datetime")), Text: string.Join(" ", new[]
            {
                FormatTime(Json.Str(dep, "Datetime")) + "発",
                FormatTime(Json.Str(arr, "Datetime")) is { } a ? a + "着" : null,
                Json.Str(l, "Name"),
                Json.Str(Json.Child(Json.Child(l, "Destination") ?? default, "Station"), "Name") is { } d ? d + "行" : null,
                Json.Str(dep, "no") is { } no ? no + "番乗り場" : null,
            }.Where(p => p is not null)));
        })
        .Where(b => b.Departure is null
                    || !TimeOnly.TryParse(b.Departure, CultureInfo.InvariantCulture, out var time)
                    || ((start is null || time >= start) && (end is null || time <= end)))
        .Select(b => b.Text)
        .ToList();
        return new
        {
            from = Json.Str(Json.Child(table, "Station"), "Name") ?? from.Trim(),
            to = to.Trim(),
            date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            buses,
            note = buses.Count == 0
                ? "この時間帯に 2 つのバス停を結ぶバスはありません。"
                : "buses は「発車時刻 到着時刻 系統 行き先 乗り場」（日本時間）。道路の混雑で遅れることがある。",
        };
    }

    [McpServerTool(Name = "get_line_stations", ReadOnly = true, Idempotent = true)]
    [Description("""
        路線の駅を順に並べて返す（例: 「中央線快速の駅は？」「山手線の駅一覧」）。
        line は駅すぱあとの路線名（例: ＪＲ中央線快速, ＪＲ山手線内回り, 東京メトロ丸ノ内線）。
        路線名が見つからないときは、その名前で始まる路線名の候補を返す。
        """)]
    public async Task<object> GetLineStations(
        [Description("路線名（例: ＪＲ中央線快速, 東急東横線）。全角・半角はどちらでもよい")] string line,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(line)) throw new McpException("line を指定してください。");
        // 駅すぱあとの路線名は「ＪＲ」などの英字が全角
        var railName = ToFullWidthAlnum(line.Trim());
        var stations = new List<object>();
        try
        {
            var result = await client.StationAsync(railName, null, ct);
            stations = Json.Items(result, "Point").Select(p => (object)new
            {
                name = StationName(p),
                code = Json.Str(Json.Child(p, "Station"), "code"),
                prefecture = Json.Str(Json.Child(p, "Prefecture"), "Name"),
            }).ToList();
        }
        catch (EkispertApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // 路線名の誤り。下で候補を返す
        }
        if (stations.Count > 0) return new { line = railName, stations, note = "路線の駅を順に並べたもの。" };

        // 会社名を省いた名前（山手線 → ＪＲ山手線内回り など）も探す
        var candidates = new List<string>();
        foreach (var prefix in new[] { railName, "ＪＲ" + railName })
        {
            candidates = Json.Items(await client.RailAsync(prefix, null, 1, ct), "Line")
                .Select(l => Json.Str(l, "Name")).OfType<string>().Take(30).ToList();
            if (candidates.Count > 0) break;
        }
        return new
        {
            line = railName,
            stations,
            candidates,
            note = candidates.Count == 0
                ? $"「{railName}」で始まる路線がありません。会社名から始まる正式な路線名（例: ＪＲ中央線快速, 東京メトロ銀座線）で指定してください。"
                : "路線名が見つかりませんでした。candidates の路線名で呼び直してください。",
        };
    }

    // ---- 補助 ----

    // 空路の路線名は空港ではなく都市名などで付いているので、よく使う空港名を読み替える
    private static readonly Dictionary<string, string> AirportAliases = new()
    {
        ["羽田"] = "東京",
        ["伊丹"] = "大阪",
        ["那覇"] = "沖縄",
        ["新千歳"] = "千歳",
        ["札幌"] = "千歳",
        ["関空"] = "関西",
        ["セントレア"] = "中部",
    };

    private static string PlaceName(string value)
    {
        var name = Normalize(value.Trim());
        foreach (var suffix in new[] { "空港", "港" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return AirportAliases.TryGetValue(name, out var alias) ? alias : name;
    }

    /// <summary>Stop 要素の並びを「駅 着時刻着 発時刻発」の文字列にする。</summary>
    private static List<string> DescribeStops(JsonElement line) =>
        Json.Items(line, "Stop").Select(s =>
        {
            var arrival = FormatTime(Json.Str(Json.Child(s, "ArrivalState"), "Datetime"));
            var departure = FormatTime(Json.Str(Json.Child(s, "DepartureState"), "Datetime"));
            var times = arrival == departure && arrival is not null
                ? arrival
                : string.Join(" ", new[] { arrival is null ? null : arrival + "着", departure is null ? null : departure + "発" }.Where(t => t is not null));
            var name = StationName(Json.Child(s, "Point"));
            return times.Length == 0 ? name ?? "" : $"{name} {times}";
        }).ToList();

    private async Task<string> DescribeBusStopCandidatesAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var name in names.Where(n => !n.All(char.IsAsciiDigit)))
        {
            try
            {
                var result = await client.StationLightAsync(new StationQuery(name.Split('／')[0], false, ["bus"], []), ct);
                var candidates = Json.Items(result, "Point").Select(p => StationName(p)).OfType<string>().ToList();
                if (candidates.Contains(name)) continue;
                lines.Add(candidates.Count == 0 ? $"「{name}」に一致するバス停がありません。" : $"「{name}」の候補: {string.Join(", ", candidates.Take(10))}");
            }
            catch (McpException)
            {
                // 候補の取得に失敗しても元のエラーを返す
            }
        }
        return lines.Count == 0 ? "" : "\n" + string.Join("\n", lines);
    }

    /// <summary>半角英数字を全角にする（駅すぱあとの路線名は「ＪＲ」のように英数字が全角）。</summary>
    private static string ToFullWidthAlnum(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) ? (char)(c - '!' + '！') : c).ToArray());
}
