# ekispert-mcp

[駅すぱあと API](https://docs.ekispert.com/v1/) で公共交通機関の経路・運賃・定期代を調べる、ローカル用の MCP サーバー（stdio）。

> [!IMPORTANT]
> **非公式**のツールです。株式会社ヴァル研究所とは関係がありません（公式の MCP サーバーは [こちら](https://docs.ekispert.com/v1/for-ai/mcp-server/)）。
> 利用には駅すぱあと API のアクセスキーが必要で、API の利用規約と料金はキーの契約に従います。

## ツール

| ツール | 使う API | 内容 |
|---|---|---|
| `search_route` | 経路探索 `/search/course/extreme`、探索条件生成 `/toolbox/course/condition` | 駅から駅への経路を探索する。経路ごとに出発/到着時刻、所要時間、乗換回数、運賃（片道・往復）、特急料金などの料金、定期代と、区間ごとの路線・行き先・発着番線を返す |
| `search_stations` | 駅簡易情報 `/station/light` | 駅・空港・港・バス停を名前で検索する（最大 30 件） |

`search_stations` と探索条件生成は課金対象外の API。

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

駅名が見つからないなど API が 400 を返したときは、駅名ごとに駅簡易情報で探した候補をエラーメッセージに入れて返す。

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
- 接続先は環境変数 `EKISPERT_BASE_URL` で変えられる（テストでモックサーバーに向けるため）。

`v*` のタグを付けて配布する場合のスモークテスト（MCP の initialize と tools/list）は `.github/scripts/smoke_test.py` にある。

```sh
python3 .github/scripts/smoke_test.py dist/ekispert-mcp
```
