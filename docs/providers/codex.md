# Codex

[← 返回项目](../../README.md) · [下载](https://github.com/chinbor/headroom/releases)

> 适用于 余量 Headroom 2.2.2。连接方式：**OAuth**。

## 数据来源

读取 `%USERPROFILE%/.codex/auth.json` 中 Codex 自己保存的 OAuth 会话；设置了 `CODEX_HOME` 时使用该目录。向 `chatgpt.com` 查询额度及限额重置额度。

## 连接步骤

1. 在 Codex 应用中登录，或运行 `codex login`。
2. 打开 余量 Headroom 的 Codex 卡片，点击刷新。未连接时可点击「在终端登录」，完成后点击「我已登录，刷新」。
3. 需要本机用量时，在 Codex 中产生会话记录；程序读取 `sessions` 和 `archived_sessions`。

## 可以查看什么

- 账户实际返回的 5 小时、每周及其他额度窗口与重置时间。
- 接口提供时展示限额重置额度与到期信息。
- 本机日志的 Token、API 等价费用、请求数、模型构成及可计时请求的输出速度。

## 第三方工具

pi、opencode 这类第三方编程工具可以用 ChatGPT 账号登录后直接调用 OpenAI，和 Codex 共用同一份订阅额度。

**额度**：5 小时、每周额度是向 OpenAI 服务器查的账号总量，本来就包含这些工具用掉的部分。服务器只给已用百分比，不给 token 数，也不区分调用方。

**token 和费用**：这些工具不经过 Codex，Codex 的 `sessions` 里没有它们的记录，所以程序会另外读各工具在本机的记录，把调用 OpenAI 的用量计入 Codex：

| 工具 | 登录信息 | 用量记录 | 说明 |
| --- | --- | --- | --- |
| [pi](https://pi.dev) | `~/.pi/agent/auth.json` | `~/.pi/agent/sessions/**/*.jsonl` | 设置 `PI_CODING_AGENT_DIR` 时用该目录；按回复的 `responseId` 去重，分叉、复制出来的会话不会重复计数 |
| [opencode](https://opencode.ai) | `~/.local/share/opencode/auth.json` | `~/.local/share/opencode/opencode.db`（`message` 表） | 设置 `XDG_DATA_HOME` 时用该目录；只读打开数据库，内容没变化时不重复查询；还在输出中的回复等完成后才计入 |

计入规则：

- 只有该工具 `auth.json` 里 `openai` 的登录类型是 `oauth`（ChatGPT 账号登录）时才计入。用 API Key 调用 OpenAI 是单独计费的，不算进来。判断时只读登录类型这个字段，不读 token。
- 只算 provider 为 `openai` 的回复。这些工具里调用 DeepSeek、Agnes 等其他平台的用量不计入。
- 计入 Codex 后和 Codex 自己的用量合在一起显示，按模型拆分，不单独列出来自哪个工具。
- 费用按同一份价目表折算；推理 token 按 Codex 的口径算进输出。
- 工具写记录时也算作 Codex 正在使用，额度会刷新得更及时。

**支持新工具**：写一个读取器，在 `source/Windows/CodexClients.cs` 的 `CodexClients.All` 里登记一行。会话存成 JSONL 文件的，提供目录和逐行读取函数，按文件增量读取；存在数据库等其他格式的，提供一个刷新函数，内容变化时整体重读。可参考 `PiLogs.cs` 和 `OpenCodeLogs.cs`。

## 限制

纯 API Key 登录不提供这里使用的 ChatGPT 订阅 OAuth 会话。接口未返回的周期不会凭空补全；重置额度接口失败不影响已取得的主额度。删除或缺失的本机会话无法从账户额度接口还原。

费用为本机记录的 API 等价估算，不是订阅账单。数据范围受服务端返回字段、本机日志与所选时间段限制。

## 隐私

OAuth 凭据保留在原应用；监控只读使用已有会话。 不存储密码。网络查询会把相应的鉴权信息发送到该提供商（自定义平台则发送到你配置的地址）。本机统计索引保存数值，不保存对话正文。

## 常见问题

令牌过期时，先打开 Codex 应用或运行一次 Codex，让原客户端续期，再刷新。若使用自定义 CODEX_HOME，请确认启动监控的进程也能看到该环境变量。

查询失败时，界面可能保留上次成功读数；请结合更新时间与错误提示判断。

## 实现位置

`source/Windows/`：`Core.cs`（ProviderService.Fetch / Parsers.Codex）、`UsageDetails.cs`、`LocalLogs.cs`、`Timing.cs`；第三方工具在 `CodexClients.cs`、`PiLogs.cs`、`OpenCodeLogs.cs`。本文依据该版本代码核对；服务商接口或客户端格式变化时可能需要更新。
