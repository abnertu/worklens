# WorkLens — Security / Privacy Plan

> Status: Proposed — 2026-11-11
> 範圍: v0.1 (本機 CLI, 由 agent CLI 驅動)。CI / 伺服器部署的額外需求於 M4 補充 (OQ-12)。

## 1. 資產與威脅

| 資產 | 所在 | 主要威脅 |
|---|---|---|
| GitHub / GitLab token | 環境變數 | 寫入日誌、shell history、agent 對話紀錄; 權限過大 |
| 原始證據 (commit message、PR/MR 描述、email、檔案路徑) | `evidence.json`、HTTP 快取 | 機密 (客戶名、內部系統、secret) 隨 digest 送進雲端模型; 工作目錄被 commit 或分享 |
| Agent 輸入 (`digest.json`) | 工作目錄 → agent 的模型供應商 | 超出必要範圍的資料離開本機 |
| 報告內容 (`report.json`、`.pptx`) | 工作目錄、分享對象 | 未經審核的推論被當成事實; 夾帶不該公開的內容 |
| Provenance (`manifest.json`) | 工作目錄 | 被竄改後無法追溯 |

信任邊界: **WorkLens 能控制它交給 agent 的內容 (digest), 但無法控制 agent 自行讀取的其他檔案或使用的模型** ([ADR-0004](../adr/0004-agent-as-analyzer.md))。後者由 Skill 指引與使用者的 agent 權限設定處理。

## 2. Secret management

- Token 只從環境變數讀取 (預設 `WORKLENS_GITHUB_TOKEN` / `WORKLENS_GITLAB_TOKEN`, 或 source 的 `token_env`); **不提供 `--token` 選項, 設定檔不接受 token 欄位** (含 token 欄位的設定檔視為錯誤 `config.secret_in_file`)。
- 最小權限: GitHub fine-grained PAT, 只授權目標 repository 的 `Contents: read`、`Pull requests: read`、`Metadata: read` (Issues 需要時加 `Issues: read`); GitLab `read_api` (或 `read_repository` + `read_api` 的最小組合, M1 驗證)。文件中列出所需 scope, `collect --dry-run` 顯示將使用的 scope。
- Token 只放在 `HttpClient` 的 `Authorization` header, 不放 URL query (GitLab 的 `private_token=` 參數不使用)。
- M4 評估 OS credential store (Windows Credential Manager / macOS Keychain / libsecret)。

## 3. 日誌脫敏

- 單一 `Redactor` 套用於所有日誌、錯誤訊息與 `--json` 輸出:
  - 已知 token 的實際值 (從環境變數讀到的字串) → `***`。
  - Header: `Authorization`、`PRIVATE-TOKEN`、`Cookie`。
  - Pattern: `ghp_`、`github_pat_`、`gho_`、`glpat-` 開頭的字串; URL 中的 `token=`、`private_token=`、`access_token=` 參數。
- 預設不記錄 HTTP body; `--verbose` 只記錄 method、URL (脫敏後)、status、rate limit header。
- 測試: 以假 token 執行失敗路徑, 斷言 stdout / stderr / 輸出檔不含該字串 (M1)。

## 4. Agent 輸入資料最小化 (digest)

預設 (`digest.redaction.mode: default`), 可在設定中收緊, 放寬需明確設定:

| 資料 | 預設處理 |
|---|---|
| Diff 內容 | **不包含**; `--include-diff` 為 opt-in, 且只含指定路徑的有限行數 (OQ-06, M2) |
| Email | 移除; 只保留 display name |
| Commit / PR body | 截斷至 `max_body_chars`; 套用 secret pattern redaction (同 §3, 外加常見 key 格式: AWS `AKIA…`、私鑰 header、`password=`、連線字串) |
| 檔案路徑 | 只保留前 `path_depth` 層目錄 |
| URL | 只保留 repository 與 external id, 移除 query |
| 自訂規則 | 設定 `digest.redaction.patterns` (`match` regex → `replace` 字串), 例如客戶名稱 |

- Digest 的 `truncation` / `redaction` metadata 記錄被處理的數量 (不記錄原文), 讓使用者知道有內容被移除。
- Skill 指引要求 agent 只使用 digest, 不自行讀取 diff 或原始碼 (除非使用者要求)。
- 公司政策是否允許雲端模型處理 commit message / PR 描述: OQ-13, 使用者自行確認; WorkLens 文件提醒此點。

## 5. 資料保留

- 工作目錄預設 `.worklens/`, 已列入 `.gitignore`; `worklens init` 在 repo 內建立工作目錄時會檢查 `.gitignore`, 未排除則警告。
- 不自動上傳、不自動刪除。保留期限依 OQ-09 決定; 在那之前文件建議: 報告完成後保留 `report.json`、`manifest.json` 與 `.pptx`, `evidence.json`、`digest.json`、`cache/` 可刪除 (可重新 collect)。
- M3 提供 `worklens clean --keep-report` 刪除可重建的中間檔。
- 不在 WorkLens 外部保存任何資料; 無遙測。

## 6. Evidence provenance 與完整性

- 每個 evidence item 保留 `source.url`、`external_id` (commit SHA / PR number)、`fetched_at`。
- Report IR 的每個 agent 產生的 work item 至少引用一個 evidence ID; `observed_facts` 必須引用證據 (schema 強制); 引用的 ID 必須存在 (M0 參照完整性)。
- `report.json` 綁定 `evidence_document.sha256`; `manifest.json` 記錄每個檔案的 SHA-256 與產生命令、版本 (WorkLens、schema `$id`、template id/version)。
- 投影片 `source_refs` 讓每頁內容可追溯回 work item 與 evidence; M0 評估在 slide notes 中寫入來源連結 (可設定關閉, 避免分享時外露內部 URL)。
- 不承諾防竄改 (無簽章); hash 用於偵測意外的過期與不一致。

## 7. 報告內容的誠實性 (交接文件 §5.5)

- 統計數字由 WorkLens 從證據計算, agent 不能填寫。
- 推論 (`agent_inference`) 在投影片上標示為推論與信心等級。
- 驗證規則對無證據支持的百分比 / 倍數宣稱發出 warning (`rule.unreferenced_numeric_claim`)。
- 不產生績效分數, 不以 commit 數排名。

## 8. 檢查清單 (各 Milestone 驗收時)

- [ ] 失敗路徑的輸出不含 token (M1)。
- [ ] 設定檔含 token 欄位時拒絕執行 (M1)。
- [ ] Digest 預設不含 email、diff、URL query (M2)。
- [ ] Secret pattern redaction 有單元測試 (M2)。
- [ ] `.worklens/` 未被 `.gitignore` 排除時警告 (M3)。
- [ ] 模板範例 slides / 備註 / 文件屬性不帶入輸出 (M0)。
