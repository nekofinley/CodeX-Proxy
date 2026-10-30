# CodeX Proxy

Windows 托盘的 ChatGPT / Codex 选择性代理工具。它通过当前用户的 WinINET PAC 设置把目标域名导向本地 HTTP 代理，其余域名保持直连；不使用 TUN、驱动、DLL 注入或 TLS 证书。

## 当前状态

MVP 已完成，包含：

- WinForms 托盘界面、一键开启/关闭、状态指标、连接日志。
- 本地 HTTP 代理（回环随机端口），支持 `CONNECT`、普通 HTTP、WebSocket/长连接所需的全双工流式转发。
- HTTP 上游代理与 SOCKS5 上游代理；SOCKS5 优先发送域名，由上游解析。
- 本地 PAC 服务（仅回环地址），只将命中规则的域名指向本地代理。
- 当前用户 WinINET 设置备份、恢复和异常退出后的启动修复。
- 用户名/密码保存到 Windows 凭据管理器，不写入配置文件或日志。
- 可编辑域名规则，以及 ChatGPT / Codex 路径和启动参数。
- 从工具启动目标应用时注入 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY` 及小写等价变量。

详细验收记录见 [docs/VALIDATION.md](docs/VALIDATION.md)。

## 下载

从 [GitHub Releases](https://github.com/nekofinley/CodeX-Proxy/releases) 下载对应架构的压缩包：

- `CodeXProxy-0.1.0-win-x64.zip`
- `CodeXProxy-0.1.0-win-arm64.zip`

解压后直接运行 `CodeXProxy.exe`。发布包为自包含构建，不需要额外安装 .NET Desktop Runtime；当前构建未做代码签名，Windows SmartScreen 可能显示安全提示。

## 环境要求

- Windows 10 或 Windows 11，x64 / ARM64。
- 开发构建需要 .NET SDK 8。
- 运行自包含发布包不需要安装 .NET；运行 framework-dependent 构建需要 .NET Desktop Runtime 8。
- 不需要管理员权限。

## 运行

```powershell
dotnet run --project src/CodeXProxy/CodeXProxy.csproj
```

首次打开后：

1. 填写上游代理地址，例如 `http://127.0.0.1:7890` 或 `socks5://127.0.0.1:1080`。
2. 如有认证，填写用户名和密码后保存配置。
3. 检查“分流规则”，默认包含 OpenAI / ChatGPT 的主要域名。
4. 点击“测试上游代理”。
5. 点击“开启代理”，再启动 ChatGPT 或 Codex。

关闭主窗口会隐藏到托盘；从托盘菜单可退出并恢复系统代理。

## 构建与检查

```powershell
dotnet build CodeXProxy.sln -c Release
dotnet run --project tests/CodeXProxy.Checks/CodeXProxy.Checks.csproj -c Release
.\scripts\publish.ps1 -Runtime win-x64
```

自包含单文件发布：

```powershell
.\scripts\publish.ps1 -Runtime win-x64 -SelfContained
```

重新生成程序图标：

```powershell
.\scripts\GenerateIcon.ps1
```

## 域名规则

- 一行一条，大小写不敏感。
- `openai.com` 同时匹配 `openai.com` 及其所有子域。
- `*.openai.com` 与 `openai.com` 等价，主域也会命中。
- `api-*.example.com` 支持局部通配。
- 以 `#` 开头的行会被忽略。
- `localhost`、`127.*`、`::1` 始终直连。

默认规则覆盖：

```text
openai.com
chatgpt.com
oaistatic.com
oaiusercontent.com
```

如实际抓包发现新的必要域名，可在界面中补充，无需改代码。

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 配置 | `%APPDATA%\CodeXProxy\config.json` |
| 日志 | `%LOCALAPPDATA%\CodeXProxy\logs\codexproxy-YYYYMMDD.log` |
| 代理恢复备份 | `%LOCALAPPDATA%\CodeXProxy\proxy-backup.json` |
| 上游凭据 | Windows Credential Manager，目标名 `CodeXProxy/UpstreamProxy` |

`proxy-backup.json` 只在代理开启期间存在。正常关闭后删除；异常退出后，下次启动会先核对 PAC URL 归属，再恢复原设置。

## 工作方式

1. 本地 PAC 服务只监听 `127.0.0.1`，脚本按规则返回 `PROXY 127.0.0.1:<port>` 或 `DIRECT`。
2. 工具先保存当前 WinINET 代理相关注册表值到恢复备份，再设置 `AutoConfigURL` 并关闭手动代理。
3. ChatGPT / Codex 的 WinINET 请求按 PAC 命中后进入本地代理。
4. 命中规则的隧道走配置的上游；未命中规则的请求即使因为环境变量进入本地代理，也会由本地代理直接连接，不会交给上游。
5. 关闭时把备份中的值逐项写回并刷新 WinINET。

## 已知限制

- PAC / WinINET 不能强制所有进程。完全无视 Windows 代理的应用、部分独立更新器和 Store/MSIX 应用可能绕过。
- 暂不支持 HTTPS 上游代理、TLS 中间人、WinHTTP 机器级代理、自动更新器或服务级强制代理。
- ChatGPT Store/MSIX 版本不能使用 `shell:AppsFolder` 路径启动；请配置可直接启动的 `.exe` 或 `.lnk`。
- 系统 PAC 会影响所有访问命中域名的 WinINET 应用，这是当前方案的已知行为。
- 更改路由规则后需要关闭并重新开启代理才能应用。
- 已经打开且缓存了旧代理配置的应用，可能需要重启才能读取新的 PAC。

## 安全边界

- 仅绑定回环地址。
- 不记录请求正文、请求头、Cookie、令牌或代理密码。
- 日志写入前会对 URL 用户信息和常见敏感键值做脱敏。
- 上游地址禁止把用户名密码写入 URL；密码只能进入 Windows 凭据管理器。
- 应用清单为 `asInvoker`，不请求管理员权限。

