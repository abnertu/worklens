# ADR-0002: Modular Monolith + Pipeline, 最小化 Project 數量

## Status

Proposed — 2026-11-11

## Context

- 交接文件 §7 列出 8 個邏輯模組 (Domain、Application、Contracts、Infrastructure、Connectors、Analysis、Rendering、Cli), 並註明不代表每個都要拆成 `.csproj`。
- v0.1 只有一個入口 (CLI)、一位使用者、一條 pipeline; AI 分析不在 WorkLens 內 ([ADR-0004](0004-agent-as-analyzer.md))。
- 主要的隔離需求是**套件相依**: IR / 驗證 / Layout 不該被 Open XML、HTTP、CLI 函式庫汙染, 這樣它們才能被未來的 MCP server 或 API 重用。

## Decision

- 單一 process、單一 solution (`WorkLens.slnx`) 的 Modular Monolith。Pipeline 每個階段以檔案為輸入輸出 (見 [ADR-0007](0007-storage-workspace-json-files.md))。
- **只在需要隔離套件相依時才建立新 project。** v0.1 骨架三個 project: `WorkLens.Core`、`WorkLens.Cli`、`WorkLens.Core.Tests`; `WorkLens.Rendering.Pptx` (M0)、`WorkLens.Connectors.Git` (M1) 於需要時建立; `WorkLens.Application` 只在出現第二個入口時抽出。
- 模組邊界在 Core 內以命名空間表示 (`WorkLens.Core.Evidence`、`.Report`、`.Presentation`、`.Templates`、`.Validation`、`.Layout`、`.Contracts`)。
- 相依規則見 [overview.md §4.3](../architecture/overview.md#43-相依規則), 並由 `CoreDependencyRulesTests` 自動檢查 Core 的套件相依。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| 交接文件 §7 的 8 個 project | 邊界一目了然 | 多數 project 在 v0.1 是空的或只有幾個型別; 跨 project 重構成本高; 過度設計 | 不採用 |
| 單一 project | 最簡單 | 無法防止 Core 相依 Open XML / HTTP; MCP 入口難重用 | 不採用 |
| 依相依隔離需求拆 (選擇) | project 數與實際邊界一致; 相依規則可測 | 命名空間邊界只靠紀律與 review | 採用 |
| Microservices / plugin 動態載入 | 獨立部署 | 交接文件 §2.1 明確排除; 單人工具無必要 | 不採用 |

## Consequences

- 新增 Connector 或 Renderer 時, 新 project 只相依 Core; 不需要修改既有 project 的相依。
- Cli 在 v0.1 同時承擔 pipeline 編排; 出現 MCP 入口時需把編排抽到 `WorkLens.Application`, 這是已知的後續重構 (M3 評估)。
- 命名空間層級的邊界違規 (例如 `Layout` 引用 `Validation` 內部型別) 不會被測試發現, 依 code review 把關。
