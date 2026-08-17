# DshPetDesktop - Misaka desktop pet + DSH Controller

御坂美琴桌面宠物（独立版）+ DSH 服务控制器一体。透明、无边框、置顶的
浮动窗口，在浏览器之外也能看到——不再依赖 DeepSeek Harness 插件。

## 使用

双击 `Pet.exe` 即可（需要 .NET Framework 4.x，Windows 7+ 自带）。

| 操作 | 效果 |
| --- | --- |
| 拖动 | 移动宠物（位置自动记住）；拖动期间动画冻结，松手后从剩余帧继续 |
| 左键单击 | 随机反应（跳跃 / 挥手 / 奔跑） |
| 宠物上右键 | 宠物面板：播放动画（9 轨）+ 宠物设置（WPF 圆角菜单，主题跟随系统） |
| 托盘图标 | DSH Controller 完整面板：服务控制 + 宠物设置（WPF 圆角菜单） |

宠物正上方有一个状态徽章（独立小窗，水平居中悬浮于头顶 4px），
实时显示 DSH 活动状态（由 dsh-pet 网页宠物推送，每 2 秒轮询同步）：

| 状态 | 圆点颜色 | 标签 |
| --- | --- | --- |
| 待机 | 灰 | 待机 |
| 思考中 | 蓝 | 思考中 |
| 使用工具 | 紫 | 工具 {工具名}（如 工具 web_search，过长自动截断） |
| 整理回复 | 青 | 整理回复 |
| 等待输入 | 琥珀 | 等待输入 |
| 成功 | 绿 | 成功（4 秒后自动消失） |
| 失败 | 红 | 失败（4 秒后自动消失） |
| 服务未运行 | 灰 | 离线 |

徽章窗口点击穿透、跟随宠物移动（走路/拖动/跳跃均同步），
宠物隐藏时同步隐藏。

## DSH Controller 集成

托盘图标显示 DSH 服务状态（运行中/已停止，图标随状态切换），
左键/右键点击托盘均弹出 Controller 风格面板：

- 状态行：运行状态 + PID + 端口 + WebUI 地址（每次弹出实时查询）
- 启动 DSH / 停止 DSH / 重启 DSH / 打开 WebUI
- 配置...：编辑启动命令 / WebUI 地址 / 工作目录 / 启动后自动打开 WebUI
  （与 `Workshop\config.json` 共享，和旧 dsh-tray 控制器同一份配置）

服务操作逻辑与 `Workshop\dsh-tray.ps1` 一致：
端口监听检测（netstat）→ 启动（cmd /c，日志重定向到 logDir）→
等待就绪（30s）→ 自动打开 WebUI；停止用 taskkill /T /F 杀进程树。

## 宠物功能

宠物右键弹出宠物专属面板（不含 DSH 控制，与托盘面板区分）：

- **闲时动画**：触发间隔（关闭 / 15 秒 / 30 秒 / 60 秒 / 120 秒 / 300 秒）。
  待机时以基础 idle（呼吸循环）为主，仅当间隔到期才随机表演一轮闲时
  动画（waving / waiting / 左右走路跑步），播完自动回到基础 idle。
  网页宠物的闲时动画推送会被节流，不会每 8 秒打断待机；
  任务类动画（running / review / jumping / failed）仍实时同步。
- **播放动画...**：9 轨动画直接选择播放（idle / running-right /
  running-left / waving / jumping / failed / waiting / running / review）
- **尺寸**：25% / 50% / 75% / 100% / 125% / 150% / 175% / 200%
- **移动**：开 / 关（走路/跑步轨道播放时横向移动）
- **移速**：20 / 40 / 60 / 80 / 120 / 160 / 240 / 320 px/s
- **鼠标穿透**：开 / 关（WS_EX_TRANSPARENT，穿透后靠托盘菜单切换）
- **置顶 / 开机启动 / 显隐**：开关项

- 待机以基础 idle（呼吸循环）为主，仅当「闲时动画」间隔到期才随机表演
  一轮（waving / waiting / 左右走路跑步），播完自动回到基础 idle；
  播放走路轨道且移动开启时宠物会在屏幕内横向移动，碰到屏幕边缘自动掉头
- 左键单击随机反应（跳跃 / 挥手 / 奔跑）
- 跳跃动画会让宠物上下跳动（峰值约 36% 宠物高度），播完落回原位；
  走路/跑步/跳跃动画无论本地触发还是由 DSH 网页宠物推送都会位移
- 拖动宠物时动画暂时冻结，松手后从剩余帧继续播放，不会被拖动打断
- 配置存于 `%APPDATA%\DshPetDesktop\config.txt`
  （位置 / 置顶 / 尺寸 / 移动 / 移速 / 穿透 / 可见性）
- 动画轨道与 dsh-pet 插件共用同一精灵表契约
  （8 列 × 9 行、192×208 单元、1536×1872），帧定义见 `Pet.cs` 的 `TRACKS`

## DSH 网页宠物联动（双通道）

宠物通过**两条通道**获取 DSH 状态，任一可用即可同步：

1. **轮询 DSH host**（主通道，推荐）：Pet 每 2 秒读取
   `http://127.0.0.1:3080/api/pet/state`（dsh-pet 插件的官方状态端点），
   解析 animation / phase / bubble 驱动动画与徽章。**不依赖浏览器页面
   是否打开**，也**不依赖本地推送端口**——3080 是 DSH 服务本身，占用即
   服务不可用，不存在"端口被其他程序抢走"的问题。
2. **本地 HTTP 推送**（即时通道）：保留 `127.0.0.1:18787`，dsh-pet 网页
   宠物动画变化时推送（带 phase 与工具名），比轮询更即时；该端口被占
   时轮询通道兜底，功能不受影响。

| 端点 | 说明 |
| --- | --- |
| `GET /health` | 存活检查：`{"ok":true,"track":"idle","pid":...}` |
| `GET /state` | 当前状态：track / row / frame / clickThrough / visible / sizePct / moveEnabled / moveSpeed |
| `GET /play?track=jumping&phase=done&label=...` | 推送播放指定动画（9 轨全支持）+ 状态徽章信息 |
| `GET /config?clickthrough=1&sizePct=75&move=1&speed=120` | 远程切换全部设置 |
| `GET /menu?sub=size` | 远程唤起 Controller 面板（sub: size/speed/play/idle） |

浏览器直接 `fetch` 即可（CORS 已放开）。

## 重新构建

```powershell
csc.exe /nologo /target:winexe /codepage:65001 /optimize+ /out:Pet.exe `
  /lib:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF `
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:Microsoft.CSharp.dll `
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:WindowsBase.dll `
  /r:PresentationCore.dll /r:PresentationFramework.dll Pet.cs
```

无 NuGet、无网络依赖，纯系统自带工具链（csc + .NET Framework）。

## 更换宠物

用任意 8 列 × 9 行、192×208 单元的精灵表 PNG 替换 `misaka.png` 即可。
程序启动时会自动逐行检测每行实际帧数（跳过透明尾帧），
`TRACKS` 帧数会按检测结果自动裁剪。

素材来源：petdex.dev（misaka-mikoto-premium）。
DSH 状态图标来源：`Workshop\resources\dscfgon.ico` / `dscfgoff.ico`
（已复制到本目录 `resources\`）。
