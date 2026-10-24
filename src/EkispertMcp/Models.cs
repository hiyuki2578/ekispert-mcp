using System.Globalization;
using System.Text.Json;

namespace EkispertMcp;

/// <summary>駅簡易情報の検索条件。Types は train / plane / ship / bus、PrefectureCodes は JIS の都道府県コード（1〜47）。</summary>
public sealed record StationQuery(
    string Name,
    bool Partial,
    IReadOnlyList<string> Types,
    IReadOnlyList<int> PrefectureCodes);

/// <summary>
/// 経路探索の条件。値は駅すぱあと API の表記に合わせてある。
/// SearchType: departure / arrival / firstTrain / lastTrain / plain、Sort: ekispert / price / time / transfer / teiki、
/// Seat: free / reserved / green、Ticket: ic / cash、TeikiKind: bussiness / university / highSchool / juniorHighSchool。
/// DateTime は日本時間。
/// </summary>
public sealed record RouteQuery(
    string From,
    string To,
    IReadOnlyList<string> Via,
    DateTime DateTime,
    string SearchType = "departure",
    string Sort = "ekispert",
    int AnswerCount = 5,
    string Seat = "free",
    string Ticket = "ic",
    string TeikiKind = "bussiness",
    bool UseAirline = true,
    bool UseShinkansen = true,
    bool UseNozomi = true,
    bool UseExpress = true,
    bool UseHighwayBus = true,
    bool UseLocalBus = true,
    bool UseFerry = true,
    bool IncludeStops = false);

/// <summary>
/// 駅すぱあと API の JSON を読む補助関数。
/// 同じキーが 1 件ならオブジェクト、複数なら配列で返り、キー自体がないこともある。値はすべて文字列で、
/// 属性を持つテキストは {"text": …} になる（公式の「利用上の注意」）。
/// </summary>
internal static class Json
{
    /// <summary>キーの値を、オブジェクトでも配列でも要素の並びとして返す。キーがなければ空。</summary>
    public static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return [];
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v];
    }

    public static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) ? v : null;

    /// <summary>文字列の値。{"text": …} の形ならその text を返す。</summary>
    public static string? Str(JsonElement parent, string name)
    {
        if (Child(parent, name) is not { } v) return null;
        return Text(v);
    }

    public static string? Str(JsonElement? parent, string name) => parent is { } p ? Str(p, name) : null;

    public static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.Object => v.TryGetProperty("text", out var t) ? Text(t) : null,
        _ => null,
    };

    public static int? Int(JsonElement parent, string name) =>
        int.TryParse(Str(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static int? Int(JsonElement? parent, string name) => parent is { } p ? Int(p, name) : null;

    /// <summary>{"text": "bus", "detail": "local"} のような要素の detail 属性。</summary>
    public static string? Attr(JsonElement parent, string name, string attribute) =>
        Child(parent, name) is { ValueKind: JsonValueKind.Object } v ? Str(v, attribute) : null;
}
