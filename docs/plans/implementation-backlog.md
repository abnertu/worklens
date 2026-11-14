# WorkLens v0.1 — Implementation Backlog

> Status: Proposed — 2026-11-13 更新
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

狀態: m1a 已完成 GitHub `collect` 實作與 fixture 測試。
最終差異審查已通過。真實 GitHub repo 驗收尚未完成。
m1b 已完成 GitLab `collect` 實作、fixture 測試與 self-managed GitLab 16.9.1 真實驗收。
使用者決定暫緩 GitHub 驗收 (2026-11-13)。m1a 維持 verifying, DC10 不勾選。

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
6. 評估 backlog: MCP server、外部 headless agent runner、SQLite。

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

## 既有 Backlog 整理

本節整合架構、M0、M1a 與 M1b 計畫的遺留項目。優先順序是建議, 各項實作仍需獨立計畫。
本輪核對程式、測試與文件, 未逐一重現歷史發現。實作前先用隔離 fixture 確認失敗。
已完成計畫維持 done。它們的 backlog 是新工作, 不影響原計畫結案。

### 優先修正

| 項目 | 具體問題 | 驗收方向 |
|---|---|---|
| PoC 程序清理 | `run-poc-checks.ps1` 會終止檢查期間新出現的所有 POWERPNT, 可能關閉使用者另開的視窗 | 只清理腳本建立的程序; 測試逾時與並行啟動 |
| 模板重建 | `build-template.ps1` 在 SaveAs 前刪除舊模板; COM 建立失敗的清理仍有缺口 | 寫入失敗時保留舊模板; 清理本次建立的程序 |
| 路徑錯誤 | 空路徑選項與含 NUL 的 `template.file` 可能回 exit 70 | 以 CLI fixture 驗證錯誤分類與 JSON envelope |
| 模板資料清理 | 未使用的損毀 part、`customXml`、section 資訊與部分文件屬性可能留在輸出 | 用含這些資料的模板驗證清理或拒絕行為 |
| 未排入 sequence 的內容 | 自訂 manifest 可讓部分 report section 靜默消失 | 先定義警告契約, 再測試遺漏的 section |

程序與模板風險應在下一次執行相關 PowerPoint 腳本前處理。其餘項目可分開安排, 不必合成大型修正計畫。

### 後續修正與驗證

| 項目 | 尚缺的行為或證據 | 建議歸屬 |
|---|---|---|
| GitLab repository 停用 | `repository_access_level: disabled` 目前回 `source.forbidden`, 提示誤指 token scope | GitLab 小型修正 |
| GitLab 最小權限 | 真實驗收的 token 權限多於 `read_api`; 最小 scope 尚未實測 | GitLab 手動驗收 |
| GitLab diff 完整性 | 伺服器可能省略路徑且不提供截斷 metadata; 目前 3000 筆上限警告未涵蓋此情境 | 先研究可證明的方法; 無方法時保留已知限制 |
| bullets 與儲存格換行 | 換行原樣寫入 `a:t`, 未轉為段落或 `a:br` | Renderer 修正 |
| placeholder 衝突 | 多個 slot 共用 placeholder, 或使用頁尾與頁碼 placeholder, 可能產生重疊 shape | Manifest 驗證 |
| 英文預設 labels | 表格欄名不套用 `max_chars`; 英文標籤與樣本容量可能不相容 | 模板容量校正 |
| 拆頁語意 | 標題缺續頁標記; L1 項目可能與 L0 父項分頁 | Layout 修正 |
| 文字寬度 | `max_chars` 以全形最壞情況計算, 英文文字過早截斷 | Layout 改善 |
| `oneOf` 診斷 | Presentation IR 錯誤列出所有分支, 未集中於 `kind` 對應分支 | 驗證訊息改善; 架構與 M0 重複項已合併 |
| UTC 契約 | `collect` 已正規化 UTC; 共用序列化與其他輸入仍未強制 UTC | 先定義剩餘範圍; 架構與 M0 重複項已合併 |
| `present` / `render --dry-run` | 目前只有 `collect` 提供 dry-run | M3 CLI 流程 |
| labels 文件 | Schema description 未逐項說明哪些 label 支援大括號代入 | 文件修正 |
| 訊息語言 | 用法錯誤可能依 UI 語系輸出, 與 JSON message 英文契約不符 | CLI 診斷 |
| summary 容量說明 | `highlights` 的 28 / 5 容量與每項兩行的說明需重新核對; 尚無實際溢出證據 | 先重現, 再校正容量或文件 |

### 有需求或證據時再做

| 項目 | 啟動條件 |
|---|---|
| `worklens template inspect` | 需要導入或診斷自訂模板 |
| 樣本模板視覺改進 | 使用者要求調整配色、字級、項目符號或封面; 修改後重跑容量校正 |
| 空簡報錯誤碼 | 自訂 manifest 與 report 可重現零張投影片 |
| 非法 UTF-8 診斷 | 需要區分 `json.deserialize_failed` 與 `json.invalid`; 目前皆為 exit 2 |
| Renderer API 原子寫入 | CLI 以外的呼叫端需要直接使用 `PptxRenderer`; CLI 已提供原子寫入 |
| `sldIdLst` 插入順序 | 模板沒有 `sldIdLst` 且含 `handoutMasterIdLst` |
| ZIP entry 時間戳 | 需要移除渲染時間或要求 PPTX 二進位確定性; 目前只比正規化結構 |
| 整數小數形式 | 需要接受 `rank: 2.0`; schema 接受但 C# `int` 反序列化失敗 |
| 跨檢視器驗證 | 收件人使用非 PowerPoint 檢視器, 或需要 Linux 轉檔驗證 |
| GitLab MR 淨統計 | 找到能跨伺服器 diff 上限維持準確性的 API 方法 |
| HTTP 回應快取 | 重跑 `collect` 的 API 用量或 rate limit 成為問題; 先前已決定延後 |
| Token 來源備援 | M4 評估 `gh` / `glab` / OS credential store; 保留環境變數並評估權限差異 |
| 離線 NuGet audit | 可重現 NU1900 被 warnaserror 升為錯誤; 目前只有未驗證疑慮 |

### GitHub 暫緩項目

| 項目 | 恢復後的驗收方向 |
|---|---|
| M1a DC10 真實 repo 驗收 | `collect` 成功、schema 通過、記錄計數, 並抽查三筆 URL 與時間 |
| PR 分頁期間更新 | 重現 `sort=updated` 位移造成漏項, 再驗證補抓策略 |
| 超過 250 commits 的 PR | 以 list commits 端點補齊, 並驗證完整性與去重 |

本次不執行 GitHub 真實驗收或擴充。共用快取與 token 備援保留於前表。

### 已完成或撤銷

| 原項目 | 處置與證據 |
|---|---|
| README 仍顯示規劃階段 | 已完成; README 已列出可用命令與 GitLab 真實驗收狀態 |
| `collect` 缺 Git ignore 檢查 | 已完成; `CollectCommand` 產生 `output.not_ignored`, `CollectCommandTests` 覆蓋三種 probe 結果 |
| exit 1 / 70 缺 JSON envelope | 已完成; `Program.RunAsync` 透過 `CliOutput.WriteFailureAsync` 輸出兩種失敗 |
| SHA-256 範圍不明 | 已完成; domain-model §5 與 schema 定義為被參照檔案寫出時的位元組 |
| ADR-0006 改選替代 Renderer | 條件未成立; Open XML SDK 已 Accepted, 撤銷此條件項 |
| 內建 LLM provider | 不納入目前 backlog; 與 AGENTS.md 的無 LLM 呼叫及 SDK 邊界衝突 |

建議先處理 PowerPoint 腳本風險與 CLI 路徑錯誤, 再推進 M2 digest 主流程。
M2 可用已驗收的 GitLab 證據規劃; GitHub DC10 仍保持未完成。
其他 renderer 與模板改善依實際模板需求安排。

## 未排入 Milestone 的擴充

| 項目 | 來源 | 觸發條件 |
|---|---|---|
| MCP server 入口 | ADR-0005 | agent 介面需要不經 shell 的呼叫, 或 CLI 權限受限 |
| 外部 headless agent runner (`claude -p` / `codex exec`) | ADR-0004 | 需要 CI 排程產生月報 (OQ-12); WorkLens 本身不呼叫 agent |
| SQLite 儲存 / 歷史比較 | ADR-0007 | OQ-09 決定需要跨期間查詢 |
| Issue tracker、CI/CD、Calendar connectors | 交接文件 §8 | 月報需要 Git 以外的證據 |
| Markdown / HTML / PDF renderer | 交接文件 §8 | 需要 PPTX 以外的輸出 |
| 團隊報告 | OQ-03 | 需要多人報告 |
| 像素級視覺回歸測試 | OQ-10 | 結構斷言不足以抓出版面回歸 |
