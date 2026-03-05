using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace FlashscoreOverlay
{
    public partial class CardNotificationWindow : Window
    {
        private readonly DispatcherTimer _dismissTimer;
        private readonly string _matchUrl;
        private bool _webViewReady;

        // Stacking support
        public int StackIndex { get; set; }
        private const double NotificationHeight = 180;
        private const double StackGap = 10;
        private const double ScreenMargin = 10;

        public event Action<CardNotificationWindow>? NotificationClosed;

        public CardNotificationWindow(
            string cardType,
            string playerName,
            string teamName,
            string incidentDescription,
            string homeTeam,
            string awayTeam,
            string homeScore,
            string awayScore,
            string homeLogo,
            string awayLogo,
            string minute,
            string stageText,
            string matchUrl,
            int stackIndex,
            string playerImageUrl = "")
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
            Tag = new CardNotifData
            {
                CardType = cardType ?? "yellowCard",
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? "" : playerName,
                TeamName = teamName ?? "",
                IncidentDescription = incidentDescription ?? "",
                HomeTeam = homeTeam ?? "",
                AwayTeam = awayTeam ?? "",
                HomeScore = homeScore ?? "0",
                AwayScore = awayScore ?? "0",
                HomeLogo = homeLogo ?? "",
                AwayLogo = awayLogo ?? "",
                Minute = minute ?? "",
                StageText = stageText ?? "",
                PlayerImageUrl = playerImageUrl ?? ""
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

                var data = (CardNotifData)Tag;
                var html = BuildNotificationHtml(data);
                NotifWebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CardNotif] Error: {ex.Message}");
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

        private string BuildNotificationHtml(CardNotifData d)
        {
            var logoPlaceholder = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='40' height='40'%3E%3Crect width='40' height='40' fill='%23333355' rx='4'/%3E%3C/svg%3E";
            var playerPlaceholder = "https://www.flashscore.es/res/image/data/placeholder-player.png";

            var homeLogo = string.IsNullOrWhiteSpace(d.HomeLogo) ? logoPlaceholder : Esc(d.HomeLogo);
            var awayLogo = string.IsNullOrWhiteSpace(d.AwayLogo) ? logoPlaceholder : Esc(d.AwayLogo);
            var playerImg = string.IsNullOrWhiteSpace(d.PlayerImageUrl) ? playerPlaceholder : Esc(d.PlayerImageUrl);

            bool isRed = d.CardType.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0;
            string cardColor = isRed ? "#FF0046" : "#FFCC00";
            string cardText = isRed ? "🟥 TARJETA ROJA" : "🟨 TARJETA AMARILLA";
            string gradient = isRed ? "linear-gradient(135deg, #FF0046 0%, #aa0033 100%)" : "linear-gradient(135deg, #FFCC00 0%, #cc9900 100%)";
            string textShadow = isRed ? "0 1px 3px rgba(0,0,0,0.4)" : "0 1px 3px rgba(0,0,0,0.6)";
            string cardColorText = isRed ? "#fff" : "#111";

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
        background: {gradient}; padding: 10px 12px;
        display: flex; flex-direction: column;
        align-items: center; justify-content: center;
        min-width: 140px; border-right: 1px solid #444;
        gap: 3px;
        color: {cardColorText};
        text-shadow: {textShadow};
    }}
    .fs-img {{
        width: 65px; height: 65px;
        border-radius: 50%; object-fit: cover;
        margin-bottom: 4px; border: 2px solid rgba(255,255,255,0.4);
        background: rgba(0,0,0,0.2);
    }}
    .fs-nm {{ font-size: 13px; font-weight: 800; text-align: center; line-height: 1.2; text-shadow: {textShadow}; }}
    .fs-tm {{ font-size: 11px; text-align: center; font-weight: 600; opacity: 0.9; text-shadow: {textShadow}; }}
    .fs-card-text {{ font-weight: 900; font-size: 13px; margin-top: 5px; letter-spacing: 0.5px; text-transform: uppercase; text-shadow: {textShadow}; }}
    .fs-desc {{ font-size: 10px; opacity: 0.85; margin-top: 3px; font-style: italic; text-align: center; line-height: 1.1; max-width: 120px; text-shadow: {textShadow}; }}

    .fs-r {{
        flex: 1; padding: 0 15px;
        display: flex; align-items: center; justify-content: center;
    }}
    .fs-duel {{ display: flex; gap: 14px; align-items: center; }}
    .fs-col {{ display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; }}
    .fs-logo img {{ width: auto; height: auto; max-width: 48px; max-height: 48px; object-fit: contain; }}
    .fs-tn {{ font-size: 13px; font-weight: 700; color: #fff; text-align: center; line-height: 1.2; max-width: 90px; word-spacing: 100vw; }}

    .fs-sc-box {{ display: flex; flex-direction: column; align-items: center; }}
    .fs-sc {{ font-size: 24px; font-weight: 700; display: flex; gap: 5px; color: #fff; }}
    .fs-dash {{ margin: 0 2px; color: #666; }}

    .fs-st {{ display: flex; flex-direction: column; align-items: center; margin-top: 4px; font-size: 10px; color: #888; }}
    .fs-stage {{ font-size: 10px; color: #888; margin-bottom: 2px; text-transform: uppercase; letter-spacing: 0.04em; }}
    .fs-time {{ color: {cardColor}; font-weight: 700; font-size: 17px; }}
    .fs-blk {{ animation: fs-b 1s infinite; }}

    @keyframes fs-in {{ from {{ transform: translateX(110%); opacity: 0; }} to {{ transform: translateX(0); opacity: 1; }} }}
    @keyframes fs-out {{ from {{ transform: translateX(0); opacity: 1; }} to {{ transform: translateX(110%); opacity: 0; }} }}
    @keyframes fs-b {{ 0%,100% {{ opacity:1; }} 50% {{ opacity:0; }} }}
</style>
</head>
<body>
<div id='notif' onclick=""window.chrome.webview.postMessage('click')"">
    <div class='fs-l'>
        {(!string.IsNullOrWhiteSpace(playerImg) ? $"<img class='fs-img' src='{playerImg}' onerror=\"this.style.display='none'\">" : "")}
        <div class='fs-nm'>{Esc(d.PlayerName)}</div>
        <div class='fs-tm'>{Esc(d.TeamName)}</div>
        <div class='fs-card-text'>{Esc(cardText)}</div>
        {(!string.IsNullOrWhiteSpace(d.IncidentDescription) ? $"<div class='fs-desc'>{Esc(d.IncidentDescription)}</div>" : "")}
    </div>
    <div class='fs-r'>
        <div class='fs-duel'>
            <div class='fs-col'>
                <div class='fs-logo'><img src='{homeLogo}' onerror=""this.src='{logoPlaceholder}'""></div>
                <div class='fs-tn'>{Esc(d.HomeTeam)}</div>
            </div>
            <div class='fs-col fs-sc-box'>
                <div class='fs-sc'>
                    <span>{Esc(d.HomeScore)}</span>
                    <span class='fs-dash'>-</span>
                    <span>{Esc(d.AwayScore)}</span>
                </div>
                <div class='fs-st'>
                    <span class='fs-stage'>{Esc(d.StageText)}</span>
                    <div class='fs-time'>{Esc(d.Minute)}<span class='fs-blk'>'</span></div>
                </div>
            </div>
            <div class='fs-col'>
                <div class='fs-logo'><img src='{awayLogo}' onerror=""this.src='{logoPlaceholder}'""></div>
                <div class='fs-tn'>{Esc(d.AwayTeam)}</div>
            </div>
        </div>
    </div>
</div>
</body></html>";
        }

        private static string Esc(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                     .Replace("\"", "&quot;").Replace("'", "&#39;");

        private class CardNotifData
        {
            public string CardType { get; set; } = "";
            public string PlayerName { get; set; } = "";
            public string TeamName { get; set; } = "";
            public string IncidentDescription { get; set; } = "";
            public string HomeTeam { get; set; } = "";
            public string AwayTeam { get; set; } = "";
            public string HomeScore { get; set; } = "";
            public string AwayScore { get; set; } = "";
            public string HomeLogo { get; set; } = "";
            public string AwayLogo { get; set; } = "";
            public string Minute { get; set; } = "";
            public string StageText { get; set; } = "";
            public string PlayerImageUrl { get; set; } = "";
        }
    }
}
