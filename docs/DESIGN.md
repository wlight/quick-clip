# QuickClip 详细架构与功能设计规范

本文档为 QuickClip Windows 剪贴板工具的详细技术规范与实现设计。

---

## 1. 核心架构设计

```mermaid
graph TD
    subgraph 系统交互层
        HK[全局热键服务<br>RegisterHotKey 优先 + WH_KEYBOARD_LL 回退<br>静默拦截 Win+V] -->|唤醒/切换| UI[浮动主窗口 Fluent UI]
        CBM[Win32 剪贴板监听<br>AddClipboardFormatListener] -->|变更通知| PIPE[剪贴板处理流水线]
    end

    subgraph 核心处理流水线
        PIPE --> DEDUP[去重与防抖判断]
        DEDUP --> PARSER[数据解析器: 文本/图片/链接/文件]
        PARSER --> SMART[智能检测: 离线二维码识别 / 链接清洗]
        SMART --> DB[(本地 SQLite<br>条数上限淘汰)]
    end

    subgraph 界面与交互层
        UI --> SEARCH[即时搜索引擎: 模糊匹配 + 拼音首字母]
        UI --> LIST[条目列表: 文本/缩略图/标签]
        UI --> ACTIONS[快捷动作: 离线二维码生成 / Windows 原生 OCR / 纯文本粘贴]
        UI --> PASTE[模拟击键粘贴 SendInput / SetClipboard]
    end
```

---

## 2. 模块技术实现细节

### 2.1 全局热键拦截（替换系统 Win+V）
`HotkeyService` 采用“`RegisterHotKey` 优先、低级钩子回退”的双层策略：

- **全局纯文本粘贴（固定 `Ctrl+Shift+V`，可启用/禁用）**：优先调用 `RegisterHotKey` + 隐藏消息窗口（`HwndSource`），由系统可靠投递 `WM_HOTKEY`；组合被其他程序占用导致注册失败时，自动回退到低级钩子检测。
- **`Win + V`（固定，不可修改）**：系统开启剪贴板历史时该组合已被 Shell 注册（`RegisterHotKey` 返回 1409），因此统一由 `WH_KEYBOARD_LL` 钩子接管，采用「Win 键状态机」：
  - **吞掉 Win 键按下**（返回 1），开始菜单根本不会弹出，从根源消除“全屏闪一下”；
  - V 键进入和弦判定：按下/弹起均拦截（返回 1），系统原生剪贴板历史不会弹出，并触发面板切换；
  - **重放保留系统快捷键**：若 Win 之后按下的是其它键（Win+E 等），钩子重放注入「Win + 该键」完整和弦（`SendInput`），系统仍可识别原始快捷键；弹起时再注入 Win 弹起保持键状态平衡；
  - **单独按 Win**：仅在弹起时重放一次 Win 按下/弹起，恢复系统“打开开始菜单”行为。
- 钩子运行在独立后台线程（`GetMessage` 消息泵），回调通过 `Dispatcher.BeginInvoke` 切回 UI 线程；自身 `SendInput` 注入的按键带 `LLKHF_INJECTED` 标志，钩子据此跳过，防止与合成 `Ctrl+V` 互相回环。
- **按键自动重复过滤**：按住 `Win+V` 或纯文本组合时系统会产生连续重复的 `WM_KEYDOWN`，钩子用按下标记（`_winVKeyDown` / `_plainPasteKeyDown`）只响应首次按下，弹起时复位，避免长按导致面板反复切换或连续粘贴。
- **前台置前兜底（解决“呼不出”）**：热键唤起时 Windows 前台锁可能拒绝 `SetForegroundWindow`（面板显示在其他窗口后面或未获得焦点）。`ShowWindow` 采用「临时 `HWND_TOPMOST` 置顶 → `SetForegroundWindow` → 恢复 `HWND_NOTOPMOST`」序列，强制把面板带到最前并激活，规避前台锁限制。
- **管理员窗口兜底（UIPI）**：`WH_KEYBOARD_LL` 钩子无法收到「以管理员权限运行」窗口的键盘输入（Windows UIPI 权限隔离），此时 `Win+V` 会漏给系统弹出原生剪贴板历史。`SystemClipboardGuard` 以 120ms 低频轮询前台窗口，命中「系统剪贴板历史」（类名 `Windows.UI.Core.CoreWindow` + 标题含「剪贴板历史 / Clipboard history」）后注入 ESC（`SendInput` 不受 UIPI 影响）并发送 `WM_CLOSE` 关闭它，再唤起 QuickClip 面板；同一窗口句柄 5 秒冷却防抖。钩子正常接管时该窗口不会出现，守卫零干扰。

### 2.2 剪贴板监听与防抖
- 使用 `AddClipboardFormatListener(IntPtr hWnd)`，`hWnd` 为服务自建的隐藏消息窗口（`ClipboardMonitor` 内部创建，不依赖主窗口）。
- 窗口消息循环处理 `WM_CLIPBOARDUPDATE (0x031D)`。
- 粘贴自身回填时置位 `_isSelfPasting = true`，并在捕获事件时过滤，避免循环捕获。

### 2.3 纯离线智能互转引擎
1. **二维码离线解析**：
   - 使用 `ZXing.Net` 库；
   - 捕获图片优先读剪贴板 PNG；WPF `GetImage()` 的 DIB 常带全 0 Alpha，须按不透明写盘，否则缩略图透明且 ZXing 解不出；
   - 捕获到图片后，在后台线程中将其送入 `BarcodeReader.Decode(bitmap)`；
   - 若解析成功，提取 URL/字符串，写入 `qr_content`（卡片 `HasQr` / `QrText`）。
   - 列表只标「已识别二维码」并保留原图，不把解码正文叠在卡片上；悬停预览展示 `QrText`。
   - 列表同一按钮：文本/链接为「转二维码」；已识别二维码图图标改为解析，悬停仍预览原图 + 解码正文，点击把 `QrText` 写成纯文本并抑制捕获。
2. **二维码离线生成**：
   - 使用 `QRCoder` 库；
   - 对任意选中的文本或 URL，在内存中直接调用 `QRCodeGenerator.CreateQrCode(...)` 渲染高清位图，无需网络请求。
3. **Windows 原生离线 OCR**：
   - 引用 WinRT 原生库 `Windows.Media.Ocr.OcrEngine`；
   - 将剪贴板位图转换为 `SoftwareBitmap`，直接本地调用 `engine.RecognizeAsync(softwareBitmap)` 毫秒级提取文本。
4. **可选 PP-OCRv6 离线包**（不打进安装包）：
   - 设置页下载 Small / Medium，或指定自备 `det` + `rec` + 字典目录；
   - 模型落在 `%LOCALAPPDATA%\QuickClip\ocr\`；失败回退系统 OCR。
   - 下载进度与失败原因保留在设置页；HTTP 状态、文件大小、SHA256 校验写入 `debug.log`。
5. **OpenAI 兼容视觉接口**：
   - 一组地址 + 模型 + 可选 API Key；默认按 chat/completions。URL 若仍是旧版 `/api/generate` 则走 Ollama 原生。

### 2.4 存储结构与条数淘汰
- 本地数据库：`quickclip.db`（SQLite）
- 数据表定义：
  ```sql
  CREATE TABLE IF NOT EXISTS clipboard_items (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      content_type TEXT NOT NULL,          -- 'text', 'image', 'link', 'file'
      text_content TEXT,                   -- 纯文本内容 / 链接 / 文件路径
      preview_path TEXT,                   -- 缩略图路径 (图片类型)
      qr_content TEXT,                     -- 解析出的二维码内容
      char_count INTEGER,                  -- 字符计数或文件大小
      is_pinned INTEGER DEFAULT 0,         -- 0: 正常, 1: 收藏置顶
      created_at DATETIME DEFAULT CURRENT_TIMESTAMP
  );

  CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_items(created_at);
  ```
- **自动淘汰**（置顶豁免）：总数超过 `MaxHistoryItems`（默认 233，可配置 50～2000）时删最旧非置顶。写入时立即裁条数；定时任务（启动约 8 分钟后，之后每 60 分钟）再裁一次并清理孤儿预览。不按时间过期。
- **捕获体积（只影响是否记历史，绝不改写系统剪贴板）**：
  - 文本/链接 &gt; 2M 字符 → 不入库  
  - 图片像素 &gt; 40MP 或落盘 PNG &gt; 30MB → 不入库（删临时预览）  
  - 文件：只记路径，不拷贝本体、不进 BLOB；路径列表文本过长则不入库  
  - 超限时用户仍可把内容粘贴到其他程序

---

## 3. UI 界面布局与键盘交互流

### 3.1 界面线框图（面板形态仿 Windows 11 剪贴板历史）
```
+-------------------------------------------------------------+
|  🔍 搜索剪贴板内容（支持拼音首字母）          [全部 ▾]   [📌] |  ← 搜索行
+-------------------------------------------------------------+
| +---------------------------------------------------------+ |
| | 文本内容最多三行，超出…                              📌 ⋯ | |  ← 单列宽卡片
| | 1 📝 文本 · 09:30                                       | |     文本三行截断
| +---------------------------------------------------------+ |
| +---------------------------------------------------------+ |
| | [缩略图等比缩放，限高 96]                            📌 ⋯ | |  ← 图片卡片
| | 2 🖼️ 图片 · 09:31                                       | |     置顶=金边
| +---------------------------------------------------------+ |
| +---------------------------------------------------------+ |
| | C:\Users\…\报告.docx（链接 / 文件：显示名称或全文） 📌 ⋯ | |  ← 悬停露出
| | 3 📁 文件 · 09:33                                       | |     二维码/OCR/…
| +---------------------------------------------------------+ |
+-------------------------------------------------------------+
| 就绪 · 仅显示最近 150 条                [?]  [🗑 清空]  [⚙]  |  ← 命令栏
+-------------------------------------------------------------+
```
- 无标题栏：面板在 `Win + V` 时于**鼠标位置**浮出（默认光标右下，贴边自动翻转到左上，并钳制在光标所在显示器工作区内；拖动过面板后改为在记忆的位置浮出，见下一条），淡入 + 8px 上浮约 150ms；点外即隐（图钉置顶时除外），Esc 关闭。
- 面板可拖动：空白处（卡片正文、搜索行留白、命令栏空白、四周留白）按下左键即可拖动，位移超过 3px 才算拖（更小的位移仍按点击处理，点条目选中不受影响）；交互控件（搜索框、筛选下拉、图钉、卡片右上角悬浮按钮、底部按钮、滚动条）与二维码 / OCR 浮层上的按下不参与拖动，拖动期间也不弹悬停预览。松手后钳制在当前显示器工作区内，并把位置写进设置（`PanelFollowCursor=false` + `PanelLeft` / `PanelTop`）：此后每次浮出都在该位置（跨重启），状态栏给提示；设置页的「面板在鼠标处浮出」勾回去即恢复跟随鼠标。
- 点外收起统一走 `HideIfFocusLost`（判定）与 `AutoHidePanel`（执行）：置顶（图钉）、二维码 / OCR 浮层、条目「…」菜单、悬停预览、设置窗打开时不隐藏（这组理由由 `HasBlockingOverlayOrPin` 提供，**不含**「前台是不是自己」——鼠标按下时前台窗口仍是自己，点击尚未派发）；前台窗口属于本进程自身（确认框、另存为对话框、自身弹层）时同样不隐藏。
- 触发有三条路径：① 全局鼠标钩子 `WH_MOUSE_LL` → `HotkeyService.PointerDownOutside` → `OnPointerDownOutside`：鼠标按在本进程窗口之外（`WindowFromPoint` 判进程，注入点击不计）立刻收起，**不依赖窗口是否激活**，这是仿 Win11 剪贴板历史的主路径；② 窗口 `Deactivated`：面板确实激活过时即时收起，热键唤起后有 1.5s 宽限（前台锁拒绝 Activate 会立刻打 Deactivated）；③ 120ms 看门狗 `OnOutsideClickWatchTick`：前台窗口换成了别的窗口（`IsForegroundTakenAway`，即不再是唤起瞬间那个 `_foregroundBeforeShow`）时兜底收起，覆盖「面板从未激活、只靠事件会漏」的情况。托盘点按与点外收起按 320ms 合并，避免面板刚被点外收起又被托盘点按重新打开；预览浮层关闭后补一次判定，避免面板滞留。
- 条目「…」菜单承载单个条目的全部动作：复制 / 粘贴到当前窗口 / 固定 / 二维码 / OCR / 另存为图片 / 复制文件全路径 / 删除。
- 条目列表用 `StackPanel` 单列自上而下堆叠（仿 Win11 剪贴板历史的宽卡片）：图片等比缩放限高、文本三行截断，序号 / 类型 / 时间作为一行元信息压在卡片底部；悬停时卡片右上角浮出图钉、二维码、OCR 与「…」菜单（浮层底色跟随卡片状态，压在文字上不糊）。该面板不虚拟化（单列 `StackPanel`），一次最多渲染 `MainViewModel.MaxRenderedItems = 150` 条，超出部分靠搜索与筛选定位，避免浮出时批量解码缩略图。
- 搜索框外框由独立 `Border` 绘制（内层 `ui:TextBox` 去边框、透明底，只负责输入），放大镜与输入框按列排布而非绝对定位叠放，避免模板描边、图标与占位文字三者互相错位；聚焦时描边转为强调色。搜索框宽度固定 200 且左对齐，右侧留白（多余空间由外层 `Grid` 裁掉），搜索框不铺满整行、也不会挤压筛选与置顶。搜索框不挂 `ToolTip`：面板浮出时搜索框会自动获得焦点，WPF 会因此把该元素的提示气泡自动弹出来（默认贴光标），鼠标落在搜索框右半边时气泡正好压住右侧的筛选与置顶，5 秒后才自行消失——拼音首字母提示因此改放右下角「?」帮助气泡，搜索行里不会再自动冒出任何气泡。
- 顶部两个图标化控件都覆盖了 WPF-UI 的默认内边距：置顶按钮 `Padding="0"`（否则 15px 图标在 34×34 里被挤变形），筛选下拉 86 宽 + `Padding="10,0"`（74 偏窄，箭头与「全部」之间只剩约 20px）。

### 3.2 键盘快捷键体系
- `Win + V`：唤起 / 隐藏主面板（全局拦截，替换系统原生剪贴板历史）。
- `Ctrl + Shift + V`：任意程序中以纯文本粘贴（全局可用，去掉原格式）。
- `1 ~ 9`：快速将对应序号项填充并粘贴至目标窗口。
- `↑ / ↓ / ← / →`：在条目列表中移动光标（单列宽卡片布局，四个方向键都按条目移动；将来换回多列网格需恢复「↑/↓ 跨行、←/→ 同行」的列数计算）。焦点在搜索框时，`←` / `→` 仅在光标已抵文本边界（左端 / 右端）且无选区时接管，否则仍由文本框移动光标。
- `Enter`：粘贴当前选中项。
- `Shift + Enter`：以纯文本粘贴当前选中项（去掉原格式）。
- `Ctrl + C`：复制当前项到系统剪贴板并自动置顶到列表首位。
- `Ctrl + P`：窗口置顶 / 取消置顶（前端固定，失焦不隐藏）。
- `Delete`：从历史中删除当前项。
- `Esc`：关闭并隐藏窗口。


---

## 4. 实际实现说明（v1.0）

### 4.1 全局热键服务线程模型
- `HotkeyService.Start` 在 UI 线程创建隐藏消息窗口（`HwndSource`），用于接收 `RegisterHotKey` 的 `WM_HOTKEY`；随后启动独立后台线程安装 `WH_KEYBOARD_LL` 钩子并运行标准 Win32 消息泵（`GetMessage` 循环）。
- `RegisterHotKey` 必须在创建隐藏窗口的 UI 线程调用。后台线程（如更新检查写时间戳）触发 `Settings.Changed` 时若直接注册，会得到 **1408**（`ERROR_WINDOW_OF_OTHER_THREAD`），并误把已成功的注册标成失败。时间戳保存不再广播 Changed；其它设置变更也切回 UI 线程再注册。
- `RegisterHotKey` 注册结果记录到日志：`Win+V` 在开启剪贴板历史时必然返回 **1409**，属预期行为，此时由钩子接管；**1408 不是系统占用**。其余热键注册失败时同样回退钩子匹配（按设置中的修饰键 + 主键判断）。
- 热键唤起后有 1.5s `HWND_TOPMOST` 宽限（`HotkeyTopmostGrace`，与 `_shownAt` 配合）并调度 `ScheduleHotkeyTopmostDemote` 回收层级，避免 Activate 被前台锁拒绝后立刻藏回托盘；宽限期内 `Activated` 不回写 z-order、`Deactivated` 不触发收起。宽限吃掉的真实点外由鼠标钩子（`OnPointerDownOutside`）与前台看门狗补齐。
- 钩子线程同时安装 `WH_KEYBOARD_LL` 与 `WH_MOUSE_LL`，两者共用同一消息泵。鼠标钩子回调会阻塞全系统鼠标消息，因此只做 `WindowFromPoint` + `GetWindowThreadProcessId` 两个 Win32 判定（不做可能卡顿的托管调用），命中后经 `_uiDispatcher.BeginInvoke` 切回 UI 线程再做收起判定。
- 卸载时依次 `UnregisterHotKey`、`UnhookWindowsHookEx`、`PostThreadMessage(WM_QUIT)` 退出消息泵，避免线程泄漏。
- 设置变更（`SettingsService.Changed`）会触发 `HotkeyService.ApplyHotkeys` 重新注册，热键即时生效。
### 4.2 SendInput 结构体（x64 易踩坑）
- Win32 `INPUT` 结构体在 x64 下为 **40 字节**（联合体需包含 `MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT` 三成员）。
- 若联合体只声明 `KEYBDINPUT`，结构体只有 32 字节，`SendInput` 会返回 0 且 `GetLastError() == 87 (ERROR_INVALID_PARAMETER)`，模拟击键**静默失败**。
- 项目中的 `NativeMethods.SendCtrlV()`（粘贴回填）已按 40 字节布局实现。

### 4.3 剪贴板监听与防循环
- 使用 `AddClipboardFormatListener` 挂到自建隐藏消息窗口，处理 `WM_CLIPBOARDUPDATE (0x031D)`；监听在 `AppServices.Initialize` 阶段建立，不能挂主窗口——开机自启是静默驻留、主窗口不显示（HWND 未创建），挂主窗口会导致「不按一次 Win+V 就不记录复制内容」。
- 自身粘贴回填时 `PasteService.IsSelfPasting` 置位 600ms，流水线据此过滤，避免「复制 → 入库 → 回填 → 再捕获」死循环。

### 4.4 诊断日志
- `DebugLog` 默认写入 `%LOCALAPPDATA%\QuickClip\debug.log`；单文件超过 2MB 滚动为 `debug.log.1` / `debug.log.2`（合计约 6MB），滚动失败时硬上限 8MB 暂停写入。
- 设置 `QUICKCLIP_DEBUG_LOG=1` 可额外记录逐键等详细调试信息。
- 日志覆盖：钩子安装结果 / Win+V 与 Ctrl+Shift+V 拦截 / 窗口切换 / OCR 模型下载（HTTP、大小、校验）/ 视觉 OCR 请求元数据（不含密钥与图片）/ 异常堆栈。
- URL 只记 host+path，去掉 query（避免 CDN `auth_key`）；禁止写入 API Key 与剪贴板正文。
- 全局异常兜底：UI 线程异常标记已处理不崩溃，进程级与未观察任务异常记录完整堆栈。

### 4.5 渲染环境自动降级（远程/虚拟显示）
- 问题：在远程控制 / 虚拟显示驱动环境（向日葵 OrayIddDriver、Huawei Virtual Display、Microsoft Remote Display Adapter 等）下，WPF 硬件渲染合成可能黑屏（窗口纯黑，UIA 元素树正常）。
- 检测：`RenderEnvironment.IsRemoteOrVirtualDisplay()` 枚举 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}` 显卡驱动描述，匹配 virtual/idd/oray/remote display 等关键字（子键逐个 try-catch，避免无权限子键中断检测）。
- 降级：检测命中时 `RenderOptions.ProcessRenderMode = SoftwareOnly`，背景材质改为 `WindowBackdropType.None` 并使用不透明深色背景（`#1B1B1F`），避免黑屏并保持深色主题可读。
- 正常物理桌面环境不受影响，仍使用 Mica / Acrylic 半透明材质。

### 4.6 设置持久化与自启动
- **设置持久化**：`SettingsService` 将设置写入 `%LOCALAPPDATA%\QuickClip\settings.json`（热键组合、是否启用、开机自启动、窗口置顶），JSON 读取不区分键名大小写，文件损坏时回退默认值。
- **窗口置顶**：主窗口 `Topmost` 与失焦自动隐藏由 `WindowAlwaysOnTop` 设置控制，`Ctrl+P`、标题栏图钉、设置窗口三处入口共享同一来源，变更经 `SettingsService.Changed` 广播即时生效。
- **开机自启动**：`AutoStartService` 读写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（`QuickClip` 值），无需管理员权限；启动时以注册表为准与设置文件对齐。托盘勾选与设置窗口共用同一来源。
- 剪贴板监听、热键、粘贴均在当前用户会话内完成，**不需要管理员权限**。

### 4.7 更新机制
- 版本号来自程序集（`csproj <Version>`，打 tag 时由 GitHub Actions 注入）。
- **双渠道**：绿色自包含 `QuickClip.exe`；安装包 `QuickClip-Setup-win-x64.exe`（framework-dependent，需 .NET 8 Desktop Runtime）。安装目录写入 `QuickClip.installed` 以识别渠道。
- **静默检查**：启动约 90 秒后查询 `releases/latest`，之后每 24 小时最多一次。设置 `AutoCheckUpdates` 默认开，关闭后不访问 GitHub。失败只写日志。
- **下载**：按渠道匹配 asset（安装包名含 `Setup`；绿色版优先 `QuickClip.exe`），校验 HTTPS 主机为 GitHub，写入 `%LOCALAPPDATA%\QuickClip\updates\`，不自动覆盖正在运行的进程。
- **安装**：托盘气泡 / 设置同一按钮。安装版启动 Setup；绿色版打开 `%LOCALAPPDATA%\QuickClip\updates\` 并选中已下载的 exe，由用户退出后自行替换（不自动覆盖正在运行的单文件）。
