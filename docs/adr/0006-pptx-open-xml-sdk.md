# ADR-0006: 以 Open XML SDK 渲染 PPTX

## Status

Accepted — 2026-11-12 (使用者依 M0 PoC 結果決定; Proposed 2026-11-11)

「run 不寫格式覆寫」只適用 1.0 manifest。1.1 manifest 改依 [ADR-0010](0010-manifest-style-roles.md)。

## Context

- 輸出必須是可編輯的 `.pptx`, 套用既有模板的 Master / Layout / Placeholder; 版面由模板與 Template Manifest 決定, 不由 AI 決定 (交接文件 §3.1、§4.3)。
- 目前沒有企業模板; PoC 使用自製樣本模板 ([pptx-poc-plan.md](../plans/pptx-poc-plan.md))。
- 渲染需在無 Office 的環境執行; 交接文件 §2.1 不建議 Office Interop。
- 授權預算未定 (OQ-11), 預設只用 OSS。

## Decision

- 以 **DocumentFormat.OpenXml** (Open XML SDK, MIT) 實作 `IOutputRenderer` 的 PPTX 版本, 放在 `WorkLens.Rendering.Pptx`。
- 渲染策略: 開啟模板 → 移除模板中的範例 slides (含 notes) 與個人資訊 parts → 依 Presentation IR 的 `layout_id` 找到 Template Manifest 指定的 slide layout → 以該 layout 建立新 slide (不複製 prototype slide) → 依 slot 的 placeholder 識別 (`idx` / name) 填入文字、項目符號、表格, run 不寫格式覆寫 → 複製 layout 的頁尾 / 頁碼 placeholder, 並在 manifest 宣告的位置寫入頁尾文字與品牌值 (單位名稱、logo) → 重寫文件屬性 → 存檔。
- Overflow 由 Layout Resolver 依 manifest 的容量 (`max_items`、`max_chars`) 事先處理 (拆頁 / 截斷 + 標記), Renderer 不做文字量測。容量以 PowerPoint 實測校正; 模板的 `normAutofit` 不能當作後備 (PowerPoint 開檔時不計算 `fontScale`)。
- 驗證: 自動測試以 Open XML 結構斷言、`OpenXmlValidator` 與正規化 snapshot 為主 (不需 Office); 可編輯性與視覺以 PowerPoint 開啟 / 編輯 / 另存與人工檢視 PNG 驗證 (`tools/poc/run-poc-checks.ps1`)。不做跨檢視器 (LibreOffice 等) 驗證 (OQ-10)。
- PoC 結果與證據: [pptx-poc-report.md](../plans/pptx-poc-report.md)。

## Alternatives

| 方案 | 授權 | 優點 | 缺點 | 結論 |
|---|---|---|---|---|
| Open XML SDK (選擇) | MIT | 微軟維護; 完整存取 PresentationML; 無 runtime 相依 | 低階 API, slide 複製需自行處理關聯 part (圖片、chart、notes); 無文字量測與渲染 | 採用 (PoC P1–P4、P7–P10 通過) |
| ShapeCrawler | MIT | 建在 Open XML SDK 上的高階 API | 社群專案, 維護者少; 對複雜模板的支援不確定 | 不需要: 由 layout 建 slide 避開了 slide 複製 |
| Aspose.Slides | 商業 | 功能最完整, 含渲染為圖片 (可做視覺回歸) | 授權費用; OQ-11 未決 | 僅在 PoC 失敗時提案 |
| Office Interop | — | 與 PowerPoint 完全一致 | 需安裝 Office; 不支援伺服器 / CI; 交接文件不建議 | 不採用 |
| python-pptx | MIT | 成熟 | 需引入 Python, 違反 ADR-0001 | 不採用 |
| 產生 HTML/PDF 而非 PPTX | — | 渲染簡單 | 不符合「可編輯 PPTX」需求 | 不採用 (未來可作為另一個 renderer) |

## Consequences

- 中文換行與字型替代取決於檢視端與字型安裝, 無法由 Renderer 保證; 以 manifest 的保守容量 (全形字最壞情況) 與 PowerPoint 實測降低風險。非 PowerPoint 檢視器未驗證 (已知風險)。
- 容量以全形字計, 英文為主的文字會提早截斷 (backlog: 寬度意識的 `max_chars`)。
- 不使用 PPTX 二進位雜湊判斷一致性 (交接文件 §4.3); 一致性測試比較抽取出的 slide 結構與文字。
- 若換成企業模板後失敗, 依 PoC 計畫的重驗清單重新評估本 ADR。
