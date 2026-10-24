# ekispert-mcp

[駅すぱあと API](https://docs.ekispert.com/v1/) で公共交通機関の経路・運賃・定期代を調べる、ローカル用の MCP サーバー（stdio）。

> [!IMPORTANT]
> **非公式**のツールです。株式会社ヴァル研究所とは関係がありません（公式の MCP サーバーは [こちら](https://docs.ekispert.com/v1/for-ai/mcp-server/)）。
> 利用には駅すぱあと API のアクセスキーが必要で、API の利用規約と料金はキーの契約に従います。

## ツール

| ツール | 使う API | 内容 |
|---|---|---|
| `search_route` | 経路探索 `/search/course/extreme`、探索条件生成 `/toolbox/course/condition` | 駅から駅への経路を探索する。経路ごとに出発/到着時刻、所要時間、乗換回数、運賃（片道・往復）、特急料金などの料金、定期代と、区間ごとの路線・行き先・発着番線を返す |
| `search_route_other_trains` | 前後のダイヤ探索 `/search/course/pattern` | `search_route` の経路の 1 本前・1 本後の便などを探す |
| `split_route_fare` | 運賃の分割計算 `/course/fare/divided`、定期代の分割計算 `/course/teiki/divided` | `search_route` の経路の乗車券・定期券を途中の駅で分けて買うと安くなるかを計算する |
| `apply_commuter_pass` | 経路の再現／定期券利用 `/course/edit` | `search_route` の経路に持っている定期券を当てはめ、定期区間を除いた運賃を出す |
| `change_route_fare` | 探索結果の運賃切り替え `/course/recalculate` | `search_route` の経路のまま、IC / きっぷ、自由席 / 指定席 / グリーン車、定期券の種類を切り替えて計算し直す |
| `get_commuter_pass_refund` | 定期券の払い戻し計算 `/course/repayment` | `search_route` の経路の定期券を途中で払い戻したときの金額 |
| `get_route_stops` | 区間の駅情報 `/course/station`、通過駅情報 `/course/passStation` | `search_route` の経路で乗る列車の停車駅と時刻（始発〜終点も可）、通過駅を含む通る駅 |
| `get_route_section_timetable` | 区間時刻表 `/course/timetable` | `search_route` の経路の区間を同じ方面へ走る列車の時刻表 |
| `get_coupons` | 回数券一覧 `/coupon/list`、回数券詳細 `/coupon/detail` | 回数券の区間・金額・枚数（名前か経路で調べる） |
| `search_stations` | 駅簡易情報 `/station/light`、駅情報 `/station`（旧駅名） | 駅・空港・港・バス停を名前で検索する（最大 30 件）。見つからなければ旧駅名として引く |
| `search_nearby_stations` | 周辺駅検索 `/geo/station`・`/address/station` | 住所か緯度経度から、近くの駅・バス停などを近い順に探す |
| `get_station_info` | 駅付加情報 `/station/info` | 乗り入れ路線、最寄りの路線、出口案内、バリアフリー設備 |
| `search_reachable_stations` | 範囲探索 `/search/multipleRange` | 起点の駅（最大 5 駅）のすべてから、指定した時間内に行ける駅を探す |
| `get_station_timetable` | 鉄道駅時刻表 `/operationLine/timetable` | 駅の路線・方面ごとの時刻表（発車時刻・種別・列車名・行き先・番線） |
| `get_train_status` | 鉄道運行情報（レスキューナウ） | 遅延・運転見合わせ・ダイヤ乱れなどの運行情報 |
| `get_train_detail` | 鉄道駅時刻表の列車情報 `/station/timetable/train` | 時刻表の列車コードから、その列車の停車駅と時刻 |
| `get_express_timetable` | 特急列車時刻表 `/train/timetable` | 特急・新幹線・寝台列車の運行区間と、列車ごとの停車駅・時刻 |
| `get_flight_or_ferry_timetable` | 航空路線時刻表 `/plane/timetable`、航海路線時刻表 `/ship/timetable`、平均路線情報 `/rail` | 国内線・フェリー・高速船の便ごとの時刻 |
| `get_bus_timetable` | バス路線時刻表 `/bus/timetable` | 2 つのバス停の間の路線バスの時刻表 |
| `get_line_stations` | 駅情報 `/station`、平均路線情報 `/rail` | 路線の駅を順に並べる |

`search_stations` と探索条件生成は課金対象外の API。それ以外の API が使えるかは、アクセスキーの契約による。

`search_route` の主な引数:

| 引数 | 既定 | 内容 |
|---|---|---|
| `from` / `to` | （必須） | 駅名（駅すぱあとの正式名称）または駅コード |
| `via` | なし | 経由駅（最大 3 駅） |
| `date` / `time` | 今（日本時間） | `YYYY-MM-DD` / `HH:MM` |
| `time_type` | `departure` | `departure`（出発）, `arrival`（到着）, `first_train`（始発）, `last_train`（終電）, `average`（時刻表を使わない平均待ち時間探索） |
| `sort` | `recommended` | `recommended`（駅すぱあとのおすすめ順）, `time`, `fare`, `transfer`, `commuter_pass` |
| `count` | `5` | 返す経路の数（1〜20） |
| `ticket` | `ic` | `ic`（IC カード）, `cash`（きっぷ） |
| `seat` | `non_reserved` | `non_reserved`（自由席）, `reserved`（指定席）, `green`（グリーン車） |
| `commuter_pass` | `commute` | 定期の種類。`commute`（通勤）, `university`, `high_school`, `junior_high_school` |
| `use_airline` など | `true` | 飛行機・新幹線・のぞみ等・有料特急・高速バス・路線バス・船を使うか |
| `include_stops` | `false` | 途中の停車駅と時刻も返す |
| `bus_only` | `false` | 路線バスだけで探す（平均待ち時間探索 `/search/course/bus`） |

駅名が見つからないなど API が 400 を返したときは、駅名ごとに駅簡易情報で探した候補をエラーメッセージに入れて返す。

`search_route` の経路には `route_id` が付き、`search_route_other_trains`・`split_route_fare`・`apply_commuter_pass`・`change_route_fare`・
`get_commuter_pass_refund`・`get_route_stops`・`get_route_section_timetable`・`get_coupons` に渡すと、その経路を元に続けて調べられる。
前後の便・停車駅の時刻・区間時刻表は、時刻表を使った探索の経路だけが対象（`time_type=average` の経路は不可）。

時刻表の発車時刻の後ろの `#番号` は列車コードで、`get_train_detail` に渡すとその列車の停車駅と時刻が分かる。

`get_station_timetable` は、駅名だけで呼ぶと路線・方面ごとの時刻表の一覧を返し、`line`（例: `中央線快速 東京`）で絞るか `timetable_code` を渡すと発車時刻を返す。
`from_time` / `to_time` で時間帯を絞れる。

## ビルド

```sh
mise install          # .NET SDK 10
mise run publish      # ./dist/ekispert-mcp（自己完結型の単一バイナリ）を作る
```

mise で入れた dotnet は `DOTNET_ROOT` を設定しないため、MCP クライアントには `bin/` 配下ではなく `dist/ekispert-mcp` を登録する。

## 登録

アクセスキーは環境変数 `EKISPERT_API_KEY` で渡す。

### Claude Code

```sh
claude mcp add ekispert --scope user -e EKISPERT_API_KEY=アクセスキー -- "$PWD/dist/ekispert-mcp"
```

### Codex

```toml
[mcp_servers.ekispert]
command = "/absolute/path/to/ekispert-mcp"
env = { EKISPERT_API_KEY = "アクセスキー" }
```

## しくみ

- `search_route` は、交通手段・座席・乗車券の指定を探索条件生成 API で `conditionDetail` に変換してから経路探索 API を呼ぶ。生成した条件はプロセス内で覚えておく。
- 乗車券は IC 運賃も計算させ（`ticketSystemType=ic`）、`ticket` に応じて IC かきっぷのどちらを優先するかを決める。
- 駅すぱあと API の JSON は、同じキーが 1 件ならオブジェクト、複数なら配列で返り、キーがないこともあるので、常に並びとして読む（公式の「[利用上の注意](https://docs.ekispert.com/v1/get-started/attention/)」）。
- `viaList` の区切りのコロンはパーセントエンコードしない（同じく利用上の注意）。
- 経路シリアライズデータは 1 経路で数百文字あるので、ツールの結果には入れず、プロセス内で覚えて短い `route_id`（最新 1000 経路）で受け渡す。MCP サーバーを再起動すると以前の `route_id` は使えない。
- 運賃の分割計算はきっぷ（普通乗車券）の運賃で行われるので、通しの運賃もきっぷの運賃で比べる。
- 鉄道駅時刻表では、運転日の違う同じ列車が別々に返るので、時刻と列車番号で 1 本にまとめる。
- 飛行機・船の時刻表は「空路(東京－大阪)」のような路線名で引くので、路線名の一覧（数百件）をプロセス内で覚えておき、出発地・到着地から路線名と向きを決める。
  空路の路線名は都市名なので、羽田→東京、伊丹→大阪、那覇→沖縄 などを読み替える。

使っていない API と理由:

- 経路簡易探索・平均待ち時間探索・範囲探索(旧版)・鉄道駅時刻表(旧版): 経路探索・範囲探索・鉄道駅時刻表で同じことができる（旧版は非推奨）。
- 列車等の停車駅情報 `/course/trainStation`・経路の生成 `/course/create`: 列車コードが平均路線情報から得る別体系で、時刻表の列車コードは使えない。停車駅は `get_route_stops` と `get_train_detail` で分かる。
- 区間時刻表の列車情報 `/course/timetable/train`: 鉄道駅時刻表の列車情報と同じ結果になるので `get_train_detail` にまとめた。
- 路線図・シェイプ・測地系変換・地点生成・経路情報の更新判定・データバージョン・運行路線情報・会社情報: チャットで使う場面がない。
- 接続先は環境変数 `EKISPERT_BASE_URL` で変えられる（テストでモックサーバーに向けるため）。

`v*` のタグを付けて配布する場合のスモークテスト（MCP の initialize と tools/list）は `.github/scripts/smoke_test.py` にある。

```sh
python3 .github/scripts/smoke_test.py dist/ekispert-mcp
```
