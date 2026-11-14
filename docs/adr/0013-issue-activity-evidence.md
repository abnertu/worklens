# ADR-0013: 以獨立證據記錄 Issue 狀態變更

## Status

Accepted — 2026-11-14

## Context

- 月報需要引用本人換看板欄位、關閉或重開的 issue。
- 目前指派給本人的 issue 也需要計入他人的上述變更。
- 既有 `issue.occurred_at` 表示關閉時間, 未關閉時表示建立時間。
- GitLab 的 label 與 state events 不提供指派歷史。

## Decision

1. Evidence IR 1.1 新增 `issue_activity`, 每個 issue 一筆證據。
2. `transitions` 記錄期間內計入的 label 新增、關閉與重開事件。
3. `occurred_at` 使用最後一個計入事件的時間。
4. `query.sources` 記錄 `issue_status_labels`, 讓查詢身分包含狀態設定。
5. `author` 記錄報告對象。存在本人事件時選 `actor`, 否則選目前符合的 `assignee`。
6. 指派人以蒐集當下為準。重新指派會改變過去月份的結果。
7. Layout 顯示 issue 的 ID、標題、狀態與日期。狀態轉換細節留在 evidence。

## Consequences

- 既有 `issue` 欄位的意義不變。
- 同一 issue 的多個事件共用一筆證據, 報告不必重複引用每個事件。
- 目前指派規則無法還原事件當時的指派人。
- 新 reader 依文件版本驗證 1.0 與 1.1。`collect` 一律寫出 1.1。
- WorkLens 不新增 LLM 呼叫或 SDK。
