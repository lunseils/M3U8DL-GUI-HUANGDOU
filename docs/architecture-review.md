# M3U8DL 深度代码审查 + 架构重构设计

审查人：高见远（Gao）· 架构师
代码基线：`M3U8DL/`（Core.cs 324 行、Engine.cs 914 行、MainForm.cs 1345 行、Ui.cs 121 行、Program.cs 25 行）+ `M3U8DL.Tests/`（Tests.cs 855 行、QaRegression.cs 327 行）
方法：全量读码 + 逐条 grep 复核 + 对关键 WinForms 行为写探针实测（DataGridView.CellValueChanged）+ dotnet 10.0.203 环境确认。

---

## 0. 对你已查实事实的独立复核结论

| 你的判断 | 我的复核 | 证据 / 修正 |
|---|---|---|
| 默认引擎下 8 个死选项 | ✅ **完全证实** | `Engine.cs:86` 分流；8 个字段只在 `ReAsync` 中读取（见下表逐条）。**补充**：不是 100% 死 —— `YtDlpAsync:517-524` 在 Cookie/CBC/403 失败时会自动降级调用 `ReAsync`，此时这 8 项会生效。但这条路径罕见，作为默认 UI 选项确实误导。 |
| 8 项逐条位置 | ✅ 全部命中 | DelAfterMerge 728 / NoDateInName 712 / MuxWhileDownload 729-733 / BinaryMerge 735 / NoLog 713-719 / CheckSegmentsCount 738 / AppendUrlParams 737 / MuxFile 711 |
| 两引擎共用 7 项 | ✅ 证实 | NoSystemProxy 225↔720、NoMerge 226(`--keep-video`)↔734(`--skip-merge`)、ConcurrentDownload 185↔736、MaxThreads 185↔739、RetryCount 187/189↔740、Timeout 191↔741、LimitSpeed 192/195↔742 |
| UI 特有 5 项 | ✅ 证实 | UseYtDlp 86/847、SkipExisting 848、CleanStaleParts 849、AlwaysOnTop（仅 MainForm，不进 Opt）、OnlyParseM3u8 86/722/839 |
| BUG-1 重命名失效 | ✅ 证实 | 已用最小探针实测：程序设置 `cell.Value` **同步触发** `CellValueChanged`；连续两次设同值只触发 1 次。 |
| BUG-2 LoadTasks 无条件 SaveTasks | ✅ 证实，且比你说得更严重 | 见 BUG-2：不只是 mtime，还会**永久落盘**去重/过滤/改名结果，是真实数据丢失路径。 |
| BUG-3 btnStop / btnLocate 从不禁用 | ⚠️ **部分修正** | btnStop 确需修复；**btnLocate 保持启用是正确设计** —— 它的用途就是在空闲态定位已完成文件（MainForm.cs:814 限定完成/失败/已取消），禁用它反而破坏功能。 |
| BUG-4 LocateTaskFile 无选中打开目录 | ✅ 证实 | MainForm.cs:802 |
| BUG-5 --dump 调试遗留 | ✅ 证实 | 全仓 grep：除 Program.cs:16/20 与 MainForm.cs:1312 外零引用；Tests.cs / QaRegression.cs **均未引用**。且 `C://tmp//` 通常不存在，--dump 模式会直接抛异常。 |
| BUG-6 Theme 17 常量中 Border 无引用 | ✅ 证实，且**只有 Border 一个** | `Card/FieldBg/FieldBorder/Hover/Pressed` 在 Ui.cs 内部以**非限定名**使用（40-42、56、87），容易误 grep 成"未使用"。**修正建议：不要删 Border，新空态虚线边框正好复用它**（见 §2.5）。 |
| MainForm.cs:967-973 缩进错乱 | ✅ 是**纯格式问题，无行为 bug** | `lock (queueLock) still = ...;` 是合法的单语句 lock 体；赋值确实在锁内。仅影响可读性。 |
| queue.Select 在 lock 内 → 死锁/竞态 | ❌ **无死锁** | 全量核查所有 `lock (queueLock)` 临界区（MainForm.cs:516,521,544,566,580,595,602,653,669,808,910,948,955,970,1059,1107,1191,1255）**均无 await、无嵌套反向加锁**，全部是短临界区。`rowLock` 与 `queueLock` 从不嵌套取得（`SaveTasks` 只取 queueLock，`OwnsRow` 只取 rowLock）。 |
| SaveTasks 每加一条写一次盘 | ✅ 证实 | MainForm.cs:545；另 1137 每个终态也写一次，1081 批次结束再写一次。 |
| FindFinished / Exists 重叠 | ✅ 证实，但**建议不要合并** | 语义不同：FindFinished(Core.cs:135-167) 排除 IsTempOutput(154)、做兄弟任务前缀排除(142,158)、排序选最优(166)、**不看 startedAt**；Exists(Core.cs:179-202) 不排除兄弟、**看 startedAt(196)**、返回首个命中。合并会改变 SkipExisting 语义。 |
| CleanupStaleParts / CleanupTaskTemps 区别不清 | ✅ 证实 | 前者 `Naming.Matches`(236)+10 秒年龄门槛(239)；后者严格 `name + '.'` 前缀(263-266)+无年龄门槛（签名刷新场景必须用）。 |
| _procs / TerminateAll 竞态 | ✅ **有真实竞态**（详见 BUG-11） | ConcurrentDictionary.Keys 枚举本身安全，但 Stop 后仍可能新起进程；`StartProc`(829-848) 不检查 `_isRunning()`。 |
| OwnsRow token 防住过期行覆盖 | ⚠️ **只防住一半** | token 只保护 `RunOneTaskAsync` 的入口(990)和终态(1037)；`SetStatus/SetProgress/AppendLog` 的 BeginInvoke 回调**没有 token 守卫**。 |
| MaxThreads 双语义 | ✅ 证实，是设计缺陷 | Engine.cs:185 `--concurrent-fragments` vs 739 `--thread-count`，默认 32、上限 100（MainForm.cs:43）。另有第三个并发旋钮 `nudConc`（42，默认 1、上限 20）= 队列并发。**但不能直接改字段名**（见 §5）。 |
| 两个判定边界重叠导致重复降级 | ⚠️ **重叠存在，但"重复降级"结论不成立** | `IsCbcDecryptFailure` 在 310 和 345 都被调用，确实重复；但 `YtDlpAsync` 里两检查是**顺序执行**：495 循环重拉 2 次 → 517 才降级 RE。不会重复降级，只会**白跑 2 次 yt-dlp**。且改这个判定会破坏 Tests.cs:280-281。 |

### 反射契约完整清单（比 team-lead 给的更长，重构必须全部保住）

`Tests.cs:735-743` 的 `GetField` 用 `BindingFlags.Instance | NonPublic`，`Call` 用 `f.GetType().GetMethod(name, Instance|NonPublic|Public)` —— 都是**精确类型查找**，所以：

- **字段（10 个，必须留在 MainForm 类型本身）**：`txtCookieFile`(351)、`cmbCookieBrowser`(352)、`txtExe`/`txtWorkDir`（`SetText` 用，765）、`chk`(792)、`grid`(812/826)、`queue`(820)、`txtLog`(836)。
- **MainForm 私有方法（6 个，必须留在 MainForm 类型本身）**：`Snapshot`(393/397)、`SaveTasks`(600)、`AddTask`(804)、`StartBatch`(806)、`PasteTasks`(808)、`CopySelection`(816)。
- **Engine 方法（2 个）**：`BuildYtDlpCmd`（非公开实例，签名 `List<string>? (string,string,Opt,IReadOnlyList<string>,bool)`，Tests.cs:329 / QaRegression.cs:324）；`FindOnPath`（静态非公开，Tests.cs:206）。
- **Engine 公开静态方法（4 个）**：`FindHttpStatus` / `IsCookieOrDecryptFailure` / `IsPlaylistSignatureExpired` / `DiagnoseYtDlpError`。
- **结论：文件拆分必须用 `partial class MainForm`，绝不能把上述方法移到独立 Helper 类**（否则 `GetMethod` 返回 null）。

> 推论：BUG-1 的重命名不能靠反射测 `RenameSelected()`（它弹模态 `PromptDialog`，测试看门狗 `Tests.cs:35,75-100` 会自动关掉标题为"提示"的窗口）。应抽出一个非模态的 `ApplyRename(int row, string newName)` 供测试直接反射调用。

---

## 1. Bug 清单（按严重级别排序）

### 🔴 严重（数据安全 / 核心功能失效）

#### BUG-1 右键「重命名任务」不生效
- **位置**：`MainForm.cs:618-631`（`RenameSelected`）+ `MainForm.cs:633-635`（`OnCellValueChanged` 早退）
- **现象**：表格显示新名，`queue[row].Name` 仍是旧名，不写盘。下载用旧名、重启后还原。
- **根因**：628 行 `updating = true` → 629 行设 `Value` → 实测**同步触发** `CellValueChanged` → 635 行 `if (updating ...) return;` 直接吞掉 → 653-660 的模型更新与 SaveTasks 全部跳过。
- **精确修复**（改 MainForm.cs:624-631）：
```csharp
var row = rows[0];
var oldName = queue[row].Name;                    // 从模型取，不从 grid 取
var asked = PromptDialog.Ask(this, $"任务 {row + 1} 的新名称：", "重命名任务", oldName);
var newName = Naming.Sanitize(asked, "");
if (newName.Length == 0 || newName == oldName) return;

lock (queueLock) queue[row].Name = newName;       // ① 先改模型（不依赖事件）
updating = true;                                   // ② 只抑制事件回写
try { grid.Rows[row].Cells[0].Value = newName; }
finally { updating = false; }
SaveTasks();                                       // ③ 显式落盘
AppendLog($"✏ 任务 {row + 1} 重命名：{oldName} → {newName}");
```
  另在 `OnCellValueChanged` 的 657 行 `if (oldName == newName) return;` 已存在，**无需再加"值没变"判断**；实测同值 set 不触发事件，且 657 行已覆盖交互式提交同值的场景。为可测性，把 626-631 抽成 `void ApplyRename(int row, string newName)`（非模态），`RenameSelected` 只负责弹窗后调用它。
- **严重级别**：严重
- **测试影响**：现有 199 项无覆盖；**需新增** `Call(f, "ApplyRename", 0, "新名")` 后断言 `queue[0].Name == "新名"` 且 grid 同步。

#### BUG-2 LoadTasks 结尾无条件 SaveTasks 造成真实数据丢失
- **位置**：`MainForm.cs:1296`（`SaveTasks()`），配合 1284-1291 的过滤/去重/改名
- **现象**：即使本次没有新增任务也重写 `tasks.json`；更糟的是加载过程中的**有损规范化被永久写回**：
  - 1286：name/url 为空的任务被丢弃（`continue`，未计数）
  - 1287-1288：按 `name|url` 去重，重复项被丢弃（仅 `skipped++` 记日志）
  - 1290：`下载中/排队中` 被改写成 `待下载`
  - `AddTask:513`：`Naming.Sanitize` 可能改名（如 `A:B` → `A_B`）
- **根因**：读取是"宽容解析 + 就地修复"，却把修复结果当成用户意图立即持久化。若文件曾被写到一半（旧版没有原子写）、或用户手工编辑过 JSON，一次启动就不可逆地丢数据。`File.Move(tmp, dest, true)` 虽是同卷原子替换，但仍改 mtime、且无 `.bak`。
- **精确修复**（`LoadTasks` 1264-1306）：
```csharp
var dirty = false;                                  // 新增
...
if (name.Length == 0 || url.Length == 0) { skipped++; dirty = true; continue; }
if (!seen.Add(key)) { skipped++; dirty = true; continue; }
if (status is "下载中" or "排队中") { status = "待下载"; resumed++; dirty = true; }
var sanitized = Naming.Sanitize(name, $"task_{grid.Rows.Count + 1}");
if (!string.Equals(sanitized, name, StringComparison.Ordinal)) dirty = true;
AddTask(name, url, status, t.Headers ?? []);
added++;
...
finally { loading = false; }
if (dirty) SaveTasks();                             // 替换原 1296 无条件调用
```
  建议同时（`SaveTasks` 1257-1259）：写盘前 `if (File.Exists(TasksFile)) File.Copy(TasksFile, TasksFile + ".bak", true);`，保留一份可回滚副本。
- **严重级别**：严重（历史事故根因）
- **测试影响**：现有测试都用隔离 APPDATA，不受影响（QaRegression.cs:58 明确注释依赖 load→save 不污染真实库）。**需新增**用例：写入含 1 条重复项的 tasks.json → 构造 Form → 断言文件字节与 mtime 均未变（无 dirty 场景）与重复项被正确落盘（dirty 场景）。

#### BUG-7 `Ui()` 全用 BeginInvoke + 缺 token 守卫 → 状态回写竞态与丢失
- **位置**：`MainForm.cs:1324-1333`（`Ui`）、`1019/1045`（SetStatus）、`1025`（RunAsync 回调）、`983`（OnBatchFinished）、`1081`（SaveTasks）、`1105-1138`
- **现象**：
  1. 任务终态 `Ui(() => SetStatus(idx,"完成"))`（1045）是**异步排队**；批次结束 `Ui(() => OnBatchFinished(epoch))`（983）也是异步排队，两者来自不同线程，**结束顺序不保证** → `SaveTasks()`(1081) 可能把 `"下载中"` 写盘；下次启动被 1290 归一成 `待下载`，"完成"记录丢失。
  2. 旧批次的 `_progress`/`_log` 回调（Engine.cs:580/586 经 MainForm:94/92 排队）没有 `OwnsRow` 守卫，可能在新批次开始后覆盖同一行。
- **根因**：`Ui()` 只有 fire-and-forget 的 `BeginInvoke`，没有同步变体；`OwnsRow`（1052-1055）只在 `RunOneTaskAsync` 入口(990)与终态(1037)使用，未贯穿 UI 写入点。
- **精确修复**：
```csharp
// MainForm.cs:1324 附近新增
void UiSync(Action a)
{
    if (IsDisposed || Disposing) return;
    try {
        if (InvokeRequired) Invoke(a);              // 同步，保证顺序
        else a();
    } catch (InvalidOperationException) { }         // 句柄销毁竞态
}

// RunOneTaskAsync:1019 / 1025 / 1045-1047 全部加 token 守卫
Ui(() => { if (OwnsRow(idx, myToken)) SetStatus(idx, "下载中"); });
...
finally {
    if (OwnsRow(idx, myToken))
        UiSync(() => {                             // 终态必须同步落定
            if (!OwnsRow(idx, myToken)) return;     // 双重检查（UI 线程上再验一次）
            ... SetSize/SetStatus ...
            SaveTasks();                           // 把 1081 的保存前移到这里
        });
}
```
  `OnBatchFinished`(1075-1082) 中的 `SaveTasks()` 保留作为兜底，但改为 `SaveTasksDeferred()`（见 BUG-8）。
- **严重级别**：严重（状态持久化一致性）
- **测试影响**：`WaitDone`（Tests.cs 轮询 Snapshot）依赖异步更新，改同步后仍兼容；**需新增**"停止→立刻重启队列→旧批次日志不得覆盖新行"回归。

### 🟠 一般（功能误导 / 性能 / 可靠性）

#### BUG-3 空闲时「停止队列」仍可点
- **位置**：`MainForm.cs:1084-1091`（`SetBatchButtons`）；调用点 900（`SetBatchButtons(false)`）、1079（`SetBatchButtons(true)`）
- **现象**：btnStop 从不禁用；内部 `StopBatch:1068 if (!batchRunning) return;` 兜底不崩，但 UI 误导。
- **根因**：方法体只列了 4 个按钮。
- **精确修复**：1090 行后加 `btnStop.Enabled = !enabled;`。**不要动 btnLocate**（见 §0 修正说明）。另建议 900 行前把 `btnStop.Enabled = true` 的时机与 `batchRunning = true` 保持同一 UI 调用内。
- **严重级别**：一般
- **测试影响**：无。

#### BUG-4 「定位文件位置」未选中却打开目录
- **位置**：`MainForm.cs:802`
- **现象**：`if (sel.Count == 0) { OpenSaveDir(); return; }` —— 名为"定位文件"，却静默改开目录，与 823-824 的"找不到才回退开目录（且有日志）"不一致。
- **精确修复**：`if (sel.Count == 0) { MessageBox.Show(this, "请先选中要定位的任务", "提示"); return; }`。若产品确实要保留"打开保存目录"，另设独立按钮/文字链（新控件树已含）。
- **严重级别**：一般
- **测试影响**：无（模态框；看门狗会自动关闭）。

#### BUG-5 `--dump` 调试遗留
- **位置**：`Program.cs:16-22`、`MainForm.cs:1312-1322`
- **现象**：硬编码 `C://tmp//dump.txt`，该目录通常不存在 → `StreamWriter` 抛异常，`--dump` 模式直接崩溃。全仓零引用（含测试）。
- **根因**：一次性调试代码进入发布路径。
- **精确修复**：**删除 Program.cs:16-22 整段**；`DumpLayout` 改为：
```csharp
#if DEBUG
    public void DumpLayout(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), "m3u8dl-layout.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        ...
    }
#endif
```
  理由：既保留排障能力，又不让发布版暴露无意义 API 与崩溃路径。若不想留，直接删除两处亦可，测试零影响。
- **严重级别**：建议
- **测试影响**：无（已 grep 确认 Tests.cs / QaRegression.cs 零引用）。

#### BUG-8 SaveTasks 写盘风暴（每任务 2~3 次全量序列化）
- **位置**：`MainForm.cs:545`（AddTask）、`1137`（SetStatus 终态）、`1081`（批次结束）、`570/587/613/660`
- **现象**：批量粘贴 100 条 = 100 次 `Directory.CreateDirectory` + 全量 JSON 序列化 + `File.WriteAllText` + `File.Move`；下载完又是 N 次。写入量 O(N²)。
- **根因**：无合并/防抖。
- **精确修复**：**保留原 `SaveTasks()` 名字与行为不动**（Tests.cs:600 直接反射它），新增：
```csharp
readonly System.Threading.Timer _saveTimer;   // 构造：DueTime=Infinite, Period=Infinite
void SaveTasksDeferred() => _saveTimer.Change(400, Timeout.Infinite);
// 回调内：try { SaveTasks(); } catch { }
```
  - `AddTask:545` → `if (!loading) SaveTasksDeferred();`
  - `SetStatus:1137` → `SaveTasksDeferred();`
  - `PasteTasks` 循环结束后显式 `SaveTasks()` 一次
  - `OnBatchFinished:1081` → `SaveTasks();`（立即，作为批次边界）
  - `OnFormClosing` → `SaveTasks();`（兜底，防丢最后 400ms）
- **严重级别**：一般（1000 任务时 UI 可感知卡顿）
- **测试影响**：低（原方法不变）；需新增"粘贴 100 条只触发 1 次真实写盘"的计数测试（可用临时 APPDATA + 文件 mtime/内容比对）。

#### BUG-9 启动 StartBatch 同步探测 yt-dlp 可冻结 UI 最长 ~64 秒
- **位置**：`MainForm.cs:872` → `Engine.cs:137 YtDlpAvailable` → `Engine.cs:94-134 FindYtDlp` → `139-159 ProbeOk`
- **现象**：`ProbeOk` 每个候选 `WaitForExit(15000)` + `drain.Wait(1000)`，候选最多 4 个（PATH / APPDATA 下每个 Python 目录 / py -m / python -m）→ 最坏 64 秒。首次调用在 UI 线程，窗口假死。
- **根因**：可用性探测放在启动下载的同步路径上。
- **精确修复**：
  1. `StartBatch` 去掉 872 行的同步检查；
  2. 把检查移进 `BatchLoopAsync`（后台线程）开头，失败则 `Ui(() => AppendLog(...))` 并退出；
  3. 或在构造函数里 `Task.Run(() => engine.YtDlpAvailable())` 预热（`FindYtDlp` 内部 `_cmdLock` + `_ytDlpCmd` 缓存已保证只探测一次），结果供状态栏与 StartBatch 读取。推荐 3，兼顾状态栏绿点。
- **严重级别**：一般
- **测试影响**：测试用假 yt-dlp 在 PATH 上，探测很快；改异步后需确保 `WaitDone` 在探测完成前不误判失败（建议保留"首个任务开始前已完成预热"的同步等待，但用 `Task.Wait(TimeSpan)` 限时）。

#### BUG-10 `_procs` / `TerminateAll` 竞态：停止后仍可能启动新进程
- **位置**：`Engine.cs:57,72-78,139-159,646-675,829-848,889`
- **现象**：
  - `TerminateAll:74` 遍历 `_procs.Keys`（快照，安全），但 `StartProc:846` 之后才注册；若进程在 `TerminateAll` 快照之后启动，就逃过 Kill。第二道防线 `ReadProcAsync:874-877` 的 `_isRunning()` 只在**读到一行输出后**触发，静默进程可逃逸。
  - `ProbeOk:144` / `SupportedFlags:654` 也调用 `StartProc` 并注册到 `_procs`，`TerminateAll` 会 "杀" 探测进程（噪音）。
  - `ReadProcAsync` 的 `_procs.TryRemove` 在 889（正常路径）；634/819 的 catch 路径可能绕过，残留已 Dispose 的 Process 条目。
- **精确修复**：
```csharp
// 1) StartProc 加 track 参数并在启动后立即检查运行标志
Process StartProc(IEnumerable<string> cmd, Encoding? enc, bool track = true)
{
    ...
    var p = Process.Start(psi)!;
    if (track) _procs[p] = 0;
    if (!_isRunning()) { try { p.Kill(entireProcessTree: true); } catch { } }
    return p;
}
// ProbeOk:144 / SupportedFlags:654 传 track: false
// 2) TerminateAll 有界重试，覆盖快照后的新进程
public void TerminateAll()
{
    for (var i = 0; i < 3; i++)
    {
        foreach (var p in _procs.Keys) { try { if (!p.HasExited) p.Kill(true); } catch { } }
        if (_procs.IsEmpty) break;
        Thread.Sleep(50);
    }
}
// 3) ReadProcAsync 把 TryRemove 放进 finally（覆盖异常路径）
```
- **严重级别**：一般（"停止"不可靠）
- **测试影响**：低；建议新增 FAKE_MODE="slow"（ sleeps 3 秒）的 Stop 用例。

#### BUG-11 `IsPlaylistSignatureExpired` 与 `IsCookieOrDecryptFailure` 重复判定 → 白跑 2 次
- **位置**：`Engine.cs:339-360`（尤其 345）vs `304-328`（310）；消费点 495 与 517
- **现象**：任何 CBC 解密失败都同时命中两个判定。`YtDlpAsync` 先跑 495 的"重拉播放列表"循环最多 2 次，再跑 517 的 RE 兜底 —— 对**不是签名过期**的 CBC 失败（错误 AES key、源站数据损坏）白跑 3 次 yt-dlp。
- **根因**：345 把 `IsCbcDecryptFailure(low)` 当作"签名过期"的充分条件，而 310 把它当作"Cookie/解密失败"的充分条件，边界完全重叠。
- **⚠️ 改动的测试代价**：`Tests.cs:280-281` 明确断言 `IsPlaylistSignatureExpired("ERROR: Data must be padded to 16 byte boundary in CBC mode") == true`；`Tests.cs:257` 断言诊断文本提到"签名过期"。**收窄判定必须同步改这 2 处断言**。
- **精确修复**（若接受改测试）：
```csharp
// Engine.cs:344-345 替换
if (IsCbcDecryptFailure(low) &&
    (low.Contains("expired") || low.Contains("403") ||
     low.Contains("signature") || low.Contains("auth_key"))) return true;
```
  FAKE_MODE=expired（Fake/Program.cs:91-92 同时输出 "403 Forbidden: expired" 与 CBC 行）仍能命中；FAKE_MODE=cbc（Fake:47 只有 CBC 行）不再空转，直接走 517 降级 RE，Tests.cs:615 的"降级成功"断言仍通过。
  **若不想动测试**：保留现状，仅在 495 循环里加"上次错误与本次完全相同则提前 break"的短路，减少无效重试。
- **严重级别**：一般
- **测试影响**：**高（2 项断言）** —— 必须在同一次提交里改 Tests.cs:280-281。

#### BUG-12 `logLines` 是"调用次数"当"行数"用 → 日志裁剪错行
- **位置**：`MainForm.cs:1206-1213`（AppendLog 1210）、`1215-1230`（TrimLog 1221）
- **现象**：`AppendLog(text)` 中 `text` 常含换行（如 924 的前导 `\n`、Engine 传入的 `string.Join('\n', ...)`），但 `logLines` 每次只 +1。`TrimLog` 用 `GetFirstCharIndexFromLine(excess)` 按"行数"删除，实际条数与行数不符 → 可能删太多或根本不删，日志无限增长。
- **根因**：计数器语义错配。
- **精确修复**：1210 改为 `logLines += text.Count(ch => ch == '\n') + 1;`；或更简单，删掉 `logLines`，`TrimLog` 直接以 `txtLog.Lines.Length - MaxLogLines` 计算 excess（仅在超阈值时调用，O(n) 可接受）。
- **严重级别**：一般
- **测试影响**：低（测试只读 txtLog.Text）。

#### BUG-13 LoadTasks 去重键与 AddTask 清洗后名称不一致
- **位置**：`MainForm.cs:1287`（用原始 name 去重）vs `513`（`Naming.Sanitize`）
- **现象**：`A:B` 与 `A/B` 清洗后都是 `A_B`，去重没拦住，队列出现同名任务；522-523 仅打警告。文件大小统计（Outputs.OwnFilesSize 按名字前缀）会互相混淆。
- **精确修复**：1287 行改为 `var key = Naming.Sanitize(name, "").ToLowerInvariant() + "|" + url;`
- **严重级别**：建议
- **测试影响**：低。

### 🔵 建议（设计层）

#### BUG-14 `MaxThreads` 一名三义
- **位置**：`MainForm.cs:42`（nudConc 队列并发）、`43`（nudThreads 默认 32/上限 100）、`850`（Snapshot）；`Engine.cs:185`（yt-dlp 分片并发）、`739`（RE 线程数）
- **现象**：同一数值在两引擎含义不同；32 个 HLS 分片并发对小播放列表可能触发 CDN 403，而 RE 的 32 线程属常规值。
- **为什么不直接拆字段**：`Opt` 是 `init`-only record，`Tests.cs:194` / `QaRegression.cs:202,235` 用对象初始化器构造；拆成 `Opt.Re` 子记录是安全的（测试没有设置任何 RE 专属字段，已 grep 确认），但纯重命名 `MaxThreads` 会破坏它们。
- **本迭代修复**（零风险）：只改 UI 标签 —— `nudConc` → "同时进行的任务数（队列并发）"；`nudThreads` → "分片并发 (yt-dlp) / 下载线程 (RE)"；"更多设置"面板内按引擎分别标注。
- **后续版本**：`Opt` 增加 `QueueConcurrency` / `YtDlpFragments` / `ReThreads`，保留 `MaxThreads` 作兼容别名。
- **严重级别**：建议
- **测试影响**：无（仅文本）。

#### BUG-15 `FindFinished` / `Exists` 与 `CleanupStaleParts` / `CleanupTaskTemps` 命名不可自解释
- **位置**：`Core.cs:135/179/225/252`
- **修复**：不合并逻辑，只重命名/补文档：`Exists` → `ExistsInThisRun`（更新 3 个调用点 Engine.cs:604、800、MainForm.cs:1028）；`CleanupStaleParts` → `CleanupStalePartsAfterSkip`（调用点 MainForm.cs:1012）；`CleanupTaskTemps` → `CleanupTaskTempsBeforeRetry`（调用点 Engine.cs:500）。若担心外部引用，保留旧名做 `[Obsolete]` 转发。
- **严重级别**：建议
- **测试影响**：先 grep 确认无测试直接调用这两个静态方法（本轮 grep 未见），再改。

---

## 2. WinForms 现代 UI 的具体实现方案（含踩坑清单）

前提：项目已开 `PerMonitorV2`（`M3U8DL.csproj:14`、`Program.cs:13`）与 `AutoScaleMode.Dpi`（`MainForm.cs:89`）。所有自绘尺寸必须按 `DeviceDpi / 96f` 缩放，否则 125%/150% 下圆角与控件比例失真。

### 2.1 圆角卡片 —— 推荐 `CardPanel : Panel` + `Region`

```csharp
internal sealed class CardPanel : Panel
{
    public int CornerRadius { get; }                 // 构造传入 12（逻辑像素）
    protected override void OnLayout(LayoutEventArgs e) { ApplyRegion(); base.OnLayout(e); }
    protected override void OnSizeChanged(EventArgs e){ ApplyRegion(); base.OnSizeChanged(e); }

    void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        float s = DeviceDpi / 96f;
        int r = Math.Min((int)Math.Round(CornerRadius * s), Math.Min(Width, Height) / 2);
        using var path = new GraphicsPath();
        // AddArc 四段弧在这里可用；但若出现接缝/月牙，改用 AddBezier 或直接 RoundRect
        path.AddArc(0, 0, 2*r, 2*r, 180, 90);
        path.AddArc(Width - 2*r, 0, 2*r, 2*r, 270, 90);
        path.AddArc(Width - 2*r, Height - 2*r, 2*r, 2*r, 0, 90);
        path.AddArc(0, Height - 2*r, 2*r, 2*r, 90, 90);
        path.CloseFigure();
        Region?.Dispose();                            // 必须！否则每次 Resize 泄漏 GDI 句柄
        Region = new Region(path);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 画 1px 边框（Region 已裁剪，边框自动是圆角）
        using var p = new Pen(Theme.Border, Math.Max(1, DeviceDpi / 96f));
        e.Graphics.DrawPath(p, ...);                  // 复用缓存的 path
    }
}
```
**踩坑**：
1. ❗`Region` 会裁剪本控件的一切绘制 —— **-shadow 不能画在同一个 CardPanel 的 OnPaint 里**，一定被切掉。
2. 阴影三种方案对比：
   - **推荐（v1）**：parent 背景上画 1~2 层偏移圆角淡色底（`#EEF2F8`）+ 1px `Theme.Border` 描边。零 GDI Interop、DPI 安全、视觉接近参考图。做法是给 CardPanel 外层再套一个 `ShadowHost : Panel`，其内部按 `Padding(0,0,2,2)` 放 CardPanel，ShadowHost 的 OnPaint 用同样的圆角 path 填淡色。
   - 备选：P/Invoke `DwmSetWindowAttribute` 只能作用于顶层窗口，**子控件无效**，排除。
   - 备选：`ControlPaint.DrawBorder3D` 风格太旧，排除。
3. `OnLayout` 首次触发时 `ClientSize` 可能仍为 0 → 必须 `if (Width<=0||Height<=0) return`，否则 `Region` 为空导致控件消失。
4. 不要在构造函数里设 `Region`（尺寸未定）。
5. `Region` 不影响布局：外层 `TableLayoutPanel` 仍按完整矩形分配空间 → 用 `Margin = new Padding(8)` 留缝。
6. 圆角处子控件（TextBox/ComboBox）会露出方形角 → `CardPanel.Padding = new Padding(12, 10, 12, 10)` 保证内边距 ≥ 8px。
7. DPI 运行时变化：`OnLayout` 会因缩放再次触发；若实测未触发，在 `OnDpiChanged` 里清掉缓存 path 并 `PerformLayout()`。
8. 高 DPI 下 `Pen.Width = 1` 是 1 物理像素，看起来偏细 → 用 `Math.Max(1, DeviceDpi/96f)`。

### 2.2 iOS 风格开关 —— 推荐 `ToggleSwitch : CheckBox`（**不是** `Control`）

**关键决策：必须继承 `CheckBox`，不能继承 `Control`。**
原因：测试 `Tests.cs:792` 做的是 `((Dictionary<string, CheckBox>)GetField(f, "chk"))[key].Checked = value;`。若换成自定义 `Control`，字段类型变了，反射强转直接失败。继承 `CheckBox` 后：
- `Checked` 属性由基类提供，测试零改动；
- 空格键切换、Automation/Accessibility 由基类白送；
- 我们只覆盖 `OnPaint`，不画基类方框。

```csharp
internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        BackColor = Color.Transparent;
        Size = new Size(40, 22);                      // 96dpi 逻辑尺寸
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // ❗ 不调用 base.OnPaint(e) —— 否则会画出原生方框
        float s = DeviceDpi / 96f;
        int w = (int)Math.Round(40 * s), h = (int)Math.Round(22 * s);
        int r = h / 2;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ✅ 轨道：FillRectangle 中间 + FillEllipse 两端半圆
        //    不要用 GraphicsPath.AddArc 四段弧 —— team-lead 实测会出现月牙形缺陷
        using var track = new SolidBrush(Enabled ? (Checked ? Theme.Primary : Theme.FieldBorder) : Theme.Cancel);
        g.FillRectangle(track, 0, 0, w, h);           // 先整块矩形
        g.FillEllipse(track, 0, 0, h, h);             // 左半圆
        g.FillEllipse(track, w - h, 0, h, h);         // 右半圆

        // ✅ 滑块：深色阴影椭圆 + 白色椭圆，避免 AddArc
        int d = (int)Math.Round((h - 4 * s));
        int x = (int)Math.Round(Checked ? w - d - 2 * s : 2 * s);
        int y = (int)Math.Round(2 * s);
        using var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
        g.FillEllipse(shadow, x, y + s, d, d);
        using var thumb = new SolidBrush(Color.White);
        g.FillEllipse(thumb, x, y, d, d);

        if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }
}
```
**WFO1000 踩坑（team-lead 实测补充）**：`.NET 10` 的 WFO1000 分析器会对**自定义 Control 子类的公开可读写属性**直接报编译错误（`IsOn` 也一样）。
- 最佳规避：**本控件不新增任何公开可读写属性**，直接用继承的 `Checked`；
- 若确需新增（例如 `public bool IsOn { get; set; }`），必须：
```csharp
[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
[Browsable(false)]
public bool IsOn { get => Checked; set => Checked = value; }
```
  或把类标记为 `[DesignerCategory("")]` / 放在 `DesignTimeVisible(false)` 的类型上。
**其他踩坑**：
1. `UseAntiAlias` 必须在每次 OnPaint 设置（Graphics 状态不保留）。
2. `BackColor = Color.Transparent` 必须配合 `SupportsTransparentBackColor`，否则透明变成父控件背景色快照。
3. 不要在 OnPaint 里 `new Font(...)`；字体走 `Theme.Fonts`。
4. 动画（可选）：用 `Timer` 在 120ms 内插值 `x`，但测试环境应可关闭（检测 `Debugger.IsAttached` 或环境变量），避免 `Checked = true` 后断言时滑块位置未到位。建议 **v1 不做动画**。

### 2.3 进度列 —— `CellPainting` 自绘，**保留文字 Value**

现状（MainForm.cs:1151）cell[2].Value 是 `"45%"`，全局 `ProgressBar bar`（54、1177）显示批次平均。
推荐：**保留文字 Value 不变（测试 Snapshot 读它），另在背后画进度条**：
```csharp
grid.CellPainting += (s, e) =>
{
    if (e.RowIndex < 0 || e.ColumnIndex != 2) return;
    e.PaintBackground(e.CellBounds, true);            // 保留选中/交替行底色
    var txt = Convert.ToString(e.FormattedValue) ?? "";
    var pct = txt.EndsWith('%') && double.TryParse(txt.TrimEnd('%'), out var v) ? v : 0;
    float sc = grid.DeviceDpi / 96f;
    var r = e.CellBounds;
    var track = new Rectangle(r.X + (int)(6*sc), r.Y + r.Height/2 - (int)(4*sc),
                              r.Width - (int)(12*sc), (int)(8*sc));
    using (var bg = new SolidBrush(Theme.Grid)) e.Graphics.FillRectangle(bg, track);
    using (var fg = new SolidBrush(pct >= 100 ? Theme.Done : Theme.Primary))
        e.Graphics.FillRectangle(fg, new Rectangle(track.X, track.Y, (int)(track.Width * pct / 100), track.Height));
    TextRenderer.DrawText(e.Graphics, txt, grid.Font, r,
                          pct >= 100 ? Theme.Done : Theme.Text,
                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    e.Handled = true;
};
```
**踩坑**：
1. 必须先 `e.PaintBackground`，否则选中行高亮丢失。
2. `e.FormattedValue` 才是本地化后的文本，不要直接读 `Value`。
3. DataGridView 默认双缓冲不足 → 建一个 `internal sealed class BufferedGrid : DataGridView { SetStyle(OptimizedDoubleBuffer|AllPaintingInWmPaint, true); }`；**但测试用 `(DataGridView)GetField(f,"grid")` 强转，子类仍可转，安全**。若不想引入新类型，可用反射设 `DoubleBuffered=true`。
4. 进度条只是"视觉增强"，`Value` 仍是字符串 → 199 项测试零影响。

### 2.4 幽灵按钮 / 文字链
- 幽灵按钮：给 `Theme.Button`（Ui.cs:26-49）加可选参数 `bool ghost = false`（**保留现有签名**，14 处调用不变）：
  `BackColor = Color.Transparent`、`FlatAppearance.BorderSize = 0`、`ForeColor = Theme.Primary`、`FlatAppearance.MouseOverBackColor = Theme.Hover`。
- 文字链：用 `LinkLabel`（`LinkColor = Theme.Primary`、`VisitedLinkColor = Theme.Muted`），比 `FlatStyle.Flat` 的 Button 语义更准，且天然支持键盘导航。
- 注意 `FlatAppearance.MouseOverBackColor` 对透明背景的 Button 仍生效，但 `Paint` 可能有边框残影 → 设 `FlatAppearance.BorderColor = BackColor`。

### 2.5 大号空态面板
- `EmptyStatePanel : Panel`，`Dock = Dock.Fill`，叠在 grid 之上（先 Add grid 再 Add emptyState，或 `BringToFront()`）。
- 内容：居中 `Label`（14pt 半透明灰图标字形 + 11pt "还没有任务" + 9pt "点击「粘贴任务」或按 Ctrl+V 添加"），`TextAlign = MiddleCenter`。
- 虚线边框在 `OnPaint` 里用 `Pen { DashStyle = DashStyle.Dash, Color = Theme.Border }` 画圆角虚线 —— **正好复用 BUG-6 中"已无引用"的 `Theme.Border`，因此建议保留而非删除该常量**。
- 显隐：在 `UpdateQueueStatusLabel`（MainForm.cs:1180）里 `emptyState.Visible = grid.Rows.Count == 0;`。
- **WinForms 坑**：`Visible=false` 的控件在 `TableLayoutPanel` 中——若所在行是 `SizeType.Absolute`/`Percent`，空间**仍被占用**；只有 `AutoSize` 行才可能随隐藏而收缩。动态显隐一组控件时，最可靠做法是把它们装进一个独立 `Panel`/`TableLayoutPanel`，切换这个容器的 `Visible`，并把外层行设为 `AutoSize`；若需要精确控制，直接把外层 `RowStyle.Height` 在 0 与期望值之间切换。

### 2.6 底部状态栏依赖可用性绿点
- `StatusStrip` + 3 个 `ToolStripStatusLabel`（`Image` 用缓存的 8×8 圆点位图，由 `Graphics.FillEllipse` 生成，按 DPI 缩放；**不要用 SystemIcons**，视觉不匹配）。
- 数据源：`engine.YtDlpAvailable()`（Engine.cs:137，已缓存）、`File.Exists(o.Executable)`、`OnPath("ffmpeg.exe")`（MainForm.cs:1335）。
- **必须在后台线程探测**（见 BUG-9），否则启动假死。

### 2.7 字体层级
现状全站 9pt（csproj:13、MainForm.cs:85、Ui.cs:36/59/71/89/109）。建议 `Theme.Fonts` 集中定义 5 级（全部按 `DeviceDpi/96f` 缩放）：

| 名称 | 字号 | 用途 |
|---|---|---|
| Caption | 8pt | 徽标、辅助说明、状态栏 |
| Body | 9pt | 表单、DataGridView（保持现状） |
| Subtitle | 11pt Semibold | 卡片标题①②③ |
| Title | 14pt Semibold | 顶部标题 |
| Mono | 10pt Consolas | 日志（保持 MainForm.cs:61） |

DPI 坑：代码里 `new Font("Microsoft YaHei UI", 9F)` 会绕过 `ApplicationDefaultFont`，在 150% 缩放下仍是物理 9pt（偏小）。统一走 `Theme.Fonts.Body` 之类的工厂，并在 `OnDpiChanged` 时重取。

### 2.8 编号徽标 ①②③
- 评价：**值得做，但用自绘而非 Unicode 字符**。Unicode ①②③ 在不同字体下字重/基线不一致。做法：`BadgeLabel : Label`（或直接在卡片标题的 `OnPaint` 里）用 `Graphics.FillEllipse` 画 20×20 圆 + `TextRenderer.DrawText` 居中写数字，颜色 `Theme.Primary`，成本极低且视觉统一。

---

## 3. 新控件树与布局

```
MainForm                              ClientSize=1180×820, MinimumSize=1024×720
│                                     AutoScaleMode=Dpi, StartPosition=CenterScreen
├─ headerPanel       Dock=Top, H=72   BackColor=Card, 底部 1px Theme.Border
│   ├─ pbIcon             24×24       app.ico
│   ├─ lblTitle           AutoSize    14pt Semibold "M3U8 批量下载器"
│   ├─ lblSubtitle        AutoSize    8pt Muted "N_m3u8DL-RE · yt-dlp 双引擎"
│   ├─ flowActions        Dock=Right  btnOpenLog / btnAbout（ghost）
│   └─ lblShortcut        Dock=Bottom 8pt Muted "Ctrl+V 粘贴 · Del 删除 · F2 重命名"
├─ bodySplit         Dock=Fill        SplitContainer
│   │                                 FixedPanel=Panel1, SplitterDistance=270(×DPI)
│   │                                 Panel1MinSize=240, Panel2MinSize=520
│   │                                 BorderStyle=None, SplitterWidth=8
│   ├─ Panel1 左栏 270px  (AutoScroll=true, Padding=12)
│   │   ├─ CardPanel "① 添加任务"          H=132
│   │   │    txtExe + 选择 / txtWorkDir + 选择 / [仅RE] txtMux + 选择
│   │   ├─ CardPanel "队列概览"            H=64
│   │   │    lblCount "共 N 项 · 完成 M" + 迷你进度条
│   │   ├─ CardPanel "② 下载选项"          AutoSize (~300)
│   │   │    ToggleSwitch useYtDlp / txtCookieFile / cmbCookieBrowser
│   │   │    [仅RE] 8 个 ToggleSwitch（见 §5.1）
│   │   │    通用 ToggleSwitch: noSystemProxy / noMerge / skipExisting /
│   │   │                      cleanStaleParts / onlyParseM3u8 / alwaysOnTop
│   │   ├─ CardPanel "更多设置"            默认折叠 H=32 / 展开 H=150
│   │   │    nudConc(队列并发) / nudThreads / nudRetry / nudTimeout / nudSpeed
│   │   ├─ btnStart         Dock=Top H=40  圆角8 Theme.Primary 白字 11pt
│   │   ├─ btnStop          Dock=Top H=36  仅运行时 Enabled（BUG-3）
│   │   └─ flowLinks        Dock=Top H=28  LinkLabel: 重试失败 / 清空任务 / 打开保存目录
│   └─ Panel2 右栏
│       └─ rightLayout     TableLayoutPanel 1列×3行, Padding=12
│           ├─ Row0 Absolute 44   CardPanel "③ 下载队列" + lblCount + btnPaste/btnDelete/btnLocate
│           ├─ Row1 Percent 100   CardPanel → BufferedGrid(grid) + emptyState(overlay)
│           └─ Row2 Absolute 160  CardPanel → txtLog（Consolas 10pt）
└─ statusStrip        Dock=Bottom H=26  SizingGrip=false
    ├─ dotYtDlp + "yt-dlp 可用"
    ├─ dotReRe     + "N_m3u8DL-RE 可用"
    ├─ dotFfmpeg   + "FFmpeg 未检测到"
    └─ lblTip      Spring=true, 右对齐 "就绪"
```
**分栏选型**：用 `SplitContainer` 而非 `TableLayoutPanel` 百分比列。理由：TLP 百分比列用户无法拖动；SplitContainer 天然提供拖拽手柄，且 `SplitterDistance` 只需在 DPI 变化时按 `270 * DeviceDpi/96f` 重算一次。`SplitterDistance` 建议持久化进 `settings.json`（见 §4）。
**窗口尺寸**：默认 1180×820（比现状 1020×970 更宽更矮，贴合参考图横版比例），最小 1024×720。日志区固定 160、卡片间距统一 12、卡片内边距 12/10。

---

## 4. 代码组织重构方案

### 4.1 必须的约束（再强调）
- `MainForm` 必须是 `partial class`，反射的 10 个字段 + 6 个方法**留在同一类型**。
- `Engine.BuildYtDlpCmd` 签名不动；4 个公开静态诊断方法不动。
- `Tests.csproj:16-23` 的 `<Compile Include>` 必须同步扩充（SDK 风格项目需要显式列出每个新 `.cs`。当前 `EnableDefaultCompileItems=false`）。

### 4.2 新文件清单

| 文件 | 类名 | 职责 | 从 MainForm 移走的方法 |
|---|---|---|---|
| `MainForm.cs`（保留） | `partial class MainForm` | 字段声明、构造、事件接线、UI 状态刷新、日志、工具 | 保留：`Ui`/`UiSync`、`OnCellValueChanged`、`RevertName`、`SetStatus`/`SetProgress`/`SetSize`、`RecalcProgress`、`UpdateQueueStatusLabel`、`AppendLog`/`TrimLog`、`OnPath`、字段与构造 |
| `MainForm.Layout.cs`（新） | `partial class MainForm` | 纯 UI 构建，不含业务逻辑 | `BuildUi`(104)、`SetupGrid`(约 296)、`Table`/`Cols`/`MakeLabel`/`AddChk`(362)、`SetBatchButtons`(1084)、EmptyState 显隐 |
| `MainForm.Queue.cs`（新） | `partial class MainForm` | 队列增删改查、剪贴板、文件定位 | `AddTask`(510)、`DeleteSelected`(556)、`ClearAll`(573)、`RetryFailed`(591)、`PasteTasks`、`CopySelection`、`RenameSelected`(618)、新增 `ApplyRename`、`SiblingNames`(1057)、`LocateTaskFile`(790)、`RevealInExplorer`(约 498)、`BrowseExe`(419)/`BrowseDir`(434)/`BrowseMux`(449)/`OpenSaveDir`(482) |
| `MainForm.Download.cs`（新） | `partial class MainForm` | 批次编排、引擎调用、并发与 token | `Snapshot`(约 832)、`StartBatch`、`StartBatchWorker`(约 900)、`BatchLoopAsync`、`RunOneTaskAsync`(987)、`OwnsRow`(1052)、`StopBatch`(1066)、`OnBatchFinished`(1075) |
| `MainForm.Persistence.cs`（新） | `partial class MainForm` | 任务与设置持久化 | `SaveTasks`(1248)、`LoadTasks`(1264)、`TasksFile`(1237)、新增 `SaveTasksDeferred` |
| `Controls/CardPanel.cs`（新） | `CardPanel : Panel` | 圆角 Region + 1px 边框 + DPI | — |
| `Controls/ShadowHost.cs`（新） | `ShadowHost : Panel` | 卡片淡色投影底 | — |
| `Controls/ToggleSwitch.cs`（新） | `ToggleSwitch : CheckBox` | 胶囊开关自绘（§2.2） | — |
| `Controls/BufferedGrid.cs`（新） | `BufferedGrid : DataGridView` | 双缓冲 + CellPainting 进度条 + 空态 overlay | — |
| `Controls/BadgeLabel.cs`（新） | `BadgeLabel : Label` | ①②③ 圆标自绘 | — |
| `Settings.cs`（新） | `AppSettings` + `SettingsStore` | `settings.json` 读写（见下） | — |
| `Ui.cs`（保留） | `Theme` + `PromptDialog` | 配色、主题工厂、字体层级、弹窗 | 保留 `Theme.Button/Field/Combo/Group` 原签名 |

`M3U8DL.Tests.csproj` 更新（第 19-22 行后追加）：
```xml
<Compile Include="..\M3U8DL\MainForm.Layout.cs" />
<Compile Include="..\M3U8DL\MainForm.Queue.cs" />
<Compile Include="..\M3U8DL\MainForm.Download.cs" />
<Compile Include="..\M3U8DL\MainForm.Persistence.cs" />
<Compile Include="..\M3U8DL\Settings.cs" />
<Compile Include="..\M3U8DL\Controls\CardPanel.cs" />
<Compile Include="..\M3U8DL\Controls\ShadowHost.cs" />
<Compile Include="..\M3U8DL\Controls\ToggleSwitch.cs" />
<Compile Include="..\M3U8DL\Controls\BufferedGrid.cs" />
<Compile Include="..\M3U8DL\Controls\BadgeLabel.cs" />
```
⚠️ 注意：`MainForm.Tests` 工程目前**没有** `ImplicitUsings` 对 `System.Drawing` 的引用（WinForms SDK 会带），新控件的 `using System.Drawing` / `System.ComponentModel` 要显式写。

### 4.3 选项持久化（建议做，收益高、风险低）
现状：`Opt` 完全不持久化（`Snapshot` 每次从控件读默认值），重启全部复位。
设计：
- 新文件 `%APPDATA%\NM3U8DL_GUI\settings.json`，**与 tasks.json 完全分离**，不动其格式。
- `AppSettings` 记录：8 个 RE 专属布尔 + 通用布尔 + 4 个性能数 + Cookie 文件/浏览器 + 执行程序/保存目录 + `SplitterDistance` + `MoreSettingsExpanded`。
- `SettingsStore.Load()` 容错：文件不存在 → 返回 `new AppSettings()`（保留当前默认值，如 useYtDlp=true、MaxThreads=32）；字段缺失 → 用默认值；异常 → 记录日志但不阻断启动。
- `BuildUi()` 末尾调用 `ApplySettings()` 把值写回控件；`OnFormClosing` 调用 `SettingsStore.Save(CaptureSettings())`。
- **不要**让 `settings.json` 影响测试：测试用隔离 APPDATA（QaRegression.cs:59），会自动隔离；但 `NewForm`（Tests.cs）会清空 tasks.json，需确认不清 settings.json，或让设置加载在 `loading` 语义下跳过。

### 4.4 `Opt` 是否拆 `Opt.Re` 子记录
- **收益**：8 个 RE 专属选项在类型层面可见；UI 可按 `opt.Re` 动态隐藏；未来删除某选项只改一处。
- **风险**：极低，已 grep 确认 `Tests.cs:194`、`QaRegression.cs:202,235` 的 `new Opt { ... }` **只设置通用字段**，没有任何测试设置 RE 专属字段；`with { CookieFile = ... }` 也不受影响。
- **建议**：**做，但放在 UI 重构之后单独一步**（先不动 Engine 参数构造，避免一次改动太多）。形式：
```csharp
internal sealed record ReOptions
{
    public string MuxFile { get; init; } = "";
    public bool DelAfterMerge { get; init; }
    public bool NoDateInName { get; init; }
    public bool MuxWhileDownload { get; init; }
    public bool BinaryMerge { get; init; }
    public bool NoLog { get; init; }
    public bool CheckSegmentsCount { get; init; }
    public bool AppendUrlParams { get; init; }
}
internal sealed record Opt
{
    ... 通用字段保持原名 ...
    public ReOptions Re { get; init; } = new();
}
```
  `ReAsync` 内把 `o.MuxFile` → `o.Re.MuxFile` 等（8 处，Engine.cs:711-738）；`Snapshot` 里构造 `Re = new ReOptions { ... }`。**所有改动点列在 §5.3 的表里。**

---

## 5. 选项裁剪的架构影响

### 5.1 「仅 RE 引擎时可见」的动态隐藏
- WinForms 里 `Visible = false` 的控件在 `TableLayoutPanel` 中：
  - 行样式是 `Absolute`/`Percent` → **仍占空间**（空白格）；
  - 行样式是 `AutoSize` → 隐藏控件不贡献高度，行会塌缩，但多个控件共享行时行为不直观。
- **推荐方案**：把 8 个 RE 专属 ToggleSwitch 单独放进一个 `reOptionsPanel`（自身是 `TableLayoutPanel`，2 列 4 行），外层卡片行设 `AutoSize`；切换引擎时只做 `reOptionsPanel.Visible = !useYtDlp.IsChecked; reOptionsPanel.PerformLayout();`。若实测仍留白，直接把外层 `RowStyle.Height` 在 0 / 132 之间切换（最可靠）。
- 事件：`useYtDlp.CheckedChanged += (_, _) => ApplyEngineVisibility();`，并在 `ApplySettings` 后调用一次。
- 注意 `chk` 字典里这 8 个 key 必须继续存在且是 `CheckBox`（ToggleSwitch 继承它），测试 792 行才不炸。

### 5.2 4 个性能参数折叠进「更多设置」
- **推荐：主窗体内的可折叠面板，不是独立 Form**。理由：
  - 独立 Form 需要额外 ShowDialog/回写逻辑，`Tests.NewForm` 只构造主窗体 → 弹窗会破坏端到端测试；
  - 折叠面板能保持 `nudThreads/nudRetry/nudTimeout/nudSpeed` 仍是 MainForm 的字段（测试未反射它们，但保持简单）；
  - 参考图也是单窗口布局。
- 实现：`CardPanel` 标题栏加一个 `Button`/`LinkLabel` 切换，容器内 `TableLayoutPanel` 的 `RowStyle.Height` 在 32 / 150 之间切换。折叠状态写进 `settings.json`。

### 5.3 每个选项彻底删除时的确切改动位置清单

| 选项 | Opt 字段 | UI 控件定义 | Snapshot 映射 | Engine 读取 | RE 命令 | 测试引用 | 合计改动点 |
|---|---|---|---|---|---|---|---|
| `DelAfterMerge` | Core.cs:20 | MainForm.cs:194 | MainForm.cs:836 | — | Engine.cs:728 | 无 | 4 |
| `NoDateInName` | Core.cs:21 | MainForm.cs:195 | MainForm.cs:837 | — | Engine.cs:712 | 无 | 4 |
| `MuxWhileDownload` | Core.cs:24 | MainForm.cs:198 | MainForm.cs:840 | — | Engine.cs:729-733 | 无 | 4 |
| `BinaryMerge` | Core.cs:26 | MainForm.cs:200 | MainForm.cs:842 | — | Engine.cs:735 | 无 | 4 |
| `NoLog` | Core.cs:27 | MainForm.cs:203 | MainForm.cs:843 | — | Engine.cs:713-719 | 无 | 4 |
| `CheckSegmentsCount` | Core.cs:28 | MainForm.cs:204 | MainForm.cs:844 | — | Engine.cs:738 | 无 | 4 |
| `AppendUrlParams` | Core.cs:30 | MainForm.cs:202 | MainForm.cs:846 | — | Engine.cs:737 | 无 | 4 |
| `MuxFile` | Core.cs:19 | MainForm.cs:36(txtMux)、127、140-145、449(`BrowseMux`) | MainForm.cs:835 | — | Engine.cs:711 | 无 | 8 |

额外统一改动点（删任意选项都要做）：
- 若已实施 `Opt.Re` 子记录：`Settings.cs` 的 `AppSettings` 字段 + `ApplySettings`/`CaptureSettings` 映射各 1 处。
- 若采用动态隐藏：`MainForm.Layout.cs` 的 `reOptionsPanel` 子控件清单 1 处。
- `Fake/Program.cs:22-27` 的 `--help` 假参数列表：若删除后 Fake 仍列出该参数不影响测试（Fake 只是回显），但建议同步删以免误导。
- **确认：8 个选项在 Tests.cs / QaRegression.cs 中零引用**（已 grep），所以删除不需要改测试 —— 这是裁剪的低风险区。
- 若删除 **共用**选项（NoMerge / NoSystemProxy / ConcurrentDownload / MaxThreads / RetryCount / Timeout / LimitSpeed）则**必须**同时改 Engine 两条路径 + 测试（`Tests.cs:194-199`、`QaRegression.cs:202,235` 构造了 MaxThreads/RetryCount/Timeout），风险高，**不建议本轮做**。

---

## 6. 风险清单

| 风险 | 类别 | 触发改动 | 缓解 |
|---|---|---|---|
| 反射契约断裂 | 测试 | 把 MainForm 方法/字段移到独立类、`ToggleSwitch` 不继承 `CheckBox`、`Engine.BuildYtDlpCmd` 签名变化 | 全部用 `partial class`；ToggleSwitch 继承 CheckBox；改动后立即 `dotnet run --project M3U8DL.Tests` 跑全量 199 项 |
| `Tests.csproj` Compile 漏加新文件 | 构建 | 新增 6+ 个 .cs 未列入 `<Compile Include>`（`EnableDefaultCompileItems=false`） | 按 §4.2 的清单一次性加全；CI 第一步就编译 Tests |
| BUG-11 判定收窄破坏 2 项断言 | 测试 | 改 `IsPlaylistSignatureExpired` | 必须同提交改 `Tests.cs:280-281`；`Tests.cs:257` 诊断文本断言也要复核 |
| tasks.json 数据丢失 | **数据安全** | BUG-2 修复不彻底、`SaveTasksDeferred` 在退出前未 flush | 加 dirty 标志 + `.bak` backup + `OnFormClosing` 强制 `SaveTasks()`；迁移前先手工备份真实 `%APPDATA%\NM3U8DL_GUI\tasks.json` |
| 重命名修复引入重复 SaveTasks | 性能 | BUG-1 里 `SaveTasks()` + `OnCellValueChanged` 也被触发 | 已用 `updating` 抑制事件；`ApplyRename` 内只调一次 SaveTasks |
| 异步 UI 更新导致断言 flaky | 测试 | `Ui()` 改同步/加 token 守卫 | `WaitDone` 轮询逻辑保持；新增回归用 `UiSync` 保证终态先落定 |
| 自绘控件 GDI 句柄泄漏 | 稳定性 | `CardPanel.ApplyRegion` 每次 Resize 都 `new Region` | 先 `Region?.Dispose()`；DPI 变化时缓存 path 复用；用任务管理器观察句柄数 |
| 自绘阴影被 Region 裁剪 | 视觉 | 试图在 CardPanel.OnPaint 画阴影 | 用独立 `ShadowHost` 父容器（§2.1） |
| WFO1000 编译错误 | 构建 | 自定义 Control 新增 `public` 可读写属性 | 不新增属性（复用 CheckBox.Checked）；必须新增时加 `[DesignerSerializationVisibility(Hidden)]` |
| 高 DPI 下圆角/开关变形 | 视觉 | 硬编码 px 尺寸 | 所有尺寸乘 `DeviceDpi/96f`；125%/150% 双分辨率人工验收 |
| SplitterDistance 在不同 DPI 下错位 | 视觉 | 固定 270 | 启动时与 `OnDpiChanged` 都按 DPI 重算并夹到 [240, 520] |
| `settings.json` 污染测试隔离 | 测试 | 新设置文件使用固定路径 | 必须走同一个 `APPDATA` 环境变量（.TasksFile 的写法，MainForm.cs:1237-1245）；新增测试断言 settings 也隔离 |
| 性能参数语义混淆恶化 | 体验 | 直接把 MaxThreads 拆名 | 本轮只改 UI 文案；字段拆分另起一步并保留兼容别名 |

---

## 7. 实施顺序（每步可独立验证）

| 步骤 | 内容 | 验证方式 | 预估风险 |
|---|---|---|---|
| **S0 基线** | 备份 `tasks.json`；跑一次 `dotnet run --project M3U8DL.Tests`，记录 199/0 基线 | 全绿 | 无 |
| **S1 Bug 修复（无 UI 改动）** | BUG-1（抽出 `ApplyRename`）、BUG-2（dirty + .bak）、BUG-3（btnStop）、BUG-4、BUG-5、BUG-6（保留 Border 改用）、BUG-12、BUG-13 | 全量测试 + 新增 3 项回归（rename / no-op load / logLines） | 低 |
| **S2 文件拆分（行为零变化）** | 建 5 个 partial 文件，原样搬方法；同步 `Tests.csproj` | 全量测试必须仍 199/0；`git diff` 只有移动 | 中（反射契约） |
| **S3 自绘控件基建** | CardPanel / ShadowHost / ToggleSwitch(继承 CheckBox) / BufferedGrid / BadgeLabel / Theme.Fonts | 编译（WFO1000）+ 单独 demo 窗体在 125%/150% 截图验收 | 中（WFO1000 / DPI） |
| **S4 新布局落地** | 按 §3 控件树重写 `MainForm.Layout.cs`；保留所有反射字段名与方法名；把 8 个 RE 选项移入 `reOptionsPanel` | 全量测试 + `LayoutTests`（Tests.cs:42）逐项核对 + 人工截图对比参考图 | **高**（布局回归） |
| **S5 并发与数据一致性** | BUG-7（UiSync + token 守卫）、BUG-8（SaveTasksDeferred）、BUG-9（预热后台化）、BUG-10（_procs） | 全量测试 + 停止/重启队列专项 + 100 条粘贴写盘计数 | 中高 |
| **S6 选项持久化 + `Opt.Re`** | `settings.json`、`AppSettings`；`Opt.Re` 子记录（Engine 8 处 + Snapshot） | 全量测试 + 重启后选项保持 + 清理 8 个选项的改动点表逐项验收 | 中 |
| **S7 选项裁剪** | 按 PM 决策表，用 §5.3 清单逐项删除 | 全量测试（确认 8 项零测试引用） | 低 |

**分步原则**：S1/S2/S3 都不改变视觉与行为，可独立回滚；S4 是唯一大规模视觉变更，建议单独提交以便 diff 审查；S5 涉及并发，必须在 S2 完成后做（否则 method 位置频繁变动会掩盖 bug）。

---

## 8. 结论摘要（给工程师的一句话版）

1. 你查的 8 个死选项、6 个 BUG 全部属实；我要补充 9 条，其中 **BUG-7（BeginInvoke + 无 token 守卫导致状态写盘竞态）** 和 **BUG-9（启动同步探测 yt-dlp 最长冻结 64 秒）** 是优先级仅次于 BUG-1/2 的 must-fix。
2. 两处要修正你的判断：**btnLocate 不应禁用**；**967-973 只是缩进问题、queueLock 无死锁**。
3. 自绘方案：圆角卡片用 `CardPanel + Region`（阴影放父容器，Region 会裁剪）；开关用 **`ToggleSwitch : CheckBox`**（保住反射契约）+ `FillRectangle`+`FillEllipse` 画胶囊；进度条用 `CellPainting` 但**保留文字 Value**；所有尺寸乘 `DeviceDpi/96f`；自定义 Control 新增公开可写属性必须加 `[DesignerSerializationVisibility(Hidden)]`。
4. 文件拆分**必须 partial**，且 `Tests.csproj` 的 Compile 清单要同步加 10 个新文件。
5. 唯一会打断现有测试的重构是 BUG-11 的判定收窄（Tests.cs:280-281）和任何反射契约变更；删除 8 个 RE 专属选项反而是零测试风险的。

---

## 9. 增补：对齐 PM 决策与 LayoutTests 硬约束（后补，优先级高于前文对应章节）

### 9.1 compatHost 方案评估：选方案 C

- **方案 A（真实控件 reparent 进浮层）**：可行但 reparent 会销毁/重建句柄、可能闪烁，`txtCookie.Parent.Parent` 在切换瞬间变化。
- **方案 B（隐藏 compatHost + 浮层视觉壳）**：❌ 否决。浮层必须显示 Cookie/性能控件，要么双份状态，要么仍要 reparent，退化成 A 且多一层容器。
- **✅ 方案 C（推荐）**：`SettingsOverlay : Panel` **本身就是真实控件的永久宿主**，不 reparent、不双份。

```
MainForm
└─ SettingsOverlay : Panel (Dock=Fill, Visible=false, 构造期显式 Size + CreateControl + PerformLayout)
   ├─ CardGroupBox "基础选项"  Dock=Top H=272 Padding=(10,2,10,6)
   │  └─ TableLayoutPanel Table(3,8,30) Dock=Fill
   │     ├─ txtCookieFile 在 (1,6) / cmbCookieBrowser 在 (1,7)
   └─ CardGroupBox "性能设置"  Dock=Top H=64  ← 同父、位于下方
      └─ TableLayoutPanel Table(8,1,28) → nudThreads/nudRetry/nudTimeout/nudSpeed
```

**`CardGroupBox : GroupBox`** 是关键：`SetStyle(UserPaint,true)` + 覆盖 `OnPaint` 画圆角卡片/标题/编号徽标，但仍继承 `GroupBox`，测试的 `as GroupBox` 与 `g.Text == "性能设置"` 全部成立。新文件清单增加 `Controls/CardGroupBox.cs`，`Tests.csproj` 新增 Compile 项由 10 变 11。

### 9.2 ⚠️ 实测：`area.Contains(ctl.Bounds)` 是坐标系错配的脆弱断言

WinForms 探针验证 Tests.cs:365/372：
- `optGroup.DisplayRectangle` 相对 **GroupBox**，原点约 `(10,18)`（Padding + 标题）；
- `ctl.Bounds` 相对 **直接父容器 TLP**，原点 `(0,0)`。

当前通过纯属巧合：`Table(3,8,30)`（MainForm.cs:192）把 Cookie 控件放在 `(1,6)`(218) / `(1,7)`(224)，Bounds 约 `(x≈330,y≈180)`，恰好落入 DisplayRectangle。

**探针反例**：相同控件放进 2 行小 TLP 的 `(0,0)/(0,1)`，完全可见且已布局，`area.Contains()` 仍为 **False**。
**另一发现**：隐藏容器中的 Dock 不会自动拉伸，探针中隐藏 overlay 的 GroupBox 宽度停在默认 200。

**硬性实施约束（否则 LayoutTests 误红 4~6 项）**：
1. `CardGroupBox` 必须继承 `GroupBox`，`Text` 保留 "基础选项" / "性能设置"
2. `Padding=(10,2,10,6)`、`Height >= 272`（性能设置 64）
3. 内层 TLP 必须 `Table(3,8,30)`，Cookie 控件严格在 `(1,6)` / `(1,7)`
4. 两个 GroupBox 同父，"性能设置"在"基础选项"之后布局（`perf.Top >= optGroup.Bottom`）
5. 构造期显式 `Size` + `CreateControl()` + `PerformLayout()`，不能依赖隐藏容器的 Dock
6. `cmbCookieBrowser.Items` 保持 10 项原顺序；`chk` 保留全部 key（测试实际只用 `useYtDlp`，Tests.cs:496/510），值类型必须是 `CheckBox`

### 9.3 因 PM 决策而变更的章节

- **§2.2 开关**：不变，`ToggleSwitch : CheckBox`，`chk["useYtDlp"]` 继续可用。
- **BUG-3**：由设计消除 —— 开始/停止合并双态 `btnRunToggle`（空闲 Primary "开始队列下载"，运行 Danger "停止下载"）。已 grep 确认测试零引用 btnStop，字段可删。`SetBatchButtons` 只管 4 个按钮；按钮文案统一由 `SyncRunButton()` 一处负责。
- **§5.1 RE 选项可见性全改**：不做"仅 RE 可见"。8 项常驻 SettingsOverlay 内「RE 执行路径选项（含 yt-dlp 失败自动兜底）」默认折叠分组（Engine.cs:517-525 用同一 Opt 调 ReAsync）。`onlyParseM3u8` 保留在 Opt（Engine.cs:86 强制 RE）。
- **§5.3**："删除改动位置清单"改为**备用清单**（8 项决定不删，表仍作为未来裁剪索引）。
- **新增 FixedExecutable/FixedWorkDir 清理**：MainForm.cs:10 硬编码另一软件安装路径、:11 `E:/Downloads`，新机器必失效。改为 PATH + 常见目录探测 `N_m3u8DL-RE.exe`（复用 `OnPath`，MainForm.cs:1335），saveDir 默认 `Environment.GetFolderPath(SpecialFolder.UserProfile)/Downloads`，并持久化到 settings.json。⚠️ `Engine.ReAsync:681` 直接用 `o.Executable` 作 FileName，空值会抛异常 → `StartBatch` 需在非 yt-dlp 路径前校验 RE 路径。
- **Theme.Border**：PM 的"无引用"对当前代码成立；建议保留复用于空态虚线/卡片描边。若删除，新增 `Theme.EmptyBorder` 即可，测试零影响。

### 9.4 数据安全与验证缺口

用户真实任务库已增长至 **18 条 / 11091 字节**，安全快照位于 `%TEMP%\m3u8dl_safety\tasks.json.baseline`。S0 步骤增加：迁移前比对快照字节数；BUG-2 修复后用同一文件验收"构造 Form 前后字节与 mtime 不变"（无 dirty 场景）。

**未能执行的验证**：本会话尝试运行基线 `dotnet run --project M3U8DL.Tests` 被沙箱安全策略拦截（非测试失败）。工程师必须在改代码前于本地跑一次并记录 199/0 基线，尤其确认 LayoutTests 当前全绿，否则新 UI 的断言失败无法归因。

### 9.5 阻断修正：定稿 PRD 7.2 的控件摆放实测必挂断言 B

PM 定稿的 `M3U8DL/PRD_UI_Refactor_Final.md` 第 358-363 行把 `txtCookieFile` 放在 2 行 TLP 的 `(1,0)`、`cmbCookieBrowser` 放 `(1,1)`，第 375 行建议 `Padding=(12,8,12,8)`。**按此实现，断言 B 必挂。**

WinForms 探针（构造期 `CreateControl()+PerformLayout()`）逐项实测：

| 配置 | `optGroup.DisplayRectangle` | `txtCookie.Bounds` | 断言 B |
|---|---|---|---|
| Padding(10,2)、2 行、H=120 | (10,18,740,96) | (249,**3**,240,23) | **False** |
| PRD 建议 Padding(12,8)、2 行、H=120 | (12,**24**,736,88) | (248,**3**,239,23) | **False** |
| Padding(12,8)、6/8 行、H=300 | (12,24,…) | (248,3,…) | **False** |
| **现状：Padding(10,2)、3 列×8 行、txt(1,6)/cmb(1,7)、H=272** | (10,18,740,248) | (249,**183**,240,23) | **True** |

第 0 行控件 `Bounds.Y≈3`，恒小于 `area.Y=18~24`。PRD 第 375 行"须留足上边距"方向写反：**加大 GroupBox Padding 只抬高 `DisplayRectangle` 原点，不会移动子控件 `Bounds`**，反而更容易失败。

**修正（二选一）**：
1. **推荐**：照抄现状 —— `Height=272`、`Padding=(10,2,10,6)`、内层 `Table(3,8,30)`、txt `(1,6)` / cmb `(1,7)`。compatHost 隐藏，尺寸不影响左栏视觉。
2. 若坚持紧凑：3 行 TLP，第 0 行留 30px 空 spacer，txt `(1,1)` / cmb `(1,2)`，`Height>=130`。

**reparent 待 team-lead 拍板**：PRD 第 98 行要求打开浮层时重挂载 compatHost、关闭时移回；我仍推荐永久宿主 + 只切 `Visible`（断言同样全过、无句柄重建/闪烁）。若照 PRD 做，必须只重挂载最外层 compatHost，内部层级原封不动。

其余 PRD 决策无冲突：分组标题与折叠态 ✅、settings.json P1 且只存 Cookie 路径/浏览器名不存 Cookie 值 ✅、「切换颜色」P2 显示禁用 ✅、覆盖式 Panel ✅、开始/停止双态按钮 ✅。
