# ADR-0017: 設定報告檔名與年月

## Status

Accepted — 2026-11-16

## Context

每月輸出需要自動帶入年月與報告對象名稱。
累計報告的顯示標籤不一定是單一年月。
民國年適合顯示，西元年月適合保存與排序。

## Decision

1. 以 Config 1.1 的 `report.output_filename` 指定單一檔名格式。
2. 在 Presentation IR 保留結構化的報告期間與對象。
3. 從報告時區的最後包含時刻取得歸屬月份。
4. 以選填的西元 `report.output_month` 覆寫累計報告的歸屬月份。
5. 讓明確的 `--output` 優先，並保留舊預設值。

代入項目與拒絕條件見 [CLI 契約](../design/cli-ux.md)。
命名 helper 不讀檔、不讀環境變數，也不使用執行時間。
不解析 label 或投影片文字。
任何命名錯誤都在寫檔前回報。
Config 的選填 `$schema` 只供編輯器提示。
執行時仍使用依 `config_version` 選擇的內建 schema。

## Consequences

同一份輸入與設定產生相同檔名。
檔名可使用西元年月或民國年顯示。
舊 Presentation IR 使用設定命名前，需要重新執行 `present`。
明確輸出路徑與舊預設值不需要新增脈絡。
