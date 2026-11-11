# ADR-0004: Agent-as-analyzer — WorkLens 不內建 LLM

## Status

Accepted (使用者決策) — 2026-11-11

## Context

- 交接文件 §2 原提案以 OpenAI SDK 或 Microsoft.Extensions.AI 在 WorkLens 內呼叫模型。
- 使用者決定 v0.1 以 Claude Code / Codex CLI 為主: WorkLens 是 agent 使用的 tool, agent 負責達成目標, WorkLens 負責限制輸出。
- 交接文件的原則仍然適用: Evidence first、Deterministic rendering、Human in the loop、不由 LLM 控制投影片幾何。

## Decision

- WorkLens v0.1 **不相依任何 LLM SDK**, 也不呼叫 LLM API 或 headless agent (`claude -p`、`codex exec`)。
- 分工:
  - WorkLens: `collect` → Evidence IR; `evidence digest` → agent 輸入; `report validate` → 驗證 agent 的 Report IR; `present` / `render` → 確定性輸出。
  - Agent: 讀 digest 與 Skill 指引, 撰寫符合 Report IR schema 的 `report.json`, 依 `report validate` 的結構化錯誤修正。
- **限制輸出的機制**:
  1. Report IR schema 嚴格 (`additionalProperties: false`、enum、必填 evidence 參照)。
  2. 參照完整性: 每個 `evidence_refs` 必須存在於 `evidence.json`; `observed_facts` 必須引用證據 (M0 實作)。
  3. Domain 規則: 例如狀態 `completed` 需有 merged PR/MR 或人工確認 (`review_status: approved`), 否則發 warning (`rule.completed_without_merged_change`); 數值性成效宣稱必須有證據來源 (規則清單見 [domain-model.md §3.2](../design/domain-model.md#32-驗證規則))。
  4. Agent 永遠不接觸 Presentation IR 與 PPTX 的生成 (由 `present` / `render` 確定性產生)。
- `IAnalysisModule` 契約保留在 Core, v0.1 無實作; 未來的內建 LLM provider 或 headless runner 實作同一契約, 產出同一 Report IR。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| 內建 LLM SDK (交接文件原案) | 可在 CI 無人執行; prompt / model 版本化 | 需管理 API key、prompt 工程、重試與成本; 使用者希望沿用既有 agent CLI | 延後 (backlog) |
| WorkLens 呼叫 headless agent | 一條命令跑完; 可排程 | 需處理 agent CLI 的版本、權限與輸出解析 | 延後 (backlog) |
| Agent-as-analyzer (選擇) | 重用 agent 的推理與工具能力; WorkLens 保持確定性、易測 | 需要人或排程驅動 agent; agent 輸出品質只能靠驗證與審核把關 | 採用 |

## Consequences

- WorkLens 的測試完全確定性, 不需 mock LLM。
- 「Prompt version / model ID」(交接文件 §4.3) 無法由 WorkLens 取得; `manifest.json` 以 agent 自報欄位 (`analysis.agent`、`analysis.model`, 選填) 記錄, 並標記為未驗證。
- 可重現性靠凍結已審核的 `report.json`, 而不是重跑 agent。
- 資料最小化的責任落在 digest: WorkLens 控制「交給 agent 什麼」, 但無法控制 agent 自行讀取 repo 的其他檔案 — 這需要在 Skill 指引與使用者的 agent 權限設定中處理 ([security-privacy.md](../security/security-privacy.md))。
