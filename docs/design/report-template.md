# 月報模板設計

> Status: Proposed — 2026-11-13

本文件描述研發工作月報模板的目標設計。
WorkLens 尚未實作這個設計。
現行行為以 domain-model.md §5 為準。
實作落差由後續計畫分析。

## 1. 讀者與內容

報告的對象是一位同時開發軟體與維護伺服器的工程師。
讀者是主管與專案經理。
開發工作來自 GitLab 證據。
伺服器工作來自 `category: ops` 的 work item, 多半由人工輸入。

## 2. 單位與字型

| 項目 | 值 |
|---|---|
| 投影片尺寸 | 13.333 in × 7.5 in (960 pt × 540 pt) |
| 視覺稿尺寸 | 1280 px × 720 px |
| 換算 | 1 px = 0.75 pt = 9525 EMU |
| 拉丁字型 | Segoe UI |
| 東亞字型 | Microsoft JhengHei |
| 視覺稿字型 | Inter 與 Noto Sans TC, 作為網頁替代字型 |

視覺稿字型的換行與 PPTX 字型不同。
實作時在 PowerPoint 中校正容量。

## 3. 字級表

| 角色 | px | pt | 字重 | 用途 |
|---|---|---|---|---|
| display | 72 | 54 | bold | 封面月份 |
| metric | 48 | 36 | bold | 數字卡的數字 |
| title | 32 | 24 | bold | 投影片標題 |
| subtitle | 24 | 18 | medium | 封面副標題 |
| heading | 18 | 13.5 | bold | 區塊標題 |
| body | 18 | 13.5 | regular | 項目符號與亮點 |
| table | 16 | 12 | regular | 表格儲存格 |
| meta | 16 | 12 | regular 或 medium | 標題列摘要、說明文字、證據列; 標籤、表頭與眉標用 medium; 編號徽章用 bold |
| tag | 14 | 10.5 | medium | 狀態標籤與標記 |
| footer | 12 | 9 | regular | 頁尾文字與頁碼 |

只使用這些字級。
footer 與 tag 角色至少 9 pt。
其他角色至少 12 pt。

## 4. 色彩 token

| Token | Hex | 用途 |
|---|---|---|
| ink | #111827 | 標題、數字卡數字、表格文字 |
| body | #1F2937 | 項目符號與段落 |
| muted | #4B5563 | meta 文字、說明文字、表頭 |
| subtle | #6B7280 | 只用於頁尾文字 |
| line | #E5E7EB | 分隔線與表格列線 |
| surface | #F6F7F9 | 卡片與交錯列 |
| paper | #FFFFFF | 投影片背景 |
| accent | #1E3A8A | 標題底線、品牌色帶、完成標籤文字 |
| accent-soft | #E8EDF8 | 完成標籤底色 |
| warn | #9A3412 | 進行中標籤文字、需要支援標題 |
| warn-soft | #FDF3E7 | 進行中標籤底色、需要支援區塊 |
| neutral-soft | #F3F4F6 | 待確認標籤底色、標記 |
| on-accent | #FFFFFF | 品牌色帶上的文字 |
| on-accent-muted | #C7D2FE | 品牌色帶上的小字 |

文字與背景的組合:

| 文字 | 背景 | 位置 |
|---|---|---|
| ink | paper | 標題、表格 |
| ink | surface | 數字卡數字、交錯列 |
| body | paper | 項目符號 |
| body | warn-soft | 需要支援的項目 |
| muted | paper | meta 文字、表頭 |
| muted | surface | 卡片說明文字 |
| muted | neutral-soft | 待確認標籤、標記 |
| subtle | paper | 頁尾 |
| accent | accent-soft | 完成標籤 |
| accent | paper | 證據 ID |
| accent | accent-soft | 編號徽章 |
| warn | warn-soft | 進行中標籤、需要支援標題 |
| on-accent-muted | accent | 封面色帶上的年份 |
| on-accent | accent | 封面色帶上的月份 |

文字只使用這些組合。

## 5. 格線

所有內容頁共用這組格線。

| 區域 | px (x, y, w, h) | pt (x, y, w, h) |
|---|---|---|
| 標題 | 64, 48, 1152, 44 | 48, 36, 864, 33 |
| 標題列 meta (靠右, 與標題同列) | 64, 48, 1152, 44 | 48, 36, 864, 33 |
| 內容 | 64, 120, 1152, 520 | 48, 90, 864, 390 |
| 頁尾分隔線 | 64, 656, 1152, 1 | 48, 492, 864, 0.75 |
| 頁尾文字 | 64, 668, 800, 20 | 48, 501, 600, 15 |
| 頁尾 logo | 1088, 668, 72, 20 | 816, 501, 54, 15 |
| 頁碼 | 1168, 668, 48, 20 | 876, 501, 36, 15 |

未設定 logo 時, 頁尾 logo 區域保持空白。

## 6. 元件

- 數字卡: surface 底色的方塊, 內含標籤 (meta)、數字 (metric) 與說明 (meta)。每個部分是各自的文字元素。
- 狀態標籤: 圓角底色加上 tag 文字。完成用 accent 字配 accent-soft 底。進行中用 warn 字配 warn-soft 底。待確認用 muted 字配 neutral-soft 底。
- 表格: 表頭列用 muted 文字, 下方有 accent 底線。內容列在 paper 與 surface 之間交錯。列之間沒有垂直線。
- 證據列: accent 的 ID、body 的標題, 以及含日期與 commit 數的 meta 列。
- 標記: neutral-soft 底色加上 tag 文字, 例如「推論 · 信心中」或「人工補充」。
- 區塊標題: heading 文字, 下方有 2 px 的 accent 底線。
- 編號徽章: 32 px 的 accent-soft 圓形, 內有 accent 的 bold meta 數字。
- Logo 區域: 固定大小的方塊。Logo 縮放到方塊內並保持長寬比。

## 7. 頁序

1. 封面
2. 本月總覽
3. 開發成果
4. 開發項目細節, 每個項目一頁
5. 伺服器維運
6. 維運項目細節, 每個項目一頁
7. 問題與處理
8. 未對應任何問題的改善事項
9. 需要支援
10. 下月規劃
11. 其他工作

沒有內容的頁不產生, 封面與本月總覽除外。
其餘 narrative kind 共用同一個 narrative layout。
報告沒有結尾頁。

## 8. 各頁與資料來源

來源欄寫出現有欄位, 或「新增: <名稱>」。
Label 是 template manifest 中的固定文字。

### 8.1 封面

| 元素 | 區域 px (x, y, w, h) | 角色 | 來源 |
|---|---|---|---|
| 眉標與 accent 短線 | 80, 72, 720, 24 | meta | 新增: label `cover_kicker` |
| 月份標題 | 80, 200, 720, 88 | display | `period.label`, 以新增的 label `period_title` 格式化 |
| 副標題 | 80, 304, 720, 32 | subtitle | 新增: label `cover_subtitle` |
| 報告對象 | 80, 520, 720, 28 | heading | `subject.display_name` |
| 期間與資料來源列 | 80, 556, 720, 24 | meta | `period.start`、`period.end`、`period.timezone`; 新增: 由證據平台與人工項目產生的來源摘要 |
| 單位名稱 | 80, 628, 600, 24 | meta | 新增: config 的單位名稱 |
| Logo | 680, 620, 120, 40 | — | 新增: config 的 logo |
| 品牌色帶 | 896, 0, 384, 720 | — | 模板裝飾 |
| 色帶年份 | 936, 72, 200, 24 | meta | `period.label` |
| 色帶月份 | 936, 584, 200, 88 | display | `period.label` |

未設定 logo 時, logo 區域保持空白。
未設定單位名稱時, 不顯示單位名稱列。

### 8.2 本月總覽

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 標題 | 標題區域 | title | label `summary_title` |
| 期間範圍 | 標題列 meta | meta | `period.start`、`period.end` |
| 數字卡 | 64, 120, 1152, 120; 4 張卡片, 間距 20 px | metric 與 meta | 計算值, 見 9.1; 卡片標籤與說明: 新增 labels |
| 亮點標題 | 64, 280, 688, 32 | heading | 新增: label `highlights_title` |
| 亮點項目 | 64, 324, 688, 240 | body 與 meta | 最前面的完成項目, 見 9.2: `work_items[].title`; 一行 meta 列, 含專案 (9.3) 與證據 (9.4) |
| 需要支援區塊 | 792, 280, 424, 284 | heading 與 body | 新增: support narratives 的前 2 項; 標題: 新增 label |
| 需要支援超出列 | 需要支援區塊內 | meta | 計算的數量; 新增: label `support_more` |
| 活動附註 | 64, 600, 1152, 24 | meta | 計算值, 見 9.1; label `summary_activity`, 加上 commit 數 |
| 亮點空值文字 | 亮點項目區域 | body | 新增: label `highlights_empty` |
| 需要支援空值文字 | 需要支援區塊 | body | 新增: label `support_empty` |

需要支援區塊有項目時, 使用 warn-soft 底色與 warn 標題。
區塊為空時, 使用 surface 底色與 ink 標題。
空值文字使用 muted。

### 8.3 開發成果

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 標題 | 標題區域 | title | label `achievements_title` |
| 狀態摘要 | 標題列 meta | meta | 表中項目的狀態計數; label `summary_status` |
| 表格 | 內容區域 | table | category 不是 `ops` 的 work item, 依 rank 排序 |
| 欄: 工作項目 | 42% | table | `work_items[].title` |
| 欄: 專案 | 18% | table | 計算值, 見 9.3 |
| 欄: 狀態 | 12% | tag | `work_items[].status`; labels `cell_status_*` |
| 欄: 證據 | 28% | meta | 計算值, 見 9.4 |
| 欄名 | 表頭列 | table | `achievements_columns`, 擴充為 4 欄 |
| 續頁標記 | 接在標題後 | title | 新增: label `continuation`, 含 `{index}` 與 `{total}` |

表格每頁最多 6 列。
其餘的列接到下一頁, 標題加上續頁標記。

### 8.4 項目細節

開發項目與維運項目都使用這一頁。

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 眉標 | 64, 48, 1152, 20 | meta | 段落標題與專案, 見 9.3 |
| 標題 | 64, 72, 1000, 44 | title | `work_items[].title` |
| 狀態標籤 | 標題右側 | tag | `work_items[].status` |
| 事實標題 | 64, 148, 640, 32 | heading | 新增: label `facts_title` |
| 事實 | 64, 192, 640, 448 | body | `observed_facts[].statement`; 沒有事實時用 `summary` |
| 推論列 | 接在事實之後 | body 與 tag | `agent_inference`; label `inference` 與 `confidence_*` |
| 人工補充列 | 接在推論之後 | body 與 tag | `human_notes`; label `human_notes` |
| 證據標題 | 752, 148, 464, 32 | heading | 新增: label `evidence_title` |
| 證據列 | 752, 192, 464, 400 | meta 與 body | 引用的 change request: Evidence IR 的 ID、標題、狀態、日期與 commit 數; 狀態與日期用字: 新增 labels |

狀態只顯示在標籤中。
這一頁沒有另外的狀態列。
超過容量的文字會被截斷, 結尾加上「…」。

### 8.5 伺服器維運

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 標題 | 標題區域 | title | 新增: label `operations_title` |
| 狀態摘要 | 標題列 meta | meta | ops 項目的狀態計數 |
| 表格 | 內容區域 | table | `category: ops` 的 work item, 依 rank 排序 |
| 欄: 工作項目 | 52% | table | `work_items[].title`; 標題中寫出主機名稱 |
| 欄: 狀態 | 14% | tag | `work_items[].status` |
| 欄: 證據 | 34% | meta | 計算值, 見 9.4 |
| 欄名 | 表頭列 | table | 新增: label `operations_columns` |

### 8.6 問題與處理

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 標題 | 標題區域 | title | label `challenges_title` |
| 狀態摘要 | 標題列 meta | meta | 已處理與處理中問題的計數; 新增: label |
| 表格 | 內容區域 | table | `challenge` narratives, 依陣列順序 |
| 欄: 問題 | 40% | table | 該 challenge 的 `narratives[].text` |
| 欄: 處理方式 | 46% | table | 每個以新欄位 `resolves` 指向此 challenge 的 `improvement` 的文字, 一項一行; 沒有時顯示「—」 |
| 欄: 狀態 | 14% | tag | 計算值: 至少一個 improvement 指向此 challenge 時為已處理, 否則為處理中; 新增: labels |
| 欄名 | 表頭列 | table | 新增: label `challenges_columns` |

已處理標籤用 accent 字配 accent-soft 底。
處理中標籤用 warn 字配 warn-soft 底。
其餘的列接到下一頁, 標題加上續頁標記。

新欄位: `improvement.resolves` 是選填的 challenge narrative ID 陣列。
有 `resolves` 的 improvement 只出現在這張表中。
沒有 `resolves` 的 improvement 出現在改善事項頁。

### 8.7 Narrative 頁

| 元素 | 區域 px | 角色 | 來源 |
|---|---|---|---|
| 標題 | 標題區域 | title | labels `improvements_title`、`next_plan_title`、`other_work_title`; 新增: label `support_title` |
| 項目 | 內容區域 | body | 單一 kind 的 `narratives[].text`; 改善事項只列沒有 `resolves` 的項目 |

下月規劃的項目顯示編號徽章。
其他 kind 顯示項目符號圓點。
其餘的項目接到下一頁, 標題加上續頁標記。

### 8.8 頁尾

| 元素 | 來源 |
|---|---|
| 頁尾文字 | 新增: config 的單位名稱, 接著是新增的 label `footer` (含 `{period}`), 例如「[單位名稱]｜研發工作月報 · 2026 年 11 月」 |
| 頁尾 logo | 新增: config 的 logo |
| 頁碼 | 模板的投影片編號欄位 |

## 9. 計算值

所有計數都排除 `review_status: rejected` 的 work item。
證據計數只包含 `occurred_at` 在 `[period.start, period.end)` 內且沒有 `duplicate` flag 的項目。

### 9.1 數字卡

| 指標 | 規則 |
|---|---|
| 完成項目 | `status: completed` 且 category 不是 `ops` 的 work item |
| 進行中 | `status: in_progress` 且 category 不是 `ops` 的 work item |
| 維運事項 | `category: ops` 的 work item, 不分狀態 |
| 涉及專案 | 計入證據的不重複 repository |
| 附註 | 計入證據中已合併的 change request、commit 與 repository |

### 9.2 本月亮點

亮點是 `status: completed` 的前 3 個 work item, 不分 category。
依 domain-model.md §3 定義的 rank 規則排序。

### 9.3 專案

專案是引用證據的 repository 名稱。
沒有引用證據的項目顯示「—」。
引用兩個以上 repository 的項目顯示第一個與數量, 例如「backend-api 等 2 個」。

### 9.4 證據

| 情況 | 格式 |
|---|---|
| 只有 change request | 每個 ID 加上平台前綴: GitLab `!128`、GitHub `#128` |
| change request 與 commit | 先列 ID, 再列 commit 數: `!128 · !131 · 14 commits` |
| 只有 commit | commit 數: `11 commits` |
| Issue | ID 前加上「issue」: `issue #42` |
| 沒有引用 | label `human_origin`: `人工補充` |

最多顯示 3 個 ID, 其餘以 `+N` 表示。
範例是 `!137 · !46 · !47 · +1 · 12 commits`。
不顯示 URL、email 或 commit SHA。
「commits」與「issue」等固定用字來自新增的 labels。

## 10. 揭露範圍

投影片會顯示這些識別資訊:

- 報告對象的名稱。
- 單位名稱。
- Repository 名稱。
- Change request 與 issue 的 ID。
- Commit 數。
- Ops 標題中的主機名稱。
- Change request 與 issue 的標題, 經過揭露處理。

在 WorkLens 計算的儲存格中, 投影片不顯示 URL、email 或 commit SHA。
Agent 或使用者撰寫的文字照原樣顯示。

WorkLens 從這些標題移除 URL、SSH remote、email 與 commit SHA, 並先遮蔽 token 樣式 ([ADR-0012](../adr/0012-slide-disclosure.md))。
沒有路徑的主機名稱不移除, 例如 `gitlab.example.com`。
`README.md/x` 這類字詞會被當成 URL 移除。
