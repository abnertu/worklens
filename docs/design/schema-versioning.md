# WorkLens — Schema 版本策略與 Migration

> Status: Proposed — 2026-11-11
> 適用: `schemas/` 下的 `evidence-ir`、`report-ir`、`presentation-ir`、`template-manifest`, 以及 CLI `--json` 輸出契約。

## 1. 識別與位置

| 項目 | 規則 |
|---|---|
| 路徑 | `schemas/<name>/<major.minor>/<name>.schema.json` |
| `$id` | `urn:worklens:schema:<name>:<major.minor>` |
| `$schema` | 每份 schema 必須明確寫 `https://json-schema.org/draft/2020-12/schema` (JsonSchema.Net 9 的預設 dialect 不是 2020-12) |
| 文件版本 | IR 文件根層 `schema_version: "<major.minor>"`, 與 `$id` 一致 (schema 中以 `const` 限定) |
| 範例 | `examples/valid/*.json`、`examples/invalid/<keyword>--<desc>.json` (`<keyword>` 為預期失敗的 JSON Schema keyword) |
| 發佈 | schema 以 embedded resource 打包進 `WorkLens.Core`, `worklens schema list` / `schema show <name>` 可查詢 |

## 2. 版本號語意

- **minor** (`1.0` → `1.1`): 向後相容的變更。舊文件在新 schema 下仍然有效。
  - 新增**選填**欄位或新的 `$defs`。
  - 放寬限制 (增加 `maxLength`、新增 enum 值 — 見下方注意)。
- **major** (`1.x` → `2.0`): 任何讓既有有效文件變成無效、或改變既有欄位意義的變更。
  - 新增必填欄位、移除或改名欄位、改變型別、收緊限制、改變 enum 值的意義。

注意: 因為所有物件都是 `additionalProperties: false`, **舊版 reader 讀不了新版 minor 新增的欄位**。相容方向是「新 reader 讀舊文件」, 不是「舊 reader 讀新文件」。新增 enum 值也屬此類 — 對 reader 是 minor, 但舊 reader 會拒絕。

## 3. 支援政策

- WorkLens 每個版本宣告各 schema 支援的 major 集合 (v0.1: 全部只有 `1`)。
- 讀取: 依文件的 `schema_version` 選擇對應 schema 驗證; 同 major 不同 minor 以**文件宣告的 minor** 驗證; 若文件的 minor 比 WorkLens 內建的最新 minor 還新, 回 `schema.unsupported_version`。
- 寫出: 永遠寫 WorkLens 內建的最新 minor。
- 不支援的 major → 錯誤 `schema.unsupported_version`, 提示執行 `worklens migrate` (若有) 或使用對應版本的 WorkLens。

## 4. Migration

- 只在 major 變更時提供, 形式為 `worklens migrate <file> --to <name>:<major.minor>` (M4 以後, 有第一個 major 變更時才實作)。
- 每個 migration 是純函式 `JsonNode → JsonNode`, 一次只升一個 major, 以 valid 範例的前後對照測試。
- **Report IR 的 migration 必須保留人工內容** (`human_notes`、`origin: human` 的項目、`review_status`)。無法自動對應的欄位寫入 migration 報告, 不默默丟棄。
- Evidence IR 可以選擇重新 `collect` 取代 migration (穩定 ID 保證 Report IR 參照仍有效)。
- Presentation IR 不需 migration: 從 Report IR 重新 `present` 即可。

## 5. Canonical JSON 形式

WorkLens 寫出的 JSON, 以及 `schemas/**/examples/valid/` 的範例, 遵循以下形式, 讓 C# models 的 round-trip 測試 (`JsonNode.DeepEquals`) 成立:

| 項目 | 規則 |
|---|---|
| 編碼 | UTF-8, 無 BOM, LF 換行 |
| 欄位名 | `snake_case` |
| 選填欄位 | 無值時**省略**, 不寫 `null` |
| 時間 | UTC, 以 System.Text.Json 對 `DateTimeOffset` 的輸出形式 `yyyy-MM-ddTHH:mm:ss[.fffffff]+00:00`。Schema 的 `format: date-time` 也接受 `Z`, 所以 agent 寫 `Z` 仍然有效, 但 WorkLens 重寫後會變成 `+00:00` |
| Enum | `snake_case` 字串 |
| 數字 | 整數欄位不寫小數點 |
| 未定義欄位 | 不允許 (schema `additionalProperties: false`; C# `JsonUnmappedMemberHandling.Disallow`) |
| `$schema` 屬性 | instance 文件**不寫** `"$schema"` (會被當成未定義欄位) |

欄位順序不屬於 canonical 形式的一部分 (DeepEquals 不比較順序), 但 WorkLens 寫出時固定依 C# record 宣告順序, 讓 diff 穩定。

## 6. 驗證設定

- `EvaluationOptions.RequireFormatValidation = true` — 否則 `format` 只是註解。
- `OutputFormat.List` — 取得每個失敗節點的 `evaluation_path` / `instance_location`, 轉成 `ValidationIssue` (code = `schema.` + `evaluation_path` 最後一段)。
- 所有 schema 註冊在同一個非 global `SchemaRegistry`, 每份只 build 一次。
- Schema 品質檢查 (測試): meta-schema 驗證、keyword allowlist (擋拼字錯誤如 `addtionalProperties`)、`format` 值 allowlist (擋 `date_time` 等未知格式, JsonSchema.Net 對未知格式不報錯)。

## 7. CLI `--json` 輸出

`--json` 輸出以 `output_version` (目前 `"1"`) 標示。新增欄位不升版; 移除或改變欄位意義時升版, 並在一個 minor release 期間同時支援 `--json-version <n>`。Exit code 語意視同 major 契約 ([cli-ux.md](cli-ux.md))。
