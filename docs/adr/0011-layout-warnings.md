# ADR-0011: Layout 回傳不中止的 warnings

## Status

Accepted — 2026-11-14 (使用者於 2026-11-14 決定 1.0 manifest 回報 `support` narrative)

## Context

- Report IR 1.1 新增 `support` narratives。1.0 manifest 沒有放置它們的 section。
- 1.1 manifest 可以省略 section, 例如 `support`。此時該 section 的 narratives 不在任何投影片上。
- Layout 原本只回傳 errors。不在投影片上的 narrative 會消失, 而 `present` 以 exit 0 結束。

## Decision

1. `LayoutResult` 在 `Errors` 之外有 `Warnings`。Warning 不中止 Layout。
2. 每個沒有被任何投影片的 `source_refs` 列出的 narrative, Layout 回報一個 `layout.unplaced_narrative`。`Details["path"]` 指向 report。
3. 1.0 manifest 下, Layout 只回報 `support` narratives。
4. `present` 把每個 warning 寫成 warning issue, 以 exit 0 結束。
5. 加上 `--strict` 時, Layout warning 讓 `present` 以 exit 2 結束, 且 `present` 不寫出 `presentation.json`。

## Consequences

- 1.0 輸入的 issues 與輸出不變, 因為 1.0 report 沒有 `support`。
- 被 `max_items` 捨去的項目不算 warning。投影片的 `source_refs` 仍列出它。
- `--strict` 現在也涵蓋 Layout, 不只 report 的規則。

## Alternatives

| 方案 | 結論與理由 |
|---|---|
| 以 error 取代 warning | 不採用: 使用者可以刻意省略 section |
| 為每個 section 列出放置規則 | 不採用: `source_refs` 的判定對每種 section 與 layout 的組合都成立 |
