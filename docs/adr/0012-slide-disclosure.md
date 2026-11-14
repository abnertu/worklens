# ADR-0012: Layout 處理投影片顯示的 Evidence IR 文字

## Status

Accepted — 2026-11-14

## Context

- 細節投影片顯示 change request 與 issue 的標題。這些標題是平台上的原文。
- 標題可能含有內部 URL、SSH remote、email、commit SHA 或 token。
- 投影片會離開本機。報告的讀者不需要連結。

## Decision

1. Layout 在投影片顯示 Evidence IR 的標題之前, 先以 `SlideDisclosure` 處理。
2. `SlideDisclosure` 先以沒有登記 secret 的 `Redactor` 遮蔽 token 樣式。
3. 之後移除 URL、SSH remote、email, 以及 7 到 40 個 hex 字元、同時含數字與字母的 commit SHA。
4. 樣式只由 ASCII 字元組成。CJK 字元不需要空白就能結束一個匹配。
5. Agent 或使用者撰寫的 work item 文字照原樣顯示。

## Consequences

- 順序有影響。先移除 hex 片段會讓 GitLab token 短於它的樣式, 其餘部分會被顯示。
- 沒有路徑的主機名稱不移除。`README.md/x` 這類字詞會被移除。
- Layout 仍是純函式: 它不讀環境, 所以不知道執行時登記的 secret。

## Alternatives

| 方案 | 結論與理由 |
|---|---|
| 不顯示證據標題 | 不採用: 標題讓讀者知道變更的內容 |
| 也處理 agent 撰寫的文字 | 不採用: agent 與使用者會審閱這些文字, 規則會移除他們選擇的字詞 |
