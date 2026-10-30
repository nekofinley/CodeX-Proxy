# 验收记录

日期：2026-10-31  
环境：Windows 10 19045 x64、.NET SDK 8.0.427、本机 HTTP 上游 `127.0.0.1:7890`

## 自动检查

命令：

```powershell
dotnet run --project tests/CodeXProxy.Checks/CodeXProxy.Checks.csproj -c Release
```

共 13 项，覆盖并通过：

- 域名精确匹配、子域匹配、局部通配、回环排除。
- PAC 目标代理与直连分支生成。
- HTTP / SOCKS5 上游地址解析和错误输入拒绝。
- HTTP 上游 `CONNECT` 隧道。
- HTTP 明文绝对 URI 转发。
- 未命中规则时本地直连。
- SOCKS5 域名传递。
- PAC HTTP 服务。
- 配置 JSON 读写与规则规范化。
- Windows 凭据管理器保存、读取、删除。
- 目标进程代理环境变量注入。
- 隧道分块流式传输。

## 手工集成验证

1. 开启代理后，`HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\AutoConfigURL` 指向 `http://127.0.0.1:<port>/proxy.pac`，备份文件存在。
2. PAC 内容包含目标域名的 `PROXY 127.0.0.1:<port>` 和默认 `DIRECT`。
3. 通过本地代理访问 `https://chatgpt.com/` 返回 HTTP 200，日志记录 `HTTP upstream`。
4. 通过同一本地代理访问 `https://example.com/` 返回 HTTP 200，日志记录 `DIRECT`。
5. 正常关闭后，`AutoConfigURL` 恢复为原来的“不存在”，`ProxyEnable`、`ProxyServer`、`ProxyOverride` 等值一致，备份文件删除。
6. 在代理开启状态强制结束进程后，`AutoConfigURL` 和备份文件仍保留；重新启动应用后自动恢复原设置并删除备份。
7. `Alt+F4` 关闭主窗口后进程继续驻留托盘；再次启动 `CodeXProxy.exe` 不会产生第二个实例，并会唤回已有窗口。
8. 在 `940 × 700` 最小窗口尺寸下，三个配置页和日志区均可完整操作。

## 尚未完成的外部验收

- 使用真实 ChatGPT 账号进行登录、会话列表、流式回复和文件上传的端到端验证。
- 使用真实 Codex 任务验证其启动工具子进程的完整链路。
- 对 ChatGPT Store/MSIX 版本和特定独立更新器做绕过诊断。

这些项目需要登录态或具体安装形态，不影响当前核心代理闭环，但不应在发布说明中宣称已经覆盖。
