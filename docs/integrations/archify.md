# 在月報中使用 Archify 圖片

專案只保留一份完整的 Archify 3.2.0，位置是 `.agents/skills/archify/`。
授權檔與第三方聲明隨 runtime 保留。
`skills-lock.json` 記錄來源與內容 hash。
Claude 的 `.claude/skills/archify/SKILL.md` 只負責導向共用技能。
技能內的相對參照以 `.agents/skills/archify/` 為基準。
下列命令一律在專案根目錄執行。
WorkLens 預設納入已宣告的圖片。流程圖由呼叫端產生。
WorkLens 執行時不需要 Node.js、瀏覽器或 LLM SDK。

## Clone 與追蹤範圍

Clone 已包含 runtime，不必另外安裝 Archify。
安裝程式的移動來源不代表固定版本。
升級時須一併審核 runtime 與 lockfile 的變更。
WorkLens 不會自動更新 Archify。

共用圖表的 candidate 與 README 使用的 SVG、PNG 納入追蹤。
私人報告 JSON、個人品牌、產生的 HTML 與驗證收據放在被忽略的 `.worklens/` 目錄。
代理設定與 CodeGraph 初始化方式見 [CodeGraph 指南](codegraph.md)。

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
Candidate metadata 與報告圖片參照使用簡短的主題標題。
保留有助理解的架構或流程用語。
將流程圖嵌入簡報時，只顯示一個標題。

1. 保留投影片標題。
2. 在匯出工作階段，只清空 HTML 的 `h1`。
3. 選擇「匯出 → PNG」。
4. 保留原始 HTML 與 SVG 的無障礙標題。
5. 記錄匯出處理與 PNG hash。
6. 不要以裁切點陣圖片的方式移除標題。

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

專案圖表使用內附的 Archify 3.2.0 渲染器。
`docs/diagrams/*/candidate.json` 保存圖表結構與固定的來源版本。
Repo 保留供 README 與報告使用的 SVG、PNG。
HTML 與驗證收據放在被忽略的 `.worklens/diagrams/`。
從 candidate 與固定渲染器版本重建 HTML，不需要 LLM。

1. 準備 Node.js 與 Chromium 瀏覽器，例如 Microsoft Edge。
2. 在專案根目錄執行下列命令。
3. 開啟產生的 HTML，進行互動檢視。

```powershell
$review = Get-Date -Format 'yyyyMMdd-HHmmssfff'
New-Item -ItemType Directory -Force .worklens/diagrams/workflow,.worklens/diagrams/architecture | Out-Null
node .agents/skills/archify/bin/archify.mjs finalize workflow docs/diagrams/workflow/candidate.json .worklens/diagrams/workflow/workflow.html --repo-root . --quality showcase --out-dir ".worklens/diagrams/workflow/review-$review" --json
if ($LASTEXITCODE -ne 0) { throw 'Workflow diagram verification failed.' }
node .agents/skills/archify/bin/archify.mjs finalize architecture docs/diagrams/architecture/candidate.json .worklens/diagrams/architecture/architecture.html --repo-root . --quality showcase --out-dir ".worklens/diagrams/architecture/review-$review" --json
if ($LASTEXITCODE -ne 0) { throw 'Architecture diagram verification failed.' }
```

每次執行都將收據寫入新的 review 目錄。
以檢視器的「匯出」控制項更新 SVG 與 PNG。
準備簡報圖片時，依照前述步驟匯出無標題 PNG。

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
