# WorkLens

> Understand Work. Capture Impact. (Proposed)

WorkLens 是給 agent CLI (Claude Code / Codex CLI) 使用的工作成果報告工具:
蒐集 GitHub/GitLab 等來源的可追溯證據 (Evidence IR), 讓 agent 依 Schema 撰寫 Report IR,
由 WorkLens 驗證後以確定性規則轉成 Presentation IR 並渲染為可編輯的 PPTX。

狀態: v0.1 規劃階段。

- 交接文件: [docs/handoff/worklens-initial-discussion.md](docs/handoff/worklens-initial-discussion.md)
- 架構總覽: [docs/architecture/overview.md](docs/architecture/overview.md)
- ADR: [docs/adr/](docs/adr/)
- Schemas: [schemas/](schemas/)

## Build

```bash
dotnet build WorkLens.slnx -c Release -warnaserror
dotnet test --solution WorkLens.slnx -c Release
```
