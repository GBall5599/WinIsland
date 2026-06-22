using System;
using System.Windows;
using System.Windows.Media;
using WinIsland.Services.AiTaskMonitor;
using MediaColor = System.Windows.Media.Color;

namespace WinIsland
{
    public partial class MainWindow
    {
        private AiSessionMonitorService? _aiSessionMonitor;
        private AiTaskStateChangedEventArgs? _pendingAiTaskNotification;

        private void InitializeAiTaskMonitor()
        {
            var settings = AppSettings.Load();

            if (_aiSessionMonitor == null)
            {
                _aiSessionMonitor = new AiSessionMonitorService();
                _aiSessionMonitor.StateChanged += AiSessionMonitor_StateChanged;
            }

            _aiSessionMonitor.Start(settings);
        }

        private void ReloadAiTaskMonitor()
        {
            if (_aiSessionMonitor == null)
            {
                InitializeAiTaskMonitor();
                return;
            }

            _aiSessionMonitor.Reload(AppSettings.Load());
        }

        private void DisposeAiTaskMonitor()
        {
            try
            {
                if (_aiSessionMonitor != null)
                {
                    _aiSessionMonitor.StateChanged -= AiSessionMonitor_StateChanged;
                    _aiSessionMonitor.Dispose();
                    _aiSessionMonitor = null;
                }
            }
            catch { }
        }

        private void AiSessionMonitor_StateChanged(object? sender, AiTaskStateChangedEventArgs e)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

                Dispatcher.Invoke(() =>
                {
                    if (_isNotificationActive && AiTaskPanel.Visibility != Visibility.Visible)
                    {
                        _pendingAiTaskNotification = e;
                        return;
                    }

                    ShowAiTaskNotification(e);
                });
            }
            catch
            {
                // Ignore monitor callbacks during shutdown.
            }
        }

        private bool ShowPendingAiTaskNotification()
        {
            if (_pendingAiTaskNotification == null) return false;

            var pending = _pendingAiTaskNotification;
            _pendingAiTaskNotification = null;
            ShowAiTaskNotification(pending);
            return true;
        }

        private void ShowAiTaskNotification(AiTaskStateChangedEventArgs e)
        {
            var settings = AppSettings.Load();
            if (!settings.EnableAiTaskMonitor) return;

            _isNotificationActive = true;
            SetMediaSuppressed(true);
            _notificationTimer.Stop();
            _notificationTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, settings.AiTaskNotificationSeconds));
            _notificationTimer.Start();

            HideAllPanels();

            AiTaskPanel.Visibility = Visibility.Visible;
            AiTaskPanel.Opacity = 0;

            DynamicIsland.IsHitTestVisible = true;
            SetClickThrough(false);

            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = 1.0;

            _widthSpring.Target = e.CurrentState == AiSessionState.Stalled ? 340 : 320;
            _heightSpring.Target = 55;

            TxtAiTaskTitle.Text = e.Provider == AiProvider.Claude ? "CLAUDE CODE" : "CODEX";
            TxtAiTaskGlyph.Text = e.Provider == AiProvider.Claude ? "CC" : "CX";

            ApplyAiTaskText(e);

#if false
            if (e.CurrentState == AiSessionState.Stalled)
            {
                TxtAiTaskBody.Text = "任务可能停住了";
                TxtAiTaskStatus.Text = "CHECK";
                TxtAiTaskStatus.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
                PlayIslandGlowEffect(Colors.Orange);
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(e.AttentionMessage)
                    ? "任务完成，等待你查看"
                    : e.AttentionMessage;
                var needsApproval = message.Contains("确认") || message.Contains("授权");

                TxtAiTaskBody.Text = message;
                TxtAiTaskStatus.Text = needsApproval ? "CHECK" : "DONE";
                TxtAiTaskStatus.Foreground = new SolidColorBrush(needsApproval
                    ? MediaColor.FromRgb(255, 159, 10)
                    : MediaColor.FromRgb(48, 209, 88));
                PlayIslandGlowEffect(needsApproval ? Colors.Orange : MediaColor.FromRgb(0, 217, 255));
            }

﻿#endif
            PlayContentEntranceAnimation(AiTaskPanel);
        }

        private void ApplyAiTaskText(AiTaskStateChangedEventArgs e)
        {
            if (e.CurrentState == AiSessionState.Stalled)
            {
                TxtAiTaskBody.Text = "\u4efb\u52a1\u53ef\u80fd\u505c\u4f4f\u4e86";
                TxtAiTaskStatus.Text = "CHECK";
                TxtAiTaskStatus.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
                PlayIslandGlowEffect(Colors.Orange);
                return;
            }

            var message = string.IsNullOrWhiteSpace(e.AttentionMessage)
                ? "\u4efb\u52a1\u5b8c\u6210\uff0c\u7b49\u5f85\u4f60\u67e5\u770b"
                : e.AttentionMessage;
            var needsApproval = message.Contains("\u786e\u8ba4") || message.Contains("\u6388\u6743");

            TxtAiTaskBody.Text = message;
            TxtAiTaskStatus.Text = needsApproval ? "CHECK" : "DONE";
            TxtAiTaskStatus.Foreground = new SolidColorBrush(needsApproval
                ? MediaColor.FromRgb(255, 159, 10)
                : MediaColor.FromRgb(48, 209, 88));
            PlayIslandGlowEffect(needsApproval ? Colors.Orange : MediaColor.FromRgb(0, 217, 255));
        }
    }
}
