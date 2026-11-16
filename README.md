# WorkLens

WorkLens 從 GitHub 與 GitLab 蒐集可追溯的證據，並將驗證過的報告渲染成可編輯的 PPTX。

[![.NET SDK](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Fabnertu%2Fworklens%2Fmain%2Fglobal.json&query=%24.sdk.version&label=.NET%20SDK&logo=dotnet&color=512BD4&logoColor=white)](https://dot.net)

## 安裝

安裝 `global.json` 指定的 .NET 10 SDK。
渲染不需要 PowerPoint。
只有呼叫端使用 Archify 產生圖表時，才需要 Node.js 與瀏覽器。

```powershell
git clone https://github.com/abnertu/worklens.git
cd worklens
dotnet build WorkLens.slnx -c Release
```

## 使用

先執行不需要來源憑證的範例報告。
下列 PowerShell 命令使用內附月報模板與示範品牌。
Fixture 刻意包含三項驗證警告。
輸出檔為 `.worklens/demo/report.pptx`。

```powershell
New-Item -ItemType Directory -Force .worklens/demo | Out-Null
Copy-Item tests/Fixtures/rd-monthly/full/report.json, tests/Fixtures/rd-monthly/full/evidence.json .worklens/demo/
dotnet run --project src/WorkLens.Cli -c Release -- report validate --workspace .worklens/demo
dotnet run --project src/WorkLens.Cli -c Release -- present --workspace .worklens/demo --manifest templates/rd-monthly/manifest.json
dotnet run --project src/WorkLens.Cli -c Release -- render --workspace .worklens/demo --manifest templates/rd-monthly/manifest.json --config templates/rd-monthly/demo/worklens.json
```

產生真實報告時，先設定身分與來源，再以環境變數提供 Token。
單月使用 `collect --period`，包含兩端的月份區間使用 `--from` 與 `--to`。
呼叫端先撰寫並審核 `report.json`，再執行驗證、排版與渲染。
設定與命令見 [CLI 使用規格](docs/design/cli-ux.md)。

## 月報流程

此圖呈現目前可用命令，以及呼叫端的審核步驟。
WorkLens 不呼叫 LLM，也不產生圖表內容。
呼叫端可在 Codex 或 Claude Code 使用已安裝的 Archify 技能。
已宣告的圖片預設納入報告。
在 `present` 或 `render` 加上 `--no-archify`，即可略過 Archify 圖片頁。

![WorkLens 月報流程](docs/diagrams/workflow/workflow.svg)

## 執行架構

CLI 負責設定、環境變數與檔案存取。
Core 負責 IR、驗證與確定性排版。
Connectors 蒐集來源證據，Renderer 以 Open XML 寫出 PPTX。
呼叫端負責報告內容、圖表產生與人工審核。
從版本化的圖表來源產生獨立 HTML 後，可互動檢視。

![WorkLens 執行架構](docs/diagrams/architecture/architecture.svg)

[Archify 整合指南與重建指令](docs/integrations/archify.md#準備與渲染)

## 狀態與文件

核心流程、來源蒐集與 Archify 圖片附件已可使用。
GitHub 與 self-managed GitLab 蒐集均已通過真實驗收。
M2 digest、M3 端到端編排與 M4 發佈強化留待後續。
目前沒有已發布版本或 CI workflow。
最近一次完整本機檢查通過 Release 建置與 888 項測試。

- [架構與相依規則](docs/architecture/overview.md)
- [資料模型與模組契約](docs/design/domain-model.md)
- [安全與隱私](docs/security/security-privacy.md)
- [後續里程碑](docs/plans/implementation-backlog.md)
- [架構決策](docs/adr/)
