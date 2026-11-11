# WorkLens — CLI UX Proposal

> Status: Proposed — 2026-11-11
> 介面決策: [ADR-0005](../adr/0005-agent-interface-cli-json-skill.md)。使用者包含**人**與 **agent CLI** (Claude Code / Codex CLI)。

## 1. 命令總覽

| 命令 | 輸入 | 輸出 | Milestone |
|---|---|---|---|
| `worklens --version` | — | 版本字串 (例 `0.1.0`) | v0.1 骨架 |
| `worklens schema list [--json]` | — | 內建 schema 的 `$id` 清單 | v0.1 骨架 |
| `worklens schema show <name>[:<version>]` | — | schema 內容 (stdout) | M0 |
| `worklens init [--workspace <dir>]` | — | `worklens.json` 範本 | M3 |
| `worklens collect --period <yyyy-MM> [--source <id>...]` | `worklens.json`、token 環境變數 | `evidence.json` | M1 |
| `worklens evidence digest` | `evidence.json` | `digest.json` | M2 |
| `worklens report validate [--report <file>] [--strict]` | `report.json`、`evidence.json` | 驗證結果 | 骨架為 stub; M0 |
| `worklens present [--manifest <file>]` | `report.json`、`evidence.json`、manifest | `presentation.json` | M0 |
| `worklens render [--template <pptx>] [--output <file>]` | `presentation.json`、manifest、模板 | `.pptx` | M0 |
| `worklens report generate --period <yyyy-MM> ...` | — | 依序執行 present + render (不含 agent 步驟) | M3 |
| `worklens status` | 工作目錄 | 各檔案存在 / 過期 (stale) 狀態與建議的下一步 | M3 |

v0.1 骨架: `--version` 與 `schema list` 可用; `collect`、`evidence digest`、`report validate`、`present`、`render` 已註冊但回 exit code **10** (not implemented)。Stub 命令**沒有必填參數**, 以確保 `worklens collect` 不帶參數也回 10 而不是解析錯誤 1。

交接文件 §5.4 的概念命令 `worklens report generate --period ... --author ... --template ... --output ...` 對應到 M3 的便利命令; 由於分析由 agent 執行, 它只負責 agent 完成 `report.json` 之後的步驟。

## 2. 共通選項

| 選項 | 說明 |
|---|---|
| `--workspace <dir>` | 工作目錄; 預設 `.worklens/reports/<period>/` (由 `--period` 或 `worklens.json` 推得) |
| `--config <file>` | 設定檔; 預設依序找 `./worklens.json`、`~/.worklens/worklens.json` |
| `--json` | stdout 輸出單一 JSON 物件 (見 §5); 不輸出進度文字 |
| `--dry-run` | 只顯示將執行的動作 (要呼叫的 API、要寫入的檔案), 不發出網路請求、不寫檔 |
| `--verbose` | stderr 輸出詳細日誌 (仍經過脫敏) |
| `--no-color` | 停用色彩; 非 TTY 時自動停用 |

## 3. 設定

### 3.1 `worklens.json` (不含任何 secret)

```json
{
  "config_version": "1",
  "subject": { "display_name": "Example User" },
  "timezone": "Asia/Taipei",
  "identities": [
    { "platform": "github", "username": "example-user" },
    { "platform": "gitlab", "username": "example.user" },
    { "email": "example-user@example.com" }
  ],
  "sources": [
    { "id": "gh-auth", "platform": "github", "base_url": "https://api.github.com", "repository": "example-org/auth-service" },
    { "id": "gl-billing", "platform": "gitlab", "base_url": "https://gitlab.example.com/api/v4", "repository": "platform/billing", "token_env": "WORKLENS_GITLAB_TOKEN_CORP" }
  ],
  "exclude": { "bots": true, "merge_commits": true, "paths": ["**/*.lock"] },
  "digest": { "include_diff": false, "max_body_chars": 1000, "path_depth": 3, "redaction": "default" },
  "report": { "template_manifest": "templates/sample-monthly/manifest.json" }
}
```

設定的 JSON Schema 於 M3 定義 (`config` 不屬於 IR 三層, 但同樣以 schema 驗證)。

### 3.2 環境變數

| 變數 | 用途 |
|---|---|
| `WORKLENS_GITHUB_TOKEN` | GitHub token (預設); 未設定時嘗試 `GITHUB_TOKEN` |
| `WORKLENS_GITLAB_TOKEN` | GitLab token (預設) |
| 自訂 (`token_env`) | 每個 source 可指定讀取哪個環境變數, 支援多個 GitLab 實例 |
| `WORKLENS_CONFIG` | 設定檔路徑 (優先於預設搜尋) |
| `WORKLENS_WORKSPACE_ROOT` | 工作目錄根 (預設 `.worklens/`) |
| `NO_COLOR` | 停用色彩 |

**Token 只能從環境變數 (或 M4 評估的 OS credential store) 讀取; 不提供 `--token` 命令列選項**, 避免 token 進入 shell history 與 agent 的命令紀錄 (交接文件 §5.4)。

## 4. 工作目錄布局

```text
.worklens/reports/2026-10/
├── evidence.json        # collect
├── digest.json          # evidence digest
├── report.json          # agent 撰寫 → 人工審核
├── presentation.json    # present
├── report.pptx          # render
├── manifest.json        # provenance (每個命令更新)
└── cache/               # HTTP 回應快取, 可刪除
```

## 5. 輸出契約

### 5.1 人類可讀 (預設)

- stdout: 結果摘要 (寫了哪些檔、幾筆證據、驗證問題清單)。
- stderr: 進度與日誌。
- 一律 UTF-8 (`Console.OutputEncoding = UTF8`), 避免 Windows 主控台 cp950 造成中文亂碼。

### 5.2 `--json`

stdout 恰好一個 JSON 物件, 結尾換行:

```json
{
  "output_version": "1",
  "command": "report validate",
  "ok": false,
  "exit_code": 2,
  "result": {
    "files": { "report": ".worklens/reports/2026-10/report.json" },
    "error_count": 1,
    "warning_count": 1
  },
  "issues": [
    {
      "code": "ref.evidence_missing",
      "severity": "error",
      "instance_path": "/work_items/1/evidence_refs/0",
      "schema_path": null,
      "message": "Evidence 'ev-0000000000000000' does not exist in evidence.json."
    },
    {
      "code": "rule.completed_without_merged_change",
      "severity": "warning",
      "instance_path": "/work_items/0/status",
      "schema_path": null,
      "message": "status is 'completed' but no referenced change request is merged; confirm manually."
    }
  ],
  "error": null
}
```

- `issues`: 驗證問題 (欄位同 [domain-model.md §7](domain-model.md) 的 `ValidationIssue`)。
- `error`: 非驗證類的失敗 `{ "code", "message", "details" }`, 例如 `config.missing_token`、`source.rate_limited`、`command.not_implemented`。
- `message` 為英文 (穩定, 便於 agent 比對); 人類可讀模式可本地化。
- 日誌與進度在 `--json` 模式下只寫 stderr。

`schema list --json` 的 `result`:

```json
{ "schemas": [ { "id": "urn:worklens:schema:evidence-ir:1.0", "name": "evidence-ir", "version": "1.0" } ] }
```

## 6. Exit codes

| Code | 意義 | agent 應採取的動作 |
|---|---|---|
| 0 | 成功 (可能有 warning) | 繼續下一步 |
| 1 | 用法 / 參數解析錯誤 (System.CommandLine 預設) | 修正命令 |
| 2 | 驗證失敗 (有 error 級 issue; `--strict` 時 warning 也算) | 依 `issues` 修正輸入檔後重跑 |
| 3 | 設定錯誤 (設定檔無效、缺 token、找不到工作目錄或輸入檔) | 回報使用者 |
| 4 | 來源 / 網路錯誤 (API 失敗、rate limit 用盡、權限不足) | 稍後重試或回報 |
| 5 | 上游檔案過期 (stale): 輸入檔在下游產生後被修改 | 依提示重跑上游命令 |
| 10 | 命令尚未實作 | 回報使用者 |
| 70 | 未預期的內部錯誤 | 回報使用者, 附 `--verbose` 日誌 |

## 7. Dry-run 與錯誤處理

- `--dry-run` 適用於 `collect` (列出將查詢的 repo、期間的 UTC 範圍、預估分頁數)、`present`、`render` (列出將產生的 slides 與輸出路徑)。
- 錯誤訊息包含: 發生什麼、哪個檔案 / 欄位、建議動作。範例: `config.missing_token: Source 'gl-billing' requires environment variable WORKLENS_GITLAB_TOKEN_CORP.`
- 所有日誌與錯誤訊息都經過脫敏 (token、Authorization header、URL 中的 `private_token=` 等), 見 [security-privacy.md](../security/security-privacy.md)。
- 中斷 (Ctrl+C) 時取消進行中的 HTTP 請求, 不留下半寫的檔案 (原子寫入)。

## 8. Agent Skill 草案

正式的 Skill 檔 (Claude Code `SKILL.md` / Codex `AGENTS.md` 片段) 於 M3 交付; 以下是內容草案。

```markdown
---
name: worklens-monthly-report
description: 使用 WorkLens 產生具證據的工作月報 (GitHub/GitLab → PPTX)。
---

# 流程
1. `worklens collect --period <yyyy-MM> --json`。exit 3 → 告知使用者缺少的設定或 token, 停止。
2. `worklens evidence digest --json`, 讀取 result.files.digest 指向的 digest.json。
3. `worklens schema show report-ir` 取得 Report IR schema。
4. 依 digest 撰寫 report.json:
   - 依主題把證據分組成 work_items; 每個 agent 產生的 work item 至少引用一個 evidence ID。
   - observed_facts 只寫證據中可直接看到的事實, 並引用 evidence ID。
   - 推論寫在 agent_inference, 並給 confidence。
   - 證據不足以判斷是否完成時, status 用 unknown。
   - 不寫統計數字 (WorkLens 會計算); 不寫證據中沒有的百分比、時間或成本節省。
   - 保留檔案中既有的 origin: human 項目與 human_notes, 不修改。
5. `worklens report validate --json`。exit 2 → 依 issues 的 code 與 instance_path 修正, 重跑; 最多 3 次後把剩餘問題交給使用者。
6. 請使用者審核 report.json (warning 需要使用者確認)。
7. 使用者同意後: `worklens present --json`、`worklens render --json`。

# 禁止事項
- 不要在命令列傳 token, 不要印出環境變數。
- 不要直接修改 evidence.json、presentation.json 或 .pptx。
- 不要讀取 digest 以外的原始碼或 diff 來補充內容, 除非使用者明確要求。
```
