# ADR-0006: 以 Open XML SDK 渲染 PPTX

## Status

Accepted — 2026-11-12 (使用者依 M0 PoC 結果決定; Proposed 2026-11-11)

「run 不寫格式覆寫」只適用 1.0 manifest。
1.1 manifest 改依 [ADR-0010](0010-manifest-style-roles.md)。

## Context

- 輸出必須是可編輯的 `.pptx`，並套用既有模板的 Master、Layout 與 Placeholder。
- 模板與 Template Manifest 決定版面，依據為交接文件 §3.1、§4.3。
- AI 不決定版面。
- 目前沒有企業模板。
- PoC 使用 [pptx-poc-plan.md](../plans/pptx-poc-plan.md) 所述的樣本模板。
- 渲染必須能在沒有 Office 的環境執行。
- 交接文件 §2.1 不建議 Office Interop。
- 授權預算仍未決，記錄於 OQ-11。
- 預設使用 OSS。

## Decision

以 **DocumentFormat.OpenXml** 在 `WorkLens.Rendering.Pptx` 實作 `IOutputRenderer` 的 PPTX 版本。
Open XML SDK 採 MIT 授權。

依下列順序渲染簡報：

1. 開啟模板。
2. 移除範例 slide、其 notes 與個人資訊 parts。
3. 依 Presentation IR 的 `layout_id` 找到 manifest 指定的 slide layout。
4. 從該 layout 建立 slide，不複製 prototype slide。
5. 透過 placeholder 的 `idx` 或 name，填入 slot 的文字、項目符號與表格。
   不寫入 run 格式覆寫。
6. 複製 layout 的頁尾與頁碼 placeholder。
   在 manifest 指定的位置寫入頁尾文字與品牌。
   品牌包含單位名稱與 logo。
7. 重寫文件屬性。
8. 存檔。

- Layout Resolver 依 manifest 容量 `max_items`、`max_chars`，在渲染前處理 overflow。
- 它會拆頁，或截斷文字並加上標記。
- Renderer 不量測文字。
- 以 PowerPoint 實測校正容量。
- 不以模板的 `normAutofit` 作為後備。
- PowerPoint 開啟檔案時不計算 `fontScale`。
- 自動驗證使用 Open XML 結構斷言、`OpenXmlValidator` 與正規化 snapshot。
- 這些檢查不需要安裝 Office。
- 透過 `tools/poc/run-poc-checks.ps1`，以 PowerPoint 驗證外觀與可編輯性。
  1. 開啟簡報。
  2. 編輯簡報。
  3. 另存簡報。
  4. 檢視匯出的 PNG。
- 依 OQ-10，不進行 LibreOffice 等跨檢視器驗證。
- PoC 結果與證據見 [pptx-poc-report.md](../plans/pptx-poc-report.md)。

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

- 中文換行與字型替代取決於檢視端與已安裝字型。
- Renderer 無法保證這些結果。
- 以全形字為基準設定保守的 manifest 容量，並以 PowerPoint 實測降低風險。
- 其他檢視器仍未驗證。
- 容量以全形字計算，英文為主的內容可能提早截斷。
- 能感知寬度的 `max_chars` 仍列於 backlog。
- 依交接文件 §4.3，不以 PPTX 二進位雜湊判斷一致性。
- 一致性測試比較抽取出的 slide 結構與文字。
- 企業模板若失敗，依 PoC 計畫的重驗清單重新評估本 ADR。
