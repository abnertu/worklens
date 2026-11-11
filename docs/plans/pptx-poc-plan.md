# WorkLens — PPTX Template PoC Plan (M0)

> Status: Completed — 2026-11-11 (結果見 [pptx-poc-report.md](pptx-poc-report.md))
> 目的: 在鎖定 Renderer 實作細節之前, 驗證 Open XML SDK 能以既有模板產生可編輯、版面正確的月報 ([ADR-0006](../adr/0006-pptx-open-xml-sdk.md))。
> 現況: 尚無企業模板 (OQ-04), PoC 使用自製樣本模板; 換成企業模板時依 §6 重驗。

## 1. 範圍

**輸入**: 手寫的 `report.json` + `evidence.json` (沿用 `schemas/*/1.0/examples/valid/`; 其 sha256 綁定與 slide 結構彼此一致, 可作為 Layout Resolver 的 golden file, 再加上壓力案例), 樣本模板 `templates/sample-monthly/sample-monthly.pptx` 與 `manifest.json`。
**輸出**: `presentation.json` (Layout Resolver) 與 `report.pptx` (Renderer)。
**不在範圍**: GitHub/GitLab 擷取、agent、圖表 (chart)、圖片、動畫、SmartArt。

## 2. 樣本模板規格

以 PowerPoint 建立 (`templates/sample-monthly/build-template.ps1`, PowerPoint COM), 由 Office 預設主題保留 `Title Slide`、`Title and Content`、`Two Content`, 複製 `Title and Content` 產生 `Title and Table`, 並刪除其餘未使用的預設 layouts。刻意包含企業模板常見的難點:

| 元素 | 內容 | 驗證目的 |
|---|---|---|
| Slide Master | 1 個, 含 logo 圖片、頁尾文字、頁碼欄位 | 套版後保留 master 元素 |
| Layout `Title Slide` | title (`ctrTitle`, 無 idx; manifest 以 idx 0 指定)、subtitle (idx 1) | 一般 placeholder 填值 |
| Layout `Title and Content` | title、content placeholder `Content Placeholder 2` (idx 1, 多層級項目符號) | bullets 與 level; 自動縮排 |
| Layout `Title and Table` | title、表格 placeholder `Table Placeholder 5` (`tbl`, idx 13) | 表格建立、欄寬、表頭樣式 |
| Layout `Two Content` | title、左右兩個 content placeholder (`Content Placeholder 2` / `Content Placeholder 3`); summary 頁左側放 bullets、右側放表格 | 依 `name` 而非 `idx` 找 placeholder; 在 content placeholder 中建立表格 |
| 主題字型 | 拉丁: Calibri; 東亞: Microsoft JhengHei (微軟正黑體) | 中文字型套用與替代 |
| 範例 slides | 每個 layout 一頁「示範」 | 渲染後必須移除, 不留在輸出 |

模板與 manifest 一起放在 `templates/sample-monthly/`, manifest 記錄模板 `sha256`, Renderer 開檔前比對。

## 3. 驗證項目與驗收條件

| # | 項目 | 方法 | 驗收條件 |
|---|---|---|---|
| P1 | Placeholder 填值 | 以 manifest 的 `idx` / `name` 定位, 寫入文字 | 每個 slot 的文字出現在正確 placeholder; 未填的選填 placeholder 被移除 (不顯示「按一下以新增文字」) |
| P2 | Slide 建立 / 複製 | 由 layout 建立新 slide; 同一 layout 多頁 | slide 數與 Presentation IR 一致; 每頁的 layout 關聯正確; 範例 slides 已移除; `presentation.xml` 的 slide id 唯一 |
| P3 | 項目符號層級 | `bullets` 的 `level` 0–2 對應 `a:pPr lvl` | 層級與縮排沿用模板樣式, 不在程式中寫死字型大小 |
| P4 | 表格 | `table` slot 建立 `a:tbl` | 欄數 / 列數正確; 套用模板表格樣式; 可在 PowerPoint 中編輯儲存格 |
| P5 | Overflow | Layout Resolver 依 `max_items` / `max_chars` 拆頁或截斷 | 20 個 facts、300 字摘要的壓力案例: `split` 產生多頁並設 `continuation`; `truncate` 截斷並加 `…`, `truncated: true`; 不出現超出 placeholder 邊界的文字 (人工檢視) |
| P6 | 中文換行 | 純中文、中英混合、長 URL、全形標點 | PowerPoint 中無字元遺失或亂碼; 標點不出現在行首 (依 PowerPoint 的東亞換行規則) |
| P7 | 樣式保持 | 不設定任何 run 層級的字型 / 顏色 / 大小 | 輸出的 run 不含 `a:rPr` 字型 / 顏色覆寫 (結構斷言); 視覺與模板一致 |
| P8 | 可編輯性 | 在 PowerPoint 開啟、修改文字、另存 | 開啟時無「修復」提示; 文字可直接編輯; 版面可切換 layout |
| P9 | 檔案有效性 | `OpenXmlValidator` (Office 2019 以上) | 0 個驗證錯誤 |
| P10 | 確定性 | 同一 Presentation IR 渲染兩次 | 抽取的 slide 結構與文字 (正規化後的 JSON) 相同; 不比較 `.pptx` 二進位雜湊 (交接文件 §4.3) |
| P11 | 跨檢視器 | — | v0.1 不執行 (使用者決定不做 LibreOffice 等跨檢視器驗證); 記為已知風險 |

## 4. 自動化測試策略

- **結構斷言 (CI 可跑)**: 以 Open XML SDK 讀回輸出檔, 斷言 slide 數、每頁 layout 名稱、每個 placeholder 的文字、`a:rPr` 無覆寫、`OpenXmlValidator` 無錯誤。這是 Renderer 的主要回歸測試。
- **Snapshot**: 把輸出抽取成正規化 JSON (`slides[].layout`, `slides[].placeholders[].paragraphs[]`) 與 snapshot 比較 (P10)。
- **PowerPoint 檢查 (本機, 需 Office)**: `tools/poc/run-poc-checks.ps1` 以 COM 開檔、匯出每頁 PNG、修改文字 / 表格儲存格 / layout 後另存並重開 (P8)。
- **視覺檢查 (人工)**: 逐頁檢視 PowerPoint 匯出的 PNG; 像素級回歸延後 (OQ-10)。
- 測試模板為 `templates/sample-monthly/`; 壓力案例放在 `tests/Fixtures/stress/` (Core.Tests 與 Rendering.Pptx.Tests 共用)。

## 5. Open XML SDK 風險與替代方案

| 風險 | 影響 | 緩解 | 若無法緩解 |
|---|---|---|---|
| Slide 複製需手動處理關聯 part (圖片、notes、chart、hyperlink) | 複製含圖片的 prototype slide 時產生壞檔 | 優先「由 layout 建新 slide」而非複製 slide; 必要時以 helper 深度複製並重建 relationship id | 評估 ShapeCrawler |
| 無文字量測 | 無法精準判斷文字是否溢出 | Manifest 容量保守設定 + 壓力案例校正 (`normAutofit` 不能當後備: PowerPoint 開檔時不計算 `fontScale`, PoC P5) | 接受人工調整; 或 Aspose.Slides (可量測與渲染) |
| Placeholder 繼承鏈 (slide → layout → master) 複雜 | 找錯 placeholder 或樣式遺失 | 以 `idx` 為主、`name` 為輔; 結構斷言檢查繼承 | 在 manifest 中改為指定 shape name |
| 東亞換行與字型替代取決於檢視器與安裝字型 | 在未安裝字型的機器上換行不同 | 模板指定東亞字型; v0.1 只以 PowerPoint 驗證 | 接受差異, 以 PowerPoint 為準 |
| 企業模板使用 SmartArt / chart / 嵌入物件 | 無法以 slot 方式填值 | v0.1 不支援, manifest 不宣告這些 slot | 圖表改為表格; chart 支援另開需求 |
| SDK 大版本 API 變更 (3.x) | 升版成本 | Renderer 封裝在 `WorkLens.Rendering.Pptx`, 不外露 SDK 型別 | — |

替代方案比較見 ADR-0006。PoC 結果: ADR-0006 已改為 Accepted (2026-11-12); ShapeCrawler / Aspose.Slides 不需要。

## 6. 換成企業模板時的重驗清單

1. 盤點 master / layouts: 名稱、placeholder `idx` / `name` / 類型, 產出新的 `manifest.json` (`worklens template inspect` 尚未提供)。注意: 檔案內存的 layout / placeholder 名稱取決於建立模板時 Office UI 的語系 (例如 zh-TW UI 會存中文名稱), manifest 必須使用檔案內實際的名稱 (`p:cSld/@name`、`p:cNvPr/@name`), 不是英文 UI 顯示的名稱。 刪除未使用的預設 layouts, 避免使用者切換到沒有 manifest 對應的 layout。
2. 檢查是否有 SmartArt、chart、嵌入物件、影片、動畫、section、隱藏 slide; 列出不支援項目。
3. 字型: 確認拉丁 / 東亞主題字型及其授權, 渲染環境是否安裝。
4. 重跑 P1–P10, 特別是 P5 (容量需依新版面重新校正)、P6、P7、P8。
5. 確認模板內的機密資訊 (範例 slides 內容、備註、隱藏 slides、文件屬性) 不會被帶入輸出。
6. 版本化: 新模板 `template.version` 從 `1.0.0` 開始, manifest 記錄 `sha256`。

## 7. 產出與結束條件

- `templates/sample-monthly/` (模板 + manifest)、`src/WorkLens.Rendering.Pptx/`、`ILayoutResolver` 實作、測試。
- PoC 報告 `docs/plans/pptx-poc-report.md`: P1–P11 結果 (P11 不執行)、截圖、容量校正值、ADR-0006 的結論。
- 結束條件: P1–P4、P7–P10 通過; P5、P6 有記錄的結果與決策 (可接受的差異或後續工作); P11 記錄為不執行。
