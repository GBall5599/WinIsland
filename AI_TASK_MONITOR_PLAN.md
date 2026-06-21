# WinIsland AI 任务完成提醒改造方案

## 结论

这个方向可行。WinIsland 不需要依赖 Windows 系统通知，也不需要先做 VS Code 插件或 CLI 包装器。第一版可以通过读取 Claude Code / Codex CLI 写在本机的 JSONL 会话文件，实现“打开 WinIsland 后自动检测 AI 会话状态，并在任务完成时通过灵动岛提醒”。

本机验证结果：

- Codex CLI 会话文件存在于 `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\*.jsonl`，当前对话期间 JSONL 会实时更新。
- Claude Code 会话文件存在于 `%USERPROFILE%\.claude\projects\**\*.jsonl`，可解析到 `tool_use`、`end_turn`、`stop_sequence` 等状态标记。
- VS Code 插件版 Claude Code 是否复用同一套 `.claude\projects` 文件，需要通过后续诊断页确认；如果复用，则可被同一套机制覆盖。

## 目标

WinIsland 启动后自动监控本机 AI 编程会话：

- Claude Code CLI
- Codex CLI
- 可能复用 CLI 日志的 Claude Code / Codex VS Code 插件

当 AI 从“正在工作”变成“已完成 / 等待用户查看”时，WinIsland 在顶部灵动岛显示提醒。后续可扩展为“疑似卡住”“需要输入”“用量恢复后可继续”等状态。

## 不做什么

第一版不做：

- 不安装 VS Code 插件。
- 不修改 Claude Code hooks。
- 不修改 Codex `config.toml notify`。
- 不包裹用户的 CLI 命令。
- 不读取或上传完整对话内容。
- 不自动执行 `continue`、`resume` 或任何高权限命令。

## 参考项目

调研后最有参考价值的是 Agent Island：

- GitHub: https://github.com/tristan666666/agent-island
- 本地源码: `research/repos/agent-island`
- 核心实现: `research/repos/agent-island/Sources/Trigger/ActivityMonitor.swift`

它的关键思路是直接读取本地 Claude/Codex transcript JSONL，通过文件修改时间和尾部事件判断会话状态。该项目是 macOS Swift 应用，不能直接用于 Windows，但检测模型可以移植到 WinIsland。

其他参考：

- `research/repos/code-notify`: 成熟的 hooks/notify 方案，但不符合“无感启动”。
- `research/repos/haunt`: 通过 Ghostty 终端标题判断 Claude 状态，只适用于 macOS + Ghostty。
- `research/repos/claude-code-usage-monitor`: Windows 任务栏用量监控，不做任务完成状态判断。

## 数据源

### Claude Code

默认路径：

```text
%USERPROFILE%\.claude\projects\**\*.jsonl
```

Claude Code 的 JSONL 中通常存在 assistant 事件：

```json
{
  "type": "assistant",
  "message": {
    "stop_reason": "end_turn"
  }
}
```

可用于完成状态判断的 `stop_reason`：

```text
end_turn
stop_sequence
stop
```

### Codex CLI

默认路径：

```text
%USERPROFILE%\.codex\sessions\YYYY\MM\DD\*.jsonl
```

Codex 的 JSONL 中会出现事件消息：

```json
{
  "type": "event_msg",
  "payload": {
    "type": "task_complete"
  }
}
```

关键事件：

```text
task_started   开始或继续执行
task_complete  本轮任务完成
```

### WSL 可选路径

如果用户在 WSL 中运行 Claude/Codex，可选扫描：

```text
\\wsl.localhost\<Distro>\home\<user>\.claude\projects\**\*.jsonl
\\wsl.localhost\<Distro>\home\<user>\.codex\sessions\**\*.jsonl
```

WSL 扫描应默认关闭或低频扫描，因为 UNC 路径可能慢、不可达或权限不稳定。

## 状态模型

新增统一状态：

```csharp
public enum AiSessionState
{
    Idle,
    Working,
    NeedsAttention,
    Stalled
}
```

状态含义：

- `Idle`: 没有近期活跃任务，或任务太旧，不应该提醒。
- `Working`: JSONL 最近仍在写入，AI 正在执行。
- `NeedsAttention`: 从工作状态结束，轮到用户查看或继续输入。
- `Stalled`: WinIsland 观察到任务曾经在工作，但之后长时间停止写入，且没有完成标记。

## 检测规则

只扫描最近活跃文件，避免启动时处理大量历史会话。

建议参数：

```text
activeWindow:     18-20 秒
attentionWindow:  30 分钟
needsYouCap:      20 分钟
stallAfter:       5 分钟
stallCap:         15 分钟
tailBytes:        128 KB
tailLines:        200 行
pollInterval:     5 秒
```

### Claude 状态判断

1. 文件最后修改时间距离当前小于 `activeWindow`：判断为 `Working`。
2. 读取文件尾部，倒序查找最近的 assistant 事件。
3. 如果最近 assistant 事件的 `message.stop_reason` 为 `end_turn` / `stop_sequence` / `stop`，且文件仍在 `needsYouCap` 内：判断为 `NeedsAttention`。
4. 如果之前观察到该文件为 `Working`，现在超过 `stallAfter` 没有更新，尾部没有完成标记，并且仍在 `stallCap` 内：判断为 `Stalled`。
5. 其他情况为 `Idle`。

### Codex 状态判断

1. 文件最后修改时间距离当前小于 `activeWindow`：判断为 `Working`。
2. 读取文件尾部，倒序查找最近的 `event_msg`。
3. 如果最近相关事件是 `payload.type == "task_complete"`，且文件仍在 `needsYouCap` 内：判断为 `NeedsAttention`。
4. 如果最近相关事件是 `payload.type == "task_started"`：判断为 `Working` 或等待下一轮确认。
5. 如果之前观察到该文件为 `Working`，现在超过 `stallAfter` 没有更新，尾部没有 `task_complete`，并且仍在 `stallCap` 内：判断为 `Stalled`。

## 通知触发规则

必须基于状态转移触发，不能启动后扫到旧文件就弹通知。

触发：

```text
Working -> NeedsAttention  显示任务完成 / 等待查看
Working -> Stalled         显示任务疑似停住
```

不触发：

```text
Idle -> NeedsAttention     启动时扫到旧完成文件，不弹
Idle -> Working            只记录，不弹
NeedsAttention -> NeedsAttention  不重复弹
Stalled -> Stalled         不重复弹
```

需要为每个 session 维护：

```text
sessionId 或文件路径
provider: Claude / Codex
lastState
lastObservedWorkingAt
lastNotifiedState
lastNotifiedAt
lastFileWriteTime
```

## 实时性方案

第一版使用轮询即可：

```text
每 5 秒扫描一次最近活跃 JSONL
```

体感延迟：通常 0-5 秒。

增强版可以加入 `FileSystemWatcher`：

- 本机 `.claude` / `.codex` 路径使用 watcher 快速触发。
- watcher 收到变更后延迟 300-800ms 再读尾部，避免读到半行。
- 5 秒轮询继续保留，用于防漏。
- WSL 路径不依赖 watcher，只使用低频轮询。

推荐最终策略：

```text
FileSystemWatcher: 低延迟触发
5 秒轮询: 防漏
读取防抖: 300-800ms
完成通知: 只在 Working -> NeedsAttention 时触发
```

## WinIsland 接入设计

### 新增文件建议

```text
WinIsland/Models/AiTaskModels.cs
WinIsland/Services/AiTaskMonitor/AiSessionMonitorService.cs
WinIsland/Services/AiTaskMonitor/AiTranscriptScanner.cs
WinIsland/Services/AiTaskMonitor/ClaudeTranscriptScanner.cs
WinIsland/Services/AiTaskMonitor/CodexTranscriptScanner.cs
WinIsland/ViewModels/MainWindow.AiTasks.cs
```

### 事件模型

服务向主窗口发事件：

```csharp
public sealed class AiTaskStateChangedEventArgs : EventArgs
{
    public AiProvider Provider { get; init; }
    public string SessionKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public AiSessionState PreviousState { get; init; }
    public AiSessionState CurrentState { get; init; }
    public string? ProjectPath { get; init; }
    public DateTime LastWriteTime { get; init; }
}
```

### 主窗口状态优先级

建议将 AI 提醒放在系统通知之后、媒体和待机之前：

```text
系统通知 / 喝水 / 待办
AI 任务完成提醒
文件中转 / 进度
番茄钟
媒体
系统监控 / 待机
```

如果已有 `_isNotificationActive` 正在展示，则 AI 事件可排队或忽略当前轮，避免覆盖用户正在看的通知。

## 灵动岛 UI

新增 `AiTaskPanel`，显示：

```text
CODEX
任务完成，等待你查看
```

或：

```text
CLAUDE CODE
任务完成，等待你查看
```

Stalled 提示：

```text
CLAUDE CODE
任务可能停住了
```

第一版只做提醒，不做点击跳转。后续可扩展：

- 点击打开最近项目目录。
- 点击聚焦 VS Code。
- 点击打开 Windows Terminal。
- 显示最近活跃 session 列表。

## 设置项

在 `AppSettings` 增加：

```csharp
public bool EnableAiTaskMonitor { get; set; } = true;
public bool MonitorClaudeCode { get; set; } = true;
public bool MonitorCodex { get; set; } = true;
public bool MonitorWslAiSessions { get; set; } = false;
public bool EnableAiStalledAlert { get; set; } = false;
public int AiTaskPollIntervalSeconds { get; set; } = 5;
public int AiTaskNotificationSeconds { get; set; } = 6;
```

设置窗口增加：

- 启用 AI 任务完成提醒
- 监测 Claude Code
- 监测 Codex
- 扫描 WSL 会话
- 启用任务疑似停住提醒
- 提醒停留时长

## 诊断页

必须加入诊断信息，方便判断 VS Code 插件是否被覆盖。

诊断内容：

```text
Claude 路径是否存在
Codex 路径是否存在
最近发现的 Claude JSONL 数量
最近发现的 Codex JSONL 数量
最近活跃文件最后修改时间
当前推断状态
最近一次通知时间
WSL 扫描是否启用
```

诊断页只显示路径、时间、大小和状态，不显示对话内容。

## 隐私边界

实现时只读：

- 文件路径
- 文件大小
- 修改时间
- JSONL 尾部有限行中的结构字段

只解析以下字段：

- `type`
- `payload.type`
- `message.stop_reason`
- 可选的 `cwd`、`session id`、时间戳

不显示、不记录、不上传：

- 用户 prompt
- AI 回复正文
- tool input/output
- 项目文件内容

## 性能策略

避免全量解析：

- 只扫描最近 30 分钟修改过的文件。
- Codex 先扫描今天和昨天目录。
- Claude 只取最近修改的若干个 JSONL，例如前 20 个。
- 每个文件只读尾部 128KB / 最多 200 行。
- 文件读取放后台线程，UI 只接收状态变化事件。

## 风险与应对

### VS Code 插件不写 CLI JSONL

风险：插件版 Claude Code 可能使用其他存储。

应对：

- 第一版明确支持 CLI 和复用 CLI 日志的插件场景。
- 诊断页显示是否发现插件对应的实时 JSONL。
- 如果检测不到，再单独研究 VS Code extension storage。

### 完成标记不稳定

风险：某些版本的 Claude/Codex JSONL 格式变化。

应对：

- 完成标记做成可扩展 scanner。
- 保留 mtime + 状态转移判断。
- 遇到未知格式只不提醒，不误报。

### 启动时误报旧任务

风险：扫到历史 `end_turn` 就弹。

应对：

- 必须要求先观察到 `Working`，再发生 `NeedsAttention`。
- `Idle -> NeedsAttention` 不弹。

### WSL 性能

风险：`\\wsl.localhost` 扫描慢。

应对：

- 默认关闭。
- 用户手动启用。
- 低频扫描，例如 10-15 秒。
- 限制扫描目录和文件数量。

## 实施阶段

### 阶段 1: 后台检测服务

目标：

- 实现 Claude/Codex JSONL 扫描。
- 控制台或 Debug 输出状态变化。
- 不接 UI。

验收：

- 当前 Codex 会话能被识别为 `Working`。
- Codex 完成后能产生 `Working -> NeedsAttention`。
- Claude 历史文件可解析完成标记，但启动时不误报。

### 阶段 2: 接入灵动岛通知

目标：

- 新增 AI 面板。
- 新增 `MainWindow.AiTasks.cs`。
- AI 完成时显示灵动岛提醒。

验收：

- Codex 完成后 0-5 秒内弹出 WinIsland 提醒。
- 不覆盖正在展示的喝水/待办/系统通知。
- 提醒结束后恢复媒体/待机状态。

### 阶段 3: 设置和诊断

目标：

- 添加设置项。
- 添加诊断信息。

验收：

- 可以开关 Claude/Codex 检测。
- 可以看到最近活跃 JSONL 文件状态。
- 不显示对话正文。

### 阶段 4: WSL 支持

目标：

- 扫描 WSL distro 下的 `.claude` / `.codex`。
- 降低扫描频率，避免卡顿。

验收：

- WSL Codex/Claude CLI 会话可被发现。
- WSL 不可用时不报错、不阻塞 UI。

### 阶段 5: 增强交互

可选目标：

- 点击通知聚焦 VS Code 或 Terminal。
- 显示最近 AI session 列表。
- Stalled 提醒。
- 支持更多 AI CLI，如 Gemini CLI。

## 第一版建议范围

第一版只做：

```text
Codex CLI + Claude Code CLI 本地 JSONL 监测
Working -> NeedsAttention 完成提醒
5 秒轮询
设置开关
诊断信息
```

暂不做：

```text
WSL
点击跳转
自动 resume
复杂会话列表
VS Code 私有存储适配
```

这样改动最小，风险可控，也能最快验证核心体验。
