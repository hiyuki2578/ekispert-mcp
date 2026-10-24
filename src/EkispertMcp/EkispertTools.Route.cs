using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EkispertMcp;

/// <summary>search_route の route_id を使って、経路を元に続けて調べるツール。</summary>
public sealed partial class EkispertTools
{
    [McpServerTool(Name = "apply_commuter_pass", ReadOnly = true, Idempotent = true)]
    [Description("""
        search_route で見つけた経路に、持っている定期券を当てはめて運賃を計算し直す。定期券の区間は運賃がかからない。
        「高円寺〜新宿の定期を持っているので、横浜までの交通費は新宿から先の分だけ」のような交通費精算に使う。
        定期券は次のどちらかで渡す:
        - commuter_pass_route_id: 定期券の区間を search_route で探した結果の route_id（例: from=高円寺, to=新宿 で探した経路）
        - commuter_pass_route: 「駅名:路線名:駅名:路線名:駅名」の形の定期経路（例: 高円寺:ＪＲ中央線快速:新宿）。
          路線名は search_route の結果の commuter_pass_route（「--」区切り）に出る駅すぱあとの路線名を使う
        結果は search_route と同じ形で、fares の type=WithTeiki の区間が定期券で乗れる区間。
        """)]
    public async Task<object> ApplyCommuterPass(
        [Description("運賃を計算し直す経路の route_id（search_route の結果）")] string route_id,
        [Description("定期券の区間を search_route で探した経路の route_id")] string? commuter_pass_route_id = null,
        [Description("定期経路（例: 高円寺:ＪＲ中央線快速:新宿）。commuter_pass_route_id と同時には使わない")] string? commuter_pass_route = null,
        CancellationToken ct = default)
    {
        var route = GetStoredRoute(route_id);
        var hasId = !string.IsNullOrWhiteSpace(commuter_pass_route_id);
        var hasRoute = !string.IsNullOrWhiteSpace(commuter_pass_route);
        if (hasId == hasRoute) throw new McpException("commuter_pass_route_id か commuter_pass_route のどちらか一方を指定してください。");

        string? teikiSerialize = null;
        if (hasId)
        {
            teikiSerialize = GetStoredRoute(commuter_pass_route_id!).TeikiSerializeData
                ?? throw new McpException("commuter_pass_route_id の経路には定期券の情報がありません。定期券の区間を search_route で探し直してください。");
        }
        var teikiRoute = hasRoute ? commuter_pass_route!.Trim().Replace("--", ":") : null;

        var result = await client.AssignTeikiAsync(route.SerializeData, teikiSerialize, teikiRoute, ct);
        var routes = Json.Items(result, "Course").Select(c => DescribeCourse(c, false)).ToList();
        return new
        {
            routes,
            note = "金額や項目の意味は search_route と同じで、fare・total は定期券の区間を除いた額。"
                   + "fares の type=WithTeiki（0 円）が定期券で乗れる区間。定期券の区間と経路が重ならなければ金額は変わらない。",
        };
    }

    [McpServerTool(Name = "change_route_fare", ReadOnly = true, Idempotent = true)]
    [Description("""
        search_route で見つけた経路はそのままで、運賃の種類（IC / きっぷ）、特急・新幹線の座席（自由席 / 指定席 / グリーン車）、
        定期券の種類（通勤 / 通学）を切り替えて金額を計算し直す。経路を探し直さずに「指定席にしたらいくら？」が分かる。
        結果は search_route と同じ形。
        """)]
    public async Task<object> ChangeRouteFare(
        [Description("search_route の結果の route_id")] string route_id,
        [Description("運賃: ic=IC カード, cash=きっぷ。省略時は変えない")] string? ticket = null,
        [Description("特急・新幹線の座席: non_reserved=自由席, reserved=指定席, green=グリーン車。省略時は変えない")] string? seat = null,
        [Description("定期の種類: commute=通勤, university=通学(大学), high_school=通学(高校), junior_high_school=通学(中学)。省略時は変えない")] string? commuter_pass = null,
        CancellationToken ct = default)
    {
        var route = GetStoredRoute(route_id);
        if (ticket is null && seat is null && commuter_pass is null)
            throw new McpException("ticket・seat・commuter_pass のどれかを指定してください。");

        var fareType = ticket is null ? null : ParseOption(ticket, nameof(ticket), new Dictionary<string, string>
        {
            ["ic"] = "FareICCard",
            ["cash"] = "Fare",
        });
        // グリーン料金は IC 用（Suica グリーン券）ときっぷ用の 2 種類があるので、運賃の種類に合わせて先に探す方を変える
        var chargeTypes = seat is null ? null : ParseOption(seat, nameof(seat), new Dictionary<string, string[]>
        {
            ["non_reserved"] = ["Free"],
            ["reserved"] = ["Reserved"],
            ["green"] = fareType == "Fare" ? ["Green", "GreenSuica"] : ["GreenSuica", "Green"],
        });
        var teikiKind = commuter_pass is null ? null : ParseOption(commuter_pass, nameof(commuter_pass), new Dictionary<string, string>
        {
            ["commute"] = "bussiness",
            ["university"] = "university",
            ["high_school"] = "highSchool",
            ["junior_high_school"] = "juniorHighSchool",
        });

        // 区間（fromLineIndex〜toLineIndex）ごとに、希望の種類の Price の index を選ぶ。なければ今の選択のまま
        var prices = Json.Items(route.Course, "Price").ToList();
        var unavailable = new List<string>();
        List<string> SelectIndexes(string kind, string[]? wanted)
        {
            var indexes = new List<string>();
            foreach (var group in prices.Where(p => Json.Str(p, "kind") == kind)
                         .GroupBy(p => (Json.Int(p, "fromLineIndex"), Json.Int(p, "toLineIndex"))))
            {
                var current = group.FirstOrDefault(p => Json.Str(p, "selected") != "false");
                var chosen = wanted?.Select(w => group.FirstOrDefault(p => Json.Str(p, "Type") == w))
                    .FirstOrDefault(p => p.ValueKind == JsonValueKind.Object) ?? default;
                if (wanted is not null && chosen.ValueKind != JsonValueKind.Object)
                {
                    // 料金のいらない区間（0 円の自由席だけの区間）は黙って今のままにする
                    if (!(kind == "Charge" && group.All(p => Json.Int(p, "Oneway") is null or 0)))
                        unavailable.Add($"{(kind == "Fare" ? "運賃" : "料金")}（{group.Key.Item1}〜{group.Key.Item2} 番目の区間）");
                    chosen = current;
                }
                if (Json.Str(chosen.ValueKind == JsonValueKind.Object ? chosen : current, "index") is { } index) indexes.Add(index);
            }
            return indexes;
        }
        var fareIndexes = SelectIndexes("Fare", fareType is null ? null : [fareType]);
        var chargeIndexes = SelectIndexes("Charge", chargeTypes);
        if (fareIndexes.Count == 0 && chargeIndexes.Count == 0 && teikiKind is null)
            throw new McpException("この経路には切り替えられる運賃・料金がありません。");

        var result = await client.RecalculateAsync(route.SerializeData, fareIndexes, chargeIndexes, teikiKind, ct);
        var routes = Json.Items(result, "Course").Select(c => DescribeCourse(c, false)).ToList();
        return new
        {
            routes,
            note = "金額や項目の意味は search_route と同じ。"
                   + (unavailable.Count == 0 ? "" : $"次の区間には指定の種類がないので、元のままにした: {string.Join("、", unavailable)}。"),
        };
    }

    [McpServerTool(Name = "get_commuter_pass_refund", ReadOnly = true, Idempotent = true)]
    [Description("""
        search_route で見つけた経路の定期券を、途中で払い戻したときの金額（払い戻し額・使用済みの額・手数料）を計算する。
        「9 月 1 日から使っている 6 か月定期を 10 月末で解約したらいくら戻る？」のようなときに使う。
        定期券の区間・種類は route_id の経路のもの（search_route の commuter_pass で指定した種類）になる。
        """)]
    public async Task<object> GetCommuterPassRefund(
        [Description("定期券の区間を search_route で探した経路の route_id")] string route_id,
        [Description("定期券の有効開始日 (YYYY-MM-DD)")] string start_date,
        [Description("定期券の有効期間（月）: 1, 3, 6, 12。既定 6")] int months = 6,
        [Description("払い戻す日 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? refund_date = null,
        [Description("定期券を買った日 (YYYY-MM-DD)。運賃改定の前に買ったときだけ指定する。省略時は有効開始日")] string? buy_date = null,
        [Description("同じ鉄道会社で別の区間の定期券に買い直す（区間変更）として計算する。既定 false（解約）")] bool change_section = false,
        CancellationToken ct = default)
    {
        var route = GetStoredRoute(route_id);
        if (months is not (1 or 3 or 6 or 12)) throw new McpException("months は 1 / 3 / 6 / 12 のいずれかで指定してください。");
        var start = ParseDate(start_date, nameof(start_date)) ?? throw new McpException("start_date を指定してください。");
        var refund = ParseDate(refund_date, nameof(refund_date)) ?? DateOnly.FromDateTime(EkispertClient.NowJst());
        var buy = ParseDate(buy_date, nameof(buy_date));

        var result = await client.RepaymentAsync(route.SerializeData, start, buy, refund, months, change_section, ct);
        var list = Json.Child(result, "RepaymentList") ?? default;
        // 払い戻しの単位（会社ごとに分かれることがある）と、その区間
        var sections = Json.Items(Json.Child(result, "TeikiRoute") ?? default, "TeikiRouteSection")
            .Select(s => Json.Items(s, "Point").Select(p => StationName(p)).ToList())
            .ToList();
        var tickets = Json.Items(list, "RepaymentTicket").Select(t =>
        {
            var from = Json.Int(t, "fromTeikiRouteSectionIndex") ?? 1;
            var to = Json.Int(t, "toTeikiRouteSectionIndex") ?? from;
            var stations = sections.Skip(from - 1).Take(to - from + 1).SelectMany(s => s).ToList();
            return new
            {
                section = stations.Count >= 2 ? $"{stations[0]}〜{stations[^1]}" : null,
                paid = Json.Int(t, "payPriceValue"),
                used = Json.Int(t, "usedPriceValue"),
                fee = Json.Int(t, "feePriceValue"),
                refund = Json.Int(t, "repayPriceValue"),
                calculated = Json.Str(t, "calculateTarget") != "False" && Json.Str(t, "state") == "0",
                section_change_possible = Json.Str(t, "changeableSection") is { } c ? c == "true" : (bool?)null,
            };
        }).ToList();
        return new
        {
            start_date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            refund_date = refund.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            months,
            tickets,
            total_refund = tickets.Sum(t => t.refund ?? 0),
            note = "金額は円。paid は購入金額、used は使用済みの額、fee は手数料、refund は払い戻される額。"
                   + "calculated=false の定期券は駅すぱあとで計算できない（未対応の会社など）ので、鉄道会社に問い合わせる。"
                   + "実際の払い戻し額は鉄道会社の規則で決まるので目安として使う。",
        };
    }

    [McpServerTool(Name = "get_route_stops", ReadOnly = true, Idempotent = true)]
    [Description("""
        search_route で見つけた経路で、乗る列車の停車駅と各駅の時刻を調べる。
        whole_train=true なら区間の前後も含めて、その列車の始発から終点まで返す（「この電車はどこ行き？どこから来る？」）。
        include_passing=true なら、時刻の代わりに通過する駅も含めて経路で通る駅をすべて返す（time_type=average の経路でも使える）。
        """)]
    public async Task<object> GetRouteStops(
        [Description("search_route の結果の route_id")] string route_id,
        [Description("調べる区間（segments の 1 始まりの番号）。省略時は最初に乗る区間。include_passing=true で省略すると経路全体")] int? segment = null,
        [Description("その列車の始発から終点までの停車駅を返す")] bool whole_train = false,
        [Description("通過駅も含めて、経路で通る駅をすべて返す（時刻は返らない）")] bool include_passing = false,
        CancellationToken ct = default)
    {
        var route = GetStoredRoute(route_id);
        if (segment is < 1) throw new McpException("segment は 1 以上で指定してください。");

        if (include_passing)
        {
            var passing = await client.PassStationAsync(route.SerializeData, segment, ct);
            var names = Json.Items(passing, "Point").Select(p => StationName(p)).ToList();
            return new
            {
                segment,
                stations = names,
                note = "経路（segment を指定したときはその区間）で通る駅を、停車しない駅も含めて順に並べたもの。",
            };
        }

        if (!route.OnTimetable)
            throw new McpException("この経路は平均待ち時間探索（time_type=average）の結果なので、停車駅の時刻は分かりません。include_passing=true なら通る駅を返せます。");
        var result = await client.CourseStationAsync(route.SerializeData, segment ?? route.FirstRideSection, !whole_train, ct);
        var points = Json.Items(result, "Point").ToList();
        var lines = Json.Items(result, "Line").ToList();
        // Line[i] は Point[i] から Point[i+1] までの区間で、出発時刻と到着時刻を持つ
        var stops = points.Select((p, i) =>
        {
            var arrival = i > 0 ? FormatTime(Json.Str(Json.Child(lines[i - 1], "ArrivalState"), "Datetime")) : null;
            var departure = i < lines.Count ? FormatTime(Json.Str(Json.Child(lines[i], "DepartureState"), "Datetime")) : null;
            var times = string.Join(" ", new[] { arrival is null ? null : arrival + "着", departure is null ? null : departure + "発" }.Where(t => t is not null));
            return times.Length == 0 ? StationName(p) : $"{StationName(p)} {times}";
        }).ToList();
        return new
        {
            segment = segment ?? route.FirstRideSection,
            stops,
            note = whole_train
                ? "その列車の始発から終点までの停車駅と時刻（日本時間）。"
                : "経路の区間の停車駅と時刻（日本時間）。whole_train=true で始発から終点まで分かる。",
        };
    }

    [McpServerTool(Name = "get_route_section_timetable", ReadOnly = true, Idempotent = true)]
    [Description("""
        search_route で見つけた経路の区間（鉄道のみ）について、その駅から同じ方面へ発車する列車の時刻表を返す。
        「この区間の電車は何分おき？」「帰りの時間帯の本数は？」のようなときに使う。
        発車時刻の後ろの #番号 は get_train_detail の train_code に渡すと、その列車の停車駅と時刻が分かる。
        """)]
    public async Task<object> GetRouteSectionTimetable(
        [Description("search_route の結果の route_id")] string route_id,
        [Description("調べる区間（segments の 1 始まりの番号、鉄道の区間のみ）。省略時は最初に乗る区間")] int? segment = null,
        [Description("この時刻 (HH:MM) 以降の発車だけ返す")] string? from_time = null,
        [Description("この時刻 (HH:MM) 以前の発車だけ返す")] string? to_time = null,
        CancellationToken ct = default)
    {
        var route = GetStoredRoute(route_id);
        if (segment is < 1) throw new McpException("segment は 1 以上で指定してください。");
        var result = await client.CourseTimetableAsync(route.SerializeData, segment ?? route.FirstRideSection, ct);
        var table = Json.Items(result, "TimeTable").FirstOrDefault();
        if (table.ValueKind != JsonValueKind.Object)
            throw new McpException("この区間の時刻表がありません（鉄道の区間のみ対応）。");
        return new
        {
            segment = segment ?? route.FirstRideSection,
            station = Json.Str(Json.Child(table, "Station"), "Name"),
            timetable = StationTools.DescribeTimetable(table, ParseTime(from_time, nameof(from_time)), ParseTime(to_time, nameof(to_time))),
            note = StationTools.DeparturesNote,
        };
    }

    [McpServerTool(Name = "get_coupons", ReadOnly = true, Idempotent = true)]
    [Description("""
        回数券を調べる。route_id を渡すとその経路で使える回数券、name で回数券の名前（例: 新幹線）を絞り込む。
        名前が 1 つに決まると、区間ごとの金額・枚数・有効期間を返す。JR の回数券は多くが販売を終えているので、今も買えるかは鉄道会社で確かめる。
        """)]
    public async Task<object> GetCoupons(
        [Description("回数券の名前の一部（例: 新幹線）。省略時はすべて")] string? name = null,
        [Description("その経路で使える回数券を調べるときの route_id（search_route の結果）")] string? route_id = null,
        CancellationToken ct = default)
    {
        JsonElement detail;
        string? couponName = null;
        if (!string.IsNullOrWhiteSpace(route_id))
        {
            try
            {
                detail = await client.CouponDetailAsync(null, GetStoredRoute(route_id).SerializeData, ct);
            }
            catch (EkispertApiException ex) when (ex.Code == "E1154")
            {
                // この経路で使える回数券がないときはエラーで返るので、結果なしとして返す
                return new { coupon = (string?)null, coupons = Array.Empty<object>(), note = "この経路で使える回数券はありません。" };
            }
        }
        else
        {
            var word = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            var list = await client.CouponListAsync(word, ct);
            var names = Json.Items(list, "Coupon").Select(c => Json.Str(c, "Name")).Where(n => n is not null).ToList();
            couponName = names.FirstOrDefault(n => n == word) ?? (names.Count == 1 ? names[0] : null);
            if (couponName is null)
            {
                return new
                {
                    coupons = names,
                    note = names.Count == 0
                        ? "該当する回数券がありません。"
                        : "回数券の名前の一覧。name に 1 つを指定して呼ぶと、区間ごとの金額・枚数が分かる。",
                };
            }
            detail = await client.CouponDetailAsync(couponName, null, ct);
        }

        var coupons = Json.Items(detail, "Coupon").Select(c =>
        {
            var d = Json.Child(c, "Detail");
            return new
            {
                name = Json.Str(c, "Name"),
                price = Json.Int(Json.Child(d ?? default, "Price"), "Amount"),
                count = Json.Int(d, "Count"),
                valid_months = Json.Int(d, "ValidPeriod"),
                both_directions = Json.Str(d, "Direction") is { } dir ? dir == "Both" : (bool?)null,
            };
        }).ToList();
        return new
        {
            coupon = couponName,
            coupons,
            note = coupons.Count == 0
                ? "使える回数券がありません。"
                : "name は「回数券の名前:区間の端:区間の端」。price は 1 冊（count 枚つづり）の金額（円）、valid_months は有効期間（月）。",
        };
    }

    private static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw new McpException($"{name} は YYYY-MM-DD 形式で指定してください。");
    }

    internal static TimeOnly? ParseTime(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeOnly.TryParseExact(value.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        throw new McpException($"{name} は HH:MM 形式（24 時間制）で指定してください。");
    }
}
