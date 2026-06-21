# 未完成任务

## AI 权限/同意等待状态未可靠通知

### 现象

当 Codex 或 Claude Code 执行到需要用户提供权限、确认、同意继续的步骤时，WinIsland 目前不一定会弹出提醒。

已确认“任务完成”类提醒可以通过本地 JSONL 轮询触发，但“等待权限/同意”这类状态在不同客户端、不同版本中的 JSONL 结构不稳定，目前第一版规则还不能可靠覆盖。

### 当前实现

当前已尝试支持：

- Codex: 识别 JSONL 结构字段中包含 `approval` / `permission` / `confirm` 的事件。
- Claude Code: 识别明确的 approval/permission/confirm 类结构事件。
- Claude Code: 当最近状态为 `tool_use` 且长时间没有继续写入时，提示“可能需要你确认或授权”。

### 未解决点

真实使用中，Codex/Claude 需要用户同意时，可能没有写入稳定、明确的 `approval` / `permission` / `confirm` 结构事件。

也可能存在以下情况：

- CLI 交互等待发生在终端层，但 JSONL 没有立刻记录等待状态。
- VS Code 插件版本和 CLI 版本写入的事件结构不同。
- Claude Code 的 `tool_use` 既可能表示正常工具调用，也可能表示等待权限，不能简单全部当作需要用户确认，否则会误报。
- Codex 新版本在等待确认时可能使用了不同的 `event_msg.payload.type` 或其它字段。

### 下一步

需要在真实“等待权限/同意”的瞬间采集 JSONL 尾部结构字段，只记录字段名和值，不记录 prompt、回复正文、工具输入输出。

建议采集字段：

```text
type
payload.type
message.stop_reason
```

如果这些字段不足以判断，再扩展到只读结构层级字段名，例如：

```text
payload.*
message.*
```

验收标准：

- Codex 请求命令权限、文件写入权限、网络权限等确认时，WinIsland 在 0-5 秒内展开提醒。
- Claude Code 请求工具权限、继续确认、用户同意时，WinIsland 在 0-5 秒内展开提醒。
- 正常工具调用过程中不频繁误报。
