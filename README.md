# CCfanghu · Windows 客户端

**不会写代码？请直接看 [运营手册.md](运营手册.md)，里面一步一步写了怎么下载安装包、开通客户。**

用户私有改造项目。保留原程序九个主要页面、样式、字体和图标；当前关闭商业授权码及售后群入口；业务数据存入自有服务器，各设备独立隔离。历史业务数据不迁入新存储。

## 已实现

- WinForms/WebView2 原生桌面窗口、原版界面资源、托盘线路选择/暂停/恢复、通知、关闭窗口后留在托盘。
- LocalSystem 后台服务；仅当前桌面所有者可连接的命名管道；双向进程身份核对；敏感操作使用 Windows 原生账户密码验证及一次性短效凭证。
- 固定的 Program Files 安装位置、安装目录和运行数据 ACL、拒绝目录链接；运行凭据只保存在 SYSTEM/管理员可读目录。
- 官方 Mihomo v1.19.33 compatible x64 组件的来源和 SHA-256 校验；原生 Windows Job 管理子进程，后台退出时清理其子进程。
- TUN、私有 DNS、连接层与传输层 WFP 拒绝策略；断线保留拒绝规则并重试；暂停后定时恢复；独立网络恢复工具只清理本软件规则。
- 证书固定、独立设备身份、设备撤销与轮换、设备隔离的出口/消息/工单/附件/账户数据。
- 周期性经显式本机代理回访自有 TLS 网关，由服务器报告该连接来源；组件存活本身不作为出口可用的证据。
- 时区设置/恢复、服务端同步时间、服务端保存并按设备隔离的自定义直连规则（本机保留离线保护缓存）、系统代理关闭与两分钟撤销、浏览器策略设置与按原值恢复。
- 账户、消息、工单、帮助、住宅 SOCKS5 出口接入、实际点击触发的测速；受保护状态下显示账户登录资料及邮箱辅助登录。
- 标准安装/卸载脚本和离线候选安装器：附 .NET 与官方 Microsoft WebView2 离线运行环境；安装时同时校验运行环境哈希及 Microsoft Authenticode 签名。
- 现有两项 Linux 服务的部署脚本：保留服务账户、读取现有基础设施配置、创建全新业务存储、导出私有连接文件、鉴权 TLS 健康检查、失败恢复原服务命令。已尝试在用户服务器运行，但最近一次部署任务失败，尚无部署成功证据。

## 验证边界与待完成项

**0.3.0：代码层面的缺项已补齐（见验收清单「0.3.0 已补齐」），尚需用户服务器重新部署和 Windows 实机验收后才能交付客户。**

仓库逐项核查及真实任务记录见 [成品缺项与验收清单](validation/product-acceptance.md)。界面迁入、接口存在及专项测试通过，均不能代表全部功能接入或完整成品验收。

已在 Linux 交叉编译 Windows 桌面、后台和检查程序。已执行核心策略/IPC、设备隔离、真实 loopback TLS、官方网络内核转发及错误证书拒绝测试，以及九个界面的 DOM 检查。

已在一次性 Windows Server 2022 x64 上完成编译、DOM、真实 TLS、原生 Job、官方内核启动、WFP 公网连接拒绝/恢复、TUN 驱动创建/清理及真实 Windows 账户密码/SID 绑定和拒绝分支清理的实际检查。四轮实际执行合计 7 分 16 秒，详见 [Windows 验证记录](validation/windows-20261116.md)。驱动测试关闭自动路由，尚不构成完整联网保护验收。

以下仍未完成或未实测：完整 TUN 自动路由/私有 DNS 与服务安装的端到端使用、浏览器 WebRTC/全应用泄露实测、休眠/唤醒和异常恢复、实际界面像素对照、第三方冲突程序识别与退出、所有原版出口链接/订阅格式、IP 归属地数据、可信签名自动更新、管理中心单点登录、用户服务器部署与域名 DoH 联调、商业容量测试。安全检查中没有独立证据的项目明确显示未检测；相关尚未接入的命令返回明确错误。

运行目录中的 `artifacts/LEYRAConnect-0.2.0-dev-internal-setup.exe` 仅为内部验证候选，不能作为“已通过完整验收”的安装包发布。脚本生成安装器并不证明 Windows 原生功能已经通过。

## 服务器运营（server/admin.py）

在服务器上（或持有管理员连接文件的电脑上）运行，默认读取 `/root/leyra-server-connection.json`。**管理员连接文件含管理令牌，不要发给客户。**

```text
python3 admin.py device-new "客户A的电脑" --out 客户A.json   # 每台电脑一个文件，客户在软件里导入
python3 admin.py devices | device-disable ID | device-rotate ID --out 新.json
python3 admin.py tickets --waiting ; python3 admin.py ticket ID 3 ; python3 admin.py reply ID 3 "已处理"
python3 admin.py broadcast "维护通知" "今晚维护 10 分钟"
python3 admin.py help-set help.json        # {"faq":[...],"guides":[...],"web_center":"https://..."}
python3 admin.py publish-update 0.3.1 artifacts/LEYRAConnect-0.3.1-setup.exe --notes "说明"
```

## 开发检查

```text
npm ci
npm run test:ui
dotnet build windows/Leyra.Windows.Checks -m:1   # Linux 上加 -p:EnableWindowsTargeting=true
dotnet windows/Leyra.Windows.Checks/bin/Debug/net8.0/Leyra.Windows.Checks.dll
dotnet build windows/Leyra.Windows.Desktop -m:1
dotnet build windows/Leyra.Windows.Agent -m:1
```

Python 测试设置 `PYTHONPATH=server`，`LEYRA_DOTNET` 为 dotnet 路径，依赖见 `requirements-dev.txt`。`python -m unittest discover -s server/tests -v`。内核联调额外使用 `scripts/prepare_kernel.py linux artifacts/kernel-linux` 下载并校验的官方 Linux 内核；没有该组件时明确跳过该项。

部署回归检查：`python -m unittest discover -s deploy/tests -v`。覆盖服务慢启动、监听地址、证书错误不重试、超时，以及某一服务回滚失败时仍尝试恢复另一服务。

`scripts/build_windows_package.py` 交叉发布自包含 Windows 程序并调用 NSIS。需提供 Microsoft 官方 WebView2 离线安装器及其来源/哈希清单；脚本不会触发云任务。

## 云测试与生产部署

工作流仅手动触发，`allow_hosted_compute` 默认关闭，没有 push/PR 自动云任务。只有明确允许消耗额度或计费后才运行一次性 Windows 测试；最长 20 分钟。工作流不会访问用户服务器或发布 Releases。

部署脚本需要在现有服务器以 root 运行，拒绝覆盖已存在的新业务存储。保留基础设施 DNS 配置、现有证书与上游连接，历史数据库不复制；原服务配置文件不覆盖。脚本的本机 TLS 检查不能替代域名 DoH、真实出口与 Windows 客户端端到端验收。

## 凭据和第三方组件

真实配置、令牌、密码、证书私钥、数据库和客户资料不得提交到仓库。测试只用临时虚构凭据和证书。

组件来源和许可见 `THIRD-PARTY-NOTICES.txt`。Mihomo 使用官方未修改兼容版本并保留 GPL 许可证、源码地址和校验记录；不使用原程序的定制脏构建内核。

