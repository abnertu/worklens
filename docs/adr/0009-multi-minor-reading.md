# ADR-0009: 讀取同一 schema 的多個 minor

## Status

Accepted — 2026-11-13

## Context

- WorkLens 0.1 的每個 schema 只有一個版本。Report IR 1.1 為月報模板新增欄位。
- 1.0 的報告與 manifest 必須維持有效。變更後, 1.0 文件的結果必須相同。
- 每個 name 只有一個 schema ID 的驗證器無法讀取兩個 minor。

## Decision

1. Catalog 列出每個 (name, version)。Schema ID 由兩者導出。
2. 驗證器依文件的版本欄位選擇 schema。
3. Report、evidence、manifest、presentation 與設定檔的讀取都經過這個選擇。
4. 版本缺少、不是字串或不是內建版本時, 回 `schema.unsupported_version`, 位置為版本欄位。
5. WorkLens 寫出最新 minor。Root type 的 `CurrentSchemaVersion` 與 `SchemaId` 表示最新 minor。
6. 一個 C# root type 讀取同一 name 的全部 minor。
7. `schema show <name>` 輸出最新 minor。

## Consequences

- 版本欄位有誤的 evidence、manifest、presentation 與設定檔現在回 `schema.unsupported_version`。之前回 `schema.const`、`schema.required` 或 `schema.type`。
- 新的 minor 需要 schema 目錄、範例、embedded resource 與 catalog 項目。一致性測試檢查每個 (name, version)。
- 新的 minor 必須是前一個 minor 的超集合, 因為一個 C# type 讀取全部 minor。

## Alternatives

| 方案 | 結論與理由 |
|---|---|
| 每個 minor 一個 C# type | 不採用: 模型與讀取它們的程式重複 |
| 只以最新 minor 驗證 | 不採用: 含 1.1 欄位的 1.0 文件會通過 |
