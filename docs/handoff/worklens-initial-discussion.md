# WorkLens - 初步討論紀錄與 Agent CLI 規劃交接文件

> Status: Initial discussion / Planning handoff  
> Date: 2026-11-11  
> Version: 0.1.0  
> Purpose: 彙整已討論的產品方向、暫定架構、技術選型、MVP 範圍與待決策事項, 供 Agent CLI 延伸為可執行的實作規劃。  
> 注意: 本文件是討論結論與設計假設, **不是已核准的完整技術規格**。請區分 `Confirmed`、`Proposed` 與 `Open`。

## 1. 專案概述

- **專案名稱**: `WorkLens` (`Confirmed`)
- **定位**: AI-powered Work Intelligence & Reporting Platform (`Proposed`)
- **標語**: *Understand Work. Capture Impact.* (`Proposed`)
- **核心目標**: 從多種工作相關資料來源提取可追溯的證據, 透過 AI 分析、歸納工作成果與洞察, 再依指定模板產生一致且可人工補充的報告。
- **首個使用案例**: 每月分析個人的 GitHub/GitLab 活動, 自動產生可編輯的 PowerPoint 月報, 降低手工整理績效與工作成果的時間。
- **產品邊界**: Git Commit Analysis 是第一個功能, 不是整個系統的唯一資料來源或用途。PowerPoint 月報也是第一個輸出形式, 不是唯一輸出形式。

### 1.1 設計原則

1. **Source agnostic**: 收集來源與核心分析邏輯解耦。
2. **Analysis agnostic**: 可增加不同分析模組, 不將核心限定為 Commit 摘要。
3. **Output agnostic**: 分析結果與特定輸出格式解耦。
4. **Evidence first**: 每項 AI 產生的關鍵成果應可回溯到原始證據。
5. **Deterministic rendering**: 同一份經驗證的 Presentation IR、模板版本與 Renderer 版本, 在相同渲染環境下應產生一致的投影片內容與視覺布局。
6. **Human in the loop**: 保留人工審核、修正與補充, 不以 Commit 次數直接等同績效。
7. **Incremental delivery**: 先實現 CLI 端到端流程, 再考慮 Web UI、複雜 Workflow、動態 Plugins。

## 2. 已確認與暫定技術選型

| 項目 | 選型 | 狀態 | 補充 |
|---|---|---|---|
| 專案名稱 | WorkLens | Confirmed | Repository 建議為 `worklens` |
| 主要語言 | C# | Confirmed | 不需為 AI 功能額外引入 Python |
| 執行平台 | .NET 10 LTS | Proposed | 依部署環境與團隊政策確認 |
| 系統架構 | Modular Monolith + Pipeline | Proposed | 避免 MVP 過度模組化 |
| 資料契約 | JSON IR + Versioned Schema | Confirmed direction | 建議分層設計 |
| JSON 序列化 | System.Text.Json | Proposed | 驗證可採 JSON Schema library |
| GitHub 整合 | GitHub API / Octokit.NET / HttpClient | Proposed | 需支援多 Repo 與作者識別 |
| GitLab 整合 | GitLab REST API / HttpClient | Proposed | 可支援 Self-managed GitLab |
| AI 整合 | OpenAI SDK 或 Microsoft.Extensions.AI | Proposed | 供應商可替換, 模型與 Prompt 可版本化 |
| PPTX 生成 | Open XML SDK | Proposed, preferred | 以實際企業模板 PoC 驗證可行性 |
| PPTX 備選 | Aspose.Slides | Optional | 複雜模板或渲染測試可評估成本 |
| 操作入口 | .NET CLI | Proposed for MVP | 後續可增加 ASP.NET Core API / Web UI |
| 測試 | xUnit + Integration / Snapshot Tests | Proposed | 視覺回歸測試需要可重現的渲染環境 |
| 排程 | GitHub Actions / GitLab CI | Later | MVP 可先手動執行 |
| 儲存 | JSON files / SQLite | Open | 依快取、歷史版本與審核需求選擇 |

### 2.1 不採用的假設

- 不要求第一版一定具備 Web UI。
- 不要求第一版支援通用 Plugin Marketplace、任意 Workflow Editor 或任意格式渲染。
- 不要求 LLM 直接輸出 PowerPoint XML 或投影片元素座標。
- 不建議在 CI 或伺服器環境依賴 Microsoft Office Interop。

## 3. 目標架構

```text
External Sources
  GitHub / GitLab / (future: Issue Tracker, CI/CD, Docs, Calendar, Manual Input)
       |
       v
Source Connectors
  Fetch -> Normalize -> Deduplicate -> Associate
       |
       v
Evidence IR
  Provenance, timestamps, entities, source references
       |
       v
Analysis Modules (AI + deterministic preprocessing)
  Work grouping, summary, classification, impact assessment
       |
       v
Report IR / Semantic IR
  Work items, achievements, evidence links, manual additions
       |
       v
Report review / manual edits (optional in first CLI iteration)
       |
       v
Layout Resolver + Template Manifest
  Content capacity, ordering, page splitting, slot bindings
       |
       v
Presentation IR
  Slide types, slot bindings, source references
       |
       v
PPTX Renderer + Corporate Template
       |
       v
Editable .pptx
```

### 3.1 三層 IR 的職責

**Evidence IR**

- 跨來源的標準證據表示, 與 GitHub/GitLab 特定 API response 解耦。
- 至少包含: 穩定 ID、來源種類、外部 ID、來源 URL、Repo/Project、作者與作者識別、發生時間、摘要、相關資料、取得時間與可追溯欄位。
- 需區分 source event time 與 ingestion time, 並保留時區資訊或正規化為 UTC。
- 預留跨來源關聯, 例如 Issue ↔ PR ↔ Commit ↔ Deployment。

**Report IR (Semantic IR)**

- 描述「完成什麼工作、有哪些證據、狀態如何、影響是否有根據」。
- 不包含 PowerPoint 座標、顏色或字型。
- 可支援人工補充或修正, 避免下次重新產生時覆蓋人工成果。
- 應區分 `observed_facts`、`ai_inference`、`human_notes` 或相當欄位, 使事實與推論不混淆。

**Presentation IR**

- 描述最終投影片結構, 例如 `layout_id`、slide order、title、slot bindings、evidence references。
- 透過固定 Layout Registry / Template Manifest 選取合法的版型與元件。
- **不允許 AI 任意決定文字框座標、字體與配色**。
- 同一 Report IR 可以轉換成不同 Presentation IR 以套用不同模板。

### 3.2 建議的模組契約

```csharp
public interface IDataSourceConnector
{
    // Query and response types to be defined during design.
}

public interface IAnalysisModule
{
    // Consume normalized evidence and produce semantic results.
}

public interface IOutputRenderer
{
    // Render a validated output IR using a versioned template.
}
```

以上僅為擴充點示意, **不是可直接編譯的 API 規格**。Agent CLI 應補齊非同步操作、CancellationToken、失敗處理、版本契約與 DI 設計。

## 4. JSON IR 與一致性策略

### 4.1 最小語意範例 (示意)

```json
{
  "schema_version": "1.0",
  "report": {
    "period": "2026-10",
    "author": "example-user"
  },
  "work_items": [
    {
      "id": "WORK-001",
      "category": "feature",
      "status": "completed",
      "title": "使用者驗證機制優化",
      "summary": "實作 Token 更新與 Session 過期處理",
      "evidence_refs": ["EVIDENCE-001"],
      "human_notes": null
    }
  ]
}
```

### 4.2 最小 Presentation IR 範例 (示意)

```json
{
  "schema_version": "1.0",
  "template": {
    "id": "engineering-monthly",
    "version": "1.0.0"
  },
  "slides": [
    {
      "id": "slide-001",
      "layout_id": "achievement_detail_v1",
      "bindings": {
        "title": "使用者驗證機制優化",
        "summary": "實作 Token 更新與 Session 過期處理"
      },
      "source_refs": ["WORK-001"]
    }
  ]
}
```

這些 JSON 僅用來表達設計方向。正式 Schema 尚待定義, 包括命名、欄位必填條件、狀態值、證據指標、相容性與 migration。

### 4.3 Renderer 的一致性規則

- 使用固定的 `layout_id`、Template Manifest、Design Tokens 與 Slot 定義。
- 固定順序與分頁規則, 超出頁面容量時自動複製合法版型或拆頁。
- 文字超長需經可測試的 Overflow Policy 處理, 而不是由 AI 即興排版。
- Schema 檢查不等於視覺驗證。仍須檢查字型替代、換行、文字遮擋、圖表、圖片裁切。
- 快取與版本紀錄: source snapshot hash、Prompt version、model ID、Report IR version、Template version、Renderer version。
- AI 內容不保證每次完全相同。若要重現審核過的月報, 需重用/凍結經審核的 Report IR, 而不是每次重新呼叫模型。
- 不建議直接以 PPTX 二進位雜湊作為視覺一致性判斷, 檔案 metadata 可能不同。

## 5. 首個 MVP - Engineering Monthly Report

### 5.1 使用者故事

> 使用者指定月份、Git 作者、GitHub/GitLab Repository 與現有 PPTX 模板後, 系統自動蒐集該期間的程式碼開發活動, 以 AI 分組歸納具證據支撐的工作成果, 輸出結構化 Report IR 與可編輯的月報 PPTX, 並保留人工補充空間。

### 5.2 建議 MVP 範圍

1. 指定月份、作者與一個或多個 GitHub/GitLab Repository。
2. 透過 API 分頁取得期間內的 Commit, 並盡可能關聯 PR/MR 與 Issue (可分階段)。
3. 正規化、去重、過濾無意義活動、識別作者別名。
4. 分組工作主題, 以 LLM 產生摘要與分類, 保留可追溯來源。
5. 產生符合 Schema 的 Report IR, 以 deterministic rules 轉成 Presentation IR。
6. 讀取既有 `.pptx` 模板, 依固定 Slot/Layout 填入內容並輸出可編輯的 PPTX。
7. 預留人工補充欄位, 尤其是非 Git 工作、問題與改善、下月規劃。
8. CLI 可重複執行並具有清楚的錯誤訊息與輸出檔案。

### 5.3 建議月報內容

| 頁面類型 | 內容 | 自動化程度 |
|---|---|---|
| Cover | 月份、作者、標題 | 高 |
| Summary | 工作重點、活動統計、完成項目數 | 高 |
| Achievements | 依主題整理完成的工作項目 | 高, 需審核 |
| Project Detail | 重要功能/修復及其證據連結 | 高, 需審核 |
| Challenges / Improvements | 遇到問題、處理方式與改善建議 | AI 草稿 + 人工 |
| Next Month Plan | 下月目標與待辦 | 人工為主, 可參考 Issue |

### 5.4 概念上的 CLI

```bash
worklens report generate \
  --period 2026-10 \
  --author example-user \
  --template templates/company-monthly.pptx \
  --output reports/2026-10.pptx
```

需在規劃階段決定 Repository 清單、憑證與 AI 設定的傳入方式, 且**不可將 Token 寫入日誌或命令列歷史**。

### 5.5 MVP 非目標

- 不自動為員工打績效分數。
- 不將 Commit 數量等同實際貢獻。
- 不在無數據下宣稱效能提升百分比、成本節省或品質改善。
- 不提供任意 PPTX 自由排版生成器。
- 不優先建置多人 Dashboard、Marketplace、Web Workflow Editor。

## 6. 資料處理與安全注意事項

- **作者身分**: 一名使用者可能在 GitHub/GitLab 使用不同 email、username 或 bot-assisted Commit, 需定義對應規則。
- **時間邊界**: 月報期間應明確定義 Timezone, 建議以使用者設定時區計算月界, API 查詢轉 UTC。
- **重複統計**: Merge Commit、Cherry-pick、Squash、跨分支/跨 Repo 可能造成重複歸因, 需定義策略。
- **工作完成判準**: Commit、merged PR/MR、closed Issue、deployment 各代表不同事件, 不應自動視為已完成或已上線。
- **推論可靠性**: `facts` 與 `interpretation` 分開儲存; 證據不足時輸出 `unknown` 或待人工確認。
- **API 限額與網路錯誤**: 需具備 Pagination、Retry with Backoff、Rate Limit 與可恢復流程。
- **機密資料**: AI 上傳最小化, 機密 Diff / Secret / PII 不應原樣送至雲端模型。考慮公司政策、Redaction 與本地模型選項。
- **權限**: 最小權限的 Personal Access Token 或 App credentials, 保護密鑰並提供日誌脫敏。
- **審計**: 保留每項成果引用的來源 URL / SHA / external ID, 及其產生流程版本。

## 7. 建議專案結構 (草案)

```text
WorkLens/
├── WorkLens.slnx
├── src/
│   ├── WorkLens.Domain/
│   ├── WorkLens.Application/
│   ├── WorkLens.Contracts/
│   ├── WorkLens.Infrastructure/
│   ├── WorkLens.Connectors/
│   ├── WorkLens.Analysis/
│   ├── WorkLens.Rendering/
│   └── WorkLens.Cli/
├── schemas/
├── templates/
├── docs/
└── tests/
```

這是邏輯結構, 不代表 MVP 一定需要每個資料夾都拆成獨立 `.csproj`。具體的 Project 邊界與 Dependency Rules 應由 Agent CLI 進一步設計並提出取捨。

## 8. 後續可擴充功能 (未承諾)

| 領域 | 候選功能 |
|---|---|
| Source Connectors | Jira、Linear、Notion、Confluence、CI/CD、Calendar、手動筆記 |
| Analysis Modules | Release Notes、Issue 進度、開發趨勢、跨來源關聯、知識萃取 |
| Reporting | 週報、月報、季度報告、年度績效、專案進度與交接文件 |
| Output Renderers | PPTX、Markdown、HTML、PDF |
| Workflow | 排程、人工審核、歷史比較、通知、自訂流程 |
| UX | ASP.NET Core API、Web UI、IR 編輯與預覽 |

建議先建立擴充契約, **不在 MVP 先做這些所有功能**。

## 9. 待決策問題 (Open Questions)

1. 首個目標環境: Windows/Linux/macOS? 本地 CLI 或 CI Runner?
2. GitLab 是 GitLab.com 還是 Self-managed? 是否需要 GitHub Enterprise?
3. 使用者身份範圍: 僅個人月報, 還是未來支援團隊?
4. 可用的企業 PPTX 樣板是否有特殊 Master、Placeholder、字型、圖表、動畫、SmartArt?
5. 是否允許使用雲端 LLM 處理 Commit Message / Diff? 是否需要 On-premises / Local LLM?
6. AI 分析應包含到什麼深度: 只分析 Message、PR/MR, 還是部分 Diff / 檔案變更?
7. 月份使用哪個時區? 以 Author Date、Committer Date、Merge Date 哪一種時間為準?
8. 首版人工修改是直接編輯 Report IR, 還是先輸出 PPTX 後自行修改?
9. 需要保存每次來源快照、審核結果與歷史版本嗎? 資料保留多久?
10. 期望 Template Renderer 的驗證標準: 僅固定版型可用, 或要求跨 PowerPoint/LibreOffice 近似一致?
11. 是否有授權預算使用商業 PPTX Library, 或只能使用 OSS?
12. CI/CD 自動化何時開始, 輸出如何交付?

## 10. Agent CLI 下一階段任務

請根據以上討論, 產出可審核、可逐步實作的 WorkLens v0.1 技術規劃, **先規劃, 不要直接建置所有功能**。

### 預期交付物

1. **Architecture Decision Records (ADR)**: C#/.NET、Modular Monolith、三層 IR、Open XML SDK、LLM integration 與 storage 選型。
2. **Domain Model 與 Interface Contracts**: Evidence、WorkItem、Report、Presentation、Template Manifest, 以及 Connector/Analyzer/Renderer 的邊界。
3. **Versioned JSON Schema**: Evidence IR、Report IR、Presentation IR 的正式 Schema, 及版本遷移策略。
4. **MVP Pipeline Sequence Diagram**: Git API → Normalization → Analysis → Validation → Layout Resolution → Rendering。
5. **PPTX Template PoC Plan**: 選一份現有模板, 驗證 Placeholder、Slide clone、Overflow、中文換行、樣式保持、可編輯性。
6. **Implementation Backlog**: Milestones、每階段驗收條件、測試策略、依賴與風險。
7. **Security / Privacy Plan**: Secret management、Prompt data minimization、資料保留、Evidence provenance。
8. **CLI UX Proposal**: Commands、Configuration、環境變數、輸出格式、Dry-run 與錯誤處理。

### 建議執行順序

- **Phase 0 - Architecture & PPTX PoC**: 先用手寫 Report IR 驗證 schema → layout → PPTX, 確認模板可行。
- **Phase 1 - Git Collectors**: GitHub/GitLab 的資料擷取、作者識別、期間查詢與 Evidence IR。
- **Phase 2 - AI Analyzer**: Commit/PR/MR 工作聚合、可追溯摘要、Report IR 與快取。
- **Phase 3 - End-to-end CLI**: 完整月報流程與人工補充接口。
- **Phase 4 - Hardening**: 回歸測試、錯誤恢復、保密措施與部署/排程。

### 要求 Agent CLI 特別遵守

- 不把示範 JSON 視為定稿 Schema。
- 不把推測技術效果寫成已證實的績效。
- 避免過度設計, 優先交付一條穩定的端到端 Vertical Slice。
- 清楚標示建議與已決策項目, 對重大選型提出替代方案與 Trade-offs。
- 不依賴 LLM 直接控制投影片幾何布局。
- 先驗證現有 PPTX 模板, 再鎖定 Renderer 的具體實作細節。

---

## 11. 可直接交給 Agent CLI 的任務指令

```text
請閱讀 WorkLens_Initial_Discussion.md, 將它視為產品方向與初步架構討論的交接資料。

目標: 擬定 WorkLens v0.1 的可執行技術規劃。
技術偏好: C#、.NET 10、Modular Monolith、Versioned JSON IR。
首個案例: GitHub/GitLab -> Evidence IR -> AI Analysis -> Report IR -> Presentation IR -> Editable PPTX。

請先:
1. 區分已確定的決策、設計假設與待確認事項。
2. 提供可擴充但不過度設計的架構與 Solution / Project 劃分。
3. 設計三層 IR 的 Schema、C# Models 與模組契約。
4. 規劃 PPTX 模板 PoC, 明確列出 Open XML SDK 的風險與替代方案。
5. 將 MVP 拆成有依賴順序的 Milestones、工作項目與驗收標準。
6. 納入資料安全、AI 證據追溯、一致性測試及失敗處理。

先輸出 Architecture Proposal、ADRs、Schema Draft 與 Implementation Plan。
不要在未確認企業 PPTX 模板與其他 Open Questions 前, 假定所有邊界條件已解決。
```
