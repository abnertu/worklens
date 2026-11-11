# ADR-0005: Agent 介面 = CLI 子命令 + JSON 檔 I/O + Skill

## Status

Accepted (使用者決策) — 2026-11-11

## Context

- Agent CLI (Claude Code / Codex CLI) 能執行 shell 命令、讀寫檔案, 也能呼叫 MCP tools。
- WorkLens 需要讓 agent 依固定流程使用, 並能讀懂錯誤以自我修正。
- 人也需要能在沒有 agent 的情況下執行同樣的命令 (例如重新渲染已審核的報告)。

## Decision

- 主要介面是 `worklens` CLI 子命令 (`collect`、`evidence digest`、`report validate`、`present`、`render`、`schema list` / `schema show`), 規格見 [cli-ux.md](../design/cli-ux.md)。
- 資料以**檔案路徑**傳遞, 不以 stdout 傳大型 JSON; stdout 只輸出結果摘要。
- 每個命令支援 `--json`: stdout 輸出單一 JSON 物件 (成功結果或結構化錯誤), 供 agent 解析。
- Exit code 有固定語意 (0 成功、2 驗證失敗、10 未實作等), agent 可依 exit code 決定下一步。
- stdout / stderr 一律 UTF-8。
- 附一份 agent Skill 指引 (草案在 cli-ux.md §8; 正式 Skill 檔於 M3), 說明流程、輸入輸出檔、修正錯誤的方式與禁止事項。
- MCP server 列入 backlog; 屆時以同一 Application 層提供, CLI 契約不變。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| MCP server | tool schema 對 agent 更明確; 不需 shell 權限 | 開發與測試成本較高; 人工使用不便; 不同 agent 的 MCP 支援程度不同 | 延後 |
| CLI + MCP 同時 | 兩種使用方式 | v0.1 工作量加倍 | 不採用 |
| CLI + JSON + Skill (選擇) | Claude Code 與 Codex CLI 都能用; 人也能用; 易於端到端測試 | 需要 shell 權限; agent 需讀 Skill 才知道流程 | 採用 |

## Consequences

- `--json` 輸出格式與 exit code 是公開契約, 變更需遵循 [schema-versioning.md](../design/schema-versioning.md) 的相容規則。
- Agent 需有在 repo 外執行 `worklens` 的權限; Skill 指引需說明不得把 token 放在命令列。
- 未來的 MCP 入口需要從 Cli 抽出 Application 層 ([ADR-0002](0002-modular-monolith.md))。
