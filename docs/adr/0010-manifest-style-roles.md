# ADR-0010: Template Manifest 的樣式角色

## Status

Accepted — 2026-11-14 (使用者於 2026-11-13 決定樣式契約)

## Context

- 月報模板依狀態標籤的值決定顏色。模板還有 accent 色的證據 ID、徽章與交錯底色的表格。
- Placeholder 繼承讓每個 placeholder 只有一種格式。它無法依值設定儲存格或 run 的格式。
- ADR-0006 不讓 Renderer 在 run 寫入格式覆寫。
- IR 不得包含字型、顏色或座標。

## Decision

1. Template Manifest 1.1 在 `styles` 定義樣式角色。
2. 角色可以設定字級、粗體、文字色、底色與下框線。每個格式都是選填。
3. Presentation IR 只帶角色名稱。
4. Renderer 只從模板與 manifest 的 `styles` 取得格式。
5. 表格 slot 指定表頭列的角色 (`header_style`) 與內容列的角色 (`row_styles`, 依列循環)。
6. 表格 slot 可以用 GUID 指定表格樣式 (`table_style`)。沒有指定時, 表格維持模板的預設樣式。
7. Presentation IR 中 `styles` 沒有定義的角色不是錯誤。此時 Renderer 不為它寫入格式。
8. Manifest 內引用未定義的角色是錯誤 (`manifest.unknown_style`)。
9. 字重為 bold 或 regular。設計規格的 medium 以 regular 呈現。

## Consequences

- ADR-0006 的「run 不寫格式覆寫」現在只適用 1.0 manifest。
- 1.0 manifest 沒有 `styles`, 所以它的輸出不變。
- 模板的外觀取決於 `.pptx` 與 manifest。改變外觀可能需要同時修改兩者。
- 表格外觀來自儲存格底色與下框線, 因為 PowerPoint COM 無法建立自訂表格樣式。

## Alternatives

| 方案 | 結論與理由 |
|---|---|
| 只用 placeholder 繼承 | 不採用: 無法依值設定格式 |
| 格式放在 Presentation IR | 不採用: IR 不得包含字型與顏色; Layout 會依賴模板的外觀 |
| 在 `tableStyles.xml` 加自訂表格樣式 | 不採用: 多一個建置步驟, 且 PowerPoint COM 無法建立它們 |
