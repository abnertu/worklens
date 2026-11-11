# ADR-0008: 函式庫選型 — JsonSchema.Net、System.CommandLine、HttpClient typed client

## Status

Accepted (JsonSchema.Net 9.x: 使用者決策) / Proposed (其餘) — 2026-11-11

## Context

- 需要在 .NET 中以 JSON Schema 2020-12 驗證 IR, 並取得可機器讀取的錯誤 (keyword、instance path)。
- JsonSchema.Net (json-everything) 自 9.0 起以 **Open Source Maintenance Fee (OSMF) EULA** 發佈 binary: 原始碼仍為 MIT, 但年營收 ≥ US$10,000 的組織在營利活動中使用官方 binary 需支付維護費; 自行從原始碼編譯不受此協議約束。8.0.5 是最後一個以 MIT 發佈 binary 的版本。
- CLI 需要子命令、選項驗證、`--version`、說明文字。
- Source connector 需要 GitHub / GitLab REST API、分頁、重試、rate limit 處理, 並支援可設定的 base URL (OQ-02)。

## Decision

1. **Schema 驗證: JsonSchema.Net 9.x** (使用者已接受 OSMF EULA)。使用方式:
   - 所有 WorkLens schema 載入同一個非 global 的 `SchemaRegistry` (避免 global registry 重複 `$id` 例外, 並支援跨 schema `$ref`), 每份只 build 一次。
   - `EvaluationOptions.RequireFormatValidation = true` (預設不驗證 `format`); `OutputFormat.Hierarchical` + `IncludeApplicatorErrors = false`, 只走訪失敗節點並略過 `if` / `not` 子樹與通過的 `oneOf` 分支, 讓錯誤只剩可修正的 leaf keyword。
   - 注意: 未知的 `format` 名稱不會報錯, 因此以測試檢查 schema 中的 format 值與 keyword 拼字。
2. **CLI: System.CommandLine 3.x** (MIT)。
3. **HTTP: `HttpClient` + typed client** (`IHttpClientFactory`, M1), 自行實作分頁 (GitHub `Link` header、GitLab `x-next-page`) 與 retry / backoff (尊重 `Retry-After`、`x-ratelimit-reset`)。

## Alternatives

| 領域 | 方案 | 結論與理由 |
|---|---|---|
| Schema | JsonSchema.Net 8.0.5 (MIT) | 授權最單純, 但不再收到更新; 使用者選擇 9.x |
| Schema | Corvus.Json (Apache-2.0) | 效能佳、持續開源; API 偏向 code generation, 與 schema-first + 手寫 records 的做法較不合 |
| Schema | 自行從原始碼編譯 JsonSchema.Net | 不受 EULA 約束, 但需維護 build pipeline; 若授權條件變更時的退路 |
| Schema | NJsonSchema | draft 2020-12 支援不完整 |
| CLI | Spectre.Console.Cli | 輸出美觀, 但 `--json` 輸出與 exit code 控制需較多客製; 可在需要時只引入 Spectre.Console 做表格輸出 |
| CLI | 手寫解析 | 無相依, 但 help / 驗證需自行實作 |
| HTTP | Octokit.NET | GitHub 專用, 型別完整; 但 GitLab 仍需另一套, 且只用到少數 endpoint; 引入大型相依 |
| HTTP | GitLabApiClient 等社群套件 | 維護狀況不一 |

## Consequences

- 若組織屬於需付 OSMF 維護費的範圍, 使用者需依協議處理; 退路是改用 8.0.5、Corvus.Json 或自行編譯, 替換範圍限於 `WorkLens.Core.Validation`。
- `CoreDependencyRulesTests` 把 `JsonSchema.Net` 列為 Core 唯一允許的直接套件相依。
- 自寫的分頁與 retry 需要以錄製的 HTTP 回應 (fixture) 測試 (M1)。
