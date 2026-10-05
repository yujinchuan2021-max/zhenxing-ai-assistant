# 枕星工具流接收与技能修订审核服务

## 云端工具目录与实时通知（新增源码，未在此轮部署）

`tool_catalog.py` 提供独立的自有工具发布通道，不启用客户端旧上游工具更新通道。数据保存在主 SQLite 所在目录的 `tool-catalog/catalog.sqlite3`，数据库事务同时保留不可变修订和切换当前指针；进程重启后继续提供最后一次成功发布的目录。

| 接口 | 用途 |
| --- | --- |
| `GET /v1/tools/catalog` | 公开完整工具清单，返回强 `ETag` 和 `Cache-Control: no-cache`；匹配 `If-None-Match` 返回 `304`。 |
| `GET /v1/tools/events` | 公开 SSE 通知，连接和重连先发当前修订，发布成功时发送新修订。 |
| `GET /v1/admin/tools/catalog` | 管理员读取当前清单，沿用 Bearer 鉴权。 |
| `POST /v1/admin/tools/publish` | 管理员发布完整清单，鉴权先于正文解析；首发 `201/created:true`，同修订同规范内容重试 `200/created:false`，冲突返回 `409`。 |
| `GET /admin/tools` | 简短发布页面，可读取和粘贴清单；静态外壳不含数据或令牌，令牌只保存在当前页面输入框中。 |

目录根字段全部必填：`schemaVersion:1`、正整数 `revision`、UTC ISO 8601 `publishedAt`、四段数字 `minClientVersion`（例如 `0.1.0.0`）、`tools`。每个工具必填 `id/name/category/categories/description/publisher/version/tags/homepage/packages/legacyPath/order`：

- `id` 是最多 80 字节的小写 ASCII 稳定 slug，字母数字之间可用单个 `-`，同一目录不能重复；`order` 是非负整数。`categories` 和 `tags` 可为空数组，名称等文本不接受控制字符。
- `homepage` 可为空或 HTTPS 官方网页；`packages` 可为空数组，表示尚无安全自动下载包，客户端保留官方获取入口。目录下架采用新修订不再列出该工具；下架不删除客户端已安装文件。
- `legacyPath` 可为空或 `/` 分段的安全相对目录，仅供客户端识别旧完整版已有工具。服务器不读取该路径；客户端复用已有目录时不覆盖或删除它。
- 包必填 `architecture`（`x64/arm64/x86/any`）、`url`、正整数 `sizeBytes`、64 位十六进制 `sha256`、安全相对 `.exe` 路径 `entryPoint`、`kind:"portable-zip"`；每工具最多 4 个包，同架构不能重复。
- 包 URL 必须为 `https://zhenxingai.com/downloads/tools/...zip`，只允许安全 ASCII 路径段，不能带账号、非默认端口、查询、片段、路径转义或回溯。发布过的 URL 不允许改大小或哈希，后续文件使用新的版本文件名。`entryPoint/legacyPath` 拒绝驱动、绝对路径、反斜杠、回溯、Windows 保留名与非法路径字符。
- JSON 最大 4 MiB，工具最多 10000 项，拒绝额外字段和重复 JSON key。修订递增；重试历史修订只返回旧回执，不会降级当前指针。回退时把旧内容作为**新的更高修订**发布。

清单发布只验证元数据，**不请求、解压或执行 ZIP，也不能证明静态文件已经存在、哈希正确或可运行**。管理员先准备并核对工具 ZIP 的实际字节数、SHA-256 和可执行入口，再将其放入 nginx 静态目录，最后发布清单；下载文件与程序更新包相互独立。

SSE 每次连接（包括携带 `Last-Event-ID` 的重连）发送：

```text
retry: 5000

event: catalog
id: 2
data: {"revision":2,"etag":"\"tools-2-<目录SHA256>\""}

```

之后默认每 15 秒发 `: heartbeat`，每连接最长 5 分钟后关闭供客户端重连，同一 Store 最多 64 个流连接。该事件仅是版本提示，不下发文件或执行指令；不提供逐条历史事件重放。客户端启动、恢复联网和每次重连都通过 `GET catalog` 比较与补同步，以清单为准，断网保留最后成功目录；最低客户端版本不兼容时保留旧目录并提示更新。首次尚未发布目录时，公开清单和 SSE 返回 `503`，客户端应保留本地目录并稍后重试。

反向代理需显式加入上述两个公开 GET 白名单，管理员接口保持私有。下面是**部署模板，尚未应用**，路径应按部署目录调整；SSE 禁止缓存与缓冲，读超时长于心跳：

```nginx
location = /api/toolflows/v1/tools/catalog {
    if ($request_method != GET) { return 405; }
    proxy_pass http://127.0.0.1:8768/v1/tools/catalog;
}
location = /api/toolflows/v1/tools/events {
    if ($request_method != GET) { return 405; }
    proxy_pass http://127.0.0.1:8768/v1/tools/events;
    proxy_buffering off;
    proxy_cache off;
    proxy_read_timeout 45s;
}
location ^~ /downloads/tools/ {
    alias /var/lib/zhenxingai/tool-files/;
    autoindex off;
    add_header X-Content-Type-Options nosniff always;
}
```

新测试仅使用临时数据库、合成清单和 `127.0.0.1` 临时端口，不需要 ZIP，不访问下载 URL：`python -m unittest test_tool_catalog -v`。此轮没有读取真实配置、连接服务器或部署；公网可用性须在部署后单独验证。

这是独立于 WinUI 客户端的标准库服务，接收**用户最终选定用于安装**的工具流、**下载按钮点击 / 真实下载请求**的结构化计数汇总，以及用户主动提交的 **SKILL.md 修订**。运行时只监听 `127.0.0.1`，数据保存在指定的 SQLite 文件中，不会随客户端启动。

2026-09-30 的部署与无真实记录路由验证材料表明，自有官网的 `https://zhenxingai.com/api/toolflows/` 已代理三个既有 POST（工具流、事件、计数），服务仍监听环回，后台和链接检查不经公网代理。本次技能接口是本地新增实现，**尚未部署，也尚未加入公网路由白名单**。只更新 Python 源码不能使客户端的技能提交入口在公网可用。

需要 Python 3.11 或更新版本，无第三方依赖：

```powershell
# 私有后台默认关闭；本机开发/审核时用 --admin-token 开启（也可用环境变量 ZXAI_TOOLFLOW_ADMIN_TOKEN）。
python .\server.py --port 8768 --db .\toolflows.sqlite3 --admin-token <本机随机长令牌>
python -m unittest discover -s . -p 'test_*.py' -v
```

仓库根目录运行测试时使用 `-s zxai-toolflow-server`。`toolflows.sqlite3` 已被本目录的 `.gitignore` 排除。测试只用临时数据库和本机环回端口；链接探测使用替身，不访问互联网。

## 访问控制（v0.2 起）

- **客户端写入接口**（`POST /v1/toolflows`、`POST /v1/toolflows/{flowId}/events`、`POST /v1/metrics`、`POST /v1/skill-revisions`）：本版没有用户级认证。工具流与计数由客户端的分享开关控制；技能修订是另一次明确的主动提交。客户端不持有管理员令牌，服务器不会把匿名投稿当成经过认证的作者身份。服务本身只监听环回；公网暴露范围由独立的反向代理白名单限制。
- **只读后台接口**（`GET /v1/stats`、`GET /v1/toolflows/{flowId}`、`GET /v1/admin/flows`、`GET /v1/admin/export.jsonl`）：**必须携带管理员 Bearer 令牌**。未配置令牌时全部返回 `403`（后台功能保持关闭）；请求缺令牌返回 `401`；令牌错误返回 `403`。令牌用 `hmac.compare_digest` 比较。**原始对话只能在这两个入口读到，不进入任何公开渠道。**
- **技能后台**（`GET /v1/admin/skill-revisions`、`GET /v1/admin/skill-revisions/{submissionId}`、`POST /v1/admin/skill-revisions/{submissionId}/review`）：同样必须携带管理员 Bearer 令牌；审核鉴权先于请求正文解析与记录查询。后台和决策接口应保持在私有环回访问范围，不加入公开客户端路由。
- `GET /admin`：内置后台页面（静态外壳，页面本身不含数据）：输入管理员令牌后可查看工具流、导出工具流 JSONL，或筛选技能队列、查看两份原文与差异、记录采纳/退回。用户内容以 `textContent` 和文本框 `value` 展示，不渲染成 HTML 或执行脚本。页面不缓存（`Cache-Control: no-store`），不把令牌写入浏览器持久存储或保存到服务器。
- **注意**：令牌通过命令行参数或环境变量传入，请使用本机生成的随机值（≥16 字符）；不要复用其他系统的密码。

## JSON 接口 v1

所有写入请求须使用 `Content-Type: application/json`，最大请求体 8 MiB，以容纳用户选择时的完整可见对话。客户端生成 UUID 并在重试时复用；相同 ID 和相同内容返回 `200`、`created:false`，首次写入返回 `201`、`created:true`，相同 ID 对应不同内容返回 `409`。

`POST /v1/toolflows`：

```json
{
  "schemaVersion": 1,
  "submissionId": "de2d9b27-1526-465f-8998-73bf8d491d6c",
  "flowId": "a39c67d8-dfe5-4ef1-8490-e9bd9b45a9f5",
  "origin": "assistant",
  "selectedAt": "2026-09-24T09:00:00Z",
  "flowName": "Godot 2D 游戏创作",
  "projectGoal": "制作一款可在 Windows 发布的 2D 像素游戏",
  "goalDescription": "做一款 2D 游戏",
  "flowText": "先用 Godot 建项目，再用选定的 AI Agent 辅助写脚本。",
  "conversation": [
    { "role": "user", "content": "我想做游戏", "at": null },
    { "role": "assistant", "content": "建议先确定平台。", "at": "2026-09-24T08:55:00Z" }
  ],
  "items": [
    {
      "itemId": "139e5758-0595-4256-86e0-37d19aecc527",
      "name": "Godot",
      "kind": "development",
      "version": "4.x",
      "sourceUrl": "https://godotengine.org/",
      "downloadUrl": "https://godotengine.org/download/windows/",
      "installTargetKey": "godot"
    }
  ]
}
```

`origin` 仅接受 `assistant` 或 `user`。`flowName` 和 `projectGoal` 是必填的顶层字符串：前者是用户最终选定的工具流标题，最多 120 字；后者是用户最终确认想做的项目目标或需求描述，最多 65,536 字，是将来匹配推荐的主字段。两者都必须至少包含一个非空白字符，服务端保留提交的原文，不自动改写标题、删去首尾空格或从聊天记录拼接项目目标。`goalDescription` 继续保留现有的目标摘要，最多 4,096 字；`flowText` 继续保留原始工具流文本和换行，最多 65,536 字。`schemaVersion` 仍为 `1`，这是尚未部署的本地原型接口。客户端在上报前会对上传载荷的**全部字符串字段**（含 items 的名称/类型/版本/来源与下载网址、固定安装目标代码，以及执行事件的详情）统一做一次轻量凭据过滤（明显形如 `sk-…`、`Bearer …`、`apiKey=…` 的片段替换为 `[已过滤]`），本地保存的原文不受影响；过滤只覆盖这几类明显形态，是兜底而**不是**「不会上传凭据」的保证。

`conversation` 最多 200 条，仅接受 `user`、`assistant` 角色，每条正文最多 8,192 字；展示记录没有逐条时间时 `at` 应传 `null`。`items` 必须有 1–32 项，每项 `itemId` 唯一。`version`、`sourceUrl`、`downloadUrl`、`installTargetKey` 可以为 `null`。非空 URL 必须为公开域名的 HTTPS 地址，不接受账号密码、IP 字面量、非 443 端口或 URL 片段。时间字段必须带 UTC 时区。

`POST /v1/toolflows/{flowId}/events`：

```json
{
  "eventId": "bcb48281-dd28-4181-b777-eebf428c6a24",
  "itemId": "139e5758-0595-4256-86e0-37d19aecc527",
  "kind": "download_succeeded",
  "at": "2026-09-24T09:10:00Z",
  "detail": null
}
```

`kind` 允许 `download_started`、`download_succeeded`、`download_failed`、`install_succeeded`、`install_failed`、`verified`。事件必须引用已提交工具流内的项目。`detail` 可为 `null` 或最多 1,000 字的说明，不应包含密钥或登录资料。事件统计按收到的**不同 eventId** 计数；重试事件不会重复计数。`verified` 只表示客户端明确发出了验证成功事件，不能从安装成功自动推定。

当前客户端只对用户明确选定的工具流执行受支持的固定安装目标，并在安装后复查，再上报可确认的安装与验证事件。`winget` 在其内部下载软件时，客户端看不到独立下载结果，因此不会虚构 `download_succeeded`；下载事件列已经预留给以后带工具流及项目 ID 的直接下载任务。统计中的零次下载不能解释为没有发生过网络传输。

`POST /v1/metrics`——客户端本机聚合的**结构化计数汇总**（不含对话、URL 或个人内容）：

```json
{
  "schemaVersion": 1,
  "batchId": "5f4b7e56-2f2f-4d1c-8c6d-1e2b3a4c5d6e",
  "counts": [
    { "kind": "download_clicked", "tool": "CPU-Z", "count": 3 },
    { "kind": "download_requested", "tool": "CPU-Z", "count": 1 }
  ]
}
```

- `kind` 允许 `download_clicked`（用户按下载按钮）、`download_requested`（实际发出的文件下载请求，含重试尝试）、`download_succeeded`、`download_failed`。客户端只发送 `kind` 计数，不发送原始日志。
- `counts` 1–64 项，`tool` 1–120 字（不含控制字符），`count` 1–1,000,000；同一批内 `(kind, tool)` 不得重复。
- `batchId` 由客户端本地生成并持久化：每个**新批次**一个唯一 ID；网络失败重试**同一冻结批次**时复用该 ID，服务端按 ID 去重，不会重复计数；同 ID 对应不同内容返回 `409`。两次内容相同但属于不同批次的快照会得到不同 ID，各自计数。
- 与工具流提交共用同一个分享开关与服务器地址配置；开关关闭或地址未配置时客户端完全不发送。**关闭开关会作废此前未发送的选定记录与计数（重开也不补传，本地保留）；重新开启后从当时的新记录、新计数计起**；选定工具流的上报资格在选定时刻判定：开关与地址**同时**具备才进入待发。
- **fail-closed 撤销门闩（内存级 + 重开闸门）**：关闭开关立即生效——选定按"选定时刻"、计数批次按"冻结时刻"在发送紧前逐次核对，且实际发送在**同一把门闩锁内原子启动**（快速"关闭→重开"或撤销恰好发生在核对处时，旧请求不得跨出撤销边界）；即使磁盘清理（作废/清空写盘）失败也一样。跨进程兜底：**每次重新开启前都会先重试作废旧记录，作废未完成则拒绝开启**（开关保持关闭）——不把重开解释为对旧数据的追认。

只读接口（均需管理员令牌）：

- `GET /health` —— 不需要令牌，仅返回 `{"status":"ok"}`。
- `GET /v1/toolflows/{flowId}` —— 单条工具流完整记录（含 `flowName`、`projectGoal`、完整对话及链接检查状态）。
- `GET /v1/stats` —— 汇总统计。新增 `downloadMetrics`：`byKind`（各计数类别的总数）与 `topTools`（按工具名的 `downloadClicked` / `downloadRequested` / `downloadSucceeded` / `downloadFailed`，按总量降序，前 100）。此处的下载计数来自客户端本机聚合，与 `eventsByKind` 的逐事件计数是两套口径：前者口径为「按钮点击 / 请求尝试」，后者为「客户端逐条上报的执行事件」。统计按提交的标题原文精确分组（含大小写及空格差异）；**统计接口不返回 `projectGoal` 或完整对话**。
- `GET /v1/admin/flows` —— 提交列表（标题 / 来源 / 时间 / 条目数 / 事件数），供后台页面使用。
- `GET /v1/admin/export.jsonl` —— 全量导出（JSONL，每行 `{"type":"flow|event|metrics","payload":{…}}`），包含完整对话与计数，仅管理员可读。

`POST /v1/links/check` 的请求是 `{ "flowId": "…", "itemId": "…" }`。提交工具流时**不会自动探测链接**；调用该接口才对项目的 `downloadUrl` 发一次 HTTPS `HEAD`。探测先解析并检查全部 DNS 地址，只允许公网地址，连接固定到检查过的 IP，并以原域名校验证书；不使用环境代理、不跟随重定向、不读取响应正文。返回 `available`、`unavailable`、`redirect_unchecked`、`head_not_supported`、`server_error` 或 `unreachable`，以及 HTTP 状态和检查时间。`HEAD` 不支持或重定向时，状态不冒充“可下载”。

## 站点统计（Umami）边界

下载量/地区等**网站访问统计**计划接入开源 Umami（由对应会话部署），不在本服务重写统计面板。客户端只会向 Umami 发送**必要的结构化汇总事件**（如「下载按钮点击」「真实下载开始」，按站点的 Umami 配置），客户端不内置、不保存 Umami 管理凭据；服务端组件也不代理 Umami 管理接口。本原型内的计数用于「提交/请求」口径的核对与导出。

## SKILL.md 冻结修订接口

`skill_revisions.py` 负责严格校验、同一 SQLite 库内的独立 `skill_revisions` 表、差异与审核状态；它不会加载、安装、发布或下发技能。用户可以先在本机保存与试用编辑结果，再主动提交冻结修订。本机试用和服务器采纳是不同状态。

`POST /v1/skill-revisions` 精确接受以下 11 个 camelCase 字段，不接受别名、额外字段、客户端审核状态/意见/时间，也拒绝重复 JSON key：

```json
{
  "schemaVersion": 1,
  "submissionId": "a5e5cf57-2a17-4974-8ffd-bc9ed5eb2a8b",
  "submittedAt": "2026-10-02T05:10:00Z",
  "clientVersion": "0.1",
  "skillId": "ai_agent_workflow",
  "skillName": "zhenxing-assistant",
  "baseDocument": "---\nname: zhenxing-assistant\ndescription: 枕星助手\n---\n原文正文\n",
  "modifiedDocument": "---\nname: zhenxing-assistant\ndescription: 枕星助手\n---\n编辑后的正文\n",
  "baseSha256": "<baseDocument 原始 UTF-8 的小写 64 位 SHA256>",
  "modifiedSha256": "<modifiedDocument 原始 UTF-8 的小写 64 位 SHA256>",
  "changeSummary": "说明本次修改"
}
```

上例中的哈希是说明占位符，实际请求必须计算原文哈希。`schemaVersion` 必须是整数 `1`，不能是布尔值、浮点数或字符串；`submissionId` 必须为非空规范 UUID。`submittedAt` 必须为含 UTC 时区的 ISO 8601 时间，接受 `Z` 或零时区偏移。所有文本字段须为有效 Unicode 字符串，不能为 `null`、数组或数字；版本非空且最多 80 个 UTF-16 单元，修改说明非空白且最多 2000 个 UTF-16 单元，与客户端 `.Length` 口径一致。技能身份固定为上例两个值。

每份文档最多 **128 KiB 原始 UTF-8**；投稿 JSON 最多 **768 KiB**，以容纳客户端默认序列化时中文的 JSON 转义。原文和新文都必须有完整 `---` frontmatter，且仅有一行固定的 `name: zhenxing-assistant`。header 支持当前随包技能使用的简单平面 `field: value` 行、空行和顶层注释；拒绝引用/转义键、复杂键、合并键及多行构造，不解释任意 YAML。只在拆分比较时将 CRLF 规范为 LF：规范后的完整 header 必须逐字相同，包括 description 和其他字段；正文必须非空且发生变化。只有换行编码变化的投稿不算正文修订。

SHA256 对提交的**未经换行规范化、未经 trim 的原文 UTF-8**计算。数据库原样冻结两份文档和投稿字段，并另存规范 JSON 的 SHA256；审核不修改快照。`baseDocument` 只是一份客户端报告的比较原文，哈希正确、header 保持一致均不能证明它来自官方版本。

首次写入返回 `201`，同 ID、同冻结内容重试返回 `200` 和 `created:false`，同 ID 对应不同内容返回 `409`。回执包括：

```json
{
  "submissionId": "a5e5cf57-2a17-4974-8ffd-bc9ed5eb2a8b",
  "modifiedSha256": "<提交的新文哈希>",
  "status": "pending",
  "receivedAt": "2026-10-02T05:10:01Z",
  "created": true
}
```

重试保留原 `receivedAt`，并返回记录当前状态；审核后重试可以返回 `accepted` 或 `rejected`。回执只含本次投稿的 ID、哈希、状态和收到时间，不公开原文、修改说明或审核意见。不存在公开的技能列表或详情 GET。

## 私有技能审核

- `GET /v1/admin/skill-revisions`：返回最多 1000 条摘要，按收到时间倒序；不含两份文档。
- `GET /v1/admin/skill-revisions?status=pending|accepted|rejected`：按状态过滤；重复、空、未知状态和其他查询字段返回 `400`。
- `GET /v1/admin/skill-revisions/{submissionId}`：管理员读取冻结投稿、状态、审核意见/时间及统一差异文本。差异按 LF 比较，原文快照保持原样。
- `POST /v1/admin/skill-revisions/{submissionId}/review`：精确接受以下三个字段，审核 JSON 最多 16 KiB：

```json
{
  "decision": "accepted",
  "reviewNote": "审核说明，可留空",
  "expectedStatus": "pending"
}
```

`decision` 仅允许 `accepted` 或 `rejected`；`reviewNote` 必须为字符串，最多 2000 个 UTF-16 单元；`expectedStatus` 必须为 `pending`。服务器在 `BEGIN IMMEDIATE` 事务中以 `WHERE status='pending'` 条件更新状态、意见和服务器生成的审核时间。相同决策、完全相同意见的重试返回原结果和原审核时间；已审核记录改成另一决策或另一意见返回 `409`。未知 ID 返回 `404`。未配置管理员令牌时所有私有 API 返回 `403`；缺令牌 `401`；错误令牌 `403`。

三个状态为 `pending`（等待审核）、`accepted`（已采纳）、`rejected`（已退回）。**采纳只记结果，不发布官方技能，也不自动应用到任何客户端。** 第一版没有重新打开审核、删除、分页、作者账号验证或推送结果功能；新的一次修订应使用新的投稿 ID。共享管理员令牌也不是 Discourse 登录，接口不声称知道操作人的独立账号。

本地验证只使用临时数据库、合成令牌和环回端口，覆盖协议、原文哈希、CRLF/LF、默认中文转义、投稿/审批幂等、并发冲突、私有鉴权与用户文字安全输出。公网部署还必须显式新增技能 POST 白名单，后台继续保持私有；此修改不附带或执行部署。

## 边界

此服务**没有用户级账号认证、自身 TLS、保留期限或删除接口**；已有管理员令牌访问控制，但令牌是单点共享口令，不是用户体系。TLS 与公开路由由独立反向代理提供。SQLite 会保存**工具流标题、完整项目目标、原始工具流文本和提交的对话正文**、结构化计数以及主动提交的两份技能全文。本次开发验证只在本机用合成数据进行；线上传输与存储策略、备份、删除机制和告知需由运营部署单独管理。客户端的“关闭分享”开关必须在发送前阻止工具流和计数请求；服务端无法替客户端判断用户设置。技能主动提交不会包含对话，且必须由用户明确点击。**部署（含反向代理/证书/DNS/线上数据）另行执行**；本目录不包含部署脚本。
