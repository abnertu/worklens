# WorkLens v0.1 — Architecture Proposal

> Status: Proposed (v0.1 規劃)
> Date: 2026-11-11
> 來源: [交接文件](../handoff/worklens-initial-discussion.md)、[ADR](../adr/)、[Open Questions](../open-questions.md)

本文件描述 WorkLens v0.1 的目標架構、Project 劃分與相依規則。標記規則:

- **Decided** — 使用者已確認, 變更需重新決策。
- **Proposed** — 規劃建議, 實作子計畫可在不違反 Decided 項目的前提下調整, 調整時更新對應 ADR。
- **Open** — 尚待確認, 見 [open-questions.md](../open-questions.md)。在決定前以「預設假設」推進, 不視為已解決。

## 1. 定位

WorkLens 從工作相關資料來源 (v0.1: GitHub / GitLab) 提取**可追溯的證據**, 由 **agent CLI** (Claude Code / Codex CLI) 歸納成工作成果, 再以**確定性規則**產生可人工補充的報告 (v0.1: 可編輯 PPTX 月報)。

v0.1 的關鍵架構決策 (Decided, [ADR-0004](../adr/0004-agent-as-analyzer.md)):
**WorkLens 本身不呼叫 LLM。** 它是 agent 使用的 tool:

| 角色 | 負責 | 不負責 |
|---|---|---|
| WorkLens (確定性) | 蒐集與正規化證據、產生精簡去敏的 agent 輸入 (digest)、以 Schema 與參照完整性驗證 agent 產出、Layout 解析、PPTX 渲染、provenance 記錄 | 摘要、分組、分類、影響推論 |
| Agent CLI (AI) | 讀 digest、分組工作主題、撰寫摘要與分類、依驗證錯誤自我修正 | 投影片幾何、字型、配色; 直接寫 PPTX; 宣稱無證據的成效 |
| 人 | 審核與修正 Report IR、補充非 Git 工作、問題與改善、下月規劃 | — |

「限制 agent 輸出」的手段: agent 只能產出 Report IR (JSON), 而 Report IR 必須通過 `worklens report validate`
(JSON Schema + 參照完整性 + domain 規則)。驗證錯誤是結構化的 (`code` / `instance_path` / `message`), 讓 agent 能自行修正後重試。

## 2. 決策登錄

| 項目 | 選擇 | 狀態 | 依據 |
|---|---|---|---|
| 專案名稱 | WorkLens | Decided | 交接文件 §1 |
| 語言 / 平台 | C# / .NET 10 (LTS) | Decided (語言) / Proposed (平台版本) | [ADR-0001](../adr/0001-csharp-dotnet-10.md) |
| 系統架構 | Modular Monolith + Pipeline | Proposed | [ADR-0002](../adr/0002-modular-monolith.md) |
| 資料契約 | 三層 JSON IR + Versioned JSON Schema (2020-12) | Decided (方向) / Proposed (細節) | [ADR-0003](../adr/0003-three-layer-ir-versioned-schema.md) |
| AI 分析 | 由 agent CLI 執行, WorkLens 不內建 LLM SDK | Decided | [ADR-0004](../adr/0004-agent-as-analyzer.md) |
| Agent 介面 | CLI 子命令 + JSON 檔 I/O + Skill; MCP 延後 | Decided | [ADR-0005](../adr/0005-agent-interface-cli-json-skill.md) |
| Headless agent runner | 不做 (backlog) | Decided | ADR-0004 |
| PPTX 生成 | Open XML SDK (樣本模板 PoC 通過, [報告](../plans/pptx-poc-report.md)) | Decided | [ADR-0006](../adr/0006-pptx-open-xml-sdk.md) |
| 儲存 | 工作目錄內 JSON 檔 + provenance manifest; SQLite 延後 | Proposed | [ADR-0007](../adr/0007-storage-workspace-json-files.md) |
| Schema 驗證 | JsonSchema.Net 9.x (接受 OSMF EULA) | Decided | [ADR-0008](../adr/0008-libraries-schema-validation-and-http.md) |
| Source connector HTTP | BCL `HttpClient` (每個 source 一個, 注入 connector); 無 `IHttpClientFactory` / DI | Proposed | ADR-0008 |
| CLI 解析 | System.CommandLine 3.x | Proposed | ADR-0008 |
| 企業 PPTX 模板 | 尚無; PoC 用自製樣本模板 | Decided (現況) | [pptx-poc-plan.md](../plans/pptx-poc-plan.md) |
| 文件語言 | 繁中敘述 + 英文識別字 | Decided | — |
| 目標 OS / 執行環境 | 預設 Windows 本機 + agent CLI; 程式碼保持跨平台 | Open (OQ-01) | — |

其餘待決事項見 [open-questions.md](../open-questions.md)。

## 3. Pipeline

```text
                  ┌──────────────────────── Agent CLI (Claude Code / Codex) ───────────────────────┐
                  │  讀 Skill 指引 → 呼叫 worklens 子命令 → 撰寫 report.json → 修正驗證錯誤       │
                  └───────┬─────────────────────┬───────────────────────┬──────────────────────────┘
                          │                     │                       │
                          v                     v                       v
 GitHub/GitLab ──> [collect] ──> evidence.json ──> [evidence digest] ──> digest.json ──(agent)──> report.json
                     確定性         Evidence IR        確定性, 去敏/精簡                         Report IR
                                                                                                  │
                                                                     [report validate] <──────────┘
                                                                       Schema + 參照 + domain 規則
                                                                                  │ (pass; 人工審核/修改)
                                                                                  v
                                       template-manifest.json ──> [present] ──> presentation.json
                                                                   Layout Resolver   Presentation IR
                                                                                  │
                                                     template.pptx ──> [render] ──> report.pptx
```

完整互動 (含失敗回饋迴圈) 見 [pipeline-sequence.md](../design/pipeline-sequence.md)。
各階段的輸入、輸出都是檔案, 可單獨重跑; 已審核的 `report.json` 是凍結點 — 重新渲染不需要重新呼叫 agent (交接文件 §4.3)。

### 3.1 為何用 digest 而不是直接給 agent evidence.json

- **資料最小化**: digest 只含 agent 需要的欄位 (commit message、PR/MR 標題與描述、檔案路徑統計), 預設不含 diff 內容, 並經 redaction ([security-privacy.md](../security/security-privacy.md))。
- **控制輸入大小**: 依 token 預算分批或截斷, 截斷是確定性的並記錄在 digest metadata。
- **穩定參照**: digest 中每筆證據保留 Evidence ID, agent 在 Report IR 中只能以這些 ID 引用證據, 驗證時檢查存在性。

## 4. Solution / Project 劃分

交接文件 §7 列出 8 個邏輯模組。v0.1 不為每個模組建立 `.csproj` (避免空專案與過早的邊界), 改以**命名空間 + 相依規則**維持邊界, 在實際需要隔離相依時才拆專案 (Proposed, [ADR-0002](../adr/0002-modular-monolith.md))。

### 4.1 已建立 (v0.1 骨架 + M0 + M1)

| Project | 內容 | 允許的相依 |
|---|---|---|
| `src/WorkLens.Core` | 三層 IR records、Template Manifest model、模組契約 interfaces、錯誤模型、`WorkLensJson` 序列化設定、`WorkLensSchemaRegistry` / `IrSchemaValidator`、embedded schemas | BCL、`JsonSchema.Net` (與其遞移相依) |
| `src/WorkLens.Rendering.Pptx` | `PptxRenderer : IOutputRenderer` (Open XML; M0) | Core、`DocumentFormat.OpenXml` |
| `src/WorkLens.Cli` | `worklens` 執行檔、命令解析、輸出格式、exit codes | Core、Rendering.Pptx、Connectors.Git (M1)、`System.CommandLine` |
| `tests/WorkLens.Core.Tests` | Schema / 範例 / 模型一致性、Core 相依規則、Validator / Layout Resolver | Core、xUnit v3 |
| `tests/WorkLens.Rendering.Pptx.Tests` | Renderer 結構斷言、`OpenXmlValidator`、正規化 snapshot (不需 Office) | Core、Rendering.Pptx、`DocumentFormat.OpenXml`、xUnit v3 |
| `src/WorkLens.Connectors.Git` (M1) | GitHub connector (M1a)、GitLab connector (M1b)、HTTP 分頁 / retry / rate limit | Core、BCL (無 NuGet 直接相依) |
| `tests/WorkLens.Connectors.Git.Tests` (M1) | HTTP client 與 connector 的 fixture 測試、Connectors 相依規則 | Core、Connectors.Git、xUnit v3 |
| `tests/WorkLens.Cli.Tests` (M1) | 以 `CliHost` in-process 執行命令 (collect、token 不外洩、M0 命令 smoke) | Core、Connectors.Git、Cli、xUnit v3 |

### 4.2 之後依 Milestone 建立 (預計)

| Project | Milestone | 內容 | 允許的相依 |
|---|---|---|---|
| `src/WorkLens.Application` | 視需要 (M3) | 若 CLI 以外出現第二個入口 (MCP / API), 把 pipeline 編排從 Cli 移出 | Core |

Layout Resolver (Report IR → Presentation IR) 是純函式, 放在 Core (`WorkLens.Core.Layout` 命名空間), 不依賴 Open XML。

### 4.3 相依規則

1. **Core 不相依任何其他 WorkLens project。**
2. **Core 不做檔案或網路 I/O (讀取自身 embedded resource 除外), 也不相依 vendor / I/O 套件。** 禁止清單 (以 `CoreDependencyRulesTests` 檢查 `obj/project.assets.json`, 含遞移相依):
   `DocumentFormat.OpenXml*`、`Octokit*`、`Microsoft.Extensions.AI*`、`OpenAI*`、`Anthropic*`、`System.CommandLine*`、`Microsoft.Extensions.Http*`。
   Core 的直接套件相依只允許 `JsonSchema.Net`。
3. Connectors 與 Rendering 之間不得互相相依; 只透過 Core 的 IR 溝通。
4. 只有入口 project (`WorkLens.Cli`, 未來的 MCP / API) 可以組裝元件與讀取環境變數 / 設定檔 (v0.1 不使用 DI 容器, ADR-0008)。
5. 測試 project 可以相依任何 project, 但不得被其他 project 相依 (也不互相相依; 共用的測試輔助檔以 `<Compile Include=... Link=...>` 編入)。
6. **`WorkLens.Connectors.Git` 只相依 Core 與 BCL**: 不相依 Rendering、`System.CommandLine*`、`DocumentFormat.OpenXml*`, 也沒有 NuGet 直接相依 (以 `ConnectorsDependencyRulesTests` 檢查)。

## 5. 模組契約 (摘要)

詳細定義見 [domain-model.md](../design/domain-model.md)。

| 契約 | 輸入 → 輸出 | v0.1 實作 |
|---|---|---|
| `ISourceConnector` | `SourceQuery` → `SourceCollectResult` (items + errors + warnings) | M1a: GitHub; M1b: GitLab |
| `IEvidenceDigester` | `EvidenceDocument` + `DigestOptions` → `EvidenceDigest` | M2 |
| `IAnalysisModule` | `EvidenceDigest` → `ReportDocument` | v0.1 由 **agent** 擔任, WorkLens 內無實作; 契約保留給未來內建 LLM / headless runner |
| `IReportValidator` | `Validate(string reportJson, EvidenceDocument? evidence, string? evidenceSha256)` → `ValidationResult` | M0: `ReportValidator` (JSON 解析、schema、參照完整性、domain 規則) |
| `ILayoutResolver` | `Resolve(ReportDocument, string reportSha256, EvidenceDocument, TemplateManifest)` → `LayoutResult` (`PresentationDocument?` + errors + warnings) | M0: `LayoutResolver` |
| `IOutputRenderer` | `RenderAsync(PresentationDocument, TemplateManifest, string templatePath, string outputPath, BrandElements?, CancellationToken)` → `RenderResult` (輸出路徑 + errors + warnings) | M0: `PptxRenderer` |

## 6. 工作目錄 (Workspace)

每次報告一個目錄 (預設 `.worklens/reports/<period>/`, 可由 `--workspace` 指定), 檔案皆為 JSON 或輸出檔 ([ADR-0007](../adr/0007-storage-workspace-json-files.md)):

| 檔案 | 產生者 | 說明 |
|---|---|---|
| `evidence.json` | `collect` | Evidence IR |
| `digest.json` | `evidence digest` | agent 輸入 (非正式 IR, 版本化但不承諾相容) |
| `report.json` | agent → 人工 | Report IR; 審核後凍結 |
| `presentation.json` | `present` | Presentation IR |
| `report.pptx` | `render` | 輸出 |
| `manifest.json` | 每個命令 | provenance: 各檔 SHA-256、schema 版本、template 版本、WorkLens 版本、agent 標示 (由 agent 自報) |

## 7. 擴充點與非目標

擴充點: 新 Source Connector (實作 `ISourceConnector`, 輸出 Evidence IR)、新輸出 (實作 `IOutputRenderer` + 對應 Presentation 或其他 output IR)、新入口 (MCP / API, 屆時抽出 Application 層)、內建 analyzer (實作 `IAnalysisModule`)。

v0.1 非目標 (交接文件 §5.5 + 本次決策): 績效評分、以 commit 數等同貢獻、無數據的成效宣稱、自由排版生成器、Web UI / Dashboard / Marketplace、WorkLens 主動呼叫 LLM 或 headless agent、MCP server。
