# ADR-0003: 三層 IR 與 Versioned JSON Schema

## Status

Accepted (方向: 使用者確認; 細節: Proposed) — 2026-11-11

## Context

- 交接文件 §3.1 定義三層 IR: Evidence IR (來源證據)、Report IR (語意成果)、Presentation IR (投影片結構), 並要求 Versioned Schema。
- v0.1 的 Report IR 由 agent 撰寫 ([ADR-0004](0004-agent-as-analyzer.md)), Schema 是限制 agent 輸出的主要手段, 必須嚴格 (不允許未定義欄位) 且錯誤可被機器讀取。
- Template Manifest 描述模板的 layout 與 slot, 也是 Layout Resolver 的輸入, 同樣需要 Schema。

## Decision

- 四份 Schema, 皆為 JSON Schema **draft 2020-12**:
  `evidence-ir`、`report-ir`、`presentation-ir`、`template-manifest`。
- 路徑 `schemas/<name>/<major.minor>/<name>.schema.json`; `$id` 為 `urn:worklens:schema:<name>:<major.minor>`; 每份 schema 必須明確寫 `$schema`。
- 每份 IR 文件根層有 `schema_version` (例 `"1.0"`), 與 `$id` 的版本一致。
- 命名: 欄位一律 `snake_case`; enum 值為 `snake_case` 字串; 物件預設 `additionalProperties: false`。
- 時間: UTC, C# 以 `DateTimeOffset` 表示; canonical 形式見 [schema-versioning.md](../design/schema-versioning.md)。
- C# models 是 Schema 的對應而非來源 (schema-first): 以測試保證每個 valid 範例能 deserialize 為 C# root type 並 round-trip 回等價 JSON。
- Report IR 區分 `observed_facts` (可由證據直接驗證)、`agent_inference` (agent 的推論)、`human_notes` (人工), 詳見 [domain-model.md](../design/domain-model.md)。
- 版本策略與 migration 見 [schema-versioning.md](../design/schema-versioning.md)。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| 單層 IR (直接從證據到投影片) | 簡單 | 語意與版面耦合; 換模板需重新分析; 違反交接文件 §1.1 Output agnostic | 不採用 |
| 兩層 (Evidence + Presentation) | 少一次轉換 | agent 會被迫決定版面; 人工審核對象不明 | 不採用 |
| 三層 (選擇) | 每層有單一責任; 審核對象 (Report IR) 與版面分離 | 多一層轉換與 schema 維護 | 採用 |
| C# code-first 產生 schema | 單一來源 | 產生的 schema 不易精準表達 enum / pattern / 條件必填; agent 讀的是 schema, 品質更重要 | 不採用 |
| Protobuf / Avro | 型別嚴格、相容規則成熟 | agent 與人工編輯不友善; 需額外工具 | 不採用 |
| draft-07 | 函式庫支援最廣 | 缺 `$defs`、`unevaluatedProperties`、`prefixItems` 等 | 不採用 |

## Consequences

- 每個 Schema 變更都要同步 C# models 與範例; 一致性測試會在不同步時失敗。
- `additionalProperties: false` 讓 agent 不能自行加欄位; 需要的擴充資訊只能放在明確定義的 `extensions` 物件 (若某層有定義)。
- URN `$id` 不可解析為網址; 工具 (編輯器) 需透過檔案路徑關聯 schema。
- instance 範例不得含 `"$schema"` 屬性 (C# models 禁止未對應欄位)。
