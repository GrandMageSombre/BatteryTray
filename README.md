# BatteryTray

Windows 托盘小工具：在任务栏时钟上方显示 2.4G / 蓝牙 / USB 无线设备的电量面板。

只有一个 exe，无安装、无后台服务、无网络请求。

## 特性

- **面板显示**：220×48 透明分层窗口，固定在时钟正上方、右对齐，随系统深浅色变化；隐藏图标弹层打开时自动避让。
- **四类设备**：手柄、鼠标、键盘、耳机各占一槽，显示淡色类别图标 + 电池图形（有电量为填充、未知为虚线空电池、0% 为红色底线、蓝牙未知叠加蓝牙符号、USB 有线显示插头）。
- **多协议读取**：标准 HID 电池字段（Usage 0x06/0x20）、罗技 HID++ 2.0（0x1004 统一电池 / 0x1001 电压 / 0x1000 状态）、BLE GATT `0x2A19` Battery Level。
- **档位与百分比区分**：设备只上报粗粒度档位（临界/低/良好/满）时显示档位并标注「（粗）」，不把固件原始值当作电量百分比。
- **插拔即时响应**：注册 `WM_DEVICECHANGE` 与 HID 设备通知，设备拔出或关闭立即清除旧电量；轮询 1 秒，带看门狗防止 HID 读取卡死后界面冻结。
- **不伪造读数**：读不到就显示「未找到标准 GATT 电量服务」或「该设备未提供电量上报」，并在状态窗口说明原因。
- **开机自启**：写入当前用户启动项，不需要管理员权限。

## 系统要求

- Windows 10 / 11（BLE 读取依赖 WinRT，需 Windows 10 及以上）
- .NET Framework 4.x（系统自带，无需额外安装）

## 构建

`lib/winrt/` 下的三个 winmd 来自 `Microsoft.Windows.SDK.Contracts` NuGet 包，已随仓库提供，无需联网还原。

用系统自带的 `csc.exe` 编译，在仓库根目录执行：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$sources = Get-ChildItem .\src\*.cs | ForEach-Object { $_.FullName }

& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ `
  /out:".\BatteryTray.exe" `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:"$fw\System.Management.dll" `
  /reference:".\lib\winrt\Windows.WinMD" `
  /reference:".\lib\winrt\Windows.Foundation.UniversalApiContract.winmd" `
  /reference:".\lib\winrt\Windows.Foundation.FoundationContract.winmd" `
  /reference:"$fw\System.Runtime.dll" `
  /reference:"$fw\System.Runtime.WindowsRuntime.dll" `
  /reference:"$fw\System.Runtime.InteropServices.WindowsRuntime.dll" `
  $sources
```

`BatteryTray.exe` 是编译产物，不纳入版本控制。

## 使用

双击运行，图标出现在通知区域，面板显示在时钟上方。右键托盘图标可打开设备状态窗口，查看每个设备的连接方式、电量和读取失败原因。

## 支持情况

| 设备类型 | 读取方式 | 状态 |
| --- | --- | --- |
| 标准 HID 设备 | HID Usage 0x06/0x20 电池字段 | 设备上报即可读 |
| 罗技（HID++ 2.0） | 0x1004 统一电池 / 0x1001 电压 / 0x1000 状态 | 键盘多为粗粒度档位，鼠标是否上报取决于固件 |
| BLE 设备 | GATT `0x180F` / `0x2A19` | 暴露标准电池服务的设备可读 |
| 私有 2.4G 协议 | 未实现 | 雷蛇、赛睿、海盗船等需按品牌单独适配 |

`IBatteryProvider` 是品牌适配的扩展点，新增协议实现该接口即可接入。

## 文档

- [版本说明](docs/版本说明.md)：逐版本的新增、修复、验证与遗留问题
- [审查报告](docs/审查报告.md)：第 1–8 轮代码审查记录（根因、处置、误报澄清）
- [文档索引](docs/README.md)：文档维护约定

## 目录结构

```
src/
  Program.cs                 入口、托盘图标、面板轮询与快照合并、状态窗口
  DevicePanelForm.cs         透明分层面板绘制（分层位图上传）
  BatteryProviders.cs        提供者接口、设备分类、标准 HID 电池读取
  LogitechBatteryReader.cs   罗技 HID++ 2.0 电量读取与档位解析
  Native.cs                  Win32 P/Invoke 声明
  AssemblyInfo.cs            程序集元数据
lib/winrt/                   WinRT 编译参考元数据
docs/
  README.md                  文档索引
  版本说明.md                逐版本变更记录与技术细节
  审查报告.md                第 1–8 轮代码审查记录
```

## 已知限制

- 私有 2.4G 协议未做自动逆向，只支持已适配的品牌。
- 部分罗技键盘固件只返回粗粒度档位（如 K270 固定返回 30%，对应「良好」），换电池后可能仍显示同一档。
- BLE 扫描每 30 秒一次、单次最长约 16 秒，首次启动时耳机电量可能短暂显示未知。
- 同型号双接收器按 `VID:PID` 合并，不区分左右。
- 副屏与竖向任务栏不显示面板。
