# 开发、测试与发布

## 项目结构

```text
src/          应用、WPF 资源、工作进程与诊断入口
tests/        与应用一起编译的离线测试夹具
assets/       通用应用图标
scripts/      构建、测试与安装
docs/         架构和维护说明
```

程序保持 C# 5 兼容，使用 .NET Framework 4.8。XAML 通过 `PhotoImportV2.FluentStyles.xaml` 和 `PhotoImportV2.FluentView.xaml` 嵌入资源后动态加载，不作为 WPF Page 编译。

## 验证

在修改后的源码上执行：

```powershell
pwsh -File .\scripts\build.ps1
pwsh -File .\scripts\test.ps1
```

测试覆盖文件冲突、内容校验、身份变更、重解析点、原件清理授权、配置迁移、优先级、WebDAV 路径与登录触发器变换，以及候选内容对比和后缀规则。新后端使用回环模拟 WebDAV / S3 服务器和公开签名夹具验证路径隔离、认证边界、条件发布、分片中止、响应清理与超时；不读取真实凭据。调度测试验证并发上限、单大文件通道、公平性与取消排空。界面检查使用合成数据渲染，并断言候选和不可用位置的按钮状态，不点击实际应用，也不修改真实登录任务。

`--check-locations <state.json> <report.json>` 是显式的实际位置只读诊断入口，使用指定配置中的凭据引用并输出可访问状态，不创建目标文件。它不在默认测试和 CI 中执行。位置编辑器／列表的“测试连接”是写入能力测试，与该只读入口不同。

合成数据中的 `nas.example` 和 `192.0.2.10` 为示例地址。真实服务器、卡片标识、照片、密码、状态文件和日志不得加入源码或 Git 历史。

## 远端兼容性诊断

只有在明确选择可写测试位置后执行，例如：

```powershell
.\bin\PhotoImport.exe --remote-probe "https://nas.example/Photos/Test" ".\artifacts\remote-probe.txt"
```

该命令会在指定位置创建随机测试子目录，上传新生成的小文件、验证读回与并发写入锁，并验证删除本地合成源文件。结束时清理本次创建的文件与空目录；失败时报告可能残留的测试路径。它不使用 SD 卡照片。此诊断不在默认测试或 CI 中执行。

## 安装与更新

构建输出位于 `bin`。安装脚本为用户会话注册登录任务，并备份原任务与快捷方式到 `%LOCALAPPDATA%\PhotoImport\deployment-backups`。更新安装前从托盘退出当前实例，不要在传输期间替换程序。

源码仓库与运行数据分离。创建或推送 GitHub 仓库不要求改变本机正在运行的安装位置。

发布前检查暂存差异和追踪文件，确认不含本机配置与媒体。编译产物由构建生成，不直接提交。发布版本时更新 `src/AssemblyInfo.cs` 与 `CHANGELOG.md`。
