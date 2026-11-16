# CodeGraph 開發設定

CodeGraph 是選用的開發工具。
WorkLens 建置與報告渲染都不需要它。
每位開發者設定一次全域代理連線，每份 clone 分別初始化索引。
下列命令已依 CodeGraph 1.6.2 檢查。

## 設定代理與 clone

1. 依[官方安裝說明](https://github.com/colbymchenry/codegraph/blob/v1.6.2/README.md#quick-start)安裝 CLI。
2. 使用下列命令設定全域代理連線。
3. 重新啟動代理，載入 MCP server。
4. 在 clone 的根目錄初始化索引。

```powershell
codegraph install --target claude,codex --location global
```

```powershell
cd <clone-directory>
codegraph init
```

以下命令只顯示 MCP 設定，不寫入檔案：

```powershell
codegraph install --print-config claude
codegraph install --print-config codex
```

官方支援全域與專案內設定。
它沒有要求本專案追蹤代理設定。
全域連線與每份 clone 的初始化分工見[官方說明](https://github.com/colbymchenry/codegraph/blob/v1.6.2/README.md#1-run-the-installer)。

## 追蹤範圍

共用規則放在 root `AGENTS.md`。
機器專用的 MCP 設定、權限與 hooks 保留在本機。
Repo 忽略 `.codegraph/`、`.mcp.json`、`.codex/` 與 `.claude/settings*.json`。
這項忽略也涵蓋產生的索引與 `.codegraph/.gitignore`。
每份 clone 重新建立索引。
只有具體的索引規則需要共用時，才新增 `codegraph.json`。

CodeGraph 1.6.2 的官方安裝程式只為 Claude Code 安裝 prompt hook。
既有的 Codex prompt hook 屬於本機整合。
更新共用文件或技能時，保留現有 hooks。
官方 hook 行為見[安裝程式原始碼](https://github.com/colbymchenry/codegraph/blob/v1.6.2/src/installer/index.ts)。
