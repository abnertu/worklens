# WorkLens v0.1 — Implementation Backlog

> Status: Proposed — 2026-11-11
> 每個 Milestone 以一份 converge 子計畫實作 (`.plans/<slug>.md`, Parent = `.plans/worklens-v0.1-architecture.md`)。
> 已完成的基礎 (v0.1 規劃計畫): 規劃文件、ADR、Schema 1.0、Solution 骨架 (Core / Cli / Core.Tests)、schema 一致性測試。
> 優先順序: 先交付一條穩定的端到端 vertical slice (交接文件 §10), 不追求每個階段功能完整。

## 依賴總覽

```text
M0 (Report IR → PPTX, 手寫輸入)
 └─> M1 (Git collectors → Evidence IR)
      └─> M2 (Digest + agent 撰寫 Report IR 的流程)
           └─> M3 (端到端 CLI + 人工補充)
                └─> M4 (Hardening)
```

M0 與 M1 只透過 Evidence IR schema 相依, 可平行開發; 但 M0 的參照完整性驗證要先完成, M2 的 agent 修正迴圈才有意義。

## M0 — Architecture & PPTX PoC (Phase 0)

子計畫: `m0-pptx-poc`

狀態: 實作完成 (2026-11-11); 結果見 [pptx-poc-report.md](pptx-poc-report.md)。`render` / `present` 在 M0 需要 `--manifest`; `worklens.json`、預設工作目錄、stale exit 5 與 `report rebind` 屬 M3。

目標: 以手寫的 `report.json` / `evidence.json` 驗證 schema → layout → PPTX, 確認樣本模板可行。

工作項目:
1. 自製樣本模板與 `manifest.json` ([pptx-poc-plan.md §2](pptx-poc-plan.md))。
2. `IReportValidator`: schema + 參照完整性 + domain 規則 (domain-model.md §3.2); `worklens report validate` 實作 (exit 0 / 2, `--json` issues)。
3. `ILayoutResolver` (Core, 純函式): sequence、rank 排序、統計計算、容量與 overflow 規則; `worklens present`。
4. `WorkLens.Rendering.Pptx`: `IOutputRenderer` (Open XML); `worklens render`。
5. `worklens schema show`。
6. PoC 報告與 ADR-0006 結論。

### Acceptance
- pptx-poc-plan.md 的 P1–P4、P7–P10 通過; P5、P6 有記錄的結果; P11 記錄為不執行 (使用者決定不做跨檢視器驗證)。
- `report validate` 對 `ref.evidence_missing`、`ref.duplicate_id`、`ref.evidence_document_mismatch` 各有測試; 所有 issue 都有正確的 `instance_path`。
- 同一 `report.json` 執行 `present` 兩次, `presentation.json` 位元組完全相同。
- Renderer 結構斷言測試在 CI 環境 (無 Office) 通過。

### Depends on
- v0.1 規劃計畫 (本 repo 現況)。
- OQ-10 (驗證標準): 已決定 — 結構斷言 + PowerPoint 開啟 / 編輯 + 人工檢視 PNG; 跨檢視器驗證移至 backlog。OQ-11 只在 PoC 失敗時需要決定 (未發生)。

測試策略: Layout Resolver 以 golden file 測試; Renderer 以 Open XML 讀回的結構斷言 + 正規化 JSON snapshot。
風險: slide 複製與 placeholder 繼承 (見 pptx-poc-plan.md §5); 若 Open XML SDK 不足, ADR-0006 改提案替代方案, M0 延長。

## M1 — Git Collectors (Phase 1)

子計畫 (使用者決定拆成兩份, 2026-11-12):
- `m1a-github-collect`: 設定 schema 與載入、期間計算、Redactor、身分比對、HTTP 基礎 (分頁 / retry / rate limit)、GitHub connector、`worklens collect`。
- `m1b-gitlab-connector`: 在 m1a 的接縫 (`ISourceConnector`、HTTP 基礎、`collect`) 上加入 GitLab 與 self-managed GitLab 實測。

狀態: m1a 進行中。

目標: 從 GitHub / GitLab 取得期間內的活動, 產生 Evidence IR。

工作項目:
1. `worklens.json` 設定 schema 與載入 (含 `config.secret_in_file` 檢查) — m1a。
2. 期間計算: IANA 時區月界 → UTC (OQ-07) — m1a。
3. `WorkLens.Connectors.Git`: GitHub (default branch commits、PRs 及其 commits) typed client, 分頁、retry / backoff、rate limit — m1a; GitLab (default branch commits、MRs 及其 commits) — m1b。
4. 作者身分比對 (`identities`、`Co-authored-by`, OQ-15)、bot / merge / revert 標記、同 SHA 去重、`contains` relations — m1a (GitLab 的對應於 m1b)。
5. 穩定 evidence ID; `worklens collect` (含 `--dry-run`) — m1a。HTTP 回應快取 (ETag) 移至 Backlog (使用者決定)。
6. Redactor 與日誌脫敏 (security-privacy.md §3) — m1a。

### Acceptance
- 以錄製的 HTTP fixture 測試: 多頁分頁、429 + `Retry-After`、5xx 重試上限、權限不足 (exit 4) — m1a (GitHub)、m1b (GitLab)。
- 同一 fixture 重跑 `collect`, evidence ID 與 `items` 內容相同 (`fetched_at` 除外) — m1a。
- 期間邊界測試: 月初 / 月底跨時區的 commit 歸屬正確 — m1a。
- 失敗路徑輸出不含 token (security-privacy.md §8) — m1a。
- 對一個真實的 repo 手動執行成功 (記錄於子計畫 evidence): GitHub 個人 repo (m1a)、self-managed GitLab (m1b)。

### Depends on
- Evidence IR schema 1.0 (已完成)。
- OQ-02、OQ-07、OQ-15: 已決定 (採預設假設, 使用者 2026-11-12)。
- m1b 依賴 m1a。

測試策略: typed client 以 `HttpMessageHandler` 替身回放 fixture; 正規化與去重為純函式單元測試。
風險: GitLab self-managed 版本差異; 大型 repo 的 API 用量; squash / cherry-pick 的重複歸因只能部分處理; 只取 default branch 與 PR commits, 未進入 PR 的分支 commit 不會被蒐集。

## M2 — Agent Analysis Flow (Phase 2)

子計畫: `m2-agent-analysis-flow`

目標: 讓 agent 能從 digest 寫出通過驗證的 Report IR。

工作項目:
1. `IEvidenceDigester` 與 `digest.json` 格式 (`digest_version` 1, 含 evidence `document_id` / `sha256`); 截斷與 redaction metadata; `worklens evidence digest`。
2. Secret pattern redaction 與自訂 `digest.redaction.patterns`。
3. `rule.unreferenced_numeric_claim` 等 domain 規則的 pattern 定案。
4. Agent Skill 草案試用: 以 Claude Code 與 Codex CLI 各跑一次完整的 digest → report → validate 迴圈, 記錄失敗模式並調整 schema 描述 / 錯誤訊息。
5. 評估 `--include-diff` (OQ-06)。

### Acceptance
- Digest 預設不含 email、diff、URL query, 有測試。
- 兩種 agent CLI 各至少一次在 3 次修正內產出通過 `report validate` 的 `report.json` (以真實或 fixture 證據, 結果記錄於子計畫)。
- 驗證錯誤訊息足以讓 agent 定位問題 (試用紀錄中沒有「看不懂錯誤」而放棄的情況)。

### Depends on
- M0 (report validate 的參照完整性)、M1 (真實 Evidence IR)。
- OQ-06、OQ-13 在 M2 開始前確認或接受預設假設。

測試策略: digest 為 golden file 測試; agent 試用為手動驗收, 不放進自動測試 (非確定性)。
風險: agent 輸出品質不穩定 — 以 schema 約束、驗證迴圈與人工審核緩解, 不以重跑 agent 追求一致 (交接文件 §4.3)。

## M3 — End-to-end CLI (Phase 3)

子計畫: `m3-end-to-end-cli`

目標: 完整月報流程可重複執行, 並支援人工補充。

工作項目:
1. `worklens init`、`worklens status` (工作目錄狀態與下一步建議)、`worklens report generate` (present + render 便利命令)。
2. `manifest.json` provenance 與過期偵測 (exit 5); `worklens report rebind`; 工作目錄 lock。
3. 人工內容保留: 重新產生 Report IR 時合併 `human_notes` 與 `origin: human` 項目 (OQ-08)。
4. 正式的 agent Skill 檔 (Claude Code `SKILL.md`、Codex 指引) 與使用說明。
5. 若出現第二個入口需求, 抽出 `WorkLens.Application` (ADR-0002)。
6. `worklens clean --keep-report`。

### Acceptance
- 端到端測試: fixture 證據 + 預先寫好的 `report.json` → `.pptx`, 全部以 CLI 命令執行, exit code 與 `--json` 輸出符合 cli-ux.md。
- 修改 `evidence.json` 後執行 `present` 回 exit 5。
- 人工新增的 work item 與 `human_notes` 在合併流程後保留, 有測試。
- 使用者以 agent CLI 實際產生一份月報並完成審核 (手動驗收)。

### Depends on
- M0、M1、M2。
- OQ-08 (人工修改方式) 在 M3 開始前確認。

測試策略: CLI 端到端測試以 process 啟動 `worklens` 並比對輸出; 合併規則為純函式單元測試。
風險: 合併規則的邊界情況 (agent 改名或拆分 work item); 以 ID 對應為主, 對不上的項目交給人工。

## M4 — Hardening (Phase 4)

子計畫: `m4-hardening`

目標: 穩定性、可發佈與後續自動化的前置作業。

工作項目:
1. Repo CI (build + test, 含 Renderer 結構斷言); 跨檢視器 (LibreOffice 等) 轉檔檢查只在需要時加入 (pptx-poc-report P11)。
2. 發佈形式: dotnet tool 或 self-contained 執行檔 (OQ-01)。
3. 錯誤恢復: 中斷後重跑的行為、部分 source 失敗時的輸出 (`--continue-on-source-error`)。
4. OS credential store 評估; 資料保留政策 (OQ-09)。
5. Schema migration 機制 (只在出現第一個 major 變更時實作)。
6. 評估 backlog: MCP server、headless agent runner、內建 LLM provider、SQLite。

### Acceptance
- CI 在 Windows 與 Linux 上通過 build 與 test。
- 發佈的執行檔可在乾淨環境中執行 `worklens --version` 與一次 M3 的端到端流程。
- 部分 source 失敗時, 其他 source 的證據仍寫出, 且 manifest 記錄失敗的 source。
- security-privacy.md §8 檢查清單全部完成。

### Depends on
- M3。
- OQ-01、OQ-09、OQ-12 在 M4 開始前確認。

測試策略: CI matrix; 發佈產物的 smoke test。
風險: 跨平台字型與換行差異 (影響 Renderer 測試穩定性) — 結構斷言不依賴字型, 視覺檢查維持人工。

## 未排入 Milestone (Backlog)

| 項目 | 來源 | 觸發條件 |
|---|---|---|
| MCP server 入口 | ADR-0005 | agent 介面需要不經 shell 的呼叫, 或 CLI 權限受限 |
| Headless agent runner (`claude -p` / `codex exec`) | ADR-0004 | 需要 CI 排程產生月報 (OQ-12) |
| 內建 LLM provider (Microsoft.Extensions.AI) | ADR-0004 | 需要不依賴 agent CLI 的執行環境 |
| SQLite 儲存 / 歷史比較 | ADR-0007 | OQ-09 決定需要跨期間查詢 |
| Issue tracker、CI/CD、Calendar connectors | 交接文件 §8 | 月報需要 Git 以外的證據 |
| Markdown / HTML / PDF renderer | 交接文件 §8 | 需要 PPTX 以外的輸出 |
| 團隊報告 | OQ-03 | 需要多人報告 |
| 像素級視覺回歸測試 | OQ-10 | 結構斷言不足以抓出版面回歸 |
| HTTP 回應快取 (`<workspace>/cache/`, ETag / `If-None-Match`) | M1 (使用者決定延後) | 重跑 `collect` 的 API 用量或 rate limit 成為問題 |
