# 图片快速筛选（QuickFilter）

用于从大量图片中快速分类的桌面工具：指定一个源目录与两个目标目录后，逐张显示图片，通过鼠标拖拽手势或键盘完成筛选，并支持局域网安卓端同屏遥控（M3 里程碑，尚未实现）。

## 操作方式

| 操作 | 鼠标拖拽 | 键盘 | 效果 |
|------|---------|------|------|
| 分入左目录 | 按住图片左滑 | ← 或 A | 移动至左目标目录 |
| 分入右目录 | 按住图片右滑 | → 或 D | 移动至右目标目录 |
| 删除 | 按住图片上滑 | ↑ 或 W | 移动至"已删除"目录（不物理删除） |
| 跳过 | 按住图片下滑 | ↓ 或 S | 保留在源目录，进入下一张 |
| 撤销上一步 | — | Ctrl+Z | 文件移回源目录原位置 |

- 同名文件自动加序号（`photo.jpg → photo_1.jpg`），不覆盖、不丢失。
- 递归扫描源目录子目录，移动时保留相对路径结构。
- 会话状态保存在源目录 `.quickfilter-session.json`，异常退出后可续传（重不漏、不重复）。
- 支持 jpg/jpeg/png/webp/bmp/gif/heic（HEIC 需系统已安装 HEIF 图像扩展，微软商店免费）。
- 可导出筛选操作日志 CSV。
- 缩放查看：滚轮缩放（10%–800%，光标为中心）、缩放时拖拽平移、双击/Esc/按钮复位；放大状态下拖拽不再触发分类，请用键盘或按钮操作。
- 信息栏显示像素尺寸、文件大小、修改时间与 EXIF 拍摄时间（如有）。
- GIF 默认只显示第一帧，可在配置页勾选"播放 GIF 动画"。
- 快速预览模式：大图降采样至 960px 显示，切换更快（默认 1920px）。
- 性能：当前图片解码后预加载后 2 张（LRU 缓存 6 张），上万张图片时翻页流畅。
- 诊断：运行日志写入 `%LOCALAPPDATA%\QuickFilter\log.txt`；可用自检模式验证扫描/解码：
  `QuickFilter.App.exe --selftest <图片目录>`（扫描并解码前 5 张后退出，结果写日志）。

## 修复记录（2026-08-30 真机联调发现并修复）

| 问题 | 根因 | 修复 |
|------|------|------|
| 安卓端自动发现失效 | 自己广播的 discover-query 被回显接收，JSON 解析异常导致发现线程退出 | JSON 容错 + 仅接受 `type=="discover"` |
| 连接每 10 秒断一次 | 服务端收到 OkHttp ping 帧后回 pong 但返回 null，消息循环关闭连接 | ping→pong 后继续读帧；另加 5 秒状态心跳保活 |
| 同屏切图闪白屏 | 5 秒心跳每次都触发 Coil 重复加载同一 URL（crossfade 反复淡入）+ 无黑色占位 | 相同 URL 跳过加载 + 黑底 placeholder/error |
| 历史连接 | —— | 认证成功自动记录（ip/端口/PIN，最多 5 条），主界面一键快速重连 |

## 功能补充（2026-08-30）

- **继续上次会话（一键）**：程序启动后配置页**顶部**显示"▶ 继续上次会话"（含上次目录与进度/总数），点击直接恢复会话（进度在源目录实时持久化）；源目录框自动预填上次目录。
- **固定遥控 PIN 设置**：配置页"设置"区可填写固定 4-8 位数字 PIN（留空=每次启动随机）；**输入即生效**（300ms 防抖自动保存，也可点保存按钮）；设置保存在 `%LOCALAPPDATA%\QuickFilter\settings.json`。
- **桌面隐私模式**：勾选后电脑端**一律不解码图片**（零加载、零残留），筛选页只显示**进度 + 最近 12 条操作记录**；手机遥控端不受影响。
- **构建注意**：`dotnet publish`（单文件）与 `dotnet build` 共用 obj 目录会产生增量污染（exe 变为框架依赖模式导致启动失败）；模式切换后请 `Remove-Item obj\bin -Recurse` 后重新构建。
- 会话/设置 JSON 读取已支持大小写不敏感（手工编辑 JSON 更安全）。

## 打包发布

- **电脑端**：`dotnet publish src\QuickFilter.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\win-x64`
  产物：`dist\win-x64\QuickFilter.App.exe`（单文件，双击即用，无依赖安装）
- **手机端**：`gradle -p android assembleRelease`
  产物：`android\app\build\outputs\apk\release\app-release.apk`（已签名，可直接安装）
- 应用图标：蓝色渐变漏斗（筛选主题），素材来自 [icons8](https://icons8.com) 免费图标
  （透明度/用途满足其许可要求，出处：icons8.com），已生成 Windows ico 与安卓各密度 mipmap（见 `assets\`）。

## 局域网安卓遥控（M3）

电脑端运行程序后，界面底部（配置页/筛选页）显示遥控状态：`📱 遥控待连接：IP : 47900 · PIN xxxx`。

**安卓端**（`android/` 目录，Kotlin 原生，Android 8.0+）：

1. 安装 `android\app\build\outputs\apk\debug\app-debug.apk`
2. 手机与电脑连接**同一 Wi-Fi**
3. 打开"快速筛选遥控"App：自动发现电脑（也可手动输入 IP + 端口 47900）
4. 输入电脑界面上显示的 4 位 PIN → 连接成功
5. 手机上同屏显示当前图片：左右滑 = 左/右目录，上滑 = 删除，下滑 = 跳过，另有按钮可点按与撤销
6. 断线后自动每 3 秒重连；电脑端流程不受手机影响（可继续用键盘/鼠标）

**注意**：首次使用若 Windows 防火墙弹窗请勾选"允许访问"（专用网络）；手机端构建：
`gradle -p android assembleDebug`（需 Android SDK；SDK 已安装在 C:\Android \| JDK 21 已就绪）。

**已在模拟器上完成全流程联调验证（2026-08-30）**：手动 IP（模拟器用 10.0.2.2）→ PIN 1234 → 同屏显示 → 左滑移动文件 ✓ 上滑删除 ✓ 下滑跳过 ✓ 自动重连（断线 3s 恢复且状态同步）✓ 撤销（文件回源）✓。
模拟器联调命令：

```
# 桌服端（后台）：固定 PIN 1234，自动复制 20 张图建临时会话
QuickFilter.App.exe --serve "示例源文件夹"
# 模拟器（已配置 AVD qf：emulator -avd qf -no-window ...）
adb install -r android\app\build\outputs\apk\debug\app-debug.apk
```

> 说明：模拟器 NAT 下 UDP 发现广播不可达（真机同 Wi-Fi 正常），联调用手动 IP；服务端协议自测：
> `QuickFilter.App.exe --server-test <图片目录>`（exit 0 = 全部通过）。

协议：UDP 47800 发现广播 / WebSocket `ws://ip:47900/ws` 命令 / HTTP `GET /image?idx=N` 图片通道（协议见需求文档 §6）。

## 构建与运行

前置：Windows 10 2004+ / Windows 11，.NET 8 SDK。

> 说明：WinUI 3 的 XAML 编译器无法在含中文的路径下工作（WMC9999），
> 因此仓库通过 [Directory.Build.props](Directory.Build.props) 将 obj/bin 重定向到
> `I:\Local Project\VS\qf-build`（ASCII 路径），源码照常放在中文目录，可直接构建。

```powershell
dotnet build QuickFilter.sln -c Debug
dotnet run --project src\QuickFilter.App -c Debug
# 已编译产物：I:\Local Project\VS\qf-build\app\bin\win-x64\QuickFilter.App.exe
```

运行测试：

```powershell
dotnet test QuickFilter.sln
```

## 快速试用（示例数据）

仓库内置示例素材，可直接体验完整流程：

- `示例源文件夹\` — 测试图片（15 张程序生成的渐变示例图：jpg/png/bmp/gif、200×150 到 2560×1920 不等，含 `风景\`、`人物\` 子目录）＋您自己放入的其他图片
- `示例左目录\`、`示例右目录\` — 预建的空目标目录

操作步骤：

1. `dotnet run --project src\QuickFilter.App -c Debug`
2. 源目录选 `示例源文件夹`，左/右目标分别选 `示例左目录`、`示例右目录`，已删除留空
3. 开始筛选 → 按住图片左滑/右滑/上滑/下滑（或 ←/→/↑/↓、A/D/W/S）进行分类
4. 上滑的图片会移入自动创建的 `示例源文件夹\已删除`，同名文件自动加序号
5. 结束后导出 CSV 查看全部操作记录；再次用同一源目录开始会提示"继续上次会话"（续传）

### 上万张压测

- `示例大集合\` — 10000 张 16×16 小图 + 2 张大图（由脚本生成），可直接用作源目录体验上万张筛选的流畅度。
- 重新生成：`powershell -File scripts\生成万张测试图片.ps1`
- 已知指标（单元测试保证）：扫描 10000 个文件 < 15s（实测约 1s）；翻页为后台解码，UI 不阻塞。

## 项目结构

```
QuickFilter.sln
src/
  QuickFilter.Core/        核心逻辑：扫描/排序/会话/移动/冲突重命名/撤销/日志（无 UI 依赖，可单测）
  QuickFilter.App/         WinUI 3 桌面程序（解包部署，可直接运行）
tests/
  QuickFilter.Core.Tests/  核心逻辑单元测试（xUnit）
```

## 里程碑进度

- [x] M1 桌面端 MVP：配置、扫描、单张显示、鼠标手势 + 键盘操作、移动/删除、冲突重命名、撤销、进度计数、CSV 导出、会话续传
- [x] M2 桌面端完善：缩放/平移与复位、EXIF 拍摄时间、GIF 首帧/动画开关、快速预览模式、±2 张预加载、上万张扫描性能验证（含 1 万张示例集）
- [x] M3 安卓端：自动发现、PIN 配对、同屏显示、滑动/按钮遥控、撤销、断线自动重连（桌面端服务 E2E 自测通过）
- [ ] M4 联调压测与打包发布

详细设计见 [快速筛选图片软件-开发需求文档.md](快速筛选图片软件-开发需求文档.md)。
