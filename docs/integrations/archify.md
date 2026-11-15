# 在月報中使用 Archify 圖片

Codex 與 Claude Code 都能使用專案內的 Archify 3.2.0。
安裝位置分別是 `.agents/skills/archify` 與 `.claude/skills/archify`。
WorkLens 預設納入已宣告的圖片，不會自行產生流程圖。
WorkLens 執行時不需要 Node.js、瀏覽器或 LLM SDK。

## 準備與渲染

1. 依允許使用的證據撰寫並審核 `report.json`。保留人工內容。
2. 使用已安裝的 Archify 技能撰寫圖表。將 candidate、檢查過的 HTML 與 receipts 保留在報告工作目錄。
3. 執行 Archify `finalize`。程式碼圖表須固定已提交的來源證據，並傳入 `--repo-root`。
4. 開啟檢查過的 HTML。使用「匯出 → PNG」取得完整圖表，將 PNG 放在報告旁。
5. 計算 PNG 的小寫 SHA-256。將參照加入所屬工作項目的 `images` 陣列。
6. 依序執行 `report validate`、`present` 與 `render`，並指定月報 manifest。

以 workflow 為例，在專案根目錄執行：

```powershell
node .agents/skills/archify/bin/archify.mjs finalize workflow .worklens/reports/2026-11/diagrams/candidate.json .worklens/reports/2026-11/diagrams/flow.html --repo-root . --quality showcase --json
(Get-FileHash .worklens/reports/2026-11/diagrams/flow.png -Algorithm SHA256).Hash.ToLowerInvariant()
```

`candidate.json` 的 `meta.output` 須與 HTML 輸出路徑相同。
非程式碼圖表可省略 `--repo-root`。
PNG 匯出包含完整圖表，瀏覽器視窗截圖不能代替它。

在 `work_items[]` 的所屬項目加入：

```json
"images": [
  {
    "id": "img-delivery-flow",
    "title": "交付流程",
    "file": "diagrams/flow.png",
    "sha256": "<圖片原始位元組的 64 字元小寫 SHA-256>",
    "producer": "archify"
  }
]
```

圖片繼承所屬工作的來源、審核狀態與證據參照。
WorkLens 不檢查圖片像素中的主張或 secret。
它檢查 PNG/JPEG header 與原始位元組的 hash，並保留中繼資料。

```powershell
dotnet run --project src/WorkLens.Cli -c Release -- report validate --workspace .worklens/reports/2026-11 --json
dotnet run --project src/WorkLens.Cli -c Release -- present --workspace .worklens/reports/2026-11 --manifest templates/rd-monthly/manifest.json --json
dotnet run --project src/WorkLens.Cli -c Release -- render --workspace .worklens/reports/2026-11 --manifest templates/rd-monthly/manifest.json --json
```

## 略過 Archify

在 `present` 或 `render` 加上 `--no-archify`。
呼叫端收到禁用要求時，也應略過圖表撰寫與匯出。
兩個開關都保留文字頁、一般圖片與品牌。
停用的圖片即使不存在也不阻擋渲染，schema 與 manifest 檢查仍會執行。
一般圖片省略 `producer`，Archify 圖片標示 `producer: archify`。

```powershell
dotnet run --project src/WorkLens.Cli -c Release -- present --workspace .worklens/reports/2026-11 --manifest templates/rd-monthly/manifest.json --no-archify
dotnet run --project src/WorkLens.Cli -c Release -- render --workspace .worklens/reports/2026-11 --manifest templates/rd-monthly/manifest.json --no-archify
```

## 路徑與相容性

圖片路徑相對於持有參照的 JSON 檔。
輸出換目錄時會重算相對路徑，允許 `..` 片段。
使用本機相對路徑，不接受絕對路徑、drive-relative、URI、UNC、device 或 alternate data stream。
分享工作目錄時，將參照的圖片與 JSON 檔一起保留。
沒有圖片宣告時，不新增頁面或執行需求。
月報模板 1.1.0 新增獨立的圖片 layout。
釘選舊模板版本的 presentation 須重新執行 `present`。
圖片依附件順序接在所屬工作的所有文字續頁之後。
遭拒絕的工作不產生圖片頁。
PowerPoint 將每張流程圖視為單一圖片，不是可編輯的節點與箭頭。

## 驗證紀錄

2026-11-15 的整合驗收使用通過 Archify `finalize` 的 workflow 完整 PNG 匯出。
報告驗證、版面配置與渲染都通過。
PowerPoint 成功開啟五頁輸出、匯出每頁，並重新開啟編輯後的副本。
目視檢查未發現圖片頁裁切、拉伸、重疊或頁碼錯誤。
Open XML 與以模板為基準的檔案驗證都通過。
這些產物保留在被 Git 忽略的 `.worklens/reports/archify-integration-check/`。
