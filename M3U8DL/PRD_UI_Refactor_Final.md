# M3U8 批量下载器 · UI 重构增量 PRD（定稿 v1.0）

> 定稿说明：本文已吸纳 team-lead 对原第 9 节 5 个问题的全部拍板结论，并修正真实任务数据基线（18 条 / 11091 字节）。未提及的行为一律保持现状。

## 0. 项目信息

| 项 | 内容 |
|---|---|
| Language | 中文（zh-CN） |
| Programming Language | C# / .NET 10 / WinForms（`net10.0-windows`），零 Python 依赖，自包含单文件 exe，不新增 NuGet |
| Project Name | `m3u8_batch_downloader_ui_refactor` |
| 文档性质 | **增量 PRD**：仅描述本次变更，不重写既有产品文档 |
| 原始需求 | 仿照参考图重构 UI 设计，更现代、更简洁、操作更便捷；审查代码 bug 并修复；优化程序框架、去除冗余；去除多余无用选项 |
| 参考设计语言 | 极浅灰蓝底 + 白色圆角卡片 + 靛蓝主色 + iOS 开关 + 编号徽标 + 空态面板 + 底部依赖状态点 |

**代码勘察补充（与简报的差异，按代码为准）**：基础选项区实际是 **15 个复选框**（非 16），性能区 **4 个数字框**，另有「同时下载数量」数字框在按钮行（合计 5 个）。`Theme` 实际 17 个颜色常量，仅 `Border` 确认零引用。`Program.cs` 的 `--dump` 与 `MainForm.DumpLayout()` 分支确为调试遗留。

---

## 1. 目标与非目标

### 目标

1. **视觉一致性**：新界面严格复用参考图的设计语言（卡片、圆角、编号徽标、开关、空态、状态栏），用户首次打开即可理解"输入 → 执行 → 结果"三段式流程。
2. **降低操作成本**：常用操作（粘贴任务、开始/停止、重试失败）在 1 次点击内可达；低频配置统一收进「更多设置」，主界面不再出现 15 个复选框网格。
3. **可靠性回归**：修复 6 项已确认 bug，且 199 项自动化测试保持全绿，用户真实 `tasks.json`（18 条任务，11091 字节）零改动。

### 非目标

1. 不重写下载引擎逻辑、命令构造、成品识别与重试策略（`Engine.cs` / `Core.cs` 行为保持不变，仅允许删调试代码与冗余常量）。
2. 不实现深色主题、多语言、云同步、任务导入导出新格式。
3. 不破坏 `tasks.json` 的 camelCase 结构与 Python 版双向兼容性；**不向 `tasks.json` 增加任何字段**。
4. 不迁移技术栈（不引入 WPF / Avalonia / 任何 NuGet 包）。
5. 不改变任务行格式 `name|url|referer|ua|cookie|origin|x-cdn-auth`。
6. 不实现「切换颜色」的深浅主题功能（P2 仅显示禁用占位，见第 2、9 节）。

---

## 2. 信息架构（新窗体完整区域树）

设计原则：**左栏只做输入与最常用的全局开关，右栏只做结果观察与行级操作，底部只做状态**。`Form.ClientSize` 默认 1280×800，MinimumSize 1120×720。

```
MainForm
├─ TopBar（通栏，白底，高 64）
│  ├─ AppIcon（app.ico，32×32）
│  ├─ TitleBlock：主标题「M3U8 批量下载器」16px/600；副标题「粘贴链接，批量下载」11px/#9CA3AF
│  └─ RightActions：
│     ├─ ShortcutHint（灰字 11px）：「Ctrl+V 粘贴 · Ctrl+Shift+C 复制」
│     ├─ GhostButton「切换颜色」——【已拍板】P2：显示但禁用，tooltip「即将推出」，不做深浅主题
│     └─ GhostButton「设置 ⚙」→ 打开 SettingsOverlay
│
├─ Body（左右两栏，间距 16，四周留白 20）
│  ├─ LeftColumn（固定 280px，垂直堆叠，间距 16；内容超高时整体可滚动）
│  │  ├─ StepCard ①「添加任务」
│  │  │  ├─ DropZone（虚线大区，高 130）：主文案「拖放任务清单或链接到此处」12px/600；
│  │  │  │  副文案「支持 .txt / .m3u8，或点此手动输入」11px/#9CA3AF；
│  │  │  │  拖拽悬停时底色变 #EEF0FF、虚线变主色
│  │  │  ├─ ManualInput（多行 TextBox，默认折叠，点主文案或「手动输入」展开，高 90）
│  │  │  └─ ButtonRow：[+ 粘贴剪贴板] 浅灰实心 / [+ 选择文件] 浅灰实心 / [清空输入] 文字链
│  │  ├─ PendingSummary（轻小节，非卡片）——【已拍板】保留只读摘要列表
│  │  │  ├─ 标题「任务队列」+ CountBadge「N 个」（主色浅底）
│  │  │  ├─ 右侧灰字提示「Shift 连选 · 右键菜单」
│  │  │  └─ 紧凑只读列表（数据源 = queue，显示序号/名称/状态点；点击联动右栏 grid 选中行并滚动到可见）
│  │  │     空态：EmptyState「还没有任务 · 粘贴或拖入任务开始」
│  │  ├─ StepCard ②「下载选项」
│  │  │  └─ 5 个 ToggleSwitch（useYtDlp / skipExisting / concurrentDownload / noSystemProxy / onlyParseM3u8）
│  │  │     右侧灰字链「更多选项 ›」→ SettingsOverlay
│  │  ├─ PrimaryButton「开始下载」（通栏，高 40）；运行中变为「停止下载」（危险色实心）
│  │  └─ TextLinkButton「清空全部任务」（底部左对齐，弱化危险色）
│  │
│  └─ RightColumn（弹性宽度）
│     ├─ SectionHeader ③：NumberBadge「③」+「下载队列」14px/600 + CountBadge「N 条」（主色数字）
│     ├─ ToolBar（高 40）
│     │  ├─ Ghost 组：[复制选中] [重试失败] [删除选中] [定位文件]
│     │  └─ 右端 PrimaryButton「打开目录」（小尺寸，高 32）
│     ├─ QueuePanel（白色圆角卡片，Flex=1）
│     │  ├─ TaskGrid（现有 DataGridView 换肤：无立体边框、圆角裁切、状态用 Pill 标签、进度用细条）
│     │  └─ EmptyState（queue 为空时覆盖）：「还没有结果 · 添加任务后点击「开始下载」」
│     ├─ LogPanel（白色圆角卡片，高 160，可折叠至 36）
│     │  └─ txtLog（RichTextBox 去边框，Consolas 11px，自动滚底，右键复制/清空保留）
│     └─ AboutLink（弱化 11px）「N_m3u8DL-RE」→ GitHub（原底部独立行移入此处）
│
├─ StatusBar（通栏，浅色，顶部 1px 分割线，高 36）
│  ├─ 左：状态文字（11px #6B7280）：「就绪 · 共 N 个任务 · 上次保存 14:32」
│  └─ 右：DependencyIndicator ×3：「yt-dlp ●」「N_m3u8DL-RE ●」「FFmpeg ●」——绿点=可用，灰点=不可用
│
└─ SettingsOverlay（覆盖式 Panel，Dock=Fill，默认隐藏）——【已拍板】采用覆盖式 Panel
   ├─ 分组 A「路径与依赖」：保存目录（txtWorkDir + 浏览）、RE 可执行（txtExe + 浏览 + 自动探测按钮）
   ├─ 分组 B「Cookie 来源」：Cookie 文件（txtCookieFile + 浏览）、浏览器 Cookie（cmbCookieBrowser + 提示「读取前请关闭浏览器」）
   │  ※ 【架构师修订】SettingsOverlay 本身即真实控件**唯一永久宿主**；compatHost 从构造起常驻其中，开关浮层只切 `SettingsOverlay.Visible`，不 reparent、不镜像、无状态同步
   │  ※ 内部固定为 CardGroupBox("基础选项") → Table(3,8,30) → txtCookieFile@(1,6) / cmb@(1,7)，见第 7.2 节
   ├─ 分组 C「性能」：同时下载数量、下载线程数、重试次数、HTTP 超时、限速（5 个数字框，2 列网格）
   │  ※ 4 个 NUD 所在的 CardGroupBox("性能设置") 必须与分组 B 的 GroupBox 同父且位于其下方
   ├─ 分组 D「界面」：alwaysOnTop 开关；左栏状态徽标显示当前 Cookie 摘要
   ├─ 分组 E「RE 执行路径选项（含 yt-dlp 失败自动兜底）」——【已拍板】标题采用此表述，默认折叠，展开后 8 项
   └─ 底部：关闭按钮 + 弱化文字「配置仅影响本次及后续新任务」+【P1，可延后】保存到独立 settings.json 的入口
```

**配置控件宿主（唯一，架构师修订 + team-lead 拍板）**：SettingsOverlay 自身就是真实配置控件的**永久宿主**；`compatHost` 从构造起常驻其中，`CardGroupBox("基础选项")` 与 `CardGroupBox("性能设置")` 是 `compatHost` 内的兄弟节点，构造期即给出**显式尺寸**。打开/关闭浮层**只切换 `SettingsOverlay.Visible`**，不 reparent、不做隐藏兼容层、不做双份镜像控件（双份状态必有 bug）。精确父子树与坐标约束见第 7.2 节。

---

## 3. 视觉规格表（可直接实现）

### 3.1 配色（十六进制）

| 用途 | Token | 色值 |
|---|---|---|
| 页面底色 | Canvas | `#F5F6FA` |
| 卡片底 | Surface | `#FFFFFF` |
| 顶部栏 / 状态栏底 | Chrome | `#FFFFFF` / `#FAFBFD` |
| 主色（实心按钮、强调数字、徽标） | Primary | `#5B6EE8` |
| 主色悬停 / 按下 / 禁用 | PrimaryHover / Pressed / Disabled | `#4F61D4` / `#4556C4` / `#B9C0EE` |
| 主色浅底（徽标、选中行、拖放悬停） | PrimarySoft | `#EEF0FF` |
| 主文字 | Text | `#1F2937` |
| 次文字 | TextSecondary | `#6B7280` |
| 弱文字 / 空态 / 禁用文字 | TextMuted | `#9CA3AF` |
| 分隔线 | Divider | `#ECEEF5` |
| 虚线边框 | Dash | `#CBD3E8` |
| 输入框底 / 边框 / 聚焦 | Field / FieldBorder / Focus | `#F8F9FC` / `#E3E7F0` / `#5B6EE8` |
| 成功 / 失败 / 警告 | Success / Danger / Warning | `#10B981` / `#EF4444` / `#F59E0B` |
| 状态点可用 / 不可用 | DotOn / DotOff | `#22C55E` / `#CBD5E1` |
| 幽灵按钮底 / 悬停 | Ghost / GhostHover | `#F3F4F8` / `#E9ECF4` |

### 3.2 尺寸、圆角、间距

| 项 | 规格 |
|---|---|
| 窗体 | 默认 1280×800；最小 1120×720；AutoScaleMode=Dpi；HighDpiMode=PerMonitorV2（保持现状） |
| TopBar / StatusBar 高度 | 64 / 36 px |
| 页面留白 / 栏间距 / 卡片内边距 | 20 / 16 / 16 px |
| 左栏宽度 | 280 px（固定） |
| 卡片圆角 | 14 px |
| 卡片阴影 | `0 2px 8px rgba(17,24,39,0.06)`（GDI+ 绘制，不用立体边框） |
| 按钮圆角 / 输入框圆角 / 空态圆角 | 8 / 8 / 12 px |
| 徽标圆角 | NumberBadge 10 px 圆形感；CountBadge 10 px 胶囊 |
| 控件间垂直间距 | 8 px；卡片间 16 px |
| DropZone 高度 | 130 px，虚线 1 px Dash，圆角 12 px |

### 3.3 字号层级（Microsoft YaHei UI；96 DPI 下 9pt≈12px）

| 层级 | 字号/字重 | 颜色 |
|---|---|---|
| H1 应用主标题 | 16 px / 600 | Text |
| H2 区块标题（①②③） | 14 px / 600 | Text |
| Body 正文与按钮 | 12 px / 400 | Text |
| Caption 副文案/提示 | 11 px / 400 | TextMuted |
| CountBadge 数字 | 11 px / 600 | Primary |
| 日志 | Consolas 11 px / 400 | Text |

### 3.4 按钮四档样式

| 档位 | 底 / 字 / 边框 | 悬停 / 按下 | 高 | 用途 |
|---|---|---|---|---|
| 主色实心 | Primary / #FFF / 无 | PrimaryHover / PrimaryPressed | 40（通栏）、32（工具栏） | 开始下载、打开目录 |
| 危险实心 | Danger / #FFF / 无 | `#DC2626` / `#B91C1C` | 40 | 运行中变为「停止下载」 |
| 浅灰实心 | Ghost / Text / 无 | GhostHover / `#DFE3EC` | 32 | 粘贴、选择文件、设置；「切换颜色」为同款禁用态 |
| 幽灵按钮 | 透明 / TextSecondary / 1px Divider | PrimarySoft 底 + Primary 字 | 32 | 复制选中、重试失败、删除选中、定位文件 |
| 文字链 | 透明 / TextMuted（危险项用弱化 Danger） / 无 | 下划线 + 字色加深 | 20 | 清空输入、清空全部任务、更多选项、关于链接 |

禁用态：底 `#F3F4F8`、字 `#B8BEC9`、无边框、`Cursor=Default`；「切换颜色」额外设 `Enabled=false` 且 tooltip「即将推出」。

### 3.5 iOS 开关规格

| 项 | 规格 |
|---|---|
| 轨道 | 40×22 px，圆角 11 px；关 `#D8DEE9`，开 `#5B6EE8` |
| 滑块 | 18×18 px 纯白圆，右/左内边距 2 px，`DropShadow 0 1px 2px rgba(0,0,0,.18)` |
| 标签 | 右侧 12 px Text；整行点击热区 ≥ 44×28 px |
| 键盘 | Space 切换；必须继承 `CheckBox` 以兼容测试（见第 7 节） |
| 状态文字 | 关=灰字，开=主色字；开启时副文案可显示（如"仅解析不下载"提示"不会写出文件"） |

### 3.6 空态面板规格

| 项 | 规格 |
|---|---|
| 尺寸 | 最小高 180 px，随容器拉伸 |
| 外观 | 底色 `#FBFCFE`，1 px 虚线 `#DCE2EF`，圆角 12 px，内部留白 16 |
| 内容 | 居中：图标 32 px `#CBD5E1`（线条风格）→ 主文案 12 px `#9CA3AF` → 副文案 11 px `#B6BDCA` |
| 文案 | 任务空：「还没有任务 · 粘贴或拖入任务开始」；结果空：「还没有结果 · 添加任务后点击「开始下载」」 |
| 行为 | 整个面板可点击 = 触发粘贴；拖拽悬停同 3.2 高亮规则 |

---

## 4. 组件清单（需自绘，WinForms 无原生支持）

| 控件名 | 基类 | 职责 | 绘制要点 |
|---|---|---|---|
| `RoundedCard` | Panel | 白色圆角卡片容器，用于主界面各卡片 | 重写 `OnPaint`：`GraphicsPath` 圆角 14；先用半透明黑画偏移阴影再填白；`Region` 裁切子控件；支持 `Padding=16` |
| `CardGroupBox` | **GroupBox** | 设置浮层内的「基础选项」「性能设置」卡片——**必须继承 GroupBox**，圆角自绘但保持 `as GroupBox` 断言成立 | 只重写 `OnPaint`/标题绘制实现圆角 14 + 极淡阴影；**保留 `Text` 精确为「基础选项」/「性能设置」**；`Padding` 固定 `(10,2,10,6)`、`Height = 272`（照抄现状，勿缩小或加大 Padding）；不得换成 Panel 或 RoundedCard |
| `NumberBadge` | Control | ①②③ 编号圆章 | 26×26 圆，底 PrimarySoft、字 Primary 12px/600 居中；右侧可接标题文字 |
| `CountBadge` | Label | 队列计数「N 个 / N 条」 | 胶囊底 PrimarySoft、字 Primary 11px/600，左右内边距 8 |
| `ToggleSwitch` | **CheckBox** | iOS 滑块开关 | 重写 `OnPaint`/`OnMouse*`：轨道+滑块按 `Checked` 绘制；`FlatStyle=Flat`、`Appearance=Normal`、`Text=""`；**必须继承 CheckBox**，保证 `chk` 字典与反射赋值不破 |
| `DropZone` | Panel | 拖放 .txt / .m3u8 与点击输入 | 虚线边框手绘（`Pen.DashStyle=Dot`）；`DragEnter/DragDrop` 高亮；点击触发 `ManualInput` 或文件选择 |
| `EmptyState` | Panel | 空态占位 | 居中绘制线条图标 + 两行文字；点击冒泡为 PasteTasks |
| `StatusPill` | Control | 表格内状态标签 | 胶囊：待下载灰、下载中 PrimarySoft+Primary、完成 `#E7F7F0`+Success、失败 `#FDECEC`+Danger、已取消灰 |
| `ThinProgressBar` | Control | 表格内细进度条 | 高 4 px、圆角 2、底 Divider、前景 Primary；100% 时 Success |
| `PrimaryButton` / `GhostButton` / `TextLinkButton` | Button / Button / Label | 四档按钮的复用实现 | `FlatStyle=Flat`、`BorderSize=0`、圆角 Region；Hover/Pressed 由 `OnMouseEnter/Leave` 切色；文字链用 Label + `Cursor=Hand` |
| `DependencyIndicator` | Control | 底部「名称 + 绿/灰点」 | 8×8 圆点 + 11px 灰字，间距 6；支持 `Available` 属性切换色 |
| `SettingsOverlay` | Panel | 配置浮层（覆盖式，已拍板）——**真实配置控件的唯一永久宿主** | Dock=Fill、半透明遮罩 + 居中 720×560 白色圆角面板；Esc/点遮罩关闭；**不 reparent、不复制控件**，开关浮层只切 `Overlay.Visible`；内部 `compatHost`/`CardGroupBox` 在构造期给显式尺寸（隐藏容器中的 Dock 不会自动拉伸，探针实测宽度会停在默认 200）；构造期完成布局 |
| `TaskGridSkin` | 行为类（非新控件） | 给现有 `grid` 换肤 | 保留 `grid` 字段名与类型 DataGridView；仅改 `Background/GridColor/RowHeadersVisible/CellPainting`；**禁止替换成第三方控件** |

**实现注意**：所有自绘控件开启双缓冲（`SetStyle(ControlStyles.OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint, true)`），DPI 缩放沿用 PerMonitorV2，不引入 GDI+ 之外的依赖。

---

## 5. 选项裁剪决策表

### 5.1 总决策表（15 个复选框 + 5 个数字框 + 3 个路径项）

| 选项 / 字段 | 决策 | 理由 |
|---|---|---|
| `useYtDlp` 使用 yt-dlp | **保留**（左栏 ② 开关，改为「引擎：yt-dlp / RE」语义） | 双引擎核心选择，两路径都生效；测试通过 `chk["useYtDlp"]` 反射赋值，必须保留 CheckBox 与 key |
| `skipExisting` 跳过成品 | **保留**（左栏 ②） | 避免重复下载，两引擎通用，高频 |
| `concurrentDownload` 并发下载音视频 | **保留**（左栏 ②） | 两路径都生效：yt-dlp `--concurrent-fragments`、RE `--concurrent-download` |
| `noSystemProxy` 不使用系统代理 | **保留**（左栏 ②） | 两路径都生效；代理问题高频，需快速切换 |
| `onlyParseM3u8` 仅解析不下载 | **保留**（左栏 ②） | 独立使用模式；强制走 RE `--skip-download`（Engine.cs:86、722），不可删 |
| `concurrentLimit` 同时下载数量 | **移入「更多设置 → 性能」** | 队列级并发，非每次都调；与 4 个性能参数同组更完整 |
| `maxThreads` 下载线程数 | **移入「更多设置 → 性能」** | 两路径都生效，但属调参，不占主界面 |
| `retryCount` 重试次数 | **移入「更多设置 → 性能」** | 同上 |
| `timeout` HTTP 超时 | **移入「更多设置 → 性能」** | 同上 |
| `limitSpeed` 限速 | **移入「更多设置 → 性能」** | 同上 |
| `cleanStaleParts` 跳过时清理 .part | **移入「更多设置 → 通用」** | 有真实价值（避免残留被误判），但依赖 skipExisting，属二级选项 |
| `noMerge` 下载后不合并 | **移入「更多设置 → 通用」** | 两路径都生效（yt-dlp `--keep-video`、RE `--skip-merge`），不能删，但低频 |
| `alwaysOnTop` 置顶窗口 | **移入「更多设置 → 界面」** | 纯 UI 偏好，需在设置里而非主网格 |
| `delAfterMerge` 合并后删除分片 | **移入「RE 执行路径选项（含 yt-dlp 失败自动兜底）」** | 仅 RE 分支 `--del-after-done`；但 yt-dlp 失败兜底会执行 RE，故不能按引擎隐藏 |
| `noDateInName` 合并时不写日期 | **同上** | RE `--no-date-info`，同理 |
| `muxWhileDownload` 边下边看 | **同上** | RE `--live-real-time-merge --live-pipe-mux`，且需 FFmpeg；高级且依赖外部工具 |
| `binaryMerge` 二进制合并 | **同上** | RE `--binary-merge`，高级调优 |
| `noLog` 关闭日志文件输出 | **同上** | RE `--no-log` / `--no-log-file`；应用自身已有日志面板，但开关对磁盘输出有实际影响，不删 |
| `checkSegmentsCount` 检测分片数量 | **同上** | RE `--check-segments-count`，高级 |
| `appendUrlParams` URL 参数附加到分片 | **同上** | RE `--append-url-params`，高级 |
| `muxFile` 混流文件 | **同上**（保留字段 `txtMux`） | RE `--mux-import` 专属且需 FFmpeg，有真实用途，不删 |
| `txtWorkDir` 保存目录 | **移入「更多设置 → 路径与依赖」** | 必需配置但无需常驻主界面；保留字段名（测试反射） |
| `txtExe` RE 可执行 | **移入「更多设置 → 路径与依赖」** | 同上；同时把硬编码 `FixedExecutable` 改为自动探测 + 用户覆盖 |
| `txtCookieFile` / `cmbCookieBrowser` | **移入「更多设置 → Cookie 来源」** | 见 5.2-Q2；必须保留字段、父级结构与项列表 |

**彻底删除清单（仅限代码级死项，不含任何功能选项）**：

| 项 | 理由 |
|---|---|
| `Program.cs` 的 `--dump` 分支 | 调试遗留，硬编码写 `C://tmp//dump.txt`，对正常用户无价值且存在路径副作用 |
| `MainForm.DumpLayout()` | 同上，随分支一并删除 |
| `Theme.Border` | 全项目零引用（已 grep 确认），冗余常量 |
| `FixedExecutable` / `FixedWorkDir` 两个硬编码常量 | 指向本机特定路径（另一软件安装目录、`E:/Downloads`），新环境必然失效，属历史遗留死配置；改为「PATH/常见位置探测 + 默认下载目录 + 可持久化」 |

> **决策原则声明**：没有一个复选框被彻底删除。原简报中的 8 个"RE-only 死选项"在默认引擎下确实走不到主路径，但 yt-dlp 失败兜底（Engine.cs:517-525）会带同一份 Opt 调用 `ReAsync`，因此它们在 `useYtDlp=true` 时仍有可达路径；按"用得少"删除不成立，统一收进默认折叠的高级分组。

### 5.2 四个必答问题（含拍板结论）

**Q1：「更多设置」放什么？**
按 5 个分组：路径与依赖（保存目录、RE 可执行）、Cookie 来源、性能（5 数字框）、界面（置顶）、**RE 执行路径选项（含 yt-dlp 失败自动兜底）**（8 项，默认折叠）。另加关于链接。**不放** 5 个主开关和开始/停止——它们必须在左栏常驻。
**拍板**：采纳该分组方案；第 5 组标题严格使用「RE 执行路径选项（含 yt-dlp 失败自动兜底）」，默认折叠，理由为 Engine.cs:517-525 兜底会用同一份 Opt 调 `ReAsync`，这些选项确实可达。

**Q2：Cookie 来源放哪？**
放「更多设置 → Cookie 来源」，左栏不出输入框（与参考图一致）。为弥补可见性：左栏 ② 卡片底部与底部状态栏各显示一枚只读摘要徽标，例如「Cookie：文件」/「浏览器：edge」/「未设置」，点击直达设置分组。技术上由 SettingsOverlay 作为真实控件的**唯一永久宿主**（不重挂载、不镜像），保留 `txtCookieFile.Parent.Parent is GroupBox`、`cmbCookieBrowser` 项列表、`Snapshot()` 读取链路与 `"(不使用)" → ""` 语义；几何位置必须照抄第 7.2 节。

**Q3：「添加任务」支持哪些输入？**
全部放左栏主卡，因为这是唯一高频入口：① 剪贴板粘贴（按钮 + 全局 `Ctrl+V`）；② 拖放 `.txt` 任务清单 / `.m3u8` 链接文件到 DropZone；③ 点击「手动输入」展开内联多行文本框（不弹窗，可直接粘贴多行）。按钮行 = `[+ 粘贴剪贴板]`、`[+ 选择文件]`、`[清空输入]`。格式说明（`name|url|...`）折叠为 11px 灰字，不放设置里。

**Q4：7 个按钮如何映射？**

| 现有 | 新位置与样式 | 说明 |
|---|---|---|
| 粘贴任务 | 左栏 ① 按钮行 `[+ 粘贴剪贴板]`（浅灰实心） | 保留 `PasteTasks()` 方法名 |
| 开始队列下载 | 左栏通栏主色按钮「开始下载」 | 保留 `StartBatch()` |
| 停止队列 | 与开始合并为**同一双态按钮**：运行中显示「停止下载」（危险实心） | 消除"未运行也能点"的误导；不再单独常驻 |
| 重试失败任务 | 右栏 ③ 工具栏幽灵「重试失败」 | 无失败任务时禁用并 tooltip 说明 |
| 删除选中任务 | 右栏 ③ 工具栏幽灵「删除选中」 | 未选中时禁用 |
| 定位文件位置 | 右栏 ③ 工具栏幽灵「定位文件」 | **仅选中行时可用**；未选中禁用，不再静默打开保存目录 |
| 清空全部任务 | 左栏底部弱化文字链「清空全部任务」 | 需二次确认 |
| （新增）复制选中 | 右栏 ③ 工具栏幽灵「复制选中」 | 对应参考图工具栏；复用 `CopySelection()` |
| （新增）打开目录 | 右栏工具栏最右主色小按钮「打开目录」 | 对应参考图右端主色按钮；语义明确为打开保存目录 |

### 5.3 实施优先级与 settings.json 边界（拍板补充）

| 优先级 | 范围 |
|---|---|
| **P0（必做）** | UI 重构骨架与视觉规格、5 条主流程、选项裁剪与分组、6 项 Bug 修复、199 项测试全绿、用户数据零改动 |
| **P1（可延后）** | `settings.json` 独立持久化；若工期紧可先交付内存态配置，后续补持久化 |

**settings.json 硬边界（不可违反）**：
1. 允许新增**独立文件** `%APPDATA%\NM3U8DL_GUI\settings.json`，**绝不触碰 `tasks.json` 的结构或字段集合**。
2. 允许存储：引擎选择（useYtDlp）、保存目录、RE 可执行路径、同时下载数量、5 个主开关、Cookie 来源（文件**路径**或浏览器名）、性能参数（线程/重试/超时/限速）、RE 执行路径选项。
3. **绝不存储 Cookie 值本身**，只存文件路径或浏览器标识。
4. 不得把上述设置项写入 `tasks.json`；`tasks.json` 仍只含任务数据且保持与旧 Python 版双向兼容。
5. 设置持久化失败（权限/磁盘满）不得阻断主流程，退化为本会话内存态并在日志提示。

---

## 6. 交互流程（5 条主流程）

### 流程 1：添加任务
| 步骤 | 用户操作 | 系统反馈 |
|---|---|---|
| 1 | `Ctrl+V` / 点 `[+ 粘贴剪贴板]` / 拖入 .txt / 点「手动输入」粘多行 | DropZone 短暂高亮 |
| 2 | 松手/松开粘贴 | 逐行解析 `name|url|referer|ua|cookie|origin|x-cdn-auth`；非法行跳过并在日志提示「第 N 行格式不正确」 |
| 3 | 解析完成 | 右栏 grid 新增行（状态 Pill=待下载）；左栏 CountBadge 与右栏「N 条」同步 +1；空态面板淡出；日志追加「＋ 添加 N 个任务」 |
| 4 | — | 自动 `SaveTasks()` 持久化（仅本次有新增时） |

### 流程 2：开始下载
| 步骤 | 用户操作 | 系统反馈 |
|---|---|---|
| 1 | 点左栏「开始下载」 | 前置检查依赖：yt-dlp / RE / FFmpeg 按当前引擎与选项校验；缺失则底部状态栏灰点 + 日志明确提示，不启动 |
| 2 | 校验通过 | 按钮原地变「停止下载」（危险色）；状态栏「运行中 · 并发 N」；选中行状态 Pill 变「下载中」 |
| 3 | — | 按并发上限启动 worker；每行细进度条推进；日志实时滚动（Cookie 值日志脱敏） |
| 4 | 单任务完成 | Pill=完成（绿）、进度 100%、状态栏统计更新；成品存在时显示文件大小 |

### 流程 3：查看进度
| 步骤 | 用户操作 | 系统反馈 |
|---|---|---|
| 1 | 运行中观察右栏 grid | 每行显示状态 Pill + 4px 细进度条 + 文件大小列；多流下载按现有折算逻辑显示单一整体进度（不回退） |
| 2 | 点左栏「待处理」列表某项（已拍板保留） | 右栏 grid 对应行高亮并滚动到可见 |
| 3 | 点 LogPanel 标题 | 日志区在 160px ↔ 36px 间折叠/展开 |
| 4 | — | 底部状态栏持续显示「完成 X / 失败 Y / 总计 N」 |

### 流程 4：重试失败
| 步骤 | 用户操作 | 系统反馈 |
|---|---|---|
| 1 | 点工具栏「重试失败」（无失败项时按钮禁用） | 所有失败行状态重置为「待下载」、进度归零；日志「↻ 重试 N 个失败任务」 |
| 2 | 自动触发开始（建议自动，不额外要求二次点击） | 复用流程 2 步骤 2-4；重试前按现有 `CleanupTaskTemps` 清旧分片残留 |
| 3 | 再次失败 | Pill 保持失败，日志保留上次错误摘要；不能降级为"跳过" |

### 流程 5：清空
| 步骤 | 用户操作 | 系统反馈 |
|---|---|---|
| 1 | 点左栏底部「清空全部任务」 | 弹二次确认：「确定清空 N 个任务？不会删除已下载文件」 |
| 2 | 确认 | 若运行中先停止并终止子进程；`queue.Clear()`；grid 清空；两侧计数归零；空态面板出现；日志「🗑 已清空任务队列」 |
| 3 | — | `SaveTasks()` 写入空数组（与 `LoadTasks()` 兼容，不写 null） |
| 4 | 取消 | 无任何变更 |

---

## 7. 兼容性约束（不可违反）

### 7.1 约束清单

| # | 约束 | 具体做法 |
|---|---|---|
| 1 | 199 项测试全绿 | 用 `dotnet run`（非 `dotnet test`）跑 `M3U8DL.Tests`；不得修改测试断言来"适配"新 UI |
| 2 | 反射字段名/类型不变 | `MainForm` 保持私有字段 `txtCookieFile`(TextBox)、`cmbCookieBrowser`(ComboBox)、`txtExe`、`txtWorkDir`、`chk`(Dictionary<string,CheckBox>)、`grid`(DataGridView)、`queue`(List&lt;DownloadTask&gt;)、`txtLog`(RichTextBox) |
| 3 | 反射方法签名不变 | `Snapshot()`、`SaveTasks()`、`AddTask(string,string,string,List<string>,bool)`、`StartBatch()`、`PasteTasks()`、`CopySelection()`；`Engine.BuildYtDlpCmd(string,string,Opt,IReadOnlyList<string>,bool)` 非 public 实例；`Engine.FindOnPath(string)` 非 public 静态 |
| 4 | LayoutTests 精确父子树与几何数值 | 见 7.2 硬约束图；`CardGroupBox` 必须继承 GroupBox、`Text` 精确；`Table(3,8,30)` 与 `(1,6)/(1,7)` 照抄；`cmbCookieBrowser.Items` 保留 "(不使用)" + 9 个浏览器；`(不使用)` → `Opt.CookieBrowser == ""` |
| 5 | Opt 保持 record | 测试用 `with` 表达式；保留全部属性名（尤其 WorkDir/UseYtDlp/ConcurrentDownload/MaxThreads/RetryCount/Timeout/LimitSpeed/NoSystemProxy/NoMerge/CookieFile/CookieBrowser），隐藏选项的属性也保留 |
| 6 | tasks.json 格式不变 | camelCase、`WriteIndented=true`、`UnsafeRelaxedJsonEscaping`；字段集合不变；与旧 Python 版双向可读；**不新增字段**；设置项只能进独立 settings.json（见 5.3） |
| 7 | **真实数据隔离（最高优先级）** | 任何实例化 `MainForm` 的验证代码，必须**先**把 `APPDATA` 指向临时目录并创建 `NM3U8DL_GUI` 子目录；用户真实数据在 `%APPDATA%\NM3U8DL_GUI\tasks.json`（**18 条任务，11091 字节**，含请求头），禁止写入/重命名/覆盖；安全基线快照已由 team-lead 保存 |
| 8 | 无新增依赖 | 只用 .NET 10 BCL + System.Drawing；保持自包含单文件发布 |

### 7.2 硬约束图：Cookie / 性能控件的精确父子树（工程师照此实现）

team-lead 核实 `Tests.cs:361-387` 的断言比初版描述更严格；software-architect 用 WinForms 探针进一步实测确认 **`area.Contains(ctl.Bounds)` 是坐标系错配的脆弱断言**，必须靠"照抄现有几何数值"来兼容，不能靠"控件可见就算了"。以下为**硬约束**，工程师照此实现。**team-lead 已拍板：断言 B 采用方案 ①（照抄现状布局）；不 reparent——`compatHost` 及其内部层级全程不移动，仅由 `SettingsOverlay.Visible` 控制显隐。**

**为什么必须照抄（架构师 .NET 10 WinForms 探针实测）**：

| 配置 | `optGroup.DisplayRectangle` | `txtCookie.Bounds` | 断言 B |
|---|---|---|---|
| Padding(10,2)、2 行、H=120 | (10,18,740,96) | (249,**3**,240,23) | **False** |
| Padding(12,8)、2 行、H=120 | (12,**24**,736,88) | (248,**3**,239,23) | **False** |
| Padding(12,8)、6/8 行、H=300 | (12,24,…) | (248,3,…) | **False** |
| **验收配置：Padding(10,2,10,6)、3 列×8 行、txt(1,6)/cmb(1,7)、H=272** | (10,18,740,248) | (249,**183**,240,23) | **True** |

根因：`DisplayRectangle` 原点 = `(Padding.Left, Padding.Top + 标题高≈16)`，而 `ctl.Bounds` 相对**内层 TableLayoutPanel** 从 `(0,0)` 起算——**根本不是同一坐标系**。第 0 行控件 Y≈3，**永远小于** `area.Y=18~24`。**加大 GroupBox Padding 只会抬高 `area.Y`，让断言更容易失败**（Padding 不会移动子控件的 Bounds）。必须让 Cookie 控件落在 TLP 第 6/7 行（Y≈183/213）才能进入 `area`。

```
SettingsOverlay : Panel                ← 真实控件唯一永久宿主；默认 Visible=false，但构造期已 CreateControl()+PerformLayout()
│                                      ← 【team-lead 拍板】不 reparent：compatHost 及全部内部层级全程不移动，仅切 Overlay.Visible
│                                      ← 隐藏容器中的 Dock 不会自动拉伸：探针实测 GroupBox 宽度停在默认 200，故必须显式设尺寸
└─ compatHost : Panel / TableLayoutPanel（垂直堆叠，顺序固定；全程常驻 SettingsOverlay，不重挂载）
   │
   ├─ optGroup : CardGroupBox            ← 继承 GroupBox（as GroupBox 断言成立）；Text 保持「基础选项」
   │  │  Padding = (10, 2, 10, 6)；Height = 272（与现状完全一致，勿缩小）
   │  │  显式 Width/Height，禁止只靠 Dock 自适应
   │  └─ optLayout : TableLayoutPanel = Table(3, 8, 30)   ← 3 列 × 8 行、行高 30、Dock=Fill，照抄 MainForm.cs:192
   │     ├─ ...
   │     ├─ (col=1, row=6) txtCookieFile  : TextBox       ← 断言 A + B：位置必须是 (1,6)，Bounds≈(249,183)
   │     ├─ (col=2, row=6) Button「浏览…」
   │     ├─ (col=1, row=7) cmbCookieBrowser : ComboBox    ← 位置必须是 (1,7)，Bounds.Y≈213；两者均须落入 area
   │     └─ ...
   │
   └─ perfGroup : CardGroupBox (Text == "性能设置")        ← 断言 C：与 optGroup 同为 compatHost 的直接子节点（兄弟）
      │  显式尺寸；布局/Z 序位于 optGroup 下方
      └─ perfLayout : TableLayoutPanel                     ← 断言 D：perfGroup.Top >= optGroup.Bottom，不重叠
         └─ nudConc / nudThreads / nudRetry / nudTimeout / nudSpeed
```

**四条断言的逐条实现要点（数值照抄，不要"优化"）**：

| 断言 | 来源 | 实现要点 |
|---|---|---|
| A | `txtCookie.Parent?.Parent as GroupBox != null`（Tests.cs:361） | `txtCookieFile` → `TableLayoutPanel` → `CardGroupBox` 共两层；**不能直接挂 GroupBox / SettingsOverlay**；`CardGroupBox` 必须继承 GroupBox |
| B | `area = optGroup.DisplayRectangle; area.Contains(ctl.Bounds)`（Tests.cs:365/372，两个控件都要） | `optGroup.Padding=(10,2,10,6)`、`Height=272`；内部 TLP 必须 `Table(3,8,30)` + `Dock=Fill`；`txtCookieFile` 放 `(1,6)`（Bounds.Y≈183）、`cmbCookieBrowser` 放 `(1,7)`（Bounds.Y≈213），落入 `area=(10,18,W-20,246)`；控件 Margin 保持默认。**坐标系错配：area 原点=(Padding.Left, Padding.Top+标题≈16)，Bounds 相对内层 TLP 从 (0,0) 起算；第 0 行 Y≈3 恒小于 area.Y=18。加大 Padding 只会抬高 area.Y，更容易失败。严禁 2 行 TLP 或把控件放左上角（探针实测恒 False）** |
| C | `root.Controls.OfType<GroupBox>().Any(g => g.Text=="性能设置")`（Tests.cs:379-380） | `perfGroup` 是 `compatHost` 的**直接**子节点，`Text` 精确为 `性能设置`；`CardGroupBox` 继承 GroupBox 才能被 `OfType<GroupBox>()` 找到 |
| D | `perf.Top - optGroup.Bottom >= 0`（Tests.cs:384） | `compatHost` 垂直堆叠：先 `optGroup` 后 `perfGroup`；显式 Top/Height，不依赖隐藏态的 Dock 拉伸 |

**不 reparent（team-lead 拍板）**：`compatHost` 从构造起就常驻 `SettingsOverlay`，打开/关闭浮层**只切换 `SettingsOverlay.Visible`**，不得 `Controls.Add/Remove` 或重设 `Parent`——避免句柄重建、闪烁与状态不同步。上述 A/B/C/D 断言在隐藏态也须成立（故必须构造期 `CreateControl()+PerformLayout()` 并给显式尺寸）。

**工程实施顺序建议**：先原样搭出上述 8 行 TLP 骨架并单跑 LayoutTests 段确认 199 项中的布局断言全绿，再逐步替换视觉皮肤；每替换一层重跑一次，避免最后一次性排查坐标问题。

**其余兼容手段**：开关类控件统一 `ToggleSwitch : CheckBox` 并仍以原 key 存入 `chk`（测试实际只反射 `useYtDlp`，其余 key 保留即零成本兼容）；`btnStop` 字段未被任何测试反射，可随双态按钮安全删除；`Engine` 与 `Core` 的公开/非公开方法签名字段一律不动。

---

## 8. 验收与风险

### 8.1 必须修复的 6 项 Bug（逐条验收）

| # | Bug | 验收动作 | 通过标准 |
|---|---|---|---|
| B1 | 重命名任务无效 | 右键重命名 A→B，检查 grid、内部 `queue[row].Name`、tasks.json，重启应用 | 三处一致为新名 B；下载使用新名；重启后仍为 B。修复必须显式写 `queue[row].Name` 并 `SaveTasks()`，不能依赖被 `updating` 抑制的 `CellValueChanged` |
| B2 | 「停止队列」从不禁用 | 启动前/运行中/停止后观察按钮状态 | 未运行时按钮不存在或禁用；运行中可点；停止后恢复开始态；点击不再静默 return |
| B3 | 「定位文件位置」语义不一致 | 未选中/选中一行/选中多行分别点击 | 未选中禁用并 tooltip「请先选中任务」；选中一行定位到该文件并选中；多行时只处理首个或明确拒绝，不再静默打开目录 |
| B4 | 启动无条件回写 tasks.json | 记录 `tasks.json` 的 mtime 与内容哈希 → 启动应用 → 不新增任务 → 退出 | mtime 与哈希均不变；仅在有实际变更时才写 |
| B5 | 调试遗留 `--dump` / `DumpLayout()` | 全局搜索 `dump.txt`、`--dump`、`DumpLayout`；带 `--dump` 启动 | 零命中；带该参数启动等价于正常启动，不产生任何 `C:\tmp` 文件 |
| B6 | 主题色板冗余 | 搜索 `Theme.Border` 等无引用常量 | `Theme` 仅保留实际引用的常量；新设计 token 集中在单一配色表 |

### 8.2 功能与视觉验收清单

- [ ] 199/199 测试通过（隔离 APPDATA 下执行），失败 0；**工程师须先在修改前跑一次全量基线并记录**，因架构师沙箱策略拦截了测试执行，当前无第三方复测基线。
- [ ] 布局断言专项通过：`txtCookieFile` 位于 `(1,6)`、`cmbCookieBrowser` 位于 `(1,7)` 的 `Table(3,8,30)` 内；`CardGroupBox("基础选项")` 的 `Padding=(10,2,10,6)`、`Height=272`；`CardGroupBox("性能设置")` 同为 `compatHost` 子节点且在其下方；浮层隐藏态下两者仍有显式尺寸（非默认 200 宽）。
- [ ] 浮层开关只切 `SettingsOverlay.Visible`：全程未对 `compatHost` 及其子控件调用 `Controls.Add/Remove` 或重设 `Parent`；打开-关闭-再打开 3 次后断言 A–D 仍全绿、无控件闪烁。
- [ ] 4 张截图验收：①空态 ②18 条任务满态 ③运行中（含进度与日志）④设置浮层展开。分辨率 1280×800、100% DPI。
- [ ] 截图配色取色比对：底色 `#F5F6FA`、卡片 `#FFFFFF`、主按钮 `#5B6EE8`，与设计表误差为 0。
- [ ] 所有卡片圆角 14px、按钮 8px、空态虚线可见；主界面无立体 GroupBox 边框残留（设置浮层内两个 `CardGroupBox` 为圆角自绘，但底层仍是 GroupBox）。
- [ ] 15 个复选框在主界面不可见；左栏 ② 仅 5 个 iOS 开关；「更多设置」可抵达全部 15 项。
- [ ] 左栏「任务队列」只读摘要存在，点击可联动右栏 grid 选中并滚动到可见。
- [ ] 右上「切换颜色」按钮**显示但禁用**，tooltip 为「即将推出」，点击无任何主题变化。
- [ ] 浮层分组 E 标题精确为「RE 执行路径选项（含 yt-dlp 失败自动兜底）」，默认折叠。
- [ ] 主窗口在 1120×720 最小尺寸下无控件裁切、无横向滚动条；200% DPI 下文字不重叠。
- [ ] 五条主流程按第 6 节逐步操作，用户操作与系统反馈逐项吻合。
- [ ] 用户真实 tasks.json（18 条任务 / 11091 字节）校验：任务数与内容哈希在整轮开发/验证后保持不变。
- [ ] 【P1，若已实现】`settings.json` 存在且仅含允许字段；`tasks.json` 字段集合与哈希未因设置变更而改变；全文搜索确认无 Cookie 值写入；设置文件损坏/不可写时主流程仍可启动。
- [ ] 发布产物仍为单个自包含 exe，无附带 dll/Python 运行时。

### 8.3 风险与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| `DisplayRectangle`/`Bounds` 坐标系错配导致 2 条布局断言失败（**实测可复现**） | 高 | 严格照抄 7.2 的 `Table(3,8,30)` 与 `(1,6)/(1,7)` 几何数值；先搭骨架跑 LayoutTests 再换皮肤；禁止把 Cookie 控件挪到 2 行小 TLP 左上角 |
| SettingsOverlay 显隐导致 compatHost 布局失效（不 reparent 方案） | 高 | 控件从构造起常驻，只切 `Visible`；构造期 `CreateControl()+PerformLayout()` 并对两个 `CardGroupBox` 给显式 Width/Height；每次切 Visible 后复跑 LayoutTests 断言 A–D |
| 双态按钮与 `StartBatch`/`StopBatch` 并发竞态 | 中 | 用现有 `batchRunning` 原子切换 UI，保持 `if (!batchRunning) return` 兜底但不让其出现在 UI 层 |
| RE 高级选项在兜底路径生效但 UI 提示不清 | 中 | 分组标题已拍板为「RE 执行路径选项（含 yt-dlp 失败自动兜底）」 |
| 新建自绘控件在 200% DPI 下位置偏移 | 中 | 全部尺寸用逻辑像素并启用 PerMonitorV2；截图验收覆盖 200% DPI |
| 清空任务误删用户数据 | 高 | 二次确认 + 文案明确"不删除已下载文件" + 只清内存队列与 json |
| settings.json 引入额外复杂度挤压主流程工期 | 中 | 已标 P1，可延后；P0 完成前不启动持久化实现 |

---

## 9. 已拍板决策记录（原 5 个待明确事项）

| # | 议题 | 拍板结论 | 落实章节 |
|---|---|---|---|
| 1 | RE 高级选项口径 | 采纳 PM 建议。标题改为「RE 执行路径选项（含 yt-dlp 失败自动兜底）」，默认折叠。理由：Engine.cs:517-525 兜底会用同一份 Opt 调 `ReAsync`，这些选项确实可达 | 第 2、5.1、5.2-Q1、8.2 节 |
| 2 | 设置浮层形态 | 采纳**覆盖式 Panel**。断言比初版更严格：必须保持 `TextBox → 容器 → GroupBox` 两层结构、GroupBox.DisplayRectangle 完整包含两个 Cookie 控件 Bounds、同级须有 `Text=="性能设置"` 的 GroupBox 且 `perf.Top >= optGroup.Bottom`。按第 7.2 节硬约束图实现。**架构师探针 + team-lead 追加拍板**：方案 ① 照抄现状布局（`Padding=(10,2,10,6)`、`Height=272`、`Table(3,8,30)`、txt(1,6)/cmb(1,7)），严禁用 2 行 TLP + 大 Padding（常量积恒 False）；不 reparent——compatHost 全程常驻，只切 `SettingsOverlay.Visible` | 第 2、4、7.1、7.2、8.2、8.3 节 |
| 3 | 左栏待处理列表 | **保留**只读摘要 + 点击联动右栏。理由：参考图本身就有「待处理 0 个」列表区，去掉反而不像 | 第 2、6-流程 3、8.2 节 |
| 4 | 「切换颜色」按钮 | **P2 占位但显示**：禁用态 + tooltip「即将推出」，不做深浅主题。理由：参考图右上角有该按钮，视觉一致性需要它存在 | 第 1-非目标、2、3.4、8.2 节 |
| 5 | settings.json | **允许新增独立文件，不碰 tasks.json 结构**。优先级 **P1**（工期紧可延后）。边界：只存引擎选择、目录、并发数、5 个主开关、Cookie 来源、性能参数、RE 执行路径选项；**绝不存 Cookie 值本身（只存文件路径）**，绝不写入 tasks.json | 第 1-非目标、2、5.3、7.1-约束 6、8.2、8.3 节 |

**数据修正**：全文用户真实任务基线由「7 条」修正为 **18 条 / 11091 字节**（含请求头），安全基线快照已由 team-lead 保存。涉及第 1 节目标 3、第 7.1 节约束 7、第 8.2 节验收。

---

关键代码依据：`Engine.cs:86,161-229,435-528,677-743`、`MainForm.cs:34-64,104-284,1248-1296`、`Ui.cs:4-24`、`Tests.cs:329-405,791-836`、`QaRegression.cs:56-103`。
