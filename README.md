# 拾刻

面向 Windows 的 PoE 工具箱，提供洗词缀、连点器、按键循环和一键回城。使用 C#、.NET 10 与原生 WPF 实现。

## 运行与更新

完整解压维护者提供的 `拾刻-win-x64.zip`，运行 `拾刻.exe`。需要安装 **[x64 .NET 10 桌面运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)**：在微软官网选择“.NET 桌面运行时”下的 Windows x64 安装程序。

启动后先检查「设置」中的目标游戏进程，再启用需要的工具。游戏使用管理员权限时，拾刻也应使用相同权限；窗口标题会显示本次运行的实际权限状态。

发现新版时，程序会提示并打开下载页。退出旧版后更新程序和包内音效，**保留原来的整个 `data` 目录和自定义音效**，不要用新包的 `data` 覆盖个人配置。

## 使用

### 洗词缀

选择模式和通货 → 点击「设定坐标」，鼠标移到游戏中的目标后按 **F7** → 填写词缀 → 切回游戏按 **F5** 开始、**F6** 停止。通货和装备都需要录制坐标。

| 模式 | 规则 |
| --- | --- |
| Mode 1 单通货 | 主词缀池任意命中 1 条即停止，不用设置主／次数量 |
| Mode 2 | 改造＋增幅，或重铸＋点金；主＋次要求最多 2 条。改造出了两条词缀会直接终检，不再增幅 |
| Mode 3 | 改造＋增幅＋富豪；主＋次要求最多 3 条。魔法阶段按总命中数减 1 筛选，富豪后终检；未成功会重铸重来。崇高可选，启用后补录其坐标 |

Mode 2/3 在词缀池标题旁选择命中数，超限会在启动时弹窗提醒。例如要 A＋B/C：主池放 A、主 1，次池放 B 和 C、次 1。

- 单通货下拉框选择币种，通货卡片负责录制坐标。移动装备／通货、改变游戏窗口或分辨率后，重新确认坐标。
- 词缀按文字包含匹配，参考 Ctrl+Alt+C 复制出的实际描述填写；不自动判断数值范围或 T1，装备名和基础类型也能参与匹配。同一条实际词缀不会被相近关键词重复计数。
- 排除词缀优先：改造阶段出现会继续洗，最终检查出现则不合格。
- 预设只保存词缀池和命中数，加载后仍需确认模式、通货、坐标、子模式与崇高开关。
- “物品信息未变化”也可能由坐标、通货适用性或游戏响应造成，必要时提高操作延迟。

### 其他工具

| 功能 | 默认操作 |
| --- | --- |
| 连点器 | 启用后 F8 开始／停止；按住 F11 连点，松开停止。点击鼠标当前位置，左／右键可选 |
| 按键循环 | 设置槽位按键和间隔（秒），开启槽位；F9 开始／停止，最多 10 槽 |
| 一键回城 | 启用后按 F2，默认发送 `/hideout`，命令可修改 |

热键可在「设置」中修改，以工具页顶部显示为准。点 **X** 退出全部工具；鼠标移至主屏幕左上角可触发紧急停止。

启动会检查版本、发送固定的匿名启动统计并获取广告；具体说明可在「设置 → 隐私与统计」查看。

## 项目结构

```text
README.md                   使用与开发入口
CHANGELOG.md                版本变化
源码/
  拾刻.csproj               主程序项目
  App.xaml / Bootstrapper.cs 启动入口
  Host/                     宿主、工具注册、导航与生命周期
  Services/                 输入、热键、存储、声音与网络等共享服务
  Tools/                    洗词缀、连点、按键循环、回城与设置
  Themes/                   WPF 主题与展示状态
  Properties/PublishProfiles/ 发布配置
  Tests/                    内部回归用例
  scripts/                  构建、打包与隔离验证入口
  验证内部用例.cmd           内部验证入口
  data/presets/             内置预设
  sounds/ / poe.ico         内置音效与图标
```

宿主统一管理热键、共享服务和分节存储；工具实现 `Host/ITool.cs`，通过 `Host/ToolRegistry.cs` 注册。坐标录制使用独立的 `ICoordinateProvider` 能力接口。

## 构建与验证

开发需要 Windows、Git 和 **[.NET 10 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)**。在 Windows PowerShell 中进入仓库根目录：

```powershell
dotnet restore ".\源码\Tests\ShiKe.InternalTests.csproj"
dotnet build ".\源码\拾刻.csproj" -c Release --no-restore
.\源码\验证内部用例.cmd
```

内部用例会短暂显示独立测试窗口，不启动日常程序、不注册实际热键、不发送游戏输入、不访问真实网络。结果生成在 `bin/Release/net10.0-windows/验证记录/内部用例/`。

生成发布程序与分享包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\源码\scripts\Build-Release.ps1"
```

输出统一在 `bin/Release/net10.0-windows/`。分享包仅包含 EXE、五个内置音效和一个内置预设；个人配置和自定义资源不进入包。已有日常程序运行时，发布入口会要求先退出。

已验证的 EXE 可用同一入口的 `-PackageOnly` 参数重新整理分享包。`Run-StartupChecks.ps1` 用于发布后的隔离启动与关闭验证，`Measure-Startup.ps1` 是它使用的测量脚本。

## 反馈与免责声明

[提交问题](https://github.com/nousedinner/poe-craft-tool/issues) · [反馈表单](https://docs.qq.com/form/page/DZkpVa05VSEtUZkhT) · [更新记录](CHANGELOG.md)

本工具不读取或修改游戏内存，仅模拟键鼠操作。使用风险由使用者自行承担；因使用本工具导致的封号、账号处罚及其他损失，作者及维护者概不负责。
