# ADR-0014：由呼叫端準備報告圖片

狀態：Accepted — 2026-11-15

## 背景

目前的渲染器只接受品牌 logo，無法呈現工作內容的流程圖。
WorkLens 必須保留不呼叫 LLM 的邊界。

## 決策

- Codex 與 Claude Code 撰寫圖的內容。
- Archify 在 WorkLens 外驗證並匯出圖片。
- WorkLens 驗證圖片參照，並嵌入本機 PNG 或 JPEG 的位元組。
- WorkLens 不啟動 LLM、Archify、Node.js 或瀏覽器。
- 圖片的幾何由模板定義。
- 圖片在框內保留原始比例。
- 圖片繼承所屬工作項目的審核狀態、來源與證據參照。
- Report IR 與 Presentation IR 保存圖片 ID、標題、路徑、SHA-256 與選填的產生者。
- `present` 與 `render` 預設納入圖片。
- `--no-archify` 只略過標示 `producer: archify` 的圖片。

## 結果

Core 不讀取圖片檔案。
CLI 將驗證後的圖片位元組交給渲染器。
沒有圖片的報告不需要額外執行環境。
PNG 與 JPEG 的標頭提供圖片尺寸。
標頭驗證不代表每個像素都能正確解碼。
圖片的位元組與中繼資料維持原樣。
呼叫端必須審核圖片的揭露範圍。
渲染器不檢查圖片像素中的主張或 secret。
每張流程圖是單一 PowerPoint 圖片，不是可編輯的節點與箭頭。
