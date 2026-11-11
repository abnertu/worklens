# WorkLens v0.1 — Domain Model 與模組契約

> Status: Proposed (Schema 1.0 草案)
> 正式欄位定義以 [`schemas/`](../../schemas/) 為準; 本文件說明語意、規則與契約。
> 交接文件 §4.1 / §4.2 的 JSON 範例僅為示意, 本文件取代之。

## 1. 共通慣例

| 項目 | 規則 |
|---|---|
| 命名 | JSON 欄位 `snake_case`; C# `PascalCase` (以 `JsonNamingPolicy.SnakeCaseLower` 對應) |
| Enum | JSON 為 `snake_case` 字串; C# enum 以 `JsonStringEnumConverter(SnakeCaseLower)` 對應 |
| 時間 | 一律 UTC; C# `DateTimeOffset`; canonical 形式見 [schema-versioning.md](schema-versioning.md) |
| 期間 | `period` = `{ label, start, end, timezone }`, `start` 含、`end` 不含 (`[start, end)`), `timezone` 為 IANA 名稱, `start`/`end` 為該時區月界換算的 UTC 時間 |
| 選填欄位 | 省略即為無值; 不寫 `null`; C# 一律 nullable |
| ID | 每層有自己的 ID 前綴與 pattern, 文件內唯一 (見各節) |
| 擴充 | 物件 `additionalProperties: false`; 不允許自訂欄位 |

## 2. Evidence IR (`urn:worklens:schema:evidence-ir:1.0`)

跨來源的標準證據。由 `worklens collect` 產生, agent 與人不應修改。

```text
EvidenceDocument
├── schema_version: "1.0"
├── document_id            "evd-<period>-<hash8>"
├── generated_at
├── generator { name, version }
├── query
│   ├── period
│   ├── identities[]       { platform?, username?, email? } — 查詢時視為本人的身分
│   └── sources[]          { platform, base_url, repository }
└── items[]  EvidenceItem
    ├── id                 "ev-<16 hex>" — 穩定 ID, 見 §2.1
    ├── kind               commit | change_request | issue
    ├── source             { platform: github|gitlab, base_url, repository, external_id, url }
    ├── title              commit 第一行 / PR/MR / issue 標題
    ├── body?              commit 其餘訊息 / 描述 (未 redaction 的原文, 只存在本機)
    ├── state?             open | closed | merged  (change_request / issue)
    ├── author             { name?, username?, email?, role: author|co_author|committer }
    ├── timestamps         { occurred_at, authored_at?, committed_at?, created_at?, merged_at?, closed_at? }
    ├── stats?             { files_changed, additions, deletions }
    ├── paths?[]           變更的檔案路徑 (相對 repo root)
    ├── relations?[]       { type: contains|references|closes, target_id: ev-... }
    ├── flags?[]           merge_commit | revert | bot | duplicate
    └── fetched_at
```

### 2.1 規則

- **穩定 ID**: `id = "ev-" + hex(SHA-256(platform | base_url | repository | kind | external_id))[0..16]`。同一證據重新蒐集得到相同 ID, 讓 Report IR 的參照在重跑 `collect` 後仍有效。
- `external_id`: commit 為完整 SHA; change_request 為 PR/MR number (`"!123"` / `"#123"` 前綴不存); issue 為 issue number。
- **`occurred_at`** 是歸屬期間的時間 (OQ-07 預設: commit = author date; change_request = merged_at, 未合併為 created_at; issue = closed_at, 未關閉為 created_at)。其他時間原樣保存。
- **時間區分**: `timestamps.*` 為來源事件時間; `fetched_at` 為擷取時間 (交接文件 §3.1)。
- **去重**: 同一 commit 出現在多個分支時只保留一筆 (以 ID); cherry-pick / squash 產生的不同 SHA 不自動合併, 而以 `relations` (`contains`: PR → commits) 關聯, 由分析者判斷 (交接文件 §6)。`duplicate` flag 只在可確定時標記 (例如同 patch-id, M1 評估)。
- **工作完成判準不在 Evidence IR**: `state: merged` 只是事實, 是否「完成」由 Report IR 判定。

## 3. Report IR (`urn:worklens:schema:report-ir:1.0`)

描述完成了什麼、證據為何、狀態如何。由 agent 撰寫, 人工審核與修改。**不含任何版面資訊。**

```text
ReportDocument
├── schema_version: "1.0"
├── report_id              "rpt-<period>-<suffix>"
├── period
├── subject                { display_name }  — v0.1 只支援個人 (OQ-03)
├── evidence_document      { document_id, sha256 } — 綁定 evidence.json
├── analysis?              { agent?, model?, notes? } — agent 自報, 未驗證
├── work_items[]  WorkItem
│   ├── id                 "wi-<slug>" (例 wi-001, wi-auth-refresh)
│   ├── origin             agent | human
│   ├── category           feature | fix | refactor | docs | test | ops | research | other
│   ├── status             completed | in_progress | unknown
│   ├── title
│   ├── summary            一兩句的成果描述
│   ├── observed_facts[]   { statement, evidence_refs[≥1] } — 可由證據直接驗證的事實
│   ├── agent_inference?   { statement, confidence: low|medium|high } — 推論 (例如影響)
│   ├── evidence_refs[]    ev-...; origin = agent 時 ≥ 1
│   ├── rank?              ≥ 1, 越小越重要; 未給則依陣列順序
│   ├── human_notes?       人工補充
│   └── review_status?     pending | approved | rejected (預設 pending)
└── narratives[]  Narrative
    ├── id                 "nr-<slug>"
    ├── kind               challenge | improvement | next_plan | other_work
    ├── origin             agent | human
    ├── text
    └── evidence_refs?[]
```

### 3.1 事實與推論的區分 (交接文件 §3.1、§6)

| 欄位 | 誰寫 | 要求 |
|---|---|---|
| `observed_facts[].statement` | agent | 必須能由 `evidence_refs` 指向的證據直接驗證 (例如「合併 PR #42 新增 token refresh」) |
| `agent_inference.statement` | agent | 明確是推論; 必須給 `confidence`; 呈現時標示為推論 |
| `summary` | agent / 人 | 不得包含證據中沒有的數值成效 (百分比、時間節省、成本) |
| `human_notes`、`origin: human` 的項目 | 人 | 重新產生 Report IR 時必須保留 (M3 合併規則) |

證據不足時 `status` 用 `unknown`, 而不是猜 `completed`。

**統計數字不在 Report IR。** Summary 頁的活動統計 (commit 數、PR 數、涉及 repo) 由 Layout Resolver 從 Evidence IR 確定性計算, agent 不能填寫, 避免數字被捏造或與證據不符。

### 3.2 驗證規則

`worklens report validate` 依序執行, 回傳所有問題 (不在第一個錯誤停止):

| 層級 | code | 嚴重度 | 規則 |
|---|---|---|---|
| Schema | `schema.<keyword>` | error | JSON Schema 驗證失敗; `<keyword>` 為失敗節點回報錯誤的 keyword (例 `schema.required`、`schema.enum`); 錯誤 key 為空字串時 (如 `additionalProperties: false`) 取該節點 evaluation path 的最後一段 (`schema.additionalProperties`) |
| 參照 | `ref.evidence_document_mismatch` | error | `evidence_document.sha256` 與實際 `evidence.json` 不符。重新 `collect` 後, 以 `worklens report rebind` 更新綁定: 只有在所有 `evidence_refs` 仍存在於新的 Evidence IR 時才改寫 `document_id` / `sha256`, 否則列出失效的參照並維持錯誤 |
| 參照 | `ref.evidence_missing` | error | `evidence_refs` 中的 ID 不存在於 Evidence IR |
| 參照 | `ref.duplicate_id` | error | `work_items` / `narratives` 的 `id` 重複 |
| 規則 | `rule.completed_without_merged_change` | warning | `status: completed`、`origin: agent`, 引用的證據中沒有 `state: merged` 的 change_request, 且 `review_status` 不是 `approved` (只有 commit 不足以判定完成; 需人工確認後設 `approved`) |
| 規則 | `rule.evidence_outside_period` | warning | 引用的證據 `occurred_at` 不在 `period` 內 |
| 規則 | `rule.unreferenced_numeric_claim` | warning | `summary` / `agent_inference.statement` 含百分比或倍數, 但該 work item 無 `observed_facts` 支持 (啟發式, M0 定義 pattern) |

v0.1 骨架只實作 **Schema** 層 (`IrSchemaValidator`, 對原始 JSON 驗證); `IReportValidator` (先 schema、再反序列化、再參照與規則) 於 M0 實作。Schema 驗證必須在反序列化之前, 因為 C# models 拒絕未定義欄位, 反序列化會直接失敗而得不到 `schema.additionalProperties`。warning 不影響 exit code (除非 `--strict`)。

## 4. Presentation IR (`urn:worklens:schema:presentation-ir:1.0`)

最終投影片結構, 由 `worklens present` 從 Report IR + Evidence IR + Template Manifest **確定性**產生。不含座標、字型、顏色。

```text
PresentationDocument
├── schema_version: "1.0"
├── presentation_id        "prs-..."
├── source_report          { report_id, sha256 }
├── template               { id, version }
└── slides[]
    ├── id                 "slide-001" (依順序編號)
    ├── layout_id          必須存在於 Template Manifest
    ├── bindings           { <slot_id>: SlotValue } — slot_id 必須是該 layout 定義的 slot
    ├── source_refs[]      wi-... / nr-... / ev-...
    └── continuation?      { index, total } — 同一內容拆成多頁時
SlotValue (以 kind 區分)
├── { kind: text, text }
├── { kind: bullets, items[]: { text, level: 0..2 } }
└── { kind: table, columns[], rows[][] }
任一 SlotValue 可帶 truncated?: true (經 overflow policy 截斷)
```

## 5. Template Manifest (`urn:worklens:schema:template-manifest:1.0`)

描述一個 `.pptx` 模板中可用的 layout 與 slot, 以及報告各段落的順序。人工維護, 與模板檔一起版本化。

```text
TemplateManifest
├── schema_version: "1.0"
├── template               { id, version (semver), file, sha256? }
├── layouts[]
│   ├── id                 例 cover_v1, achievement_detail_v1
│   ├── slide_layout_name  模板中 slide layout 的名稱
│   ├── purpose            cover | summary | achievements | work_item_detail | narrative | closing
│   └── slots[]
│       ├── id             例 title, summary, facts
│       ├── placeholder    { idx?, name? } (至少一個)
│       ├── kind           text | bullets | table
│       ├── required?      預設 false
│       ├── max_chars?     | max_items?
│       └── overflow?      split | truncate (預設 truncate)
└── sequence[]             { section, layout_id }
    section ∈ cover | summary | achievements | work_item_details | challenges | improvements | next_plan | other_work | closing
```

Layout Resolver 規則 (M0 實作): 依 `sequence` 產生 slides; `work_item_details` 對每個 `review_status != rejected` 的 work item 依 `rank` 產生一頁; 內容超過 `max_items` 且 `overflow: split` 時複製同 layout 拆頁並設定 `continuation`; `truncate` 時截斷並設 `truncated: true`。同樣輸入必定得到同樣輸出。

## 6. Evidence Digest (agent 輸入, 非正式 IR)

`worklens evidence digest` 的輸出 (`digest.json`), 版本化 (`digest_version`) 但不保證相容, 也不提供 JSON Schema (M2 可能補上)。內容:

- 來源 Evidence IR 的 `document_id` 與 `sha256` — agent 原樣填入 Report IR 的 `evidence_document`, 不需讀 `evidence.json`。
- 期間、本人身分 (只有 display name)、來源 repo 清單。
- 每筆證據: `id`、`kind`、`repository`、`title`、`body` (redaction 後, 截斷至設定長度)、`state`、`occurred_at`、`stats`、`paths` (可設定只保留目錄層級)、`relations`。
- **不含**: email、diff 內容 (除非 `--include-diff`, OQ-06)、token、URL 中的查詢參數。
- `truncation` metadata: 被截斷或省略的項目數量與原因。
- 給 agent 的輸出要求: Report IR schema `$id` 與 `worklens schema show report-ir` 的提示。

## 7. 模組契約 (C#)

所有非同步方法接受 `CancellationToken`, 不吞例外; 可預期的失敗 (驗證、來源錯誤) 以結果物件回傳, 程式錯誤才丟例外。

```csharp
namespace WorkLens.Core.Contracts;

public interface ISourceConnector
{
    SourcePlatform Platform { get; }
    Task<SourceCollectResult> CollectAsync(SourceQuery query, CancellationToken cancellationToken);
}

public interface IEvidenceDigester
{
    EvidenceDigest CreateDigest(EvidenceDocument evidence, DigestOptions options);
}

// v0.1 由 agent 擔任, WorkLens 內無實作 (ADR-0004)。
public interface IAnalysisModule
{
    string Name { get; }
    Task<AnalysisResult> AnalyzeAsync(EvidenceDigest digest, CancellationToken cancellationToken);
}

public interface IReportValidator
{
    // 對原始 JSON 驗證 (先 schema), 再以 evidence 與其 SHA-256 檢查參照與規則。
    ValidationResult Validate(JsonElement report, EvidenceDocument? evidence, string? evidenceSha256);
}

public interface ILayoutResolver
{
    LayoutResult Resolve(ReportDocument report, EvidenceDocument evidence, TemplateManifest manifest);
}

public interface IOutputRenderer
{
    string Format { get; }   // "pptx"
    Task<RenderResult> RenderAsync(PresentationDocument presentation, TemplateManifest manifest,
        string templatePath, string outputPath, CancellationToken cancellationToken);
}
```

錯誤模型 (`WorkLens.Core.Errors`):

```csharp
public enum IssueSeverity { Error, Warning }

public sealed record ValidationIssue(
    string Code,            // schema.required, ref.evidence_missing, ...
    IssueSeverity Severity,
    string InstancePath,    // JSON Pointer, 例 /work_items/0/evidence_refs/1
    string? SchemaPath,     // 失敗節點的 evaluation path (JsonSchema.Net 格式, 含 $ref 段落), 供除錯, 不保證穩定
    string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

public sealed record WorkLensError(string Code, string Message, IReadOnlyDictionary<string, string>? Details = null);
```

`SourceCollectResult`、`AnalysisResult`、`LayoutResult`、`RenderResult` 皆為「成功值 或 `WorkLensError` 清單」的形式, 於實作的 Milestone 定義細節。

### 7.1 版本契約

- 每個 IR root record 公開 `public const string CurrentSchemaVersion = "1.0"` 與對應 `$id`。
- 讀取時先讀 `schema_version`: 同 major 可讀; 不同 major 回 `schema.unsupported_version`, 不嘗試猜測 (migration 見 [schema-versioning.md](schema-versioning.md))。
- Validator 依文件的 `schema_version` 選擇 schema, 不是依程式的最新版本。

### 7.2 DI

- 只有入口 project (Cli) 組裝 DI (`Microsoft.Extensions.DependencyInjection`, M1 起需要時引入)。
- Connectors 以 `ISourceConnector` 多重註冊, 依 `Platform` 選擇; Renderer 依 `Format` 選擇。
- Core 型別不依賴 DI 容器; `WorkLensSchemaRegistry` 為 process 內單例 (lazy 載入 embedded schemas)。
