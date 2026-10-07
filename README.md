# Photo Import Assistant · 相机导入助手

Windows 存储卡照片与视频导入工具，使用原生 WPF 界面和托盘运行方式。

- 按卡内 UUID 登记存储卡，支持改名、离线管理和独立导入偏好。
- 导入到本地文件夹、UNC 网络目录或 WebDAV，支持位置优先级和失败切换。
- 完整 SHA-256 校验、同名冲突保护和取消操作。
- 导入前对比已启用位置的媒体内容，相同副本不再计入待导入；支持排除文件后缀。
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
3. 等待目标内容对比，确认新增文件的导入；完成后选择保留或清理原件。

在“设置 → 文件范围 → 排除后缀”配置不导入的媒体后缀，逗号或空格分隔，例如 `.jpg, .mov`。只扫描 DCIM 下支持的照片和视频，Nikon `.DAT` 等辅助文件始终忽略。

目标对比递归检查已启用位置内的支持媒体，按文件大小筛选后做 SHA-256 校验，支持目标文件改名。同名同大小但不同内容仍可导入。目标不可达、文件不可读或对比无法确认时，不把它当作重复文件。首次对比大量照片或视频可能需要较长时间，可取消。

对比结果在本次插卡会话中用于展示，不会每四秒重复读回全部媒体。拔插卡、手动“刷新”或点击“导入”、修改位置或排除规则、媒体变化及完成导入后重新对比；目标文件被外部删除或修改后，可点击“刷新”更新待导入数量。确认期间候选快照发生变化，本次确认作废。已有副本只影响候选列表，不授权清理原件。

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
