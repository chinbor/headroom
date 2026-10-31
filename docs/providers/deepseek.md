# DeepSeek

[← 返回项目](../../README.md) · [下载](https://github.com/chinbor/headroom/releases)

> 适用于 余量 Headroom 2.1.0。连接方式：**网页登录**（推荐）或 **API Key**（只读余额）；本机用量来自 **DeepSeek Harness**。

## 数据来源

- 网页登录：用 DeepSeek 开放平台控制台的登录 token（`localStorage.userToken`），读 `platform.deepseek.com` 的账户概要（余额）和按天、按模型的用量与扣费。接入方案是内置的 `platforms/deepseek.json`。
- API Key：没有网页登录时，用保存的 API Key 向 `api.deepseek.com/user/balance` 查询余额，没有每日用量。
- 本机用量：读取 DeepSeek Harness（`dsh`，`@deepseek-ai/dsh-*`）保存在 `~/.dsh/sessions/<工作区>/<会话>/session.v4.jsonl.zstd` 的会话记录。文件由多个追加的 zstd 帧组成，本软件自带解压（.NET Framework 没有 zstd），增量读取：只解压上次之后新写入的完整帧。每次模型调用由 `request/header`（发出时间、模型）和 `assistant/message`（`usage` 与回复写入时间）组成。

## 连接步骤

1. 打开 设置 →“添加订阅”，选 DeepSeek，点“登录添加”，在弹出的窗口里正常登录开放平台，程序自动读取并保存 token。
2. 或者在同一处选择 API Key，粘贴在 DeepSeek API 平台创建的密钥（`sk-` 开头）。两者都有时用网页登录。

## 可以查看什么

- 账户余额（充值余额和赠送余额），按接口返回的币种分别显示。
- 网页登录时，消耗页显示 DeepSeek **实际扣费**：每天、每个模型的 Token（缓存命中、未命中、输出）、请求数和金额。
- 只用 DeepSeek Harness、没有连接账户时，卡片显示“DeepSeek Harness · 本机用量”，不提示连接。DeepSeek Harness 自己的登录（`~/.dsh/.credentials.yaml`）查不了余额：`api.deepseek.com/user/balance` 返回 401，平台接口返回 “invalid token”。
- 详情页的**本机用量**（使用 DeepSeek Harness 时）：费用、Token、请求数、Token 构成、柱状图、各模型用量和**输出速度**。

## 限制

这是 DeepSeek API 余额，不是网页聊天的额度。余额变化不用来推算费用。

- 网页登录用的是控制台内部接口，没有公开文档，平台改版后可能需要更新 `platforms/deepseek.json`。
- 本机用量只包括 DeepSeek Harness 的调用；直接调用 API 的其他程序不计入（网页登录的实际扣费包括全部 API 调用）。
- 会话标题的生成调用在记录里没有用量，不计入。
- `inputTokens` 按“不含缓存命中”的输入处理；若某条记录的 `totalTokens` 表明缓存命中已含在输入里，会自动扣除。目前只用无缓存命中的会话核对过。
- 数据目录固定为 `~/.dsh`（未发现可改目录的环境变量）。

本机用量的费用为 API 等价估算，不是账单；以网页登录读到的实际扣费为准。

## 隐私

token 和 API Key 使用 Windows DPAPI 加密保存在本机数据目录（`deepseek.key`、`deepseek-apikey.key`），只供当前 Windows 用户解密。不存储密码。网络查询只把鉴权信息发送到 DeepSeek。本机统计索引保存数值，不保存对话正文。

## 常见问题

提示登录过期时在设置里重新登录。只填 API Key 时确认密钥来自 DeepSeek API 平台且仍有效。网络失败时检查设置里的代理模式。

查询失败时，界面可能保留上次成功读数；请结合更新时间与错误提示判断。

## 实现位置

`source/Windows/`：`platforms/deepseek.json`（网页登录方案）、`CustomProviders.cs`、`ConsoleLogin.cs`、`Core.cs`（ProviderService.Fetch / Parsers.DeepSeek，API Key 余额）、`DeepSeekHarness.cs`（会话记录）、`Zstd.cs`（解压）。本文依据该版本代码核对；服务商接口或客户端格式变化时可能需要更新。
