# ADR-0007: 以工作目錄 JSON 檔 + provenance manifest 儲存

## Status

Proposed — 2026-11-11

## Context

- 交接文件 §2 把儲存列為 Open (JSON files / SQLite)。
- v0.1 pipeline 的每個階段由不同角色執行 (WorkLens、agent、人), 需要一個彼此都能讀寫的交換媒介。Agent 與人都擅長讀寫檔案。
- 需要記錄 provenance: source snapshot hash、schema 版本、template 版本、renderer 版本 (交接文件 §4.3、§6)。
- 歷史比較、跨期間查詢、保留期限尚未決定 (OQ-09)。

## Decision

- 每份報告一個工作目錄 (預設 `.worklens/reports/<period>/`, 可由 `--workspace` 覆寫), 內含 `evidence.json`、`digest.json`、`report.json`、`presentation.json`、輸出檔、`manifest.json`。
- `manifest.json` 記錄每個產出檔的 SHA-256、產生命令、schema `$id`、template id / version、WorkLens 版本、時間; 命令執行前比對輸入檔 hash, 若上游檔案在下游產生後被修改, 回報為過期 (stale) 並要求重跑下游。
- 寫檔採「寫入暫存檔 → 原子 rename」, 避免中斷造成半個檔案。
- 不使用資料庫。HTTP 回應快取 (M1) 也放在工作目錄下 `cache/`, 可安全刪除。
- 工作目錄預設在 repo 外或被 `.gitignore` 排除 (`.worklens/`), 因為內容可能含機密 ([security-privacy.md](../security/security-privacy.md))。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| JSON 檔 (選擇) | agent 與人可直接讀寫; 易於 diff 與審核; 無 schema migration 工具需求 | 無跨期間查詢; 並行寫入需自行處理 | 採用 |
| SQLite | 查詢、歷史、交易 | agent 與人不易直接編輯; 需 migration; v0.1 無查詢需求 | 延後, OQ-09 決定後評估 |
| 只輸出 PPTX, 不保存中間檔 | 最簡單 | 失去 provenance 與可重現性; 違反 Evidence first | 不採用 |

## Consequences

- 同一工作目錄不支援並行執行多個命令; CLI 以 lock 檔偵測並拒絕 (M3)。
- 若未來需要歷史比較, 以匯入工作目錄的方式建立索引, 不需改變 pipeline 檔案格式。
