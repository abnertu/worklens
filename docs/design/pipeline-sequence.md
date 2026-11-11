# WorkLens — MVP Pipeline Sequence

> Status: Proposed — 2026-11-11
> 對應交接文件 §10 交付物 4: Git API → Normalization → Analysis → Validation → Layout Resolution → Rendering。
> 在 v0.1 中 Analysis 由 agent CLI 執行 ([ADR-0004](../adr/0004-agent-as-analyzer.md))。命令與 exit codes 見 [cli-ux.md](cli-ux.md)。

```mermaid
sequenceDiagram
    autonumber
    actor User as 使用者
    participant Agent as Agent CLI (Claude Code / Codex)
    participant WL as worklens CLI
    participant API as GitHub / GitLab API
    participant WS as 工作目錄 (.worklens/reports/2026-10)

    User->>Agent: 產生 2026-10 月報
    Agent->>WL: collect --period 2026-10 --json
    WL->>WL: 讀 worklens.json, 從環境變數取 token
    alt 缺設定或 token
        WL-->>Agent: exit 3 + error(config.*)
        Agent-->>User: 回報需要的設定, 停止
    end
    loop 每個 source, 每一頁
        WL->>API: GET commits / PRs / MRs (期間轉 UTC)
        alt rate limit 或暫時性錯誤
            API-->>WL: 403/429/5xx + Retry-After
            WL->>WL: backoff 後重試 (有上限)
        else 成功
            API-->>WL: page + next link
        end
    end
    WL->>WL: Normalize, 作者身分比對, 去重, 標記 bot/merge commit, 建立 relations
    WL->>WS: 寫 evidence.json (Evidence IR) + manifest.json
    WL-->>Agent: exit 0 + 證據筆數

    Agent->>WL: evidence digest --json
    WL->>WS: 讀 evidence.json, 寫 digest.json (去敏, 精簡, 無 diff)
    WL-->>Agent: exit 0 + digest 路徑

    Agent->>WS: 讀 digest.json (含 evidence document id 與 sha256)
    Agent->>WL: schema show report-ir
    WL-->>Agent: Report IR schema
    Agent->>Agent: 分組工作主題, 撰寫 facts / inference / summary
    Agent->>WS: 寫 report.json (Report IR)

    loop 直到通過, 最多 3 次
        Agent->>WL: report validate --json
        WL->>WS: 讀 report.json + evidence.json
        WL->>WL: Schema 驗證, 參照完整性, domain 規則
        alt 有 error 級 issue
            WL-->>Agent: exit 2 + issues[code, instance_path, message]
            Agent->>WS: 依 issues 修正 report.json
        else 通過 (可能有 warning)
            WL-->>Agent: exit 0 + warnings
        end
    end

    Agent-->>User: 請審核 report.json 與 warnings
    User->>WS: 修改 report.json (human_notes, 下月規劃, 非 Git 工作)
    User->>Agent: 同意

    Agent->>WL: present --json
    WL->>WS: 讀 report.json, evidence.json, template manifest
    WL->>WL: 再次驗證 report.json, 檢查 manifest 中的 hash 是否過期
    WL->>WL: Layout Resolver: 依 sequence 排序, 計算統計, 套用容量與 overflow 規則
    WL->>WS: 寫 presentation.json (Presentation IR)
    WL-->>Agent: exit 0 + slide 數

    Agent->>WL: render --json
    WL->>WS: 讀 presentation.json + 模板 .pptx
    WL->>WL: Open XML: 依 layout 建立 slides, 填入 placeholders
    WL->>WS: 寫 report.pptx (原子寫入), 更新 manifest.json
    WL-->>Agent: exit 0 + 輸出路徑
    Agent-->>User: 月報完成: report.pptx
```

## 補充

- **確定性區段**: `present` 與 `render` 只依賴檔案內容與版本; 以同一份已審核的 `report.json` 重跑, 得到相同的 Presentation IR 與相同的 slide 結構與文字 (交接文件 §1.1 原則 5)。重跑不需要 agent。
- **過期偵測**: 若 `evidence.json` 在 `report.json` 之後被重新產生 (hash 改變), `present` 回 exit 5; 執行 `worklens report rebind` (所有參照仍存在時更新綁定) 後重新 `report validate`。
- **人工介入點**: 「請審核 report.json 與 warnings」之後的人工審核是必要的; v0.1 不提供自動跳過審核的選項。
- **失敗處理**: 每個命令失敗時不覆寫既有輸出檔; agent 依 exit code 決定重試、修正或回報 (cli-ux.md §6)。
