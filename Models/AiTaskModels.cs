using System;

namespace WinIsland
{
    public enum AiProvider
    {
        Claude,
        Codex
    }

    public enum AiSessionState
    {
        Idle,
        Working,
        NeedsAttention,
        Stalled
    }

    public sealed class AiTaskStateChangedEventArgs : EventArgs
    {
        public AiProvider Provider { get; init; }
        public string SessionKey { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string AttentionMessage { get; init; } = string.Empty;
        public AiSessionState PreviousState { get; init; }
        public AiSessionState CurrentState { get; init; }
        public DateTime LastWriteTime { get; init; }
    }
}
