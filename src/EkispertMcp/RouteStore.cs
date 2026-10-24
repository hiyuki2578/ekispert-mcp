using System.Security.Cryptography;
using System.Text;

namespace EkispertMcp;

/// <summary>search_route などで返した経路を、後続の操作のために覚えておく経路。</summary>
/// <param name="SerializeData">経路シリアライズデータ（数百文字の Base64）</param>
/// <param name="OnTimetable">ダイヤによる探索の経路か（平均待ち時間探索の経路は前後のダイヤ探索に使えない）</param>
/// <param name="CashFare">きっぷ（普通乗車券）の片道運賃の合計。分割計算はきっぷの運賃で行われるので、比べるのに使う</param>
/// <param name="CommuterPass">1・3・6 か月の定期代</param>
/// <param name="FirstRideSection">徒歩でない最初の区間のインデックス（1 始まり）</param>
public sealed record StoredRoute(string SerializeData, bool OnTimetable, int? CashFare, (int? One, int? Three, int? Six) CommuterPass, int FirstRideSection);

/// <summary>
/// 経路シリアライズデータを短い route_id で覚えておく。
/// シリアライズデータは 1 経路で数百文字あり、経路ごとにそのまま返すとトークンを消費するので、
/// MCP サーバーのプロセス内で覚えておき、ツールには route_id だけを渡す。
/// </summary>
public sealed class RouteStore
{
    private const int Capacity = 1000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, StoredRoute> _routes = new();
    private readonly Queue<string> _order = new();

    public string Add(StoredRoute route)
    {
        // 同じ経路には同じ ID を振る
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(route.SerializeData));
        var id = "r" + Convert.ToHexStringLower(hash)[..10];
        lock (_lock)
        {
            if (_routes.TryAdd(id, route))
            {
                _order.Enqueue(id);
                while (_order.Count > Capacity) _routes.Remove(_order.Dequeue());
            }
        }
        return id;
    }

    public StoredRoute? Get(string id)
    {
        lock (_lock) return _routes.GetValueOrDefault(id.Trim());
    }
}
