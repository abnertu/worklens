# WorkLens — CLI UX Proposal

> Status: Proposed — 2026-11-11
> 介面決策: [ADR-0005](../adr/0005-agent-interface-cli-json-skill.md)。使用者包含**人**與 **agent CLI** (Claude Code / Codex CLI)。

## 1. 命令總覽

| 命令 | 輸入 | 輸出 | Milestone |
|---|---|---|---|
| `worklens --version` | — | 版本字串 (例 `0.1.0`) | v0.1 骨架 |
| `worklens schema list [--json]` | — | 內建 schema 的 `$id` 清單, 每個版本一筆 | v0.1 骨架 |
| `worklens schema show <name>[:<version>]` | — | schema 內容 (stdout); 未指定版本時為內建的最新 minor | M0 |
| `worklens init [--workspace <dir>]` | — | `worklens.json` 範本 | M3 |
| `worklens collect --period <yyyy-MM> [--source <id>...]` | `worklens.json`、token 環境變數 | `evidence.json` | M1 |
| `worklens evidence digest` | `evidence.json` | `digest.json` | M2 |
| `worklens report validate [--report <file>] [--evidence <file>] [--strict]` | `report.json`、`evidence.json` | 驗證結果 | M0 |
| `worklens report rebind` | `report.json`、新的 `evidence.json` | 更新 `report.json` 的 `evidence_document` (所有參照仍存在時) | M3 |
| `worklens present --manifest <file> [--strict]` | `report.json`、`evidence.json`、manifest | `presentation.json` | M0 |
| `worklens render --manifest <file> [--template <pptx>] [--config <file>] [--output <file>]` | `presentation.json`、manifest、模板 | `.pptx` | M0 |
| `worklens report generate --period <yyyy-MM> ...` | — | 依序執行 present + render (不含 agent 步驟) | M3 |
| `worklens status` | 工作目錄 | 各檔案存在 / 過期 (stale) 狀態與建議的下一步 | M3 |

M1 現況: `collect` 支援 GitHub 與 GitLab。M0 命令維持可用。
`evidence digest` 仍為 stub, 回 exit code **10** (`command.not_implemented`)。

交接文件 §5.4 的概念命令 `worklens report generate --period ... --author ... --template ... --output ...` 對應到 M3 的便利命令; 由於分析由 agent 執行, 它只負責 agent 完成 `report.json` 之後的步驟。

## 2. 共通選項

| 選項 | 說明 |
|---|---|
| `--workspace <dir>` | 工作目錄; 目前預設為目前目錄, 自動期間目錄於 M3 提供 |
| `--config <file>` | 設定檔; 搜尋順序見 §3.1 |
| `--json` | stdout 輸出單一 JSON 物件 (見 §5); 不輸出進度文字 |
| `--dry-run` | 只顯示將執行的動作 (要呼叫的 API、要寫入的檔案), 不發出網路請求、不寫檔 |
| `--verbose` | stderr 輸出詳細日誌 (仍經過脫敏) |
| `--no-color` | 停用色彩; 非 TTY 時自動停用 |

### 2.1 M0 實際提供的選項

M0 命令提供下列選項。M1 的 `collect` 另提供 `--config`、`--dry-run`、`--verbose` 與 `worklens.json`。
`--no-color` 仍為後續規劃。

| 命令 | 選項 |
|---|---|
| `report validate` | `--workspace`、`--report`、`--evidence`、`--strict`、`--json` |
| `present` | `--workspace`、`--report`、`--evidence`、`--manifest`、`--output`、`--strict`、`--json` |
| `render` | `--workspace`、`--presentation`、`--manifest`、`--template`、`--config`、`--output`、`--json` |
| `schema show <name>[:<version>]` | 無 (stdout 為 schema 原文位元組; 未指定 `:<version>` 時輸出該名稱內建的最新 minor; 不認得的名稱或版本 → stderr `schema.not_found`, exit 1) |

- `--workspace <dir>` 預設為目前目錄; 檔名 `report.json`、`evidence.json`、`presentation.json`、`report.pptx`, 各由 `--report` / `--evidence` / `--presentation` / `--output` 覆寫。其他相對路徑以目前目錄解析。
- `--manifest` 在 M0 必須提供, 未提供 → exit 3 `config.missing_manifest` (M3 從 `worklens.json` 取預設)。`--template` 預設為 manifest 的 `template.file` (相對於 manifest 所在目錄)。
- `report validate` 與 `present` 都需要 evidence 檔; `present` 先執行與 `report validate` 相同的驗證。
- 輸出一律原子寫入 (同目錄暫存檔 + rename); 失敗時不留下暫存檔, 也不覆寫既有輸出。
- 讀檔以位元組讀入: SHA-256 對原始位元組計算, 解析前去除 UTF-8 BOM。JSON 輸出為 UTF-8 無 BOM、LF、結尾換行。

### 2.2 M1 的 `collect`

```bash
worklens collect --period 2026-10 --source gh-auth --config worklens.json --workspace .worklens/reports/2026-10 --json
```

- `--period` 必填, 格式為 `yyyy-MM`。缺少或格式錯誤回 exit 1 `command.usage_error`。
- 月份與下一個月的 UTC 邊界必須可表示。年份 `0000`、`9999-12` 或時區換算溢位也回 exit 1。
- `--source <id>` 可重複。未指定時選取全部 sources, 選取結果維持設定順序。
- 選取的 sources 依設定順序逐一蒐集。GitHub 與 GitLab sources 可混用。
- `--workspace` 預設為目前目錄。`--output` 預設為 workspace 中的 `evidence.json`; 明確指定的相對路徑以目前目錄解析。
- `collect` 在 HTTP 請求前建立缺少的輸出目錄。任一 source 失敗即停止, 回 exit 4, 並保留既有輸出。
- `collect` 只標記 bot、merge 與 revert。`exclude` 由 M2 的 digest 套用。

GitHub 蒐集範圍與限制:

- 讀取 default branch commits 與本人的 PR, 並收錄這些 PR 中身分相符的 commits。
- Commit 以 author date 歸屬期間。PR 以 merged_at 歸屬期間, 未合併時用 created_at。
- PR 中的 commits 可在期間外。身分比對與 `contains` 關聯見 [domain-model.md §2.1](domain-model.md#21-規則)。
- Default branch 查詢窗結束於期間結束後 30 天。更晚的 committer date 可能使較早的 author date 漏收。
- PR 依更新時間分頁。翻頁期間的 PR 更新可能造成漏收。
- PR commits 最多取得 250 筆。單一 commit 的檔案最多取得 3000 筆。
- 達截斷條件時輸出 `source.truncated` warning, exit 0。

GitLab 蒐集範圍與限制 (驗證目標為 GitLab 16.9.1):

- `repository` 為 project 路徑, 例如 `group/sub/app`。WorkLens 先解析 project, 之後以數字 ID 請求。
- 讀取 default branch commits 與本人的 merge requests (MR), 並收錄這些 MR 中身分相符的 commits。
- Commit 回應不含可靠的帳號名稱。Commit 以 author email、committer email 與 co-author trailer 比對, 所以 `identities` 必須列出 commit email。
- MR 以 API username 比對作者。MR 以 merged_at 歸屬期間, 未合併時用 created_at。
- Project 停用 MR 時 (`merge_requests_access_level` 為 `disabled`), 不查詢 MR, 只蒐集 commits。
- MR 的 `external_id` 為 project 內的 `iid`。`opened` 與 `locked` 對應為 `open`。
- MR commits 不設筆數上限。MR 不輸出 `stats` 與 `paths`, 因為 commit 統計的加總不是 MR 的淨變更。
- Commit 的檔案路徑取自 diff 分頁, 最多 3000 筆。達上限時輸出 `source.truncated` warning, exit 0。
- GitLab 伺服器的 diff 上限可能省略路徑, 且 16.9 不提供可偵測的截斷 metadata。
- 只有 API 明確標示時才標記 bot。舊版回應沒有 bot 欄位時, 不從顯示名稱推測。
- Default branch 查詢窗與 GitHub 相同, 結束於期間結束後 30 天。MR 以 `updated_after` 查詢並依更新時間分頁。
- 沒有分頁 header 的滿頁回應以 offset 分頁續讀, 每次最多 1000 頁。超過上限或內容重複時回 `source.invalid_response`。
- `Link` header 沒有任何含 `rel` 的連結時, 視為格式錯誤, 回 `source.invalid_response`。

## 3. 設定

### 3.1 `worklens.json` (不含任何 secret)

```json
{
  "config_version": "1.0",
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
  "digest": {
    "include_diff": false,
    "max_body_chars": 1000,
    "path_depth": 3,
    "redaction": { "mode": "default", "patterns": [ { "match": "Acme Corp", "replace": "[customer]" } ] }
  },
  "report": { "template_manifest": "templates/sample-monthly/manifest.json" }
}
```

M1a 提供 `urn:worklens:schema:config:1.0`。`worklens schema show config` 輸出設定 schema。
Config 1.1 新增選填的 `report.organization` (單位名稱) 與 `report.logo` (PNG 或 JPEG, 相對於設定檔)。只有 `render --config` 讀取它們; 沒有 `--config` 時, `render` 不加品牌元素。
`config` 不屬於 IR 三層, 但同樣以 schema 驗證。

設定搜尋依下列順序進行。明確指定的檔案不存在時不嘗試下一項。

1. `--config <file>`。
2. `WORKLENS_CONFIG`。
3. 目前目錄的 `worklens.json`。
4. `~/.worklens/worklens.json`。

- `identities` 與 `sources` 皆不可為空。每個 identity 至少有一個身分欄位。
- Source `id` 不可重複。正規化後的 platform、base_url 與 repository 組合也不可重複。
- `base_url` 必須使用 HTTPS, 且不可含 userinfo、query 或 fragment。
- `timezone` 必須為 IANA 名稱。未設定時使用本機時區轉換後的 IANA 名稱。
- 本機時區無法轉換時回 `config.invalid`, 並要求設定 `timezone`。
- 設定不接受 secret 欄位或完整 token 值。拒絕訊息只列 JSON Pointer, 見 [security-privacy.md §2](../security/security-privacy.md#2-secret-management)。
- 同一物件內不可有重複的 JSON 屬性。重複屬性回 `config.invalid`, 並附 `json.invalid` issue。

### 3.2 環境變數

| 變數 | 用途 |
|---|---|
| `WORKLENS_GITHUB_TOKEN` | 僅限 `https://api.github.com`; 未設定時嘗試 `GITHUB_TOKEN` |
| `WORKLENS_GITLAB_TOKEN` | 僅限 `https://gitlab.com/api/v4` |
| 自訂 (`token_env`) | 每個 source 可指定讀取哪個環境變數, 支援多個 GitLab 實例 |
| `WORKLENS_CONFIG` | 設定檔路徑 (優先於預設搜尋) |
| `WORKLENS_WORKSPACE_ROOT` | 工作目錄根 (M3 規劃; 目前未讀取) |
| `NO_COLOR` | 停用色彩 (規劃) |

**Token 只能從環境變數 (或 M4 評估的 OS credential store) 讀取; 不提供 `--token` 命令列選項**, 避免 token 進入 shell history 與 agent 的命令紀錄 (交接文件 §5.4)。

Source 指定 `token_env` 時只讀該變數, 不 fallback。空字串或只有空白視為未設定。
預設變數不會送往其他主機或其他平台。GitHub Enterprise 與自架 GitLab 必須指定 `token_env`。
一般蒐集缺少 token 時回 `config.missing_token`; dry-run 不要求 token。

## 4. 工作目錄布局

```text
.worklens/reports/2026-10/
├── evidence.json        # collect
├── digest.json          # evidence digest
├── report.json          # agent 撰寫 → 人工審核
├── presentation.json    # present
├── report.pptx          # render
└── manifest.json        # provenance (M3)
```

此布局由使用者以 `--workspace` 選取。HTTP 回應快取已延後, M1a 不建立 `cache/`。

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
    "files": { "report": "C:\\work\\2026-10\\report.json", "evidence": "C:\\work\\2026-10\\evidence.json" },
    "error_count": 2,
    "warning_count": 0
  },
  "issues": [
    {
      "code": "ref.evidence_missing",
      "severity": "error",
      "instance_path": "/work_items/1/evidence_refs/0",
      "schema_path": null,
      "message": "Evidence 'ev-0000000000000000' does not exist in the evidence document."
    },
    {
      "code": "ref.duplicate_id",
      "severity": "error",
      "instance_path": "/work_items/2/id",
      "schema_path": null,
      "message": "..."
    }
  ],
  "error": null
}
```

`files` 為完整路徑。驗證分層執行 (domain-model §3.2): 參照層有 error 時不執行規則層, 所以 `rule.*` warning 只會在 `ref.*` error 修正後才出現; 同一次輸出不會同時有 `ref.*` error 與 `rule.*` warning。

- `issues`: 驗證問題 (欄位同 [domain-model.md §7](domain-model.md) 的 `ValidationIssue`)。`schema_path` 只有 `schema.*` issue 有值 (JSON Schema evaluation path); 其他 issue 輸出 `null` (欄位一律存在)。
- `error`: 非驗證類的失敗 `{ "code", "message", "details" }`, 例如 `config.missing_token`、`source.rate_limited`、`command.not_implemented`。
- `message` 為英文 (穩定, 便於 agent 比對); 人類可讀模式可本地化。
- 日誌與進度在 `--json` 模式下只寫 stderr。

### 5.3 M0 的 code

| Code | 出現位置 | Exit |
|---|---|---|
| `json.invalid` | 輸入不是有效 JSON, 或含無法讀取的字串 (例如單獨的 UTF-16 surrogate escape `\ud800`) (report / presentation 為 issue; evidence / manifest 包在 `input.invalid` 內) | 2 (report、presentation) / 3 |
| `json.deserialize_failed` | schema 通過但無法轉成 C# 模型 (例 `rank` 超出 int) | 同上 |
| `schema.*` | JSON Schema 違規 (`schema.required`、`schema.additionalProperties`、`schema.type` 等) | 同上 |
| `schema.unsupported_version` | 版本欄位缺少、不是字串, 或不是內建的版本。`instance_path` 為版本欄位: `/schema_version`, 設定檔為 `/config_version`。訊息列出內建的版本 | 同上 |
| `ref.*`、`rule.*` | Report IR 參照完整性 (error, 含 `resolves`) 與 domain 規則 (warning), 見 domain-model §3.2 | 2 (`rule.*` 只在 `--strict` 時) |
| `presentation.*` | presentation 與 manifest 不一致: `template_mismatch`、`unknown_layout`、`unknown_slot`、`slot_kind_mismatch`、`missing_required_slot`、`table_row_width`、`table_column_count`、`footer_without_position`、`footer_too_long`、`invalid_character` | 2 |
| `layout.unplaced_narrative` | `present` 對沒有出現在任何投影片的 report narrative 給 warning issue。1.1 manifest 回報每個這樣的 narrative。1.0 manifest 只回報 `support` narrative。1.0 manifest 下, 其他 kind 的未放置 narrative 沒有 issue。`instance_path` 為 report 的 pointer `/narratives/<i>` | 2 (只在 `--strict` 時) |
| `input.not_found` | 輸入檔 (含模板) 不存在 | 3 |
| `input.unreadable` | 輸入檔存在但讀取失敗 (IO 錯誤、權限) | 3 |
| `input.invalid` | evidence / manifest 不是有效 JSON 或不符合 schema (`issues` 附上原因) | 3 |
| `config.missing_manifest` | `present` / `render` 未給 `--manifest` | 3 |
| `config.invalid_logo` | `--config` 的 logo 不是能讀出尺寸的 PNG 或 JPEG 圖檔 | 3 |
| `render.brand_not_written` | `render` 對沒有任何位置接收的品牌值給 warning issue。`instance_path` 為 manifest 的 pointer: 頁尾放不下時為 `/footer`, 沒有 layout 宣告位置時為 `/layouts` | 0 (warning) |
| `manifest.invalid` | manifest 內容有問題。`issues` 含 `manifest.duplicate_id` (layout id 或同一 layout 內的 slot id 重複)、`manifest.column_mismatch` 與 `manifest.unknown_style`, 以及 `manifest.unknown_layout`、`manifest.purpose_mismatch` 與 `manifest.slot_mismatch` (只有 `present`) | 3 |
| `template.invalid` | Renderer 回報模板問題; `issues` 含 `template.sha256_mismatch`、`template.unreadable`、`template.layout_missing`、`template.placeholder_missing`、`template.placeholder_position_missing` | 3 |
| `output.write_failed` | 輸出目錄不存在或目標檔被鎖定 (例如在 PowerPoint 中開啟) | 3 |
| `schema.not_found` | `schema show` 不認得的名稱或版本 (stderr) | 1 |
| `command.usage_error` | 參數解析錯誤或期間邊界無法表示 (`--json` 時仍輸出 envelope) | 1 |
| `command.not_implemented` | stub 命令 | 10 |
| `command.internal_error` | 未預期的例外 (`--json` 時仍輸出 envelope) | 70 |

`manifest.invalid` 的 1.1 issue:

- `manifest.column_mismatch`: 欄 key 不屬於該 purpose 的集合、purpose 沒有集合卻有 `columns`、key 重複, 或寬度總和不是 100。
- `manifest.unknown_style`: `header_style` 或 `row_styles` 的項目不是 `styles` 定義的角色。
- `present` 與 `render` 接受 1.0 與 1.1 manifest。

`presentation.table_column_count`: 有 `columns` 的 slot, 其表格的欄數與 `columns` 不同。
`presentation.footer_without_position`: presentation 有 `footer`, 但 manifest 沒有宣告頁尾位置。`presentation.footer_too_long`: `footer` 超過 manifest `footer.max_chars`。
`template.invalid` 中的品牌與頁尾 code: `template.slot_on_footer`: slot 或品牌位置綁定到頁尾或頁碼 placeholder。`template.brand_on_slot`: 品牌位置綁定到 slot 使用的 placeholder。`template.brand_conflict`: 同一 layout 的 logo 與單位名稱綁定到同一個 placeholder。模板沒有品牌位置的 placeholder 時為 `template.placeholder_missing`, logo placeholder 沒有位置時為 `template.placeholder_position_missing`。

`render` 的品牌規則: 單位名稱與 logo 只寫入 manifest 宣告的品牌位置。每個頁尾顯示單位名稱與 presentation `footer`, 以 `｜` 連接。超過頁尾容量時只截斷單位名稱; 放不下一個字時, 頁尾不放單位名稱, `render` 回 warning。

`schema list --json` 的 `result` 每個 (name, version) 一筆:

```json
{ "schemas": [ { "id": "urn:worklens:schema:evidence-ir:1.0", "name": "evidence-ir", "version": "1.0" } ] }
```

### 5.4 M1 的 code 與結果

| Code | 原因 | Exit |
|---|---|---|
| `config.not_found` | 找不到設定檔 | 3 |
| `config.invalid` | 設定無法讀取、schema 不符或語意規則失敗 | 3 |
| `config.secret_in_file` | 設定含 secret 欄位或完整 token 值 | 3 |
| `config.unknown_source` | `--source` 指定未知 id | 3 |
| `config.unsupported_platform` | 選取的 source 尚無 connector (保留給未來的平台) | 3 |
| `config.missing_token` | 所需 token 變數未設定 | 3 |
| `source.unauthorized` | HTTP 401 | 4 |
| `source.forbidden` | HTTP 403, 且不是 GitHub 可重試的 rate limit; GitLab 的 403 一律使用此 code | 4 |
| `source.not_found` | HTTP 404 | 4 |
| `source.moved` | HTTP 3xx; 不自動跟隨重新導向 | 4 |
| `source.request_failed` | 其他不可重試的 4xx | 4 |
| `source.rate_limited` | Rate limit 重試用盡或等待超過上限 | 4 |
| `source.server_error` | 5xx 重試用盡或等待超過上限 | 4 |
| `source.unreachable` | 網路錯誤或逾時重試用盡 | 4 |
| `source.invalid_response` | 回應無效、分頁連結離開 API 主機, 或 GitLab 分頁無效或用盡 | 4 |
| `source.truncated` | GitHub PR commits 或 commit files 截斷, 或 GitLab commit files 達上限 (warning) | 0 |
| `output.not_ignored` | 輸出位於 Git work tree 且未被忽略 (warning) | 0 |
| `command.cancelled` | 使用者取消 | 130 |

輸出目錄建立或寫檔失敗使用 `output.write_failed` (exit 3)。蒐集結果未通過內建 schema 使用 `command.internal_error` (exit 70)。

成功的 `collect --json` 結果:

```json
{
  "files": { "evidence": "/work/evidence.json", "config": "/work/worklens.json" },
  "document_id": "evd-2026-10-12345678",
  "period": { "label": "2026-10", "start": "2026-09-30T16:00:00Z", "end": "2026-10-31T16:00:00Z", "timezone": "Asia/Taipei" },
  "items_by_kind": { "commit": 12, "change_request": 3 },
  "requests": 20,
  "warnings": 0
}
```

`requests` 為所有 sources 的 HTTP 嘗試總數, 包含重試。`warnings` 為 warning issue 數量, 詳細內容在 envelope 的 `issues`。

Dry-run 的 `result` 包含 `dry_run: true`、`files.evidence`、`period` 與 `sources`。
每個 source 包含 `id`、`platform`、`base_url`、`repository`、`endpoints`、`token` 與 `scopes`。
`token` 只含 `variable` 與 `set`。GitHub Enterprise 或自架 GitLab 未指定變數時, `variable` 為 `null`。
GitHub 的 `scopes` 列出 fine-grained PAT 權限。GitLab 的 `scopes` 為 `read_api`。
GitLab 端點以 `projects/<id>` 表示解析後的 project ID。

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
| 130 | 使用者取消 | 需要時重新執行 |

## 7. Dry-run 與錯誤處理

- `collect --dry-run` 列出 repo、UTC 範圍、端點、token 變數名稱與設定狀態、所需權限。
- Dry-run 不要求 token、不建立目錄、不寫檔, 也不發出網路請求。設定與平台檢查仍會執行。
- `--verbose` 的 `remaining=` 取自 GitHub 的 `X-RateLimit-Remaining` 或 GitLab 的 `RateLimit-Remaining`。缺少時輸出 `-`。
- Dry-run 不估算分頁數, 因為分頁需要伺服器回應。`present` 與 `render` 的 dry-run 留待後續提供。
- 錯誤訊息包含: 發生什麼、哪個檔案 / 欄位、建議動作。範例: `config.missing_token: Source 'gl-billing' requires environment variable WORKLENS_GITLAB_TOKEN_CORP.`
- 所有日誌與錯誤訊息都經過脫敏 (token、Authorization header、URL 中的 `private_token=` 等), 見 [security-privacy.md](../security/security-privacy.md)。
- 中斷 (Ctrl+C) 時取消進行中的 HTTP 請求, 不留下半寫的檔案 (原子寫入)。
- 一般取消回 exit 130 `command.cancelled`。超過 System.CommandLine 的 `ProcessTerminationTimeout` 時, 強制結束可只回 130, 沒有 JSON envelope。

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
   - Report IR 的 evidence_document 直接複製 digest 中的 evidence_document (document_id 與 sha256)。
3. `worklens schema show report-ir` 取得 Report IR schema。
4. 依 digest 撰寫 report.json:
   - 寫 `schema_version: "1.1"`。
   - 依主題把證據分組成 work_items; 每個 agent 產生的 work item 至少引用一個 evidence ID。
   - observed_facts 只寫證據中可直接看到的事實, 並引用 evidence ID。
   - 推論寫在 agent_inference, 並給 confidence。
   - 證據不足以判斷是否完成時, status 用 unknown。
   - 伺服器維運工作用 `category: ops`。這類項目多半來自人工輸入。
   - subject 需要的支援寫成 `kind: support` 的 narrative。
   - improvement 解決某個 challenge 時, 把 challenge narrative 的 ID 寫在該 improvement 的 `resolves`。
   - 不寫統計數字 (WorkLens 會計算); 不寫證據中沒有的百分比、時間或成本節省。
   - 保留檔案中既有的 origin: human 項目與 human_notes, 不修改。
5. `worklens report validate --json`。exit 2 → 依 issues 的 code 與 instance_path 修正, 重跑; 最多 3 次後把剩餘問題交給使用者。
6. 請使用者審核 report.json (warning 需要使用者確認)。
7. 使用者同意後: `worklens present --json`, 再 `worklens render --config <worklens.json> --json`。

# 禁止事項
- 不要在命令列傳 token, 不要印出環境變數。
- 不要直接修改 evidence.json、presentation.json 或 .pptx。
- 不要讀取 digest 以外的原始碼或 diff 來補充內容, 除非使用者明確要求。
```
