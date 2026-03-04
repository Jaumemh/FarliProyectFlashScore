using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlashscoreOverlay
{
    public partial class GoalToastWindow : Window
    {
        private readonly MessageData _data;
        private readonly string? _tabId;
        private readonly MatchData _match;
        private DispatcherTimer? _closeTimer;

        public GoalToastWindow(MessageData data, string? tabId, MatchData match)
        {
            InitializeComponent();
            _data = data;
            _tabId = tabId;
            _match = match;

            // Display data
            PlayerNameText.Text = string.IsNullOrWhiteSpace(data.PlayerName) ? "GOL" : data.PlayerName;
            TeamNameText.Text = string.IsNullOrWhiteSpace(data.TeamSide) ? "" : 
                                (data.TeamSide == "home" ? match.HomeTeam : match.AwayTeam);
            
            // Check if player photo URL exists, else collapse the image
            if (!string.IsNullOrWhiteSpace(data.PlayerPhotoUrl))
            {
                try
                {
                    PlayerImage.Source = new BitmapImage(new Uri(data.PlayerPhotoUrl));
                }
                catch { PlayerImage.Visibility = Visibility.Collapsed; }
            }
            else
            {
                PlayerImage.Visibility = Visibility.Collapsed;
            }

            // Shield/Teams data
            HomeTeamName.Text = match.HomeTeam;
            AwayTeamName.Text = match.AwayTeam;
            
            if (!string.IsNullOrWhiteSpace(match.HomeLogo))
                try { HomeLogo.Source = new BitmapImage(new Uri(match.HomeLogo)); } catch { }
            
            if (!string.IsNullOrWhiteSpace(match.AwayLogo))
                try { AwayLogo.Source = new BitmapImage(new Uri(match.AwayLogo)); } catch { }

            // Score and time
            HomeScore.Text = match.HomeScore;
            AwayScore.Text = match.AwayScore;
            MinuteText.Text = string.IsNullOrWhiteSpace(data.Minute) ? (match.Time ?? "") : data.Minute;

            // Reposition window to bottom right
            Loaded += GoalToastWindow_Loaded;
            
            // Auto close timer
            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _closeTimer.Tick += (s, e) => CloseWithAnimation();
            _closeTimer.Start();
        }

        private void GoalToastWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var desktopWorkingArea = SystemParameters.WorkArea;
            
            // Offset from edges
            Left = desktopWorkingArea.Right - Width - 20;
            Top = desktopWorkingArea.Bottom - Height - 20;

            // TODO: Stack them if multiple exist, but out of scope for MVP
            
            // Slide in animation
            var slideIn = new DoubleAnimation
            {
                From = Left + Width,
                To = Left,
                Duration = TimeSpan.FromSeconds(0.4),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(LeftProperty, slideIn);
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Stop timer so it doesn't close while processing
            _closeTimer?.Stop();

            // Navigate
            var targetUrl = _data.MatchUrl ?? _match.Url;
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                if (!string.IsNullOrWhiteSpace(_tabId))
                {
                    var main = Application.Current.MainWindow as MainWindow;
                    main?.SetPendingCommandForTab(_tabId, new BrowserCommand { Action = "navigate", Href = targetUrl });
                }
                else
                {
                    try { Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true }); } catch { }
                }
            }

            CloseWithAnimation();
        }

        private void CloseWithAnimation()
        {
            _closeTimer?.Stop();
            var slideOut = new DoubleAnimation
            {
                From = Left,
                To = Left + Width + 20,
                Duration = TimeSpan.FromSeconds(0.4),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn }
            };
            slideOut.Completed += (s, e) => Close();
            BeginAnimation(LeftProperty, slideOut);
        }
    }
}
