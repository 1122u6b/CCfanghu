# CCfanghu · Windows 客户端

开发仓库。目标是保留用户提供程序的 Windows 界面和可观察功能，关闭商业授权码与售后群入口，连接用户的自有服务器，采用全新的业务数据。后续可增加商业授权模块。

## 当前已迁入并验证

- 原版九个主要页面及样式、图标和字体；导入连接文件替代激活界面，群入口移除，页面不依赖演示状态显示“已保护”。
- Windows 原生 WinForms/WebView2 桌面壳：窗口、通知、托盘和受限命令入口；界面禁止外部导航与直接网络请求。
- 桌面至后台的有界 JSON 帧协议、超时与错误传播，以及连接后台前对服务进程安装路径的检查。
- 服务器连接文件解析、证书 SHA-256 固定、独立设备凭据与私有 DNS 绑定。
- 原有 TLS SOCKS5 网关和管理功能的多设备隔离改造。
- WFP 原生接口和策略生成，以及仅在一次性 Windows 测试机执行的原生验证入口。

## 当前尚未完成

完整的 Windows 后台服务、服务身份和 IPC 访问控制、Mihomo 进程及 TUN 生命周期、真实防直连与恢复验收、完整功能逐项接入、自动更新、安装/卸载包、线上部署和商业容量测试。因此现在没有通过完整验收的 Windows 成品安装包，不应将单独桌面壳或策略测试程序用于客户部署。

## 本地验证证据

- 桌面壳和核心检查项目：Microsoft .NET 8.0.428 编译，零警告、零错误。
- 11 项核心与 IPC 检查通过。
- 15 项网关设备隔离测试通过。
- 2 项真实 loopback TLS 联调通过：.NET 设备登记与认证、权限隔离、错误证书拒绝，以及挂起正文的超时取消。
- 4 项 DOM 检查通过：九个页面、缺少服务时的状态、连接文件入口、关闭的授权入口。

这些证据来自 Linux 构建和受控本机测试，未执行 Windows WFP/TUN/浏览器/休眠恢复的实际验收，未访问客户服务器。

## 开发者检查

```text
npm ci
npm run test:ui
dotnet build windows/Leyra.Windows.Checks -m:1
dotnet windows/Leyra.Windows.Checks/bin/Debug/net8.0/Leyra.Windows.Checks.dll
dotnet build windows/Leyra.Windows.Desktop -m:1
```

Python 测试需设置 `PYTHONPATH=server`，真实 TLS 联调还需设置 `LEYRA_DOTNET` 为 dotnet 路径，或确保 dotnet 位于 PATH；然后运行 `python -m unittest discover -s server/tests -v`。开发测试依赖见 `requirements-dev.txt`。

## 云测试

工作流仅允许手动触发，`allow_hosted_compute` 默认关闭；没有 push/PR 自动云任务。只有明确允许消耗额度或计费后才运行一次性 Windows 测试。默认最长 20 分钟，不部署服务器，不发布 Releases，不生成或宣称成品安装器。

## 凭据和组件

不提交真实服务器配置、令牌、上游密码、私钥、证书私钥、数据库或客户资料。测试使用临时虚构凭据和证书。

界面资源来自用户提供并声明归属自己的程序，仅用于其私有改造项目。Geist 字体保留随附 OFL 许可。WebView2 由 Microsoft 提供，Mihomo 的最终打包需保留其许可证和来源校验；仓库不包含原程序可执行文件或原服务的连接凭据。
