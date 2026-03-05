using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

namespace FlashscoreOverlay
{
    public partial class MatchDetailWindow : Window
    {
        private static CoreWebView2Environment? _sharedEnv;
        private bool _webViewReady;
        private bool _pollingInjected;

        /// <summary>
        /// The match ID this window is tracking.
        /// </summary>
        public string MatchId { get; }

        /// <summary>
        /// The Flashscore match detail URL.
        /// </summary>
        public string MatchUrl { get; }

        /// <summary>
        /// Fired when match detail data (events, score, minute) is extracted.
        /// Args: (matchId, rawJson)
        /// </summary>
        public event Action<string, string>? MatchDetailDataReceived;

        public MatchDetailWindow(string matchId, string matchUrl)
        {
            InitializeComponent();
            ShowInTaskbar = false;
            Topmost = false;

            MatchId = matchId;
            MatchUrl = matchUrl;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                if (_sharedEnv == null)
                {
                    // Ultra-light profile for background scraping
                    var options = new CoreWebView2EnvironmentOptions(
                        "--disable-web-security " +
                        "--disable-gpu " + // No hardware acceleration needed for 1x1 window
                        "--disable-features=IsolateOrigins,site-per-process " + // Prevent spawning +30MB processes for cross-site iframes
                        "--blink-settings=imagesEnabled=false" // Tell blink engine not to fetch images
                    );
                    var userDataFolder = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "FlashscoreOverlay_MatchDetail");
                    _sharedEnv = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }

                await DetailWebView.EnsureCoreWebView2Async(_sharedEnv);
                DetailWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                DetailWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                // --- RESOURCE OPTIMIZATION ---
                DetailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
                DetailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
                // Also capture Fonts and Stylesheets to supply dummy responses
                DetailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
                DetailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Stylesheet);
                
                DetailWebView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;

                DetailWebView.CoreWebView2.WebMessageReceived += DetailWebView_WebMessageReceived;
                DetailWebView.NavigationCompleted += DetailWebView_NavigationCompleted;
                _webViewReady = true;

                System.Diagnostics.Debug.WriteLine($"[MatchDetail:{MatchId}] Navigating to {MatchUrl}");
                DetailWebView.CoreWebView2.Navigate(MatchUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MatchDetail:{MatchId}] Init error: {ex.Message}");
            }
        }

        private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var ctx = e.ResourceContext;

            // Hard block for media and true images
            if (ctx == CoreWebView2WebResourceContext.Image ||
                ctx == CoreWebView2WebResourceContext.Media)
            {
                e.Response = _sharedEnv!.CreateWebResourceResponse(null, 403, "Blocked", "");
                return;
            }

            // Dummy response for Fonts and Stylesheets to trick Flashscore SPA into thinking it loaded
            if (ctx == CoreWebView2WebResourceContext.Font ||
                ctx == CoreWebView2WebResourceContext.Stylesheet)
            {
                // Create an empty memory stream
                var emptyStream = new System.IO.MemoryStream(Array.Empty<byte>());
                e.Response = _sharedEnv!.CreateWebResourceResponse(emptyStream, 200, "OK", "Content-Type: text/plain");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
        {
                // Force WebView to navigate to about:blank to immediately release resources 
                // associated with the heavy page before the window is fully disposed.
                if (DetailWebView != null && _webViewReady && DetailWebView.CoreWebView2 != null)
                {
                    DetailWebView.CoreWebView2.Navigate("about:blank");
                }
                DetailWebView?.Dispose();
            }
            catch { }
            
            base.OnClosed(e);
        }

        private async void DetailWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;
            if (_pollingInjected) return;

            // Wait for Flashscore JS to render the match detail page
            await Task.Delay(6000);

            // Inject JS that polls every 3s and extracts match detail data
            // Uses verified CSS selectors from real Flashscore match detail DOM
            var script = @"
                (function() {
                    if (window.__matchDetailPolling) return;
                    window.__matchDetailPolling = true;

                    function extractMatchDetail() {
                        try {
                            // === SCORE ===
                            // .detailScore__wrapper contains 3 spans: homeScore, separator(-), awayScore
                            const scoreWrapper = document.querySelector('.detailScore__wrapper');
                            let homeScore = '';
                            let awayScore = '';
                            if (scoreWrapper) {
                                const spans = scoreWrapper.querySelectorAll('span');
                                if (spans.length >= 3) {
                                    homeScore = (spans[0].textContent || '').trim();
                                    awayScore = (spans[2].textContent || '').trim();
                                } else if (spans.length >= 1) {
                                    homeScore = (spans[0].textContent || '').trim();
                                    if (spans.length >= 2) awayScore = (spans[1].textContent || '').trim();
                                }
                            }

                            // === TIME / STAGE ===
                            // .detailScore__status shows text like '1er Tiempo 47:53 +3', 'Descanso', 'FINALIZADO'
                            const statusEl = document.querySelector('.detailScore__status');
                            const rawStage = statusEl ? statusEl.textContent.trim() : '';

                            let minute = '';
                            let isLive = false;

                            // If rawStage is just a date/time or empty, it's not live
                            if (!rawStage || /^\d{2}\.\d{2}\.\d{4}/.test(rawStage)) {
                                minute = '';
                                isLive = false;
                            } else if (/finalizado|final/i.test(rawStage)) {
                                minute = '';
                                isLive = false;
                            } else if (/descanso/i.test(rawStage)) {
                                minute = 'Descanso';
                                isLive = true;
                            } else {
                                // Extract the time HH:MM or MM:SS
                                const timeMatches = [...rawStage.matchAll(/(\d+):(\d{2})/g)];
                                if (timeMatches.length > 0) {
                                    const lastMatch = timeMatches[timeMatches.length - 1];
                                    let m = parseInt(lastMatch[1], 10);
                                    let runningMin = m + 1;
                                    
                                    if (rawStage.includes('+')) {
                                        if (m >= 90) minute = `90+${runningMin - 90}`;
                                        else if (m >= 45) minute = `45+${runningMin - 45}`;
                                        else minute = String(runningMin);
                                    } else {
                                        minute = String(runningMin);
                                    }
                                    isLive = true;
                                } else {
                                    // Fallback: sometimes Flashscore might pre-format explicitly without MM:SS
                                    const explicitAdded = rawStage.match(/(\d+)\+(\d+)/);
                                    const explicitMinute = rawStage.match(/(\d+)'/);

                                    if (explicitAdded) {
                                        minute = `${explicitAdded[1]}+${explicitAdded[2]}`;
                                        isLive = true;
                                    } else if (explicitMinute) {
                                        minute = explicitMinute[1];
                                        isLive = true;
                                    } else if (/prórroga|extra|penal/i.test(rawStage)) {
                                        minute = rawStage;
                                        isLive = true;
                                    }
                                }
                            }

                            // === EVENTS (goals, cards, substitutions) ===
                            const events = [];
                            // Each event row is .smv__participantRow with class .smv__homeParticipant or .smv__awayParticipant
                            const rows = document.querySelectorAll('.smv__participantRow');

                            for (const row of rows) {
                                try {
                                    // Determine team side
                                    let teamSide = 'unknown';
                                    if (row.classList.contains('smv__homeParticipant')) teamSide = 'home';
                                    else if (row.classList.contains('smv__awayParticipant')) teamSide = 'away';

                                    // Find incident container inside the row
                                    const incident = row.querySelector('.smv__incident');
                                    if (!incident) continue;

                                    // Minute: .smv__timeBox
                                    const minuteEl = row.querySelector('.smv__timeBox');
                                    const eventMinute = minuteEl ? minuteEl.textContent.trim() : '';

                                    // Detect event type by icon classes (escaped quotes for verbatim string)
                                    let eventType = '';
                                    if (incident.querySelector('.yellowCard-ico')) eventType = 'yellowCard';
                                    else if (incident.querySelector('.redCard-ico, .yellowRedCard-ico')) eventType = 'redCard';
                                    else if (incident.querySelector('.substitution-ico, .smv__subDown')) eventType = 'substitution';
                                    else if (incident.querySelector('[class*=""footballGoal""], [class*=""Goal""], .smv__incidentGoalScore, .smv__incidentHomeScore, .smv__incidentAwayScore')) eventType = 'goal';
                                    else if (incident.querySelector('.penaltyGoal-ico, .ownGoal-ico')) eventType = 'goal';

                                    if (!eventType) {
                                        const scoreInc = incident.querySelector('[class*=""Score""]');
                                        if (scoreInc) eventType = 'goal';
                                        else continue;
                                    }

                                    // Player name: .smv__playerName
                                    const playerEl = row.querySelector('.smv__playerName');
                                    const playerName = playerEl ? playerEl.textContent.trim() : '';

                                    // Player URL: look for anchor tag in the incident
                                    let playerUrl = '';
                                    const playerLink = row.querySelector('a[href*=""/jugador/""], a[href*=""/player/""]');
                                    if (playerLink) {
                                        const href = playerLink.getAttribute('href') || '';
                                        playerUrl = href.startsWith('http') ? href : 'https://www.flashscore.es' + href;
                                    }

                                    let incidentDescription = '';
                                    if (eventType === 'yellowCard' || eventType === 'redCard') {
                                        const descEl = incident.querySelector('.smv__incidentSubObj');
                                        if (descEl) {
                                            incidentDescription = descEl.textContent.trim().replace(/^[\(\[]|[\)\]]$/g, ''); 
                                        }
                                    } else if (eventType === 'goal') {
                                        const assistEl = row.querySelector('.smv__assist');
                                        if (assistEl) {
                                            incidentDescription = 'Asist: ' + assistEl.textContent.trim().replace(/^[\(\[]|[\)\]]$/g, '');
                                        }
                                    }

                                    events.push({
                                        type: eventType,
                                        minute: eventMinute,
                                        playerName: playerName,
                                        playerUrl: playerUrl,
                                        teamSide: teamSide,
                                        incidentDescription: incidentDescription
                                    });
                                } catch (rowErr) { /* skip row errors */ }
                            }

                            // === BANDERA DE LIGA ===
                            let leagueFlagUrl = '';
                            try {
                                // 1. Try specific breadcrumb flag
                                const flagImg = document.querySelector('img.wcl-flag_bU4cQ, .headerLeague__flag, .bc__flag img');
                                if (flagImg && flagImg.src) {
                                    leagueFlagUrl = flagImg.src;
                                } else {
                                    // 2. Try looking into breadcrumb list specifically
                                    const breadcrumb = document.querySelector('ol.wcl-breadcrumbList_lC9sI, .bc__list');
                                    if (breadcrumb) {
                                        const bImg = breadcrumb.querySelector('img');
                                        if (bImg && bImg.src) leagueFlagUrl = bImg.src;
                                    }
                                }
                            } catch (flagErr) { leagueFlagUrl = ''; }

                            const result = {
                                type: 'matchDetail',
                                homeScore: homeScore,
                                awayScore: awayScore,
                                stage: minute,
                                minute: minute,
                                isLive: isLive,
                                leagueFlagUrl: leagueFlagUrl,
                                events: events,
                                timestamp: Date.now()
                            };

                            window.chrome.webview.postMessage(JSON.stringify(result));
                        } catch (err) {
                            console.error('[MatchDetail] extraction error:', err);
                        }
                    }

                    // Run immediately once, then every 3 seconds
                    extractMatchDetail();
                    setInterval(extractMatchDetail, 3000);
                    console.log('[MatchDetail] Polling started');
                })();
            ";

            try
            {
                await DetailWebView.CoreWebView2.ExecuteScriptAsync(script);
                _pollingInjected = true;
                System.Diagnostics.Debug.WriteLine($"[MatchDetail:{MatchId}] Polling JS injected");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MatchDetail:{MatchId}] JS injection error: {ex.Message}");
            }
        }

        private void DetailWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                System.Diagnostics.Debug.WriteLine($"[MatchDetail:{MatchId}] Data received ({raw.Length} chars)");
                MatchDetailDataReceived?.Invoke(MatchId, raw);
            }
            catch { }
        }
    }

    /// <summary>
    /// Represents detailed event data extracted from a match detail page.
    /// </summary>
    public class MatchDetailData
    {
        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("homeScore")]
        public string? HomeScore { get; set; }

        [JsonProperty("awayScore")]
        public string? AwayScore { get; set; }

        [JsonProperty("stage")]
        public string? Stage { get; set; }

        [JsonProperty("minute")]
        public string? Minute { get; set; }

        [JsonProperty("isLive")]
        public bool IsLive { get; set; }

        [JsonProperty("events")]
        public List<MatchEvent>? Events { get; set; }

        [JsonProperty("leagueFlagUrl")]
        public string? LeagueFlagUrl { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

    }

    /// <summary>
    /// A single match event (goal, card, substitution).
    /// </summary>
    public class MatchEvent
    {
        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("minute")]
        public string? Minute { get; set; }

        [JsonProperty("playerName")]
        public string? PlayerName { get; set; }

        [JsonProperty("playerUrl")]
        public string? PlayerUrl { get; set; }

        [JsonProperty("teamSide")]
        public string? TeamSide { get; set; }

        [JsonProperty("incidentDescription")]
        public string? IncidentDescription { get; set; }
    }
}
