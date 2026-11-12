# WorkLens — Open Questions 登錄

> Date: 2026-11-11
> OQ-01..OQ-12 依[交接文件](handoff/worklens-initial-discussion.md) §9 原順序; OQ-13 起為規劃過程新增。
> `Decided` 項目記錄決策與決策者; `Open` 項目記錄在決定前採用的**預設假設**與最晚需決定的 Milestone ([implementation-backlog.md](plans/implementation-backlog.md))。
> 預設假設不代表已解決。

### OQ-01 首個目標環境: Windows/Linux/macOS? 本地 CLI 或 CI Runner?
- Status: Open
- 影響: 路徑處理、字型 (中文換行與替代)、secret 存放方式。
- 預設假設: 主要在 Windows 本機, 由使用者在 agent CLI (Claude Code / Codex CLI) 中執行; 程式碼保持跨平台 (不用 Windows 專屬 API), CI Runner 延後 (見 OQ-12)。
- 需決定於: M4 之前 (部署 / 排程)。

### OQ-02 GitLab 是 GitLab.com 還是 Self-managed? 是否需要 GitHub Enterprise?
- Status: Decided — 採預設假設: 兩者都以可設定的 `base_url` 支援 (GitHub 預設 `https://api.github.com`, GitLab 預設 `https://gitlab.com/api/v4`), 包含 GitHub Enterprise (`https://<host>/api/v3`) 與 self-managed GitLab; `base_url` 只允許 `https`。實作以 REST API v3 / v4 公開文件為準; 真實環境驗證: GitHub 個人 repo (M1a)、self-managed GitLab (M1b)。決策者: 使用者 (2026-11-12)。
- 影響: base URL 設定、API 版本差異、TLS / proxy、認證方式。
- 規則: `base_url` 正規化 (scheme / host 小寫、去除預設 port 443、去除結尾 `/`) 後參與 evidence ID ([domain-model.md](design/domain-model.md) §2.1); 預設 token 變數 (`WORKLENS_GITHUB_TOKEN` → `GITHUB_TOKEN`) 只用於 `https://api.github.com`, Enterprise 主機必須以 `token_env` 指定變數。

### OQ-03 使用者身份範圍: 僅個人月報, 還是未來支援團隊?
- Status: Open
- 影響: Report IR 的 `subject` 結構、作者別名對照、權限與隱私。
- 預設假設: v0.1 僅個人月報; Report IR 以 `subject` 物件 (而非單一字串) 表示對象, 為團隊報告保留擴充空間, 但不實作。
- 需決定於: M3 之後 (不阻擋 v0.1)。

### OQ-04 可用的企業 PPTX 樣板是否有特殊 Master、Placeholder、字型、圖表、動畫、SmartArt?
- Status: Decided (現況) — 目前沒有企業模板, PoC 以自製樣本模板進行。決策者: 使用者 (2026-11-11)。
- 影響: Renderer 的能力邊界、Template Manifest 的 slot 類型。
- 後續: 取得企業模板時依 [pptx-poc-plan.md](plans/pptx-poc-plan.md) §6 的重驗清單重新驗證; 在那之前 Renderer 只承諾支援樣本模板使用到的元素 (文字 placeholder、項目符號、表格)。

### OQ-05 是否允許使用雲端 LLM 處理 Commit Message / Diff? 是否需要 On-premises / Local LLM?
- Status: Decided — v0.1 由 agent CLI (Claude Code / Codex CLI) 進行分析, WorkLens 本身不呼叫 LLM; 資料送往哪個模型由使用者的 agent CLI 設定決定。決策者: 使用者 (2026-11-11)。
- 影響: WorkLens 的責任是**控制交給 agent 的內容** (digest 最小化與 redaction), 見 [security-privacy.md](security/security-privacy.md)。
- 仍未決: 公司政策是否允許把 commit message / PR 描述交給雲端 agent — 見 OQ-13。

### OQ-06 AI 分析應包含到什麼深度: 只分析 Message、PR/MR, 還是部分 Diff / 檔案變更?
- Status: Open
- 影響: digest 內容、資料外洩風險、token 用量、摘要品質。
- 預設假設: digest 預設只含 commit message、PR/MR 標題與描述、檔案路徑與增刪行數統計; **不含 diff 內容**。diff 摘錄列為 opt-in 選項 (`--include-diff`), 於 M2 評估。
- 需決定於: M2 之前。

### OQ-07 月份使用哪個時區? 以 Author Date、Committer Date、Merge Date 哪一種時間為準?
- Status: Decided — 採預設假設: 期間以設定檔的 IANA 時區 (`timezone`, 未設定時為系統時區) 計算月界, 查詢時轉 UTC; commit 以 **author date** 歸屬期間, PR/MR 以 **merged_at** (未合併則 created_at) 歸屬。Evidence IR 同時保存 author / committer / merged 各時間, 規則可改不需重新蒐集。決策者: 使用者 (2026-11-12)。
- 影響: 月界計算、跨月 PR、rebase 後的 commit 日期。
- 規則:
  - 月初午夜在該時區不存在 (DST 跳躍) 時, 取該日第一個有效時刻。
  - Commit 範圍 = default branch 歷史 + 期間內 PR/MR 的 commits; 不列舉所有分支。
  - GitHub commits API 依 committer date 篩選, 因此查詢窗為 `since = period.start`、`until = period.end + 30 天`, 本地再以 author date ∈ `[start, end)` 篩選; committer date 晚於 end + 30 天的 commit 不會被取得 (已知限制)。
  - 期間內 PR 的 commits 中身分相符者全部收錄, `occurred_at` 為其 author date (可能在期間外), 由 PR 以 `contains` 關聯。

### OQ-08 首版人工修改是直接編輯 Report IR, 還是先輸出 PPTX 後自行修改?
- Status: Open
- 影響: Report IR 的 `human_notes` / 覆寫欄位、重新產生時的合併策略。
- 預設假設: 兩者都允許; **建議**流程為人工 (或請 agent 依指示) 編輯 `report.json` 後再渲染, 以保留可重現性。PPTX 上的手動修改不回寫。重新產生 Report IR 時, 由人填寫的欄位 (`human_notes`、`origin: human` 的 work item) 必須保留, 合併規則於 M3 實作。
- 需決定於: M3 之前。

### OQ-09 需要保存每次來源快照、審核結果與歷史版本嗎? 資料保留多久?
- Status: Open
- 影響: 儲存選型 (JSON 檔 vs SQLite)、磁碟用量、隱私。
- 預設假設: 每次報告的工作目錄保留所有中間檔與 `manifest.json` (provenance hash); 不自動刪除, 不跨期間建立索引。保留期限與歷史比較需求確定後再評估 SQLite ([ADR-0007](adr/0007-storage-workspace-json-files.md))。
- 需決定於: M4 之前。

### OQ-10 期望 Template Renderer 的驗證標準: 僅固定版型可用, 或要求跨 PowerPoint/LibreOffice 近似一致?
- Status: Decided — 正確性以 Open XML 結構斷言 (slot 文字、slide 數、layout 對應、`OpenXmlValidator`、正規化 snapshot; 不需 Office) 為主; 可編輯性以 PowerPoint 開啟 / 編輯 / 另存 / 重開 (`tools/poc/run-poc-checks.ps1`) 驗證; 視覺以人工檢視 PowerPoint 匯出的 PNG。不做跨檢視器 (LibreOffice 等) 驗證, 移至 backlog (觸發條件: 收件人使用非 PowerPoint 檢視器, 或 M4 需要 Linux CI 轉檔檢查); 像素級回歸測試延後。決策者: 使用者 (2026-11-11; 結果見 [pptx-poc-report.md](plans/pptx-poc-report.md))。
- 影響: 測試策略; CI 不需安裝 Office 或 LibreOffice。

### OQ-11 是否有授權預算使用商業 PPTX Library, 或只能使用 OSS?
- Status: Open
- 影響: Open XML SDK (MIT) 不足時的替代方案 (Aspose.Slides 為商業授權)。
- 預設假設: 只使用 OSS; Aspose.Slides 只在 PoC 證明 Open XML SDK 無法滿足必要需求時才提案。
- 註: Schema 驗證函式庫 JsonSchema.Net 9.x 的 OSMF EULA 已由使用者接受 (見 OQ-14)。
- 需決定於: M0 PoC 結束時 (若 PoC 失敗)。

### OQ-12 CI/CD 自動化何時開始, 輸出如何交付?
- Status: Open
- 影響: headless agent runner、secret 存放、輸出交付 (artifact / email / 共享資料夾)。
- 預設假設: v0.1 不做 CI 排程 (agent 由人驅動); headless runner 已列 backlog。repo 本身的 build/test CI 可於 M4 加入。
- 需決定於: M4。

### OQ-13 公司政策是否允許把 commit message、PR/MR 描述送進雲端 agent 的模型?
- Status: Open
- 影響: 是否需要更嚴格的 redaction、或要求使用本地模型的 agent 設定。
- 預設假設: 允許 commit message 與 PR/MR 標題描述; 不送 diff; redaction 規則預設開啟。使用者需自行確認所屬組織政策。
- 需決定於: M2 之前。

### OQ-14 JsonSchema.Net 9.x 的 OSMF EULA (營收 ≥ US$10,000 的商業使用需付維護費) 是否接受?
- Status: Decided — 接受, 使用 9.x。決策者: 使用者 (2026-11-11)。替代方案記錄於 [ADR-0008](adr/0008-libraries-schema-validation-and-http.md)。

### OQ-15 作者身分對照規則由誰維護?
- Status: Decided — 採預設假設: 使用者在設定檔 `identities` 列出本人的 email 與各平台 username; 只計入明確列出的身分; `Co-authored-by` 中出現本人身分的 commit 也計入但標記 `role: co_author`; bot 帳號以 `bot` flag 標記, 由 digest (M2) 依 `exclude` 排除, collect 不排除。決策者: 使用者 (2026-11-12)。
- 影響: 作者別名 (多個 email / username / bot-assisted commit, 如 `Co-authored-by`) 是否歸屬本人。
- 規則:
  - identity 沒有 `platform` 時適用所有平台; email 與 username 比對不分大小寫。
  - GitHub noreply email (`<id>+<login>@users.noreply.github.com` 與舊式 `<login>@users.noreply.github.com`) 解析出 login 後以 username 比對。
  - 角色優先序: author (author login 或 email) > co_author (`Co-authored-by` trailer email) > committer (committer login / email); 都不符合則不收錄。`author` 欄位記錄本人在該 item 中出現的身分。
