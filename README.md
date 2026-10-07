# Photo Import Assistant · 相机导入助手

Windows 存储卡照片与视频导入工具，使用原生 WPF 界面和托盘运行方式。

- 按卡内 UUID 登记存储卡，支持改名、离线管理和独立导入偏好。
- 导入到本地文件夹、UNC 网络目录或 WebDAV，支持位置优先级和失败切换。
- 完整 SHA-256 校验、同名冲突保护和取消操作。
- 上传完成后询问是否清理原件；本地或映射盘副本不触发原件删除。
- 浅色、深色、跟随系统主题，以及可配置的登录自启。

## 构建与测试

要求 Windows 10/11、.NET Framework 4.8。推荐使用 PowerShell 7；构建使用 Windows 自带的 .NET Framework C# 编译器，无需下载依赖。

```powershell
pwsh -File .\scripts\build.ps1
pwsh -File .\scripts\test.ps1
```

程序位于 `bin\PhotoImport.exe`。测试报告与合成界面渲染位于 `artifacts\`；测试不访问实际 SD 卡、NAS 或登录任务。

也可使用 Visual Studio / MSBuild 打开 `PhotoImport.sln`，安装 .NET Framework 4.8 开发工具后构建。

## 安装与使用

```powershell
pwsh -File .\scripts\install.ps1
```

安装脚本创建当前用户的 `PhotoImport-Agent` 登录任务，以及桌面和开始菜单快捷方式。已有助手正在运行时，请先从托盘菜单退出再安装。运行时使用当前登录用户已有的 Windows 网络凭据。

1. 在“导入位置”添加照片保存位置。全新配置没有预设服务器或文件夹。
2. 插入 SD 卡，确认登记，或将格式化后的卡关联到已有记录。
3. 确认导入；完成后选择保留或清理原件。

关闭窗口会收起至托盘；退出应用使用托盘菜单。关闭登录自启后，仍可通过快捷方式手动启动。位置修改在下一批导入生效。

设置、登记、日志和导入凭据保存在 `%LOCALAPPDATA%\PhotoImport`，不属于项目源码。支持 DCIM 下的 JPG/JPEG、NEF/NRW、MOV/MP4/AVI 和 TIF/TIFF。

## 预览与诊断

```powershell
# 合成数据预览，不操作真实卡片或保存设置
.\bin\PhotoImport.exe --preview

# 生成原生 WPF 界面状态图
.\bin\PhotoImport.exe --render-previews .\artifacts\ui-previews
```

远端兼容性诊断需显式提供测试位置，详情见 [开发说明](docs/development.md)。

## 维护

- [架构与数据安全](docs/architecture.md)
- [开发、测试与发布](docs/development.md)
- [更新记录](CHANGELOG.md)

GitHub Actions 在 Windows 上自动构建、运行离线测试并保存测试产物。
