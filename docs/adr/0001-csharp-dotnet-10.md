# ADR-0001: 以 C# / .NET 10 開發

## Status

Accepted (語言: 使用者確認; .NET 10 版本: Proposed) — 2026-11-11

## Context

- 交接文件 §2 已確認主要語言為 C#, 且不需為 AI 功能引入 Python。
- v0.1 的 AI 分析由 agent CLI 執行 ([ADR-0004](0004-agent-as-analyzer.md)), WorkLens 本身只做確定性處理: HTTP API、JSON、Schema 驗證、Open XML。這些在 .NET 生態都有成熟的函式庫。
- 開發機已安裝 .NET SDK 10.0.1xx 與 10.0.4xx。.NET 10 是 LTS (支援至 2028-11)。

## Decision

- 以 C# (latest LangVersion) 與 .NET 10 (`net10.0`) 開發。
- `global.json` 固定 SDK `10.0.400` 並 `rollForward: latestFeature`, test runner 設為 Microsoft.Testing.Platform。
- 全 repo 啟用 Nullable reference types、`TreatWarningsAsErrors`、Central Package Management (`Directory.Packages.props`)。
- repo 層級 `nuget.config` 只保留 nuget.org (`<clear/>`), 避免開發機的其他套件來源造成 NU1507 與不可重現的 restore。

## Alternatives

| 方案 | 優點 | 缺點 | 結論 |
|---|---|---|---|
| .NET 8 LTS | 支援至 2026-11, 生態最穩定 | 已接近 EOL; 新專案無理由選 | 不採用 |
| .NET 10 (選擇) | LTS 至 2028-11; `.slnx`、`dotnet test` + MTP 原生支援 | 部分函式庫仍在跟進 | 採用 |
| Python | python-pptx、LLM SDK 豐富 | 與使用者已確認的 C# 方向衝突 | 不採用 |
| TypeScript / Node | agent 生態常用 | 同上; Open XML 函式庫較弱 | 不採用 |

## Consequences

- 部署目標需能安裝 .NET 10 runtime, 或之後以 self-contained / dotnet tool 發佈 (M4 決定, 見 OQ-01)。
- 若組織政策要求其他版本 (交接文件 §2 註記「依部署環境與團隊政策確認」), 只需調整 `Directory.Build.props` 的 `TargetFramework` 與 `global.json`。
- `TreatWarningsAsErrors` 會把套件 audit 警告 (例如 NU1900, 離線 restore) 升為錯誤; 必要時以 `WarningsNotAsErrors` 個別放行, 並在此 ADR 記錄。
