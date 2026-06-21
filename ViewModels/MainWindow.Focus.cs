using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinIsland.Services.FocusTimer;
using WinIsland.Services.SystemMonitor;

namespace WinIsland
{
    public partial class MainWindow
    {
        private SystemStatsService _statsService = null!;
        private PomodoroService _pomodoroService = null!;
        private FocusOverlayWindow _focusOverlayWindow = null!;
        private DispatcherTimer _autoHideTimer = null!;
        private bool _isCompactIsland = false;
        private DateTime _mediaPeekUntil = DateTime.MinValue;

        private const double CompactIslandSize = 34;
        private static readonly TimeSpan MediaPeekDuration = TimeSpan.FromSeconds(4);

        private void InitializeAutoHide()
        {
            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(120);
            _autoHideTimer.Tick += OnAutoHideTick;
            _autoHideTimer.Start();
        }

        private void OnAutoHideTick(object? sender, EventArgs e)
        {
            // 防御性编程：如果有高优先级任务正在显示，绝对禁止执行自动隐藏逻辑
            if (_isNotificationActive || _isFileStationActive || _isProgressActive || 
                (_pomodoroService.CurrentState != PomodoroState.Idle && _pomodoroService.CurrentState != PomodoroState.Finished))
            {
                DynamicIsland.Opacity = 1.0;
                return;
            }

            if (ShouldCompactIsland())
            {
                EnterCompactIslandMode();
                return;
            }

            if (_isCompactIsland)
            {
                ExitCompactIslandMode();
                CheckCurrentSession();
            }
        }

        private void StopAutoHide()
        {
            _autoHideTimer?.Stop();
            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = 1.0;
        }

        private bool ShouldCompactIsland()
        {
            var settings = AppSettings.Load();
            if (!settings.EnableAutoHide) return false;
            if (this.IsMouseOver) return false;
            if (DateTime.Now < _mediaPeekUntil) return false;
            if (_isNotificationActive || _isFileStationActive || _isProgressActive) return false;
            if (_storedFiles.Count > 0) return false;
            if (_pomodoroService.CurrentState != PomodoroState.Idle &&
                _pomodoroService.CurrentState != PomodoroState.Finished)
            {
                return false;
            }

            return true;
        }

        private void EnterCompactIslandMode()
        {
            SetMediaSuppressed(true);
            HideAllPanels();
            StopAudioCapture();
            _statsService.Stop();

            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = 0.9;
            DynamicIsland.IsHitTestVisible = true;

            _widthSpring.Target = CompactIslandSize;
            _heightSpring.Target = CompactIslandSize;
            _isCompactIsland = true;
        }

        private void ExitCompactIslandMode()
        {
            if (!_isCompactIsland) return;

            _isCompactIsland = false;
            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = 1.0;
            SetMediaSuppressed(false);
        }

        private void TriggerMediaPeek()
        {
            if (_isNotificationActive || _isFileStationActive || _isProgressActive) return;
            if (_pomodoroService.CurrentState != PomodoroState.Idle &&
                _pomodoroService.CurrentState != PomodoroState.Finished)
            {
                return;
            }

            _mediaPeekUntil = DateTime.Now.Add(MediaPeekDuration);
            ExitCompactIslandMode();
            CheckCurrentSession();
        }

        private void EnterStandbyMode()
        {
            if (_isNotificationActive) return;
            if (_isProgressActive) return;

            if (ShouldCompactIsland())
            {
                EnterCompactIslandMode();
                return;
            }

            // 停止不需要的后台任务以节省内存
            StopAudioCapture(); 

            Dispatcher.Invoke(() =>
            {
                if (_pomodoroService.CurrentState == PomodoroState.Running || _pomodoroService.CurrentState == PomodoroState.Paused)
                {
                    StopAutoHide();
                    ShowPomodoroPanel();
                    return;
                }

                var settings = AppSettings.Load();

                if (settings.EnableAutoHide)
                {
                    if (settings.EnableSystemMonitor)
                    {
                        ShowSystemMonitorPanel();
                        // 隐身模式下，初始默认不启动，等滑出来再启动
                        _statsService.Stop(); 
                    }
                    else
                    {
                        _statsService.Stop();
                        HideAllPanels();
                        _widthSpring.Target = 120;
                        _heightSpring.Target = 35;
                    }
                    
                    if (!_autoHideTimer.IsEnabled) _autoHideTimer.Start();
                    return;
                }

                StopAutoHide();

                if (settings.EnableSystemMonitor)
                {
                    ShowSystemMonitorPanel();
                    _statsService.Start();
                }
                else
                {
                    _statsService.Stop();
                    HideAllPanels();
                    
                    _widthSpring.Target = 120;
                    _heightSpring.Target = 35;
                    DynamicIsland.Opacity = 0.4;
                    
                    SystemMonitorPanel.Visibility = Visibility.Collapsed;
                    PomodoroPanel.Visibility = Visibility.Collapsed;
                }
            });
        }


        private void ShowSystemMonitorPanel()
        {
            HideAllPanels();
            SystemMonitorPanel.Visibility = Visibility.Visible;
            
            _widthSpring.Target = 220;
            _heightSpring.Target = 35;
            DynamicIsland.Opacity = 0.95;
        }

        private void ShowPomodoroPanel()
        {
            HideAllPanels();
            PomodoroPanel.Visibility = Visibility.Visible;

            _widthSpring.Target = 200;
            _heightSpring.Target = 35;
            DynamicIsland.Opacity = 1.0;
        }

        private void OnSystemStatsUpdated(object? sender, SystemStats stats)
        {
            Dispatcher.Invoke(() =>
            {
                if (SystemMonitorPanel.Visibility != Visibility.Visible) return;

                TxtCpuUsage.Text = stats.GetFormattedCpu();
                TxtRamUsage.Text = stats.GetFormattedRam();
                TxtUploadSpeed.Text = stats.GetFormattedUpload();
                TxtDownloadSpeed.Text = stats.GetFormattedDownload();
            });
        }

        private void OnPomodoroTick(TimeSpan remaining, double progress)
        {
            Dispatcher.Invoke(() =>
            {
                if (PomodoroPanel.Visibility != Visibility.Visible) return;
                
                TxtFocusTimer.Text = remaining.ToString(@"mm\:ss");
                
                double totalWidth = _widthSpring.Target - 24; 
                if (totalWidth < 0) totalWidth = 0;
                
                PomodoroProgressBar.Width = totalWidth * progress;
            });
        }

        private void OnPomodoroStateChanged(PomodoroState state)
        {
            Dispatcher.Invoke(() =>
            {
                if (state != PomodoroState.Finished)
                {
                    _focusOverlayWindow.StopBreathing();
                }

                if (state == PomodoroState.Running)
                {
                     TxtFocusStatus.Text = "FOCUS";
                     TxtFocusStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
                     BadgeFocusStatus.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(44, 44, 46));
                     CheckCurrentSession(); 
                }
                else if (state == PomodoroState.Paused)
                {
                     TxtFocusStatus.Text = "PAUSED";
                     TxtFocusStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 142, 147));
                }
                else if (state == PomodoroState.Finished)
                {
                     TxtFocusStatus.Text = "DONE";
                     TxtFocusStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
                     
                     PlayIslandPulseAnimation(Colors.Gold);
                     CheckCurrentSession(); 
                }
                else
                {
                     StopIslandPulseAnimation();
                     CheckCurrentSession();
                }
            });
        }

    }
}
