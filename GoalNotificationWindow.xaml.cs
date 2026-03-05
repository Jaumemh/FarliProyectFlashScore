using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace FlashscoreOverlay
{
    public partial class GoalNotificationWindow : Window
    {
        private readonly DispatcherTimer _dismissTimer;
        private readonly string _matchUrl;
        private bool _webViewReady;

        // Stacking support
        public int StackIndex { get; set; }
        private const double NotificationHeight = 180;
        private const double StackGap = 10;
        private const double ScreenMargin = 10;

        public event Action<GoalNotificationWindow>? NotificationClosed;

        public GoalNotificationWindow(
            string playerName,
            string teamName,
            string playerImageUrl,
            string homeTeam,
            string awayTeam,
            string homeScore,
            string awayScore,
            string homeLogo,
            string awayLogo,
            string minute,
            string stageText,
            string matchUrl,
            string scoringTeamSide,
            int stackIndex)
        {
            InitializeComponent();
            _matchUrl = matchUrl;
            StackIndex = stackIndex;

            // Position bottom-right
            var screen = SystemParameters.WorkArea;
            Left = screen.Right - Width - ScreenMargin;
            Top = screen.Bottom - ((NotificationHeight + StackGap) * (stackIndex + 1));

            // Auto-dismiss after 10 seconds
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _dismissTimer.Tick += (s, e) => DismissWithAnimation();
            _dismissTimer.Start();

            // Store data for HTML rendering
            Tag = new GoalNotifData
            {
                PlayerName = playerName ?? "",
                TeamName = teamName ?? "",
                PlayerImageUrl = playerImageUrl ?? "",
                HomeTeam = homeTeam ?? "",
                AwayTeam = awayTeam ?? "",
                HomeScore = homeScore ?? "0",
                AwayScore = awayScore ?? "0",
                HomeLogo = homeLogo ?? "",
                AwayLogo = awayLogo ?? "",
                Minute = minute ?? "",
                StageText = stageText ?? "",
                ScoringTeamSide = scoringTeamSide ?? "home"
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    null, null,
                    new CoreWebView2EnvironmentOptions("--disable-web-security"));

                await NotifWebView.EnsureCoreWebView2Async(env);
                _webViewReady = true;

                NotifWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                NotifWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                NotifWebView.CoreWebView2.WebMessageReceived += (s, args) =>
                {
                    var msg = args.TryGetWebMessageAsString();
                    if (msg == "click")
                    {
                        // Open match URL in default browser
                        if (!string.IsNullOrWhiteSpace(_matchUrl))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(_matchUrl) { UseShellExecute = true });
                            }
                            catch { }
                        }
                        DismissWithAnimation();
                    }
                    else if (msg == "dismiss")
                    {
                        DismissWithAnimation();
                    }
                };

                var data = (GoalNotifData)Tag;
                var html = BuildNotificationHtml(data);
                NotifWebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GoalNotif] Error: {ex.Message}");
                Close();
            }
        }

        public void UpdateStackPosition(int newIndex)
        {
            StackIndex = newIndex;
            var screen = SystemParameters.WorkArea;
            Top = screen.Bottom - ((NotificationHeight + StackGap) * (newIndex + 1));
        }

        private async void DismissWithAnimation()
        {
            _dismissTimer.Stop();

            if (_webViewReady && NotifWebView.CoreWebView2 != null)
            {
                try
                {
                    await NotifWebView.CoreWebView2.ExecuteScriptAsync(
                        "document.getElementById('notif').style.animation = 'fs-out 0.4s forwards';");
                    await Task.Delay(400);
                }
                catch { }
            }

            NotificationClosed?.Invoke(this);
            Close();
        }

        private string BuildNotificationHtml(GoalNotifData d)
        {
            var logoPlaceholder = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='40' height='40'%3E%3Crect width='40' height='40' fill='%23333355' rx='4'/%3E%3C/svg%3E";
            var playerPlaceholder = "https://www.flashscore.es/res/image/data/placeholder-player.png";
            
            var playerImg = string.IsNullOrWhiteSpace(d.PlayerImageUrl) ? playerPlaceholder : Esc(d.PlayerImageUrl);
            var homeLogo = string.IsNullOrWhiteSpace(d.HomeLogo) ? logoPlaceholder : Esc(d.HomeLogo);
            var awayLogo = string.IsNullOrWhiteSpace(d.AwayLogo) ? logoPlaceholder : Esc(d.AwayLogo);

            // Determine which score is red (the scoring team)
            var homeScoreClass = d.ScoringTeamSide == "home" ? "fs-rd" : "";
            var awayScoreClass = d.ScoringTeamSide == "away" ? "fs-rd" : "";

            // Determine which team name to highlight
            var homeNameClass = d.ScoringTeamSide == "home" ? "fs-tn fs-tn-hl" : "fs-tn";
            var awayNameClass = d.ScoringTeamSide == "away" ? "fs-tn fs-tn-hl" : "fs-tn";

            return $@"<!DOCTYPE html>
<html><head><meta charset='UTF-8'>
<style>
    * {{ margin:0; padding:0; box-sizing:border-box; }}
    html, body {{ background: transparent; overflow: hidden; font-family: -apple-system,system-ui,'Segoe UI',Arial,sans-serif; }}
    
    #notif {{
        position: fixed; bottom: 0; right: 0; left: 0; top: 0;
        background: #1f1f1f; color: #fff;
        border-radius: 6px; cursor: pointer;
        user-select: none;
        animation: fs-in 0.4s ease-out;
        display: flex;
    }}

    .fs-l {{
        background: #2b2b2b; padding: 12px 14px;
        display: flex; flex-direction: column;
        align-items: center; justify-content: center;
        min-width: 130px; border-right: 1px solid #444;
        gap: 3px;
    }}
    .fs-img {{
        width: 75px; height: 75px;
        border-radius: 50%; object-fit: cover;
        margin-bottom: 4px; border: 2px solid #FF0046;
        background: #333;
    }}
    .fs-nm {{ font-size: 13px; font-weight: 800; color: #fff; text-align: center; line-height: 1.2; }}
    .fs-tm {{ font-size: 11px; color: #aaa; text-align: center; }}
    .fs-gol {{ color: #FF0046; font-weight: 900; font-size: 16px; margin-top: 4px; letter-spacing: 1px; }}

    .fs-r {{
        flex: 1; padding: 0 15px;
        display: flex; align-items: center; justify-content: center;
    }}
    .fs-duel {{ display: flex; gap: 14px; align-items: center; }}
    .fs-col {{ display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; }}
    .fs-logo img {{ width: auto; height: auto; max-width: 48px; max-height: 48px; object-fit: contain; }}
    .fs-tn {{ font-size: 13px; font-weight: 700; color: #fff; text-align: center; line-height: 1.2; max-width: 90px; word-spacing: 100vw; }}
    .fs-tn-hl {{ color: #FF0046 !important; font-weight: 700; }}

    .fs-sc-box {{ display: flex; flex-direction: column; align-items: center; }}
    .fs-sc {{ font-size: 24px; font-weight: 700; display: flex; gap: 5px; color: #fff; }}
    .fs-dash {{ margin: 0 2px; color: #666; }}
    .fs-rd {{ color: #FF0046; }}

    .fs-st {{ display: flex; flex-direction: column; align-items: center; margin-top: 4px; font-size: 10px; color: #888; }}
    .fs-stage {{ font-size: 10px; color: #888; margin-bottom: 2px; text-transform: uppercase; letter-spacing: 0.04em; }}
    .fs-time {{ color: #FF0046; font-weight: 700; font-size: 17px; }}
    .fs-blk {{ animation: fs-b 1s infinite; }}

    @keyframes fs-in {{ from {{ transform: translateX(110%); opacity: 0; }} to {{ transform: translateX(0); opacity: 1; }} }}
    @keyframes fs-out {{ from {{ transform: translateX(0); opacity: 1; }} to {{ transform: translateX(110%); opacity: 0; }} }}
    @keyframes fs-b {{ 0%,100% {{ opacity:1; }} 50% {{ opacity:0; }} }}
</style>
</head>
<body>
<div id='notif' onclick=""window.chrome.webview.postMessage('click')"">
    <div class='fs-l'>
        <img class='fs-img' src='{playerImg}' onerror=""this.style.display='none'"">
        <div class='fs-nm'>{Esc(d.PlayerName)}</div>
        <div class='fs-tm'>{Esc(d.TeamName)}</div>
        <div class='fs-gol'>⚽ GOL</div>
    </div>
    <div class='fs-r'>
        <div class='fs-duel'>
            <div class='fs-col'>
                <div class='fs-logo'><img src='{homeLogo}' onerror=""this.src='{logoPlaceholder}'""></div>
                <div class='{homeNameClass}'>{Esc(d.HomeTeam)}</div>
            </div>
            <div class='fs-col fs-sc-box'>
                <div class='fs-sc'>
                    <span class='{homeScoreClass}'>{Esc(d.HomeScore)}</span>
                    <span class='fs-dash'>-</span>
                    <span class='{awayScoreClass}'>{Esc(d.AwayScore)}</span>
                </div>
                <div class='fs-st'>
                    <span class='fs-stage'>{Esc(d.StageText)}</span>
                    <div class='fs-time'>{Esc(d.Minute)}<span class='fs-blk'>'</span></div>
                </div>
            </div>
            <div class='fs-col'>
                <div class='fs-logo'><img src='{awayLogo}' onerror=""this.src='{logoPlaceholder}'""></div>
                <div class='{awayNameClass}'>{Esc(d.AwayTeam)}</div>
            </div>
        </div>
    </div>
</div>
</body></html>";
        }

        private static string Esc(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                     .Replace("\"", "&quot;").Replace("'", "&#39;");

        private class GoalNotifData
        {
            public string PlayerName { get; set; } = "";
            public string TeamName { get; set; } = "";
            public string PlayerImageUrl { get; set; } = "";
            public string HomeTeam { get; set; } = "";
            public string AwayTeam { get; set; } = "";
            public string HomeScore { get; set; } = "";
            public string AwayScore { get; set; } = "";
            public string HomeLogo { get; set; } = "";
            public string AwayLogo { get; set; } = "";
            public string Minute { get; set; } = "";
            public string StageText { get; set; } = "";
            public string ScoringTeamSide { get; set; } = "";
        }
    }
}
