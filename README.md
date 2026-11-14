# WorkLens

WorkLens 從 GitHub 與 GitLab 蒐集可追溯的工作證據, 並把驗證過的工作報告渲染成可編輯的 PPTX。

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dot.net)

WorkLens 負責月報中確定性的步驟:

1. `collect` 把你一個月內的 commits 與 pull requests 寫入 `evidence.json`。
2. 你或 AI agent 依據這些證據撰寫 `report.json`。WorkLens 不呼叫任何 LLM。
3. `report validate` 依 schema 與證據檢查 `report.json`。
4. `present` 以模板 manifest 排版報告, `render` 寫出 `.pptx` 檔。

狀態: v0.1 開發中。GitHub 的 `collect`、`report validate`、`present` 與 `render` 已可使用。
GitHub 真實 repo 驗收尚未完成。GitLab 的 `collect` 與 `evidence digest` 尚未提供。

## 安裝

需求: [.NET 10 SDK](https://dot.net)。渲染不需要 PowerPoint。

```bash
git clone https://github.com/abnertu/worklens.git
cd worklens
dotnet build WorkLens.slnx -c Release
```

## 使用

以範例模板渲染範例報告:

```bash
mkdir demo
cp schemas/evidence-ir/1.0/examples/valid/monthly-github-gitlab.json demo/evidence.json
cp schemas/report-ir/1.0/examples/valid/monthly-report.json demo/report.json
dotnet run --project src/WorkLens.Cli -c Release -- report validate --workspace demo
dotnet run --project src/WorkLens.Cli -c Release -- present --workspace demo --manifest templates/sample-monthly/manifest.json
dotnet run --project src/WorkLens.Cli -c Release -- render --workspace demo --manifest templates/sample-monthly/manifest.json
```

```text
/path/to/worklens/demo/report.json: 0 error(s), 0 warning(s)
Wrote /path/to/worklens/demo/presentation.json (8 slides).
Wrote /path/to/worklens/demo/report.pptx (8 slides).
```

在命令加上 `--json`, stdout 會輸出單一 JSON 物件, 供 agent 解析。Exit code 表示結果的類別。

## 文件

- 命令、選項、輸出與 exit code: [docs/design/cli-ux.md](docs/design/cli-ux.md)
- 架構: [docs/architecture/overview.md](docs/architecture/overview.md)
- 決策紀錄: [docs/adr/](docs/adr/)
- JSON Schemas: [schemas/](schemas/)
