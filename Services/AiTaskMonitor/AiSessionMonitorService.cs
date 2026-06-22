using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace WinIsland.Services.AiTaskMonitor
{
    public sealed class AiSessionMonitorService : IDisposable
    {
        private const int MaxClaudeFiles = 30;
        private const int MaxCodexFiles = 30;
        private const int TailBytes = 128 * 1024;
        private const int TailLines = 200;

        private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AttentionWindow = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NeedsAttentionCap = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ApprovalAttentionAfter = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ApprovalStartupWindow = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StallCap = TimeSpan.FromMinutes(15);

        private readonly object _sync = new();
        private readonly Dictionary<string, SessionHistory> _history = new();
        private System.Threading.Timer? _timer;
        private MonitorOptions _options = MonitorOptions.FromSettings(new AppSettings());
        private int _isScanning;
        private bool _disposed;

        public event EventHandler<AiTaskStateChangedEventArgs>? StateChanged;

        public void Start(AppSettings settings)
        {
            ApplySettings(settings);
        }

        public void Reload(AppSettings settings)
        {
            ApplySettings(settings);
        }

        private void ApplySettings(AppSettings settings)
        {
            if (_disposed) return;

            _options = MonitorOptions.FromSettings(settings);

            if (!_options.Enabled)
            {
                StopTimer();
                lock (_sync)
                {
                    _history.Clear();
                }
                return;
            }

            var interval = TimeSpan.FromSeconds(Math.Max(2, _options.PollIntervalSeconds));
            if (_timer == null)
            {
                _timer = new System.Threading.Timer(ScanTimerCallback, null, TimeSpan.Zero, interval);
            }
            else
            {
                _timer.Change(TimeSpan.Zero, interval);
            }
        }

        private void StopTimer()
        {
            try { _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        }

        private void ScanTimerCallback(object? state)
        {
            if (_disposed || !_options.Enabled) return;
            if (Interlocked.Exchange(ref _isScanning, 1) == 1) return;

            try
            {
                ScanOnce(DateTime.Now);
            }
            catch
            {
                // Monitoring should never break the island UI.
            }
            finally
            {
                Interlocked.Exchange(ref _isScanning, 0);
            }
        }

        private void ScanOnce(DateTime now)
        {
            var snapshots = EnumerateSnapshots(_options, now).ToList();
            var liveKeys = new HashSet<string>(snapshots.Select(s => s.SessionKey));
            var notifications = new List<AiTaskStateChangedEventArgs>();

            lock (_sync)
            {
                foreach (var staleKey in _history.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                {
                    _history.Remove(staleKey);
                }

                foreach (var snapshot in snapshots)
                {
                    if (!_history.TryGetValue(snapshot.SessionKey, out var history))
                    {
                        history = new SessionHistory();
                        _history[snapshot.SessionKey] = history;
                    }

                    var previous = history.LastState;

                    if (snapshot.IsActivelyWriting)
                    {
                        history.WasObservedActive = true;
                        history.LastObservedWorkingAt = now;
                    }

                    history.LastState = snapshot.State;
                    history.LastWriteTime = snapshot.LastWriteTime;

                    if (snapshot.State == AiSessionState.Working)
                    {
                        history.LastNotifiedState = null;
                    }

                    var isApprovalAttention = IsApprovalAttention(snapshot.AttentionMessage);
                    bool shouldNotify =
                        history.LastNotifiedState != snapshot.State &&
                        (
                            previous == AiSessionState.Working &&
                            history.WasObservedActive &&
                            (snapshot.State == AiSessionState.NeedsAttention ||
                             (_options.StalledAlertEnabled && snapshot.State == AiSessionState.Stalled)) ||
                            snapshot.State == AiSessionState.NeedsAttention &&
                            isApprovalAttention &&
                            now - snapshot.LastWriteTime <= ApprovalStartupWindow
                        );

                    if (shouldNotify)
                    {
                        history.LastNotifiedState = snapshot.State;
                        history.LastNotifiedAt = now;

                        notifications.Add(new AiTaskStateChangedEventArgs
                        {
                            Provider = snapshot.Provider,
                            SessionKey = snapshot.SessionKey,
                            DisplayName = snapshot.DisplayName,
                            FilePath = snapshot.FilePath,
                            AttentionMessage = snapshot.AttentionMessage,
                            PreviousState = previous,
                            CurrentState = snapshot.State,
                            LastWriteTime = snapshot.LastWriteTime
                        });
                    }
                }
            }

            foreach (var notification in notifications)
            {
                StateChanged?.Invoke(this, notification);
            }
        }

        public static string BuildDiagnostics(AppSettings settings)
        {
            var options = MonitorOptions.FromSettings(settings);
            var now = DateTime.Now;
            var lines = new List<string>
            {
                $"AI monitor: {(options.Enabled ? "enabled" : "disabled")}",
                $"Claude: {(options.MonitorClaude ? "on" : "off")}",
                $"Codex: {(options.MonitorCodex ? "on" : "off")}",
                $"Poll: {Math.Max(2, options.PollIntervalSeconds)}s"
            };

            try
            {
                var snapshots = EnumerateSnapshots(options with { Enabled = true }, now)
                    .OrderByDescending(s => s.LastWriteTime)
                    .Take(8)
                    .ToList();

                lines.Add($"Recent sessions: {snapshots.Count}");
                if (snapshots.Count == 0)
                {
                    lines.Add("No recent JSONL sessions found.");
                }
                else
                {
                    foreach (var item in snapshots)
                    {
                        var age = now - item.LastWriteTime;
                        var message = string.IsNullOrWhiteSpace(item.AttentionMessage) ? string.Empty : $", {item.AttentionMessage}";
                        lines.Add($"{item.Provider}: {item.State}{message}, {FormatAge(age)} ago");
                        lines.Add($"  {item.FilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Diagnostics failed: {ex.Message}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static IEnumerable<AiSessionSnapshot> EnumerateSnapshots(MonitorOptions options, DateTime now)
        {
            if (!options.Enabled) yield break;

            if (options.MonitorClaude)
            {
                foreach (var file in EnumerateClaudeFiles(now))
                {
                    var snapshot = TryCreateSnapshot(AiProvider.Claude, file, now);
                    if (snapshot != null) yield return snapshot;
                }
            }

            if (options.MonitorCodex)
            {
                foreach (var file in EnumerateCodexFiles(now))
                {
                    var snapshot = TryCreateSnapshot(AiProvider.Codex, file, now);
                    if (snapshot != null) yield return snapshot;
                }
            }
        }

        private static IEnumerable<FileInfo> EnumerateClaudeFiles(DateTime now)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "projects");

            if (!Directory.Exists(root)) yield break;

            IEnumerable<FileInfo> files;
            try
            {
                files = Directory.EnumerateDirectories(root)
                    .SelectMany(project =>
                    {
                        try
                        {
                            return Directory.EnumerateFiles(project, "*.jsonl", SearchOption.TopDirectoryOnly);
                        }
                        catch
                        {
                            return Enumerable.Empty<string>();
                        }
                    })
                    .Select(path => new FileInfo(path))
                    .Where(f => f.Exists && now - f.LastWriteTime <= AttentionWindow)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(MaxClaudeFiles)
                    .ToList();
            }
            catch
            {
                yield break;
            }

            foreach (var file in files)
                yield return file;
        }

        private static IEnumerable<FileInfo> EnumerateCodexFiles(DateTime now)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions");

            if (!Directory.Exists(root)) yield break;

            var dirs = new List<string>();
            for (var offset = 0; offset <= 1; offset++)
            {
                var day = now.Date.AddDays(-offset);
                dirs.Add(Path.Combine(root, day.Year.ToString("0000"), day.Month.ToString("00"), day.Day.ToString("00")));
            }

            IEnumerable<FileInfo> files;
            try
            {
                files = dirs
                    .Where(Directory.Exists)
                    .SelectMany(dir =>
                    {
                        try
                        {
                            return Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly);
                        }
                        catch
                        {
                            return Enumerable.Empty<string>();
                        }
                    })
                    .Select(path => new FileInfo(path))
                    .Where(f => f.Exists && now - f.LastWriteTime <= AttentionWindow)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(MaxCodexFiles)
                    .ToList();
            }
            catch
            {
                yield break;
            }

            foreach (var file in files)
                yield return file;
        }

        private static AiSessionSnapshot? TryCreateSnapshot(AiProvider provider, FileInfo file, DateTime now)
        {
            try
            {
                var lines = TailLinesFromFile(file.FullName);
                var age = now - file.LastWriteTime;
                var isActive = age <= ActiveWindow;
                var state = provider == AiProvider.Claude
                    ? ClassifyClaude(lines, age, isActive)
                    : ClassifyCodex(lines, age, isActive);

                return new AiSessionSnapshot
                {
                    Provider = provider,
                    SessionKey = $"{provider}:{file.FullName}",
                    DisplayName = provider == AiProvider.Claude ? "Claude Code" : "Codex",
                    FilePath = file.FullName,
                    LastWriteTime = file.LastWriteTime,
                    State = state.State,
                    AttentionMessage = state.AttentionMessage,
                    IsActivelyWriting = isActive
                };
            }
            catch
            {
                return null;
            }
        }

        private static AiStateResult ClassifyClaude(IReadOnlyList<string> lines, TimeSpan age, bool isActive)
        {
            var marker = FindLatestClaudeStateMarker(lines);
            if (marker == "complete")
            {
                return age <= NeedsAttentionCap
                    ? AiStateResult.NeedsAttention("任务完成，等待你查看")
                    : AiStateResult.Idle();
            }

            if (marker == "approval")
            {
                return age <= NeedsAttentionCap
                    ? AiStateResult.NeedsAttention("需要你确认或授权")
                    : AiStateResult.Idle();
            }

            if (marker == "tool_use" && age >= ApprovalAttentionAfter && age <= NeedsAttentionCap)
            {
                return AiStateResult.NeedsAttention("可能需要你确认或授权");
            }

            if (isActive || age < StallAfter)
            {
                return AiStateResult.Working();
            }

            return age <= StallCap ? AiStateResult.Stalled() : AiStateResult.Idle();
        }

        private static AiStateResult ClassifyCodex(IReadOnlyList<string> lines, TimeSpan age, bool isActive)
        {
            var marker = FindLatestCodexStateMarker(lines);
            if (marker == "approval")
            {
                return age <= NeedsAttentionCap
                    ? AiStateResult.NeedsAttention("需要你确认或授权")
                    : AiStateResult.Idle();
            }

            if (marker == "tool_call" && age >= ApprovalAttentionAfter && age <= NeedsAttentionCap)
            {
                return AiStateResult.NeedsAttention("可能需要你确认或授权");
            }

            if (marker == "task_complete" || marker == "message")
            {
                return age <= NeedsAttentionCap
                    ? AiStateResult.NeedsAttention("任务完成，等待你查看")
                    : AiStateResult.Idle();
            }

            if (isActive || age < StallAfter)
            {
                return AiStateResult.Working();
            }

            return age <= StallCap ? AiStateResult.Stalled() : AiStateResult.Idle();
        }

        private static string? FindLatestClaudeStateMarker(IReadOnlyList<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    var root = doc.RootElement;
                    if (!TryGetString(root, "type", out var type))
                        continue;

                    if (type == "user")
                    {
                        return "working";
                    }

                    if (IsApprovalMarker(type))
                    {
                        return "approval";
                    }

                    if (type == "attachment" &&
                        root.TryGetProperty("attachment", out var attachment) &&
                        TryGetString(attachment, "type", out var attachmentType))
                    {
                        if (IsApprovalMarker(attachmentType))
                        {
                            return "approval";
                        }

                        continue;
                    }

                    if (type != "assistant")
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("message", out var message))
                    {
                        return "working";
                    }

                    if (TryGetString(message, "stop_reason", out var stopReason))
                    {
                        if (stopReason is "end_turn" or "stop_sequence" or "stop")
                        {
                            return "complete";
                        }

                        if (stopReason == "tool_use")
                        {
                            return "tool_use";
                        }

                        return "working";
                    }

                    if (MessageContainsContentType(message, "tool_use"))
                    {
                        return "tool_use";
                    }

                    return "working";
                }
                catch
                {
                    // Ignore half-written or schema-unknown lines.
                }
            }

            return null;
        }

        private static string? FindLatestCodexStateMarker(IReadOnlyList<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    var root = doc.RootElement;
                    if (!TryGetString(root, "type", out var type))
                        continue;

                    if (type == "event_msg" &&
                        root.TryGetProperty("payload", out var payload) &&
                        TryGetString(payload, "type", out var payloadType))
                    {
                        if (IsApprovalMarker(payloadType))
                        {
                            return "approval";
                        }

                        if (payloadType == "task_complete" || payloadType == "task_started")
                        {
                            return payloadType;
                        }

                        continue;
                    }

                    if (type == "response_item" &&
                        root.TryGetProperty("payload", out var responsePayload) &&
                        TryGetString(responsePayload, "type", out var responseType))
                    {
                        if (responseType is "function_call" or "custom_tool_call")
                        {
                            return "tool_call";
                        }

                        if (responseType is "message" or "reasoning" or "function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")
                        {
                            return responseType;
                        }
                    }
                }
                catch
                {
                    // Ignore half-written or schema-unknown lines.
                }
            }

            return null;
        }

        private static bool MessageContainsContentType(JsonElement message, string contentType)
        {
            if (!message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (TryGetString(item, "type", out var type) && type == contentType)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsApprovalMarker(string value)
        {
            if (value.Equals("permission-mode", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return value.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("confirm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsApprovalAttention(string message)
        {
            return message.Contains("确认", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("授权", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;
            if (!element.TryGetProperty(propertyName, out var property)) return false;
            if (property.ValueKind != JsonValueKind.String) return false;
            value = property.GetString() ?? string.Empty;
            return true;
        }

        private static List<string> TailLinesFromFile(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var readBytes = (int)Math.Min(TailBytes, length);
                stream.Seek(-readBytes, SeekOrigin.End);

                var buffer = new byte[readBytes];
                var total = 0;
                while (total < readBytes)
                {
                    var read = stream.Read(buffer, total, readBytes - total);
                    if (read == 0) break;
                    total += read;
                }

                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(TailLines)
                    .ToList();

                if (length > TailBytes && lines.Count > 0)
                {
                    lines.RemoveAt(0);
                }

                return lines;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age.TotalSeconds < 60) return $"{Math.Round(age.TotalSeconds)}s";
            if (age.TotalMinutes < 60) return $"{Math.Round(age.TotalMinutes)}m";
            return $"{Math.Round(age.TotalHours)}h";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        private sealed class SessionHistory
        {
            public AiSessionState LastState { get; set; } = AiSessionState.Idle;
            public AiSessionState? LastNotifiedState { get; set; }
            public DateTime? LastNotifiedAt { get; set; }
            public DateTime? LastObservedWorkingAt { get; set; }
            public DateTime LastWriteTime { get; set; }
            public bool WasObservedActive { get; set; }
        }

        private sealed class AiSessionSnapshot
        {
            public AiProvider Provider { get; init; }
            public string SessionKey { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string FilePath { get; init; } = string.Empty;
            public string AttentionMessage { get; init; } = string.Empty;
            public DateTime LastWriteTime { get; init; }
            public AiSessionState State { get; init; }
            public bool IsActivelyWriting { get; init; }
        }

        private readonly record struct AiStateResult(AiSessionState State, string AttentionMessage)
        {
            public static AiStateResult Idle() => new(AiSessionState.Idle, string.Empty);
            public static AiStateResult Working() => new(AiSessionState.Working, string.Empty);
            public static AiStateResult Stalled() => new(AiSessionState.Stalled, "任务可能停住了");
            public static AiStateResult NeedsAttention(string message) => new(AiSessionState.NeedsAttention, message);
        }

        private readonly record struct MonitorOptions(
            bool Enabled,
            bool MonitorClaude,
            bool MonitorCodex,
            bool StalledAlertEnabled,
            int PollIntervalSeconds)
        {
            public static MonitorOptions FromSettings(AppSettings settings)
            {
                return new MonitorOptions(
                    settings.EnableAiTaskMonitor,
                    settings.MonitorClaudeCode,
                    settings.MonitorCodex,
                    settings.EnableAiStalledAlert,
                    settings.AiTaskPollIntervalSeconds);
            }
        }
    }
}
