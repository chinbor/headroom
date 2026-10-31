# Agnes

[← 返回项目](../../README.md) · [下载](https://github.com/chinbor/headroom/releases)

> 适用于 余量 Headroom 2.1.0。连接方式：**网页登录**（控制台登录 token，不是 API Key）。

## 数据来源

Agnes 没有公开的额度接口，本软件读取网页控制台自己用的接口，接入方案是内置的 `platforms/agnes.json`：

- 额度：`platform-backend.agnes-ai.cn/api/cn/user/subscription`，Bearer 鉴权，带 `X-User-Language: zh-CN`。返回 `code` 为 0 或 200 视为成功，401、403 视为登录过期。
- 每日用量：`platform-backend.agnes-ai.cn/api/usage/series`，查询最近 30 天（`range=custom&start_date=…&end_date=…`），每天一条，取 `text_tokens` 和 `request_count`。

两个请求用的都是控制台网页登录后保存在 `localStorage.token` 里的 token。

## 连接步骤

1. 打开 设置 →“添加订阅”，选 Agnes，点“登录添加”。
2. 在弹出的窗口（内置 Edge WebView2，打开 `platform.agnes-ai.cn/settings/usage`）里正常登录。
3. 登录完成后程序自动读取 token，向额度接口验证一次，通过就保存并连接，窗口自动关闭。

网页登录的浏览器资料保存在数据目录里，下次重新登录通常点一下就好。没有 WebView2 运行时、或在演示模式下，会改为手动方式：按页面说明在控制台的开发者工具里复制 token，再粘贴到程序里。

## 可以查看什么

- 套餐名称（`data.plan_name`，没有时显示“Token Plan”）。
- 四个额度，各自显示已用、总量、剩余比例、重置时间和使用节奏：
  - 文本 · 5 小时
  - 文本 · 每周
  - 图像 · 每日
  - 视频 · 每日
- 消耗页的**每日用量**：最近 30 天每天的文本 token 和请求数，柱状图和平台拆分。这份数据来自 Agnes 服务端，代替本机日志。

## 限制

- 接口没有公开文档，平台改版后可能读不到，需要更新 `platforms/agnes.json`。
- 每日用量只有文本 token 和请求数，没有按模型拆分，也不估算费用；图像、视频只有额度百分比。
- 某个额度接口没有返回时就不显示，不会当成“全部剩余”或“已用完”。
- token 过期后卡片提示重新登录；token 的有效期由 Agnes 决定。

## 隐私

token 使用 Windows DPAPI 加密保存在本机数据目录（`agnes.key`），只供当前 Windows 用户解密。不存储密码。网络查询只把 token 发送到 Agnes 的接口。

## 常见问题

提示登录过期（401 / 403）时，在设置里点“重新登录”。网络失败时检查设置里的代理模式。

查询失败时，界面可能保留上次成功读数；请结合更新时间与错误提示判断。

## 实现位置

`source/Windows/`：`platforms/agnes.json`（接入方案）、`CustomProviders.cs`（按方案请求和解析额度、每日用量）、`ConsoleLogin.cs`（网页登录窗口）、`PlatformUsage.cs`（每日用量）。本文依据该版本代码核对；服务商接口变化时可能需要更新。
