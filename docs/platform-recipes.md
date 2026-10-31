# 平台接入方案（配置化）

除 Claude 和 Codex 外，其他平台都用一段 JSON 描述怎么读取，这段 JSON 叫“接入方案”。程序按方案发请求、取字段，并按方案生成“设置 token”引导，新增一个平台不需要改代码。

- **内置方案**：放在程序目录的 `platforms/*.json`（源码在 `source/Windows/platforms/`），目前有 DeepSeek 和 Agnes。它们和用户方案走同一套解析，id 不加前缀。
- **用户方案**：设置 → 订阅 →「添加接入方案」，粘贴 JSON 后保存，存在 `data/custom-providers.json`。id 自动加 `custom-` 前缀，不会和内置平台冲突。
- token 和密钥不写进 JSON，由“设置 token”引导或编辑页填写，用 Windows DPAPI 加密保存在 `data/<id>.key`。

## 字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `name` | 是 | 显示名，1–40 个字符 |
| `id` | 否 | 小写字母、数字、连字符；省略时由 name 生成 |
| `color` | 否 | `#RRGGBB`，图标和图表用 |
| `icon` | 否 | `icons/` 里的图标名（不含 `.svg`）；省略时找 `<id>.svg`，没有就显示首字 |
| `url` | 是 | 读额度的 GET 地址；公网必须 https，http 只允许本机或局域网 |
| `auth` | 否 | `none`（默认）、`bearer`、`token`、`x-api-key`、`header`（配合 `header` 指定请求头名） |
| `headers` | 否 | 额外的固定请求头，最多 8 个；不能设置 Authorization、Cookie、Host 等 |
| `token` | 否 | 用户怎么拿到 token，见下文 |
| `success` | 否 | `{ "code": 路径, "ok": [成功码], "expired": [失效码] }`。code 不在 ok 里算失败，在 expired 里提示 token 失效。HTTP 401/403 总是算失效 |
| `windows` | 二选一 | 额度窗口，最多 6 个，见下文 |
| `balance` | 二选一 | 余额，见下文 |
| `plan` / `account` | 否 | 套餐名、账户名的字段路径 |
| `planLabel` | 否 | 没有 plan 字段时显示的套餐名 |
| `usage` | 否 | 每日用量接口，见下文 |

字段路径写法是 `data.items[0].used`，只支持点号和数字下标。路径取不到值时显示“未知”，不会当作 0。

### token

```json
"token": {
  "kind": "console",
  "console": "https://platform.example.com/usage",
  "read": "localStorage.token",
  "copy": "copy(localStorage.token), 'token 已复制'",
  "hint": "用网页控制台的登录 token 读取……"
}
```

- `kind: "console"`：网页控制台的登录 token。设置页和额度卡片上出现“登录”，在程序内置的浏览器（WebView2）里打开 `console`，用户正常登录后，程序每秒在页面里求值一次 `read`，拿到字符串就向平台验证，通过就保存并关闭窗口。平台拒绝的 token 不会保存，原来的凭据保持不变。登录状态保存在数据目录的 `webview2` 文件夹，过期后点“重新登录”通常一步完成。
  - `read`：在控制台页面里返回 token 字符串的 JavaScript 表达式。不写时取 `copy(...)` 括号里的部分。
  - `copy`：手动方式用的命令。没有 WebView2 运行时，或用户点“改用手动方式”时，显示 4 步引导：打开 `console` → F12 打开 Console → 粘贴这行命令（建议用逗号表达式在末尾返回一句提示）→ 回到程序从剪贴板粘贴并验证。
- `kind: "apikey"`：普通 API Key，在编辑页填写。

### windows

每个窗口需要 `label`，再用下面任一种写法给出已用比例：

- `usedPercent` 或 `remainingPercent`（0–100；`"ratio": true` 表示 0–1）
- `used` 或 `remaining`，再加上 `limit`

两种写法可以同时给出：有百分比时用百分比，同时保留“已用 / 上限”的计数显示。`limit` 为 0 视为已用完。重置时间用 `resetsAt`（时间字符串或 Unix 秒 / 毫秒）或 `resetInSeconds`。`windowHours` 是窗口长度，填了才能算配速线和预计用完时间。

### balance

```json
"balance": { "amount": "data.balance", "currency": "CNY" }
```

`currencyPath` 可以从返回里读币种。返回里有多个钱包时，用 `lists` 列出钱包数组的路径，`amount` / `currencyPath` 相对每个钱包，金额按币种相加：

```json
"balance": { "lists": ["data.normal_wallets", "data.bonus_wallets"], "amount": "balance", "currencyPath": "currency" }
```

### usage（每日用量）

读最近 30 天，每天（或每天每个模型）一条，代替本机日志里这个平台的统计。地址必须和 `url` 在同一主机，token 不会发给别的地址。读取失败只影响用量，不影响额度；token 失效则整个平台提示失效。

| 字段 | 说明 |
| --- | --- |
| `url` | 占位符：`{from}` `{to}`（yyyy-MM-dd）、`{fromUnix}` `{toUnix}`（第一天零点 / 明天零点的 Unix 秒，本地时间）、`{tz}`（UTC 偏移秒数） |
| `items` | 每条记录所在的数组 |
| `groups` | 可选。记录分组的数组（比如每个模型一组），这时 `items` 相对每组 |
| `day` | 日期字段：`yyyy-MM-dd` 开头的字符串，或 Unix 秒 / 毫秒（按本地时区取日期） |
| `model` | 可选。模型名，先在记录里找，再在所在组里找 |
| `tokens` | token 字段列表，相加作为总 token；省略时用 input + cached + output + cacheWrite |
| `input` `cached` `output` `cacheWrite` `requests` | 可选。分项 token 和请求数，用于缓存命中率等 |
| `priced` | `true` 时按模型名查官方价格表折算 API 等价费用；否则费用记为未知 |

## 内置方案示例

DeepSeek（`platforms/deepseek.json`）：

```json
{
  "id": "deepseek",
  "name": "DeepSeek",
  "url": "https://platform.deepseek.com/api/v0/users/get_user_summary",
  "auth": "bearer",
  "token": { "kind": "console", "console": "https://platform.deepseek.com/usage", "copy": "copy(JSON.parse(localStorage.userToken).value), 'token 已复制'" },
  "success": { "code": "code", "ok": [0], "expired": [40001, 40002, 40003] },
  "planLabel": "账户余额",
  "balance": { "lists": ["data.biz_data.normal_wallets", "data.biz_data.bonus_wallets"], "amount": "balance", "currencyPath": "currency" },
  "usage": {
    "url": "https://platform.deepseek.com/api/v0/usage/by_api_key/amount?start={fromUnix}&end={toUnix}&tz={tz}",
    "groups": "data.biz_data.series", "items": "buckets", "model": "model", "day": "time",
    "input": "usage.PROMPT_CACHE_MISS_TOKEN", "cached": "usage.PROMPT_CACHE_HIT_TOKEN", "output": "usage.RESPONSE_TOKEN", "requests": "usage.REQUEST",
    "priced": true
  }
}
```

Agnes（`platforms/agnes.json`）读 `/api/cn/user/subscription` 的 4 个套餐窗口（文本 5 小时 / 每周、图像 / 视频每日）和 `/api/usage/series` 的每日文本 token，完整内容见源码。

这两个平台用的都是网页控制台的内部接口，没有公开文档，平台改版后可能需要改方案里的路径。

## 安全边界

- 只发 GET；不跟随重定向；单个平台总限时 20 秒，响应上限 1 MB。
- 密钥只放进方案声明的认证请求头，不进 URL 和日志；用户方案的 `usage.url` 必须和 `url` 同主机。
- 不回显接口返回的错误原文，以免带出账户信息。
