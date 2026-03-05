using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FlashscoreOverlay
{
    public partial class OverlayWindow : Window
    {
        // Fixed sizes for height calculation (in pixels)
        private const double TitleBarHeight = 32;
        private const double BorderExtra = 4;
        private const double PaddingTotal = 16;
        private const double HeaderHeight = 46;
        private const double MatchRowHeight = 50;
        private const double GroupMargin = 8;
        private const double SportHeaderHeight = 32;
        private const double EmptyStateHeight = 80;

        // Key: MatchId (or OverlayId)
        private readonly Dictionary<string, MatchData> _matches = new();
        private readonly Dictionary<string, CompetitionData> _competitions = new();
        private readonly HashSet<string> _highlightedMatches = new(StringComparer.Ordinal);

        private bool _webViewReady;
        private bool _scrollEnabled;

        private static CoreWebView2Environment? _sharedEnv;

        // Per-match detail windows: Key = MatchId
        private readonly Dictionary<string, MatchDetailWindow> _matchDetailWindows = new();

        // Track goal counts per match to detect new goals: Key = MatchId
        private readonly Dictionary<string, int> _matchGoalCounts = new();

        // Track card counts per match to detect new cards: Key = MatchId
        private readonly Dictionary<string, int> _matchCardCounts = new();

        // Active goal notification windows for stacking
        private readonly List<GoalNotificationWindow> _activeNotifications = new();

        // Active card notification windows for stacking
        private readonly List<CardNotificationWindow> _activeCardNotifications = new();

        private class PendingGoal
        {
            public int GoalCount { get; set; }
            public DateTime DetectedAt { get; set; }
        }

        // Keep track of goals waiting for player names to be published
        private readonly Dictionary<string, PendingGoal> _pendingGoals = new();

        private readonly DispatcherTimer _minuteSyncTimer;

        public event Action<string>? MatchRemoved;

        public OverlayWindow()
        {
            InitializeComponent();
            Topmost = true;
            ShowInTaskbar = false;
            Title = "Flashscore Overlay";

            // Periodic sync every 5 seconds as requested
            _minuteSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _minuteSyncTimer.Tick += ComprobarMinutoActualizado;
            _minuteSyncTimer.Start();

            _ = InitializeAsync();
        }

        public void UpdateMatches(IEnumerable<MatchData> matches, IEnumerable<CompetitionData> competitions)
        {
            var currentIds = new HashSet<string>();

            foreach (var comp in competitions)
            {
                if (comp.CompetitionId != null)
                    _competitions[comp.CompetitionId] = comp;
            }

            foreach (var m in matches)
            {
                var id = m.OverlayId ?? m.MatchId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                currentIds.Add(id);
                _matches[id] = m;
            }

            var toRemove = _matches.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var k in toRemove) _matches.Remove(k);

            RenderOverlay();
        }

        private async Task InitializeAsync()
        {
            try
            {
                if (_sharedEnv == null)
                {
                    // Block autoplay and mute audio globally for all WebViews
                    var options = new CoreWebView2EnvironmentOptions("--disable-web-security --mute-audio");
                    var userDataFolder = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "FlashscoreOverlay_WebView2");
                    _sharedEnv = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }

                // Init visible overlay WebView2
                await MatchWebView.EnsureCoreWebView2Async(_sharedEnv);
                MatchWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                MatchWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                MatchWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                MatchWebView.NavigationCompleted += MatchWebView_NavigationCompleted;
                _webViewReady = true;

                RenderOverlay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir el overlay:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(json)) return;

                var msg = JsonConvert.DeserializeObject<WebMessage>(json);
                if (msg == null) return;

                switch (msg.Type)
                {
                    case "close":
                        Close();
                        break;
                    case "navigate":
                        if (!string.IsNullOrWhiteSpace(msg.Href))
                        {
                            NavigateInTab(msg.TabId, msg.Href);
                        }
                        break;
                    case "openTeam":
                        _ = HandleOpenTeamAsync(msg);
                        break;
                    case "remove":
                        if (!string.IsNullOrWhiteSpace(msg.MatchId))
                        {
                            MatchRemoved?.Invoke(msg.MatchId!);
                        }
                        break;
                    case "removeSport":
                        if (msg.MatchIds != null)
                        {
                            foreach (var matchId in msg.MatchIds)
                            {
                                if (!string.IsNullOrWhiteSpace(matchId))
                                {
                                    MatchRemoved?.Invoke(matchId);
                                }
                            }
                        }
                        break;
                    case "setMatchHighlight":
                        if (!string.IsNullOrWhiteSpace(msg.MatchId) && msg.Highlighted.HasValue)
                        {
                            if (msg.Highlighted.Value)
                            {
                                _highlightedMatches.Add(msg.MatchId!);
                            }
                            else
                            {
                                _highlightedMatches.Remove(msg.MatchId!);
                            }
                        }
                        break;
                    case "goalEvent":
                        if (msg.MatchId != null)
                        {
                            var mainWin = Application.Current.MainWindow as MainWindow;
                            if (mainWin != null)
                            {
                                var goalData = new MessageData
                                {
                                    MatchMid = msg.MatchId,
                                    MatchUrl = msg.MatchUrl,
                                    PlayerName = msg.Name,
                                    Minute = msg.Href,
                                    TeamSide = msg.Sport,
                                    TabId = msg.TabId
                                };
                                mainWin.HandleGoalEventFromOverlay(goalData);
                            }
                        }
                        break;
                }
            }
            catch { }
        }

        public bool IsHighlighted(string matchId)
        {
            if (string.IsNullOrWhiteSpace(matchId)) return false;
            return _highlightedMatches.Contains(matchId);
        }

        private void NavigateInTab(string? tabId, string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return;
            // Use the first available tabId from either the message or any active match
            var effectiveTab = tabId;
            if (string.IsNullOrWhiteSpace(effectiveTab))
            {
                effectiveTab = _matches.Values.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.TabId))?.TabId;
            }

            if (!string.IsNullOrWhiteSpace(effectiveTab))
            {
                var main = Application.Current.MainWindow as MainWindow;
                main?.SetPendingCommandForTab(effectiveTab!, new BrowserCommand { Action = "navigate", Href = href });
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(href) { UseShellExecute = true }); } catch { }
            }
        }

        private async Task HandleOpenTeamAsync(WebMessage msg)
        {
            try
            {
                var teamName = msg.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(teamName)) return;

                // Build a URL to the match page with fc_open_team param so the
                // Tampermonkey script can auto-click the team link
                var matchUrl = msg.MatchUrl;
                if (string.IsNullOrWhiteSpace(matchUrl))
                {
                    // Try to find from stored matches
                    matchUrl = _matches.Values.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Url))?.Url;
                }

                if (!string.IsNullOrWhiteSpace(matchUrl))
                {
                    try
                    {
                        var ub = new UriBuilder(matchUrl!);
                        var qp = System.Web.HttpUtility.ParseQueryString(ub.Query);
                        qp["fc_open_team"] = teamName;
                        // If we have a direct team href, pass it as fallback
                        if (!string.IsNullOrWhiteSpace(msg.Href))
                            qp["fc_open_href"] = msg.Href;
                        ub.Query = qp.ToString();
                        var target = ub.ToString();

                        await Dispatcher.InvokeAsync(() => NavigateInTab(msg.TabId, target));
                        return;
                    }
                    catch { /* fallthrough */ }
                }

                // Fallback: navigate directly to team href or search
                var teamHref = msg.Href;
                if (string.IsNullOrWhiteSpace(teamHref))
                {
                    teamHref = "https://www.flashscore.es/search/?q=" + Uri.EscapeDataString(teamName);
                }

                await Dispatcher.InvokeAsync(() => NavigateInTab(msg.TabId, teamHref!));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling openTeam: {ex.Message}");
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) { }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => RenderOverlay();

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool AdjustWindowHeight(int sportCount, int groupCount, int totalMatchCount)
        {
            double contentHeight;
            if (groupCount == 0)
            {
                contentHeight = EmptyStateHeight + PaddingTotal;
            }
            else
            {
                contentHeight = PaddingTotal
                    + (sportCount * SportHeaderHeight)
                    + (groupCount * (HeaderHeight + GroupMargin))
                    + (totalMatchCount * MatchRowHeight);
            }

            var idealHeight = TitleBarHeight + BorderExtra + contentHeight;

            var screenHeight = SystemParameters.WorkArea.Height;
            var maxAllowed = screenHeight * 0.85;

            Height = idealHeight > maxAllowed ? maxAllowed : idealHeight;

            return idealHeight > maxAllowed;
        }

        private string GetSportForCompetition(CompetitionData? comp)
        {
            if (!string.IsNullOrWhiteSpace(comp?.Sport)) return comp!.Sport!;
            // Fallback: try to extract from href
            if (!string.IsNullOrWhiteSpace(comp?.Href))
            {
                try
                {
                    var uri = new Uri(comp!.Href!);
                    var segments = uri.AbsolutePath.Trim('/').Split('/');
                    if (segments.Length > 0 && !string.IsNullOrWhiteSpace(segments[0]))
                    {
                        var slug = segments[0].ToLowerInvariant();
                        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(slug.Replace('-', ' '));
                    }
                }
                catch { }
            }
            return "Otros";
        }

        private void RenderOverlay()
        {
            if (!_webViewReady || MatchWebView.CoreWebView2 == null) return;

            PruneHighlightedMatches();
            var groups = _matches.Values
                .GroupBy(m => GetCompetitionId(m))
                .Select(g =>
                {
                    CompetitionData? comp = null;
                    if (_competitions.TryGetValue(g.Key, out var c))
                    {
                        comp = c;
                    }
                    else
                    {
                        if (g.Key.Contains(":"))
                        {
                            var parts = g.Key.Split(new[] { ':' }, 2);
                            if (parts.Length == 2)
                                comp = new CompetitionData { Category = parts[0], Title = parts[1], CompetitionId = g.Key };
                        }
                        comp ??= new CompetitionData { Title = g.Key == "default" ? "Otros" : g.Key, CompetitionId = g.Key };
                    }

                    return new MatchGroup
                    {
                        Competition = comp,
                        Matches = g.Select(m => CreateDisplayData(m)).ToList(),
                        TabId = g.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.TabId))?.TabId
                    };
                })
                .OrderBy(g => g.Competition?.Title)
                .ToList();

            // Group by sport
            var sportGroups = groups
                .GroupBy(g => GetSportForCompetition(g.Competition))
                .Select(sg => new SportGroup
                {
                    Sport = sg.Key,
                    Groups = sg.ToList()
                })
                .OrderBy(sg => sg.Sport)
                .ToList();

            var totalMatches = groups.Sum(g => g.Matches.Count);
            var enableScroll = AdjustWindowHeight(sportGroups.Count, groups.Count, totalMatches);

            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            var payload = JsonConvert.SerializeObject(sportGroups, settings);
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

            var highlightedMatchesJson = JsonConvert.SerializeObject(_highlightedMatches);
            var html = BuildOverlayHtml(payloadBase64, enableScroll, highlightedMatchesJson);
            MatchWebView.NavigateToString(html);
            _ = SetScrollStateAsync(enableScroll);

            MatchTitle.Text = $"Flashscore ({_matches.Count})";

            // Sync per-match detail windows
            SyncMatchDetailWindows();
        }

        private void SyncMatchDetailWindows()
        {
            // Collect current match IDs that have a valid URL
            var currentMatchIds = new HashSet<string>();
            foreach (var m in _matches.Values)
            {
                var id = m.OverlayId ?? m.MatchId;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(m.Url))
                {
                    currentMatchIds.Add(id);
                }
            }

            // Close detail windows for matches no longer active
            var toClose = _matchDetailWindows.Keys.Where(k => !currentMatchIds.Contains(k)).ToList();
            foreach (var id in toClose)
            {
                try
                {
                    _matchDetailWindows[id].Close();
                }
                catch { }
                _matchDetailWindows.Remove(id);
                Debug.WriteLine($"[OverlayWindow] Closed MatchDetailWindow for {id}");
            }

            // Open detail windows for new matches
            foreach (var m in _matches.Values)
            {
                var id = m.OverlayId ?? m.MatchId;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(m.Url)) continue;
                if (_matchDetailWindows.ContainsKey(id)) continue;

                var detailWin = new MatchDetailWindow(id, m.Url);
                detailWin.MatchDetailDataReceived += OnMatchDetailDataReceived;
                detailWin.Closed += (s, e) =>
                {
                    _matchDetailWindows.Remove(id);
                };
                detailWin.Show();
                _matchDetailWindows[id] = detailWin;
                Debug.WriteLine($"[OverlayWindow] Opened MatchDetailWindow for {id} -> {m.Url}");
            }
        }

        private async void OnMatchDetailDataReceived(string matchId, string rawJson)
        {
            try
            {
                var detailData = JsonConvert.DeserializeObject<MatchDetailData>(rawJson);
                if (detailData == null) return;

                // Update the _matches dictionary with fresh data from the detail page
                MatchData? match = null;
                foreach (var kvp in _matches)
                {
                    var m = kvp.Value;
                    var id = m.OverlayId ?? m.MatchId;
                    if (string.Equals(id, matchId, StringComparison.OrdinalIgnoreCase))
                    {
                        match = m;
                        break;
                    }
                }

                if (match == null) return;

                // Update score if available
                if (!string.IsNullOrWhiteSpace(detailData.HomeScore))
                    match.HomeScore = detailData.HomeScore;
                if (!string.IsNullOrWhiteSpace(detailData.AwayScore))
                    match.AwayScore = detailData.AwayScore;

                // Update stage/time if available
                if (!string.IsNullOrWhiteSpace(detailData.Stage))
                {
                    var oldStage = match.Stage ?? "";
                    var newStage = detailData.Stage;
                    
                    match.Stage = newStage;

                    // If status changes to finished, force a re-render
                    if (!oldStage.ToLowerInvariant().Contains("finalizado") && 
                        (newStage.ToLowerInvariant().Contains("finalizado") || newStage.ToLowerInvariant().Contains("final") || newStage.ToLowerInvariant().Contains("terminado")))
                    {
                        Debug.WriteLine($"[OverlayWindow] Match {matchId} status changed to Finished. Forcing refresh.");
                        RenderOverlay();
                    }
                }

                // Update league flag URL in CompetitionData (only set once, flag never changes)
                if (!string.IsNullOrWhiteSpace(detailData.LeagueFlagUrl))
                {
                    var compId = GetCompetitionId(match);
                    if (_competitions.TryGetValue(compId, out var comp) && string.IsNullOrWhiteSpace(comp.LeagueFlagUrl))
                    {
                        comp.LeagueFlagUrl = detailData.LeagueFlagUrl;
                        Debug.WriteLine($"[OverlayWindow] LeagueFlagUrl set for {compId}: {detailData.LeagueFlagUrl}");
                        
                        // Force a re-render so the header shows the flag immediately
                        RenderOverlay();
                    }
                }

                // Count goals and red cards from events
                int currentGoalCount = 0;
                int currentCardCount = 0;
                int homeReds = 0, awayReds = 0;
                MatchEvent? latestGoal = null;
                MatchEvent? latestCard = null;

                if (detailData.Events != null)
                {
                    foreach (var ev in detailData.Events)
                    {
                        if (ev.Type == "goal" || ev.Type == "penaltyGoal" || ev.Type == "ownGoal")
                        {
                            currentGoalCount++;
                            // Capture the LAST goal encountered (Flashscore lists them chronologically, latest at bottom)
                            latestGoal = ev; 
                        }
                        if (ev.Type == "redCard" || ev.Type == "yellowCard")
                        {
                            currentCardCount++;
                            // Capture the LAST card (latest in cronology)
                            latestCard = ev;
                        }
                        if (ev.Type == "redCard")
                        {
                            if (ev.TeamSide == "home") homeReds++;
                            else if (ev.TeamSide == "away") awayReds++;
                        }
                    }
                    match.HomeRedCards = homeReds;
                    match.AwayRedCards = awayReds;
                }

                // Detect NEW goals by comparing with previous count
                bool isFirstPoll = !_matchGoalCounts.ContainsKey(matchId);
                _matchGoalCounts.TryGetValue(matchId, out var previousGoalCount);
                bool isNewGoal = false;

                if (isFirstPoll)
                {
                    // First poll: only show badge if latest goal is in current minute ±1
                    _matchGoalCounts[matchId] = currentGoalCount;
                    if (latestGoal != null && !string.IsNullOrWhiteSpace(latestGoal.Minute) && !string.IsNullOrWhiteSpace(detailData.Stage))
                    {
                        // Parse both minutes: goal minute (e.g. "67'") and current minute (e.g. "68")
                        var goalMinStr = System.Text.RegularExpressions.Regex.Match(latestGoal.Minute, @"\d+").Value;
                        var curMinStr = System.Text.RegularExpressions.Regex.Match(detailData.Stage, @"\d+").Value;
                        if (int.TryParse(goalMinStr, out var goalMin) && int.TryParse(curMinStr, out var curMin))
                        {
                            if (Math.Abs(curMin - goalMin) <= 1)
                            {
                                isNewGoal = true;
                                Debug.WriteLine($"[OverlayWindow] First poll GOL in current minute: goal@{goalMin} current@{curMin}");
                            }
                        }
                    }
                }
                else
                {
                    // Subsequent polls: any increase means a goal just happened live
                    isNewGoal = currentGoalCount > previousGoalCount;
                    _matchGoalCounts[matchId] = currentGoalCount;
                }

                // Detect NEW cards by comparing with previous count
                bool isFirstCardPoll = !_matchCardCounts.ContainsKey(matchId);
                _matchCardCounts.TryGetValue(matchId, out var previousCardCount);
                bool isNewCard = false;

                if (isFirstCardPoll)
                {
                    _matchCardCounts[matchId] = currentCardCount;
                    if (latestCard != null && !string.IsNullOrWhiteSpace(latestCard.Minute) && !string.IsNullOrWhiteSpace(detailData.Stage))
                    {
                        var cardMinStr = System.Text.RegularExpressions.Regex.Match(latestCard.Minute, @"\d+").Value;
                        var curMinStr = System.Text.RegularExpressions.Regex.Match(detailData.Stage, @"\d+").Value;
                        if (int.TryParse(cardMinStr, out var cardMin) && int.TryParse(curMinStr, out var curMin))
                        {
                            if (Math.Abs(curMin - cardMin) <= 1) isNewCard = true;
                        }
                    }
                }
                else
                {
                    isNewCard = currentCardCount > previousCardCount;
                    _matchCardCounts[matchId] = currentCardCount;
                }

                // CHECK PENDING GOALS SYSTEM
                bool triggerGoalBadge = false;
                bool triggerGoalNotification = false;

                if (isNewGoal && latestGoal != null)
                {
                    // A new goal was detected right now
                    triggerGoalBadge = true; // Always show badge in list immediately

                    bool hasPlayerName = !string.IsNullOrWhiteSpace(latestGoal.PlayerName);
                    if (hasPlayerName)
                    {
                        // Name published instantly, fire popup immediately
                        triggerGoalNotification = true;
                    }
                    else
                    {
                        // Name missing, queue popup for up to 60 seconds
                        _pendingGoals[matchId] = new PendingGoal
                        {
                            GoalCount = currentGoalCount,
                            DetectedAt = DateTime.Now
                        };
                        Debug.WriteLine($"[OverlayWindow] Goal detected for {matchId} but missing player name. Popup queued.");
                    }
                }
                else if (_pendingGoals.TryGetValue(matchId, out var pending))
                {
                    // We are waiting for a player name for this match's popup
                    if (latestGoal != null && !string.IsNullOrWhiteSpace(latestGoal.PlayerName))
                    {
                        // The name has finally arrived! Trigger popup and update badge
                        triggerGoalNotification = true;
                        triggerGoalBadge = true; 
                        _pendingGoals.Remove(matchId);
                        Debug.WriteLine($"[OverlayWindow] Player name arrived for pending goal on {matchId}. Triggering popup.");
                    }
                    else if ((DateTime.Now - pending.DetectedAt).TotalSeconds >= 180)
                    {
                        // Timeout reached, show popup anyway
                        triggerGoalNotification = true;
                        _pendingGoals.Remove(matchId);
                        Debug.WriteLine($"[OverlayWindow] 180s timeout reached for pending goal on {matchId}. Showing popup without name.");
                    }
                }

                // Now pipe the data through the existing applyLiveUpdate mechanism
                if (_webViewReady && MatchWebView.CoreWebView2 != null)
                {
                    var livePayload = new
                    {
                        type = "liveData",
                        matches = new[]
                        {
                            new
                            {
                                matchId = match.MatchId,
                                stage = detailData.Stage ?? "",
                                time = detailData.Stage ?? "",
                                homeScore = detailData.HomeScore ?? "",
                                awayScore = detailData.AwayScore ?? "",
                                hasBlink = !string.IsNullOrWhiteSpace(detailData.Stage)
                                    && detailData.Stage != "Descanso"
                                    && detailData.IsLive,
                                homeRedCards = match.HomeRedCards,
                                awayRedCards = match.AwayRedCards
                            }
                        }
                    };
                    var json = JsonConvert.SerializeObject(livePayload);
                    var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "").Replace("\r", "");
                    await MatchWebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(window.applyLiveUpdate) window.applyLiveUpdate('{escaped}');"
                    );

                    // Show GOL badge if triggered (immediately when score changes)
                    if (triggerGoalBadge && latestGoal != null)
                    {
                        var side = (latestGoal.TeamSide ?? "home").Replace("'", "\\'");
                        var player = (latestGoal.PlayerName ?? "").Replace("'", "\\'");
                        var badgeMinute = !string.IsNullOrWhiteSpace(latestGoal.Minute) 
                            ? latestGoal.Minute.Replace("'", "\\'") 
                            : (detailData.Stage ?? "").Replace("'", "\\'");

                        await MatchWebView.CoreWebView2.ExecuteScriptAsync(
                            $"if(window.showGoalBadge) window.showGoalBadge('{match.MatchId}', '{side}', '{player}', '{badgeMinute}');"
                        );
                        Debug.WriteLine($"[OverlayWindow] Badge GOL! {latestGoal.TeamSide} - {latestGoal.PlayerName} ({match.MatchId})");
                    }

                    // Show popup notification if triggered (immediately with name or after delay)
                    if (triggerGoalNotification && latestGoal != null)
                    {
                        var lookupId = match.OverlayId ?? match.MatchId;
                        if (lookupId != null && _highlightedMatches.Contains(lookupId))
                        {
                            ShowGoalNotification(match, latestGoal, detailData.Stage ?? "");
                        }
                    }

                    // Show popup notification for cards on highlighted matches
                    if (isNewCard && latestCard != null)
                    {
                        var lookupId = match.OverlayId ?? match.MatchId;
                        if (lookupId != null && _highlightedMatches.Contains(lookupId))
                        {
                            ShowCardNotification(match, latestCard, detailData.Stage ?? "");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error processing match detail for {matchId}: {ex.Message}");
            }
        }

        private void PruneHighlightedMatches()
        {
            var toRemove = _highlightedMatches.Where(id => !_matches.ContainsKey(id)).ToList();
            foreach (var id in toRemove)
            {
                _highlightedMatches.Remove(id);
            }
        }

        private void ShowGoalNotification(MatchData match, MatchEvent goalEvent, string currentMinute)
        {
            try
            {
                var teamName = goalEvent.TeamSide == "away" ? match.AwayTeam : match.HomeTeam;
                var stackIndex = _activeNotifications.Count;

                // Priority: Use the actual minute the goal happened (from the timeline event)
                // Fallback: The current live minute of the match.
                var displayMinute = !string.IsNullOrWhiteSpace(goalEvent.Minute) 
                    ? goalEvent.Minute.Replace("'", "").Trim() 
                    : (currentMinute ?? "").Replace("'", "").Trim();

                // Derive stage text from minute
                var stageText = "En juego";
                var minNum = 0;
                var minPart = displayMinute.Split('+')[0];
                if (int.TryParse(minPart, out minNum))
                {
                    if (minNum <= 45) stageText = "1er tiempo";
                    else if (minNum <= 90) stageText = "2º tiempo";
                    else stageText = "Prórroga";
                }

                var notif = new GoalNotificationWindow(
                    playerName: goalEvent.PlayerName ?? "",
                    teamName: teamName ?? "",
                    playerImageUrl: "", // Player image not always available
                    homeTeam: match.HomeTeam ?? "",
                    awayTeam: match.AwayTeam ?? "",
                    homeScore: match.HomeScore ?? "0",
                    awayScore: match.AwayScore ?? "0",
                    homeLogo: match.HomeLogo ?? "",
                    awayLogo: match.AwayLogo ?? "",
                    minute: displayMinute,
                    stageText: stageText,
                    matchUrl: match.Url ?? "",
                    scoringTeamSide: goalEvent.TeamSide ?? "home",
                    stackIndex: stackIndex
                );

                notif.NotificationClosed += (closedNotif) =>
                {
                    _activeNotifications.Remove(closedNotif);
                    // Re-stack remaining notifications
                    for (int i = 0; i < _activeNotifications.Count; i++)
                    {
                        _activeNotifications[i].UpdateStackPosition(i);
                    }
                };

                _activeNotifications.Add(notif);
                notif.Show();
                Debug.WriteLine($"[OverlayWindow] Goal notification shown for {match.HomeTeam} vs {match.AwayTeam} (stack: {stackIndex})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error pushing goal notification: {ex.Message}");
            }
        }

        private void ShowCardNotification(MatchData match, MatchEvent cardEvent, string currentMinute)
        {
            try
            {
                var teamName = cardEvent.TeamSide == "away" ? match.AwayTeam : match.HomeTeam;
                var stackIndex = _activeCardNotifications.Count;

                var displayMinute = !string.IsNullOrWhiteSpace(cardEvent.Minute) 
                    ? cardEvent.Minute.Replace("'", "").Trim() 
                    : (currentMinute ?? "").Replace("'", "").Trim();

                var stageText = "En juego";
                var minNum = 0;
                var minPart = displayMinute.Split('+')[0];
                if (int.TryParse(minPart, out minNum))
                {
                    if (minNum <= 45) stageText = "1er tiempo";
                    else if (minNum <= 90) stageText = "2º tiempo";
                    else stageText = "Prórroga";
                }

                var notif = new CardNotificationWindow(
                    cardType: cardEvent.Type ?? "yellowCard",
                    playerName: cardEvent.PlayerName ?? "",
                    teamName: teamName ?? "",
                    incidentDescription: cardEvent.IncidentDescription ?? "",
                    homeTeam: match.HomeTeam ?? "",
                    awayTeam: match.AwayTeam ?? "",
                    homeScore: match.HomeScore ?? "0",
                    awayScore: match.AwayScore ?? "0",
                    homeLogo: match.HomeLogo ?? "",
                    awayLogo: match.AwayLogo ?? "",
                    minute: displayMinute,
                    stageText: stageText,
                    matchUrl: match.Url ?? "",
                    stackIndex: stackIndex
                );

                notif.NotificationClosed += (closedNotif) =>
                {
                    _activeCardNotifications.Remove(closedNotif);
                    // Re-stack remaining notifications
                    for (int i = 0; i < _activeCardNotifications.Count; i++)
                    {
                        _activeCardNotifications[i].UpdateStackPosition(i);
                    }
                };

                _activeCardNotifications.Add(notif);
                notif.Show();
                Debug.WriteLine($"[OverlayWindow] Card notification shown for {match.HomeTeam} vs {match.AwayTeam} (stack: {stackIndex})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error pushing card notification: {ex.Message}");
            }
        }

        private async void ComprobarMinutoActualizado(object? sender, EventArgs e)
        {
            try
            {
                if (!_webViewReady || MatchWebView.CoreWebView2 == null) return;
                if (_matches.Count == 0) return;

                var liveUpdates = new List<object>();

                foreach (var match in _matches.Values)
                {
                    // If we have stage/time data, queue an update
                    if (!string.IsNullOrWhiteSpace(match.Stage) || !string.IsNullOrWhiteSpace(match.HomeScore))
                    {
                        bool isLive = match.Stage != "Descanso" && match.Stage != "Finalizado" && !string.IsNullOrWhiteSpace(match.Stage);

                        liveUpdates.Add(new
                        {
                            matchId = match.MatchId,
                            stage = match.Stage ?? "",
                            time = match.Stage ?? "",
                            homeScore = match.HomeScore ?? "",
                            awayScore = match.AwayScore ?? "",
                            hasBlink = isLive,
                            homeRedCards = match.HomeRedCards,
                            awayRedCards = match.AwayRedCards
                        });
                    }
                }

                if (liveUpdates.Count > 0)
                {
                    var livePayload = new
                    {
                        type = "liveData",
                        matches = liveUpdates
                    };
                    var json = JsonConvert.SerializeObject(livePayload);
                    var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "").Replace("\r", "");
                    await MatchWebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(window.applyLiveUpdate) window.applyLiveUpdate('{escaped}');"
                    );
                    Debug.WriteLine($"[OverlayWindow] ComprobarMinutoActualizado: sent sync for {liveUpdates.Count} active matches.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayWindow] Error in ComprobarMinutoActualizado: {ex.Message}");
            }
        }

        private async void MatchWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;
            await UpdateHeightFromWebViewAsync();
        }

        private async Task UpdateHeightFromWebViewAsync()
        {
            if (!_webViewReady || MatchWebView.CoreWebView2 == null) return;

            try
            {
                var script = "Math.max(document.body.scrollHeight, document.documentElement.scrollHeight)";
                var raw = await MatchWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (string.IsNullOrWhiteSpace(raw)) return;

                raw = raw.Trim();
                if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.EndsWith("\"", StringComparison.Ordinal))
                {
                    raw = raw[1..^1];
                }

                if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var contentHeight)) return;

                var desiredHeight = TitleBarHeight + BorderExtra + contentHeight + PaddingTotal;
                var screenHeight = SystemParameters.WorkArea.Height;
                var maxAllowed = screenHeight * 0.85;
                var clampedHeight = desiredHeight > maxAllowed ? maxAllowed : desiredHeight;
                clampedHeight = Math.Max(clampedHeight, MinHeight);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (Math.Abs(Height - clampedHeight) > 1)
                    {
                        Height = clampedHeight;
                    }
                });

                var needsScroll = desiredHeight > maxAllowed;
                await SetScrollStateAsync(needsScroll);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error ajustando altura del overlay: {ex.Message}");
            }
        }

        private async Task SetScrollStateAsync(bool enable)
        {
            if (_scrollEnabled == enable || !_webViewReady || MatchWebView.CoreWebView2 == null) return;

            _scrollEnabled = enable;
            var value = enable ? "auto" : "hidden";

            try
            {
                await MatchWebView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        document.body.style.overflowY = '{value}';
                        document.body.style.overflowX = 'hidden';
                    }} catch (e) {{}}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error actualizando scroll del overlay: {ex.Message}");
            }
        }

        private string GetCompetitionId(MatchData m) => m.CompetitionId ?? m.Stage ?? "default";



        private OverlayDisplayData CreateDisplayData(MatchData md)
        {
            var (timeLabel, trailing) = SplitTimeSegment(md.Time);
            var stage = md.Stage?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stage)) stage = trailing;

            var hasScore = !string.IsNullOrWhiteSpace(md.HomeScore) || !string.IsNullOrWhiteSpace(md.AwayScore);
            var isLive = DetermineLiveState(md.Time, md.Stage);

            var shouldBlink = (md.Time?.Contains("'") == true || md.Stage?.Contains("'") == true);
            var isDescanso = stage.ToLowerInvariant().Contains("descanso");
            var displayTimeLabel = isDescanso
                ? "Descanso"
                : (string.IsNullOrWhiteSpace(timeLabel) ? "\u2014" : timeLabel);
            var displayStageLabel = isDescanso ? string.Empty : stage;

            var isTennisMatch = !string.IsNullOrWhiteSpace(md.HomeFlag) || !string.IsNullOrWhiteSpace(md.AwayFlag)
                || (md.Url?.Contains("/tenis/") == true) || (md.Url?.Contains("/tennis/") == true)
                || (md.SetScores != null && md.SetScores.Count > 0);
            if (isTennisMatch && !string.IsNullOrWhiteSpace(stage))
            {
                displayTimeLabel = stage;
                displayStageLabel = string.Empty;
            }

            return new OverlayDisplayData
            {
                MatchId = md.MatchId,
                OverlayId = md.OverlayId,
                TimeLabel = displayTimeLabel,
                StageLabel = displayStageLabel,
                HomeTeam = md.HomeTeam ?? "Local",
                AwayTeam = md.AwayTeam ?? "Visitante",
                HomeLogo = md.HomeLogo ?? string.Empty,
                AwayLogo = md.AwayLogo ?? string.Empty,
                HomeHref = md.HomeHref ?? string.Empty,
                AwayHref = md.AwayHref ?? string.Empty,
                HomeRedCards = md.HomeRedCards,
                AwayRedCards = md.AwayRedCards,
                MatchHref = md.Url ?? string.Empty,
                HomeScore = string.IsNullOrWhiteSpace(md.HomeScore) ? string.Empty : md.HomeScore.Trim(),
                AwayScore = string.IsNullOrWhiteSpace(md.AwayScore) ? string.Empty : md.AwayScore.Trim(),
                HasScore = hasScore,
                IsLive = isLive,
                IsBlinking = shouldBlink && !isDescanso,
                Url = md.Url,
                Category = null, 
                CompetitionTitle = null, 
                
                HomeFlag = md.HomeFlag,
                AwayFlag = md.AwayFlag,
                HomeSets = md.HomeSets,
                AwaySets = md.AwaySets,
                HomeGamePoints = md.HomeGamePoints,
                AwayGamePoints = md.AwayGamePoints,
                HomeService = md.HomeService,
                AwayService = md.AwayService,
                SetScores = md.SetScores
            };
        }

        private static (string time, string trailing) SplitTimeSegment(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, string.Empty);
            var t = raw.Trim();
            var idx = -1;
            for (var i = 0; i < t.Length; i++) { if (char.IsLetter(t[i])) { idx = i; break; } }
            if (idx <= 0) return (t, string.Empty);
            return (t.Substring(0, idx).Trim(), t.Substring(idx).Trim());
        }

        private static bool DetermineLiveState(string? timeSource, string? stageSource)
        {
            var stage = (stageSource ?? "").Trim().ToLowerInvariant();
            var time = (timeSource ?? "").Trim().ToLowerInvariant();

            if (stage.Contains("preview") || stage.Contains("finalizado") || stage.Contains("final") || stage.Contains("terminado")) return false;
            if (stage.Contains("penaltis") || stage.Contains("postergado") || stage.Contains("aplazado") || stage.Contains("cancelado")) return false;

            if (stage.Contains("descanso") || stage.Contains("en directo") || stage.Contains("en curso") || stage.Contains("juego") || stage.Contains("gol")) return true;
            if (stage.Contains("parte") || stage.Contains("tiempo") || stage.Contains("prórroga") || stage.Contains("extra")) return true;
            if (stage.Contains("set")) return true; 
            if (stage.Contains("'") || time.Contains("'")) return true;

            if (time.Length > 0 && char.IsDigit(time[0]) && !time.Contains(":") && !time.Contains(".")) return true;

            return false;
        }

        private string BuildOverlayHtml(string initialGroupsBase64, bool enableScroll, string highlightedMatchesJson)
        {
            var overflowRule = enableScroll ? "overflow-y: auto;" : "overflow: hidden;";

            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width,initial-scale=1'>
    <style>
        :root {{
            --bg-color: #1a1a2e;
            --header-bg: #16213e;
            --text-main: #e8e8e8;
            --text-secondary: #8a8a9a;
            --border-color: #2a2a4a;
            --hover-bg: rgba(255, 255, 255, 0.05);
            --color-primary: #C80037;

            --row-bg: #1e1e3a;
            --row-hover: #252550;
            --text-time: #9a9ab0;
            --text-time-live: var(--color-primary);
            --border-row: #2a2a4a;
            --score-live: var(--color-primary);
            --score-finished: #e0e0e0;
        }}

        @keyframes blinkAnim {{
            0%, 100% {{ opacity: 1; }}
            50% {{ opacity: 0; }}
        }}

        .blink {{
            animation: blinkAnim 1s step-end infinite;
        }}

        * {{ box-sizing: border-box; margin: 0; padding: 0; }}

        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background: var(--bg-color);
            {overflowRule}
            user-select: none;
        }}

        body::-webkit-scrollbar {{ width: 6px; }}
        body::-webkit-scrollbar-track {{ background: var(--bg-color); }}
        body::-webkit-scrollbar-thumb {{ background: #444466; border-radius: 3px; }}
        body::-webkit-scrollbar-thumb:hover {{ background: #5a5a80; }}

        #app-container {{ padding: 6px; }}

        /* === League Header === */
        .competition-container {{
            margin-bottom: 6px;
            border-radius: 8px;
            overflow: visible;
            box-shadow: 0 2px 12px rgba(0, 0, 0, 0.35);
            background: var(--header-bg);
            padding-bottom: 6px;
        }}

        .headerLeague {{
            display: flex;
            align-items: center;
            padding: 10px 14px;
            min-height: 44px;
            border-bottom: 1px solid var(--border-color);
            background: var(--header-bg);
        }}

        .headerLeague__body {{
            display: flex;
            align-items: center;
            flex-grow: 1;
            overflow: hidden;
        }}

        .headerLeague__titleWrapper {{
            display: flex;
            align-items: baseline;
            min-width: 0;
            overflow: hidden;
            white-space: nowrap;
            text-overflow: ellipsis;
            gap: 0;
        }}

        .headerLeague__category {{
            color: var(--text-secondary);
            font-weight: 700;
            font-size: 12px;
            text-transform: uppercase;
            letter-spacing: 0.03em;
            flex-shrink: 0;
        }}

        .headerLeague__separator {{
            color: var(--text-secondary);
            font-size: 12px;
            margin: 0 5px;
            flex-shrink: 0;
        }}

        .headerLeague__title {{
            text-decoration: none;
            color: var(--text-main);
            font-weight: 700;
            font-size: 12px;
            letter-spacing: 0.03em;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            cursor: pointer;
            transition: color 0.15s ease;
        }}

        .headerLeague__title:hover {{
            color: #ffffff;
        }}

        /* === Match Row === */
        .match-row-wrapper {{
            display: flex;
            flex-direction: column;
            overflow: visible;
        }}

        .match-row {{
            display: flex;
            align-items: center;
            background: var(--row-bg);
            border-bottom: 1px solid var(--border-row);
            padding: 10px 14px;
            position: relative;
            text-decoration: none;
            color: inherit;
            transition: background 0.12s ease-out, box-shadow 0.2s ease;
            min-height: 48px;
            cursor: pointer;
            border-radius: 8px;
            margin: 2px 0;
        }}

        .match-row--highlight {{
            box-shadow: 0 0 0 2px rgba(199, 0, 53, 0.8);
            z-index: 1;
        }}

        .match-row:hover {{ background: var(--row-hover); }}
        .match-row:last-child {{ border-bottom: none; }}

        .row-link {{
            position: absolute;
            top: 0; left: 0; right: 0; bottom: 0;
            z-index: 1;
            outline: none;
        }}

        /* Time / Stage column */
        .event__time {{
            font-size: 11px;
            color: var(--text-time);
            width: 70px;
            font-weight: 500;
            letter-spacing: 0.02em;
            text-align: center;
            flex-shrink: 0;
            white-space: normal;
            word-break: break-word;
            line-height: 1.3;
        }}

        .event__stage {{
            color: var(--color-primary);
            font-weight: 700;
            font-size: 11px;
            width: 70px;
            text-align: center;
            flex-shrink: 0;
            white-space: normal;
            word-break: break-word;
            line-height: 1.3;
        }}

        .event__stage--block {{
            display: inline;
        }}

        .status-finished {{
            color: var(--text-secondary) !important;
            font-weight: 400 !important;
        }}

        .red-card-icon {{
            display: inline-block;
            width: 8px;
            height: 10px;
            background-color: #d20000;
            margin-left: 4px;
            border-radius: 1px;
            vertical-align: middle;
        }}

        /* GOL badge */
        @keyframes golFadeIn {{
            0% {{ opacity: 0; transform: scale(0.5); }}
            50% {{ opacity: 1; transform: scale(1.2); }}
            100% {{ opacity: 1; transform: scale(1); }}
        }}

        .gol-badge {{
            display: inline-block;
            color: var(--color-primary);
            font-weight: 900;
            font-size: 11px;
            margin-left: 6px;
            letter-spacing: 0.5px;
            vertical-align: middle;
            animation: golFadeIn 0.4s ease-out;
        }}

        /* Red highlight on match row during GOL */
        .match-row.gol-active {{
            background: rgba(200, 0, 55, 0.12) !important;
            border-left: 3px solid var(--color-primary);
        }}

        /* Red team name during GOL */
        .team-item.gol-team-highlight .team-name {{
            color: var(--color-primary) !important;
            font-weight: 700;
        }}

        /* Teams block */
        .teams-container {{
            display: flex;
            flex-direction: column;
            flex-grow: 1;
            gap: 4px;
            justify-content: center;
            margin-left: 14px;
            min-width: 0;
        }}

        .team-item {{
            display: flex;
            align-items: center;
            gap: 8px;
            position: relative;
            z-index: 2;
            cursor: pointer;
        }}

        .team-logo {{
            width: 18px;
            height: 18px;
            object-fit: contain;
            flex-shrink: 0;
            border-radius: 2px;
            cursor: pointer;
        }}

        .flag-icon {{
            width: 18px;
            height: 13px;
            object-fit: cover;
            flex-shrink: 0;
            border-radius: 1px;
            margin-right: 8px;
        }}

        .team-name {{
            font-size: 12px;
            font-weight: 600;
            color: var(--text-main);
            line-height: 1.25;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            cursor: pointer;
            transition: color 0.15s ease;
        }}

        /* Tennis Service Indicator */
        .service-icon {{
            display: inline-flex;
            align-items: center;
            justify-content: center;
            width: 10px;
            height: 10px;
            margin-left: 4px;
            flex-shrink: 0;
            color: #8a8a9a;
            font-size: 10px;
            line-height: 1;
        }}

        /* Score column (standard football) */
        .score-col {{
            display: flex;
            flex-direction: column;
            align-items: flex-end;
            gap: 4px;
            margin-left: auto;
            padding-left: 10px;
            flex-shrink: 0;
        }}

        .score-val {{
            font-weight: 700;
            font-size: 12px;
            color: var(--score-live);
            min-width: 16px;
            text-align: center;
        }}

        /* === Tennis Score Grid === */
        .tennis-row {{
            display: flex;
            align-items: center;
            background: var(--row-bg);
            border-bottom: 1px solid var(--border-row);
            padding: 6px 14px;
            position: relative;
            text-decoration: none;
            color: inherit;
            transition: background 0.12s ease-out, box-shadow 0.2s ease;
            min-height: 52px;
            cursor: pointer;
            border-radius: 8px;
            margin: 2px 0;
        }}
        .tennis-row:hover {{ background: var(--row-hover); }}
        .tennis-row:last-child {{ border-bottom: none; }}

        .tennis-body {{
            display: flex;
            align-items: center;
            flex: 1;
            min-width: 0;
        }}

        .tennis-names {{
            display: flex;
            flex-direction: column;
            gap: 3px;
            flex: 1;
            min-width: 0;
        }}

        .tennis-player {{
            display: flex;
            align-items: center;
            gap: 6px;
            min-width: 0;
            position: relative;
            z-index: 2;
        }}

        .tennis-player .team-name {{
            flex: 1;
            min-width: 0;
        }}

        .tennis-scores {{
            display: flex;
            align-items: stretch;
            gap: 0;
            margin-left: 8px;
            flex-shrink: 0;
            font-variant-numeric: tabular-nums;
        }}

        .tennis-score-col {{
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 3px;
            min-width: 20px;
            padding: 0 3px;
        }}

        .tennis-score-col.sets-won {{
            color: var(--color-primary);
            font-weight: 700;
            min-width: 22px;
            margin-right: 4px;
        }}

        .tennis-score-col.set-score {{
            color: #b0b0b8;
        }}

        .tennis-score-col.game-points {{
            color: var(--text-main);
            min-width: 24px;
            margin-left: 6px;
            padding: 2px 4px;
            background: rgba(255, 255, 255, 0.06);
            border-radius: 3px;
        }}

        .sc-val {{
            font-size: 12px;
            text-align: center;
            line-height: 1.3;
        }}

        .sc-val.bold {{
            font-weight: 700;
        }}

        .score-val.finished {{ color: var(--score-finished); }}
        .score-val.preview {{ color: transparent; }}

        .stage-label {{
            display: block;
            font-size: 9px;
            color: var(--text-secondary);
            text-transform: uppercase;
            letter-spacing: 0.04em;
            font-weight: 600;
            margin-top: 2px;
            text-align: center;
        }}

        /* Empty state */
        .empty-state {{
            padding: 30px 16px;
            text-align: center;
            color: var(--text-secondary);
            font-size: 13px;
            background: var(--header-bg);
            border-radius: 8px;
        }}

        /* === Sport Header === */
        .sport-header {{
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 5px 14px;
            background: #0e0e1a;
            border-bottom: 1px solid #2a2a3a;
            margin-top: 4px;
        }}

        .sport-header:first-child {{
            margin-top: 0;
        }}

        .sport-header__icon {{
            font-size: 11px;
            flex-shrink: 0;
            filter: grayscale(1) brightness(1.4);
            opacity: 1;
        }}

        .sport-header__name {{
            font-size: 10px;
            font-weight: 400;
            text-transform: uppercase;
            letter-spacing: 0.06em;
            color: #6b6b82;
        }}
    </style>
</head>
<body>
    <div id='app-container'></div>

    <script>
        const base64Payload = '{initialGroupsBase64}';
        let initialGroups = [];

        try {{
            const decoded = decodeURIComponent(escape(atob(base64Payload)));
            initialGroups = JSON.parse(decoded);
        }} catch(e) {{
            try {{
                initialGroups = JSON.parse(atob(base64Payload));
            }} catch(e2) {{
                document.getElementById('app-container').innerHTML = '<div class=""empty-state"">Error al cargar datos</div>';
            }}
        }}

        const container = document.getElementById('app-container');
        const placeholder = ""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='18' height='18'%3E%3Crect width='18' height='18' fill='%23333355' rx='2'/%3E%3C/svg%3E"";
        const highlightedMatches = new Set({highlightedMatchesJson});

        function applyHighlights() {{
            const rows = container.querySelectorAll('.match-row, .tennis-row');
            rows.forEach(row => {{
                const matchId = row.getAttribute('data-id') || '';
                if (matchId && highlightedMatches.has(matchId)) {{
                    row.classList.add('match-row--highlight');
                }} else {{
                    row.classList.remove('match-row--highlight');
                }}
            }});
        }}

        function send(type, data) {{
            if (window.chrome?.webview) window.chrome.webview.postMessage(JSON.stringify({{ type, ...data }}));
        }}

        // Single-click: prevent navigation on links
        document.addEventListener('click', (e) => {{
            const link = e.target.closest('a');
            if (link && link.href && !link.href.endsWith('#')) {{
                e.preventDefault();
            }}
        }});

        // Double-click: navigate
        document.addEventListener('dblclick', (e) => {{
            const clickedEl = e.target;
            const isTeamName = clickedEl.classList.contains('team-name');
            const isTeamLogo = clickedEl.classList.contains('team-logo') || clickedEl.classList.contains('flag-icon');

            if (isTeamName || isTeamLogo) {{
                const teamItem = clickedEl.closest('.team-item, .tennis-player');
                if (teamItem) {{
                    e.preventDefault();
                    e.stopPropagation();
                    const teamName = teamItem.getAttribute('data-team-name') || '';
                    const teamHref = teamItem.getAttribute('data-team-href') || '';
                    const matchUrl = teamItem.getAttribute('data-match-url') || '';
                    if (teamHref) {{
                        send('openTeam', {{ Name: teamName, Href: teamHref, MatchUrl: matchUrl }});
                    }} else if (teamName) {{
                        send('openTeam', {{ Name: teamName, MatchUrl: matchUrl }});
                    }}
                    return;
                }}
            }}

            const link = e.target.closest('a');
            if (link && link.href && !link.href.endsWith('#')) {{
                e.preventDefault();
                e.stopPropagation();
                send('navigate', {{ href: link.href }});
            }}
        }});

        // Middle-click toggles border highlight for multiple matches
        document.addEventListener('auxclick', (e) => {{
            if (e.button !== 1) return;
            const row = e.target.closest('.match-row, .tennis-row');
            if (!row) return;
            e.preventDefault();
            const matchId = row.getAttribute('data-id') || '';
            if (!matchId) return;
            const willHighlight = !highlightedMatches.has(matchId);
            if (willHighlight) {{
                highlightedMatches.add(matchId);
            }} else {{
                highlightedMatches.delete(matchId);
            }}
            applyHighlights();
            send('setMatchHighlight', {{ MatchId: matchId, Highlighted: willHighlight }});
        }});

        // Right-click on match rows or sport headers
        document.addEventListener('contextmenu', (e) => {{
            const sportHeader = e.target.closest('.sport-header');
            if (sportHeader) {{
                e.preventDefault();
                const idsAttr = sportHeader.getAttribute('data-match-ids') || '';
                const ids = idsAttr
                    .split(',')
                    .map(id => id.trim())
                    .filter(Boolean)
                    .map(id => decodeURIComponent(id));
                const sportName = sportHeader.getAttribute('data-sport-name') || '';
                if (ids.length) {{
                    send('removeSport', {{ matchIds: ids, sport: sportName }});
                }}
                return;
            }}

            const row = e.target.closest('.match-row, .tennis-row');
            if (row) {{
                e.preventDefault();
                const matchId = row.getAttribute('data-id') || '';
                if (matchId) send('remove', {{ MatchId: matchId }});
            }}
        }});

        const SPORT_ICONS = {{
            'Fútbol': '⚽', 'Baloncesto': '🏀', 'Tenis': '🎾',
            'Hockey': '🏒', 'Hockey Hielo': '🏒', 'Balonmano': '🤾',
            'Béisbol': '⚾', 'Rugby': '🏉', 'Voleibol': '🏐',
            'Fútbol Americano': '🏈', 'Cricket': '🏏', 'eSports': '🎮',
            'Dardos': '🎯', 'Futsal': '⚽', 'Golf': '⛳', 'MMA': '🥊',
            'Motorsport': '🏎️', 'Ciclismo': '🚴', 'Waterpolo': '🤽',
            'Bádminton': '🏸', 'Snooker': '🎱', 'Tenis de Mesa': '🏓',
            'Boxeo': '🥊', 'Pádel': '🎾', 'Rugby League': '🏉',
            'AFL': '🏈', 'Floorball': '🏒', 'Bandy': '🏒',
            'Otros': '🏅'
        }};

        function attrEscape(value) {{
            return (value || '').replace(/'/g, '&#39;').replace(/""/g, '&quot;');
        }}

        function renderSportHeader(sportGroup) {{
            const sportName = sportGroup?.sport || 'Otros';
            const icon = SPORT_ICONS[sportName] || '🏅';
            const matchIds = (sportGroup?.groups || [])
                .flatMap(g => (g.matches || []).map(m => m.matchId || m.overlayId || ''))
                .filter(Boolean);

            const header = document.createElement('div');
            header.className = 'sport-header';
            header.setAttribute('data-sport-name', attrEscape(sportName));
            if (matchIds.length) {{
                const encoded = matchIds.map(id => encodeURIComponent(id));
                header.setAttribute('data-match-ids', encoded.join(','));
            }}

            const iconSpan = document.createElement('span');
            iconSpan.className = 'sport-header__icon';
            iconSpan.textContent = icon;

            const nameSpan = document.createElement('span');
            nameSpan.className = 'sport-header__name';
            nameSpan.textContent = sportName;

            header.appendChild(iconSpan);
            header.appendChild(nameSpan);

            return header.outerHTML;
        }}

        function renderHeader(comp) {{
            const url = comp.hrefWithParam || comp.href || '#';
            const title = comp.title || 'Competición';
            const category = comp.category || '';
            const flagUrl = comp.leagueFlagUrl || '';

            let flagHtml = '';
            if (flagUrl) {{
                flagHtml = `<img src='${{flagUrl}}' class='headerLeague__flag' alt='Flag' style='width:18px; height:10px; margin-right:8px; vertical-align:middle; border-radius:1px; object-fit:contain;'>`;
            }}

            let headerContent = '';
            if (category) {{
                headerContent = `${{flagHtml}}<span class='headerLeague__category'>${{category}}</span><span class='headerLeague__separator'>:</span><a href='${{url}}' class='headerLeague__title'>${{title}}</a>`;
            }} else {{
                headerContent = `${{flagHtml}}<a href='${{url}}' class='headerLeague__title'>${{title}}</a>`;
            }}

            return `
            <header class='headerLeague'>
                <div class='headerLeague__body'>
                    <div class='headerLeague__titleWrapper'>
                        ${{headerContent}}
                    </div>
                </div>
            </header>`;
        }}

        function renderMatch(match) {{
            const matchId = match.matchId || match.overlayId || '';
            const matchUrl = match.url || '#';
            const isLive = !!match.isLive;
            
            // --- Common Time Column ---
            let timeColHtml;
            if (isLive) {{
                let displayTime = match.timeLabel || '';
                const isBlinking = !!match.isBlinking;
                if (isBlinking) {{
                    displayTime = displayTime.replace(/'/g, '').trim();
                }}
                const blinkHtml = isBlinking ? `<span class='blink'>'</span>` : '';
                timeColHtml = `<div class='event__stage'><div class='event__stage--block'>${{displayTime}}${{blinkHtml}}</div></div>`;
            }} else {{
                let inner = match.timeLabel || '\u2014';
                let statusClass = '';
                if (match.stageLabel) {{
                    const stageLower = match.stageLabel.toLowerCase();
                    if (stageLower.includes('finalizado') || stageLower.includes('final') || stageLower.includes('terminado')) {{
                         statusClass = 'status-finished';
                    }}
                    inner += `<div class='stage-label ${{statusClass}}'>${{match.stageLabel}}</div>`;
                }}
                timeColHtml = `<div class='event__time'>${{inner}}</div>`;
            }}

            // --- Detect Tennis Mode ---
            const cat = (match.category || '').toUpperCase();
            const title = (match.competitionTitle || '').toUpperCase();
            const url = (match.url || '').toLowerCase();
            
            let isTennis = (!!match.homeFlag || !!match.awayFlag || !!match.homeSets || (match.setScores && match.setScores.length > 0));
            if (!isTennis && (url.includes('/tenis/') || url.includes('/tennis/'))) isTennis = true;
            
            if (!isTennis) {{
                if (cat.includes('ATP') || cat.includes('WTA') || cat.includes('CHALLENGER') || cat.includes('ITF') || 
                    cat.includes('TENIS') || title.includes('TENIS')) {{
                    isTennis = true;
                }}
            }}
            
            const homeName = match.homeTeam || 'Local';
            const awayName = match.awayTeam || 'Visitante';
            const homeHref = match.homeHref || '';
            const awayHref = match.awayHref || '';
            
            if (isTennis) {{
                const homeFlagSrc = match.homeFlag || placeholder;
                const awayFlagSrc = match.awayFlag || placeholder;

                let homeSetsVal = match.homeSets;
                let awaySetsVal = match.awaySets;
                if (!homeSetsVal && !awaySetsVal && (match.homeScore || match.awayScore)) {{
                    homeSetsVal = match.homeScore || '0';
                    awaySetsVal = match.awayScore || '0';
                }}
                homeSetsVal = homeSetsVal || '0';
                awaySetsVal = awaySetsVal || '0';

                const setScores = match.setScores || [];
                const parsedSets = setScores.map(s => {{
                    const parts = s.split(' ');
                    return {{ h: parts[0] || '-', a: parts[1] || '-' }};
                }});

                const homePts = match.homeGamePoints || '';
                const awayPts = match.awayGamePoints || '';
                const hasGamePoints = (homePts !== '' && homePts !== '0') || (awayPts !== '' && awayPts !== '0') || (homePts === '0' && awayPts !== '') || (awayPts === '0' && homePts !== '');

                const homeServiceHtml = match.homeService ? `<span class='service-icon'>⊕</span>` : '';
                const awayServiceHtml = match.awayService ? `<span class='service-icon'>⊕</span>` : '';

                let scoreColumnsHtml = '';
                scoreColumnsHtml += `<div class='tennis-score-col sets-won'>
                    <span class='sc-val bold'>${{homeSetsVal}}</span>
                    <span class='sc-val bold'>${{awaySetsVal}}</span>
                </div>`;

                parsedSets.forEach(s => {{
                    scoreColumnsHtml += `<div class='tennis-score-col set-score'>
                        <span class='sc-val'>${{s.h}}</span>
                        <span class='sc-val'>${{s.a}}</span>
                    </div>`;
                }});

                if (isLive && hasGamePoints) {{
                    scoreColumnsHtml += `<div class='tennis-score-col game-points'>
                        <span class='sc-val bold'>${{homePts || '0'}}</span>
                        <span class='sc-val bold'>${{awayPts || '0'}}</span>
                    </div>`;
                }}

                return `
            <div class='tennis-row' data-id='${{matchId}}' data-url='${{matchUrl}}'>
                <a href='${{matchUrl}}' class='row-link' aria-label='Ver partido'></a>
                ${{timeColHtml}}
                <div class='tennis-body'>
                    <div class='tennis-names'>
                        <div class='tennis-player' data-team-name='${{homeName}}' data-team-href='${{homeHref}}' data-match-url='${{matchUrl}}'>
                            <img class='flag-icon' src='${{homeFlagSrc}}' onerror=""this.onerror=null;this.src='${{placeholder}}'"" alt=''>
                            <span class='team-name'>${{homeName}}</span>
                            ${{homeServiceHtml}}
                        </div>
                        <div class='tennis-player' data-team-name='${{awayName}}' data-team-href='${{awayHref}}' data-match-url='${{matchUrl}}'>
                            <img class='flag-icon' src='${{awayFlagSrc}}' onerror=""this.onerror=null;this.src='${{placeholder}}'"" alt=''>
                            <span class='team-name'>${{awayName}}</span>
                            ${{awayServiceHtml}}
                        </div>
                    </div>
                    <div class='tennis-scores'>
                        ${{scoreColumnsHtml}}
                    </div>
                </div>
            </div>`;
            }}
            
            // --- Fallback Standard Mode ---
            const homeLogoSrc = match.homeLogo || placeholder;
            const awayLogoSrc = match.awayLogo || placeholder;
            const hasScore = !!match.hasScore;
            const scoreClass = isLive ? 'score-val' : (hasScore ? 'score-val finished' : 'score-val preview');

            const homeScoreText = match.homeScore || (hasScore ? '0' : '-');
            const awayScoreText = match.awayScore || (hasScore ? '0' : '-');

            return `
            <div class='match-row' data-id='${{matchId}}' data-url='${{matchUrl}}'>
                <a href='${{matchUrl}}' class='row-link' aria-label='Ver partido'></a>
                ${{timeColHtml}}
                <div class='teams-container'>
                    <div class='team-item' data-team-name='${{homeName}}' data-team-href='${{homeHref}}' data-match-url='${{matchUrl}}'>
                        <img class='team-logo' src='${{homeLogoSrc}}' onerror=""this.onerror=null;this.src='${{placeholder}}'"" alt=''>
                        <span class='team-name'>${{homeName}}</span>
                        ${{match.homeRedCards > 0 ? `<span class='red-card-icon'></span>` : ''}}
                    </div>
                    <div class='team-item' data-team-name='${{awayName}}' data-team-href='${{awayHref}}' data-match-url='${{matchUrl}}'>
                        <img class='team-logo' src='${{awayLogoSrc}}' onerror=""this.onerror=null;this.src='${{placeholder}}'"" alt=''>
                        <span class='team-name'>${{awayName}}</span>
                        ${{match.awayRedCards > 0 ? `<span class='red-card-icon'></span>` : ''}}
                    </div>
                </div>
                <div class='score-col'>
                    <span class='${{scoreClass}}'>${{homeScoreText}}</span>
                    <span class='${{scoreClass}}'>${{awayScoreText}}</span>
                </div>
            </div>`;
        }}

        function render(sportGroups) {{
            if (!sportGroups || sportGroups.length === 0) {{
                container.innerHTML = `<div class='empty-state'>No hay partidos fijados.<br>Agrega partidos desde Flashscore.</div>`;
                return;
            }}

            container.innerHTML = '';

            sportGroups.forEach(sportGroup => {{
                const sportName = sportGroup.sport || 'Otros';
                const groups = sportGroup.groups || [];

                const sportDiv = document.createElement('div');
                sportDiv.innerHTML = renderSportHeader(sportGroup);
                if (sportDiv.firstChild) container.appendChild(sportDiv.firstChild);

                groups.forEach(group => {{
                    const comp = group.competition;
                    if (!comp) return;

                    const compDiv = document.createElement('div');
                    compDiv.className = 'competition-container';
                    compDiv.innerHTML = renderHeader(comp);

                    const wrapper = document.createElement('div');
                    wrapper.className = 'match-row-wrapper';

                    (group.matches || []).forEach(m => {{
                        const d = document.createElement('div');
                        d.innerHTML = renderMatch(m).trim();
                        if (d.firstChild) wrapper.appendChild(d.firstChild);
                    }});

                    compDiv.appendChild(wrapper);
                    container.appendChild(compDiv);
                }});
            }});
            applyHighlights();
        }}

        try {{ render(initialGroups); }} catch(e) {{
            document.body.innerHTML += '<div style=""color:red;padding:10px"">Error: ' + e.message + '</div>';
        }}

        // ========== LIVE UPDATE receiver (data from hidden WebView2) ==========
        window.applyLiveUpdate = function(jsonStr) {{
            try {{
                const data = JSON.parse(jsonStr);
                if (!data.matches) return;
                data.matches.forEach(m => {{
                    const row = container.querySelector('[data-id=""' + m.matchId + '""]');
                    if (!row) return;

                    // Update time/stage
                    const stageEl = row.querySelector('.event__stage');
                    const timeEl = row.querySelector('.event__time');

                    if (m.stage) {{
                        if (stageEl) {{
                            const stageBlock = stageEl.querySelector('.event__stage--block');
                            if (stageBlock) {{
                                let displayTime = m.stage.replace(/'/g, '').trim();
                                const blinkSpan = m.hasBlink ? ""<span class='blink'>&#39;</span>"" : '';
                                stageBlock.innerHTML = displayTime + blinkSpan;
                            }}
                        }} else if (timeEl) {{
                            const newDiv = document.createElement('div');
                            newDiv.className = 'event__stage';
                            let displayTime = m.stage.replace(/'/g, '').trim();
                            const blinkSpan = m.hasBlink ? ""<span class='blink'>&#39;</span>"" : '';
                            newDiv.innerHTML = ""<div class='event__stage--block'>"" + displayTime + blinkSpan + '</div>';
                            timeEl.replaceWith(newDiv);
                        }}
                    }} else if (m.time && timeEl) {{
                        timeEl.textContent = m.time;
                    }}

                    // Update scores + detect goals
                    const scoreVals = row.querySelectorAll('.score-val');
                    if (scoreVals.length >= 2) {{
                        const oldH = parseInt((scoreVals[0].textContent || '').trim(), 10);
                        const oldA = parseInt((scoreVals[1].textContent || '').trim(), 10);
                        const newH = parseInt(m.homeScore, 10);
                        const newA = parseInt(m.awayScore, 10);

                        if (!isNaN(oldH) && !isNaN(newH) && newH > oldH) {{
                            send('goalEvent', {{ MatchId: m.matchId, Name: '', Href: m.stage || m.time, Sport: 'home' }});
                        }}
                        if (!isNaN(oldA) && !isNaN(newA) && newA > oldA) {{
                            send('goalEvent', {{ MatchId: m.matchId, Name: '', Href: m.stage || m.time, Sport: 'away' }});
                        }}

                        if (m.homeScore) scoreVals[0].textContent = m.homeScore;
                        if (m.awayScore) scoreVals[1].textContent = m.awayScore;
                    }}

                    // Update red cards
                    const teams = row.querySelectorAll('.team-item');
                    if (teams.length >= 2) {{
                        updateRedCards(teams[0], m.homeRedCards || 0);
                        updateRedCards(teams[1], m.awayRedCards || 0);
                    }}
                }});
            }} catch(err) {{ console.error('[applyLiveUpdate]', err); }}
        }};

        function updateRedCards(teamItem, count) {{
            const existing = teamItem.querySelector('.red-card-icon');
            if (count > 0 && !existing) {{
                const span = document.createElement('span');
                span.className = 'red-card-icon';
                teamItem.appendChild(span);
            }} else if (count === 0 && existing) {{
                existing.remove();
            }}
        }}

        // ========== GOL BADGE ==========
        window.showGoalBadge = function(matchId, teamSide, playerName, goalMinute) {{
            try {{
                const row = container.querySelector('[data-id=""' + matchId + '""]');
                if (!row) return;
                const teams = row.querySelectorAll('.team-item');
                if (teams.length < 2) return;
                const teamEl = teamSide === 'away' ? teams[1] : teams[0];

                // Remove existing GOL badge/highlights if any
                const existing = teamEl.querySelector('.gol-badge');
                if (existing) existing.remove();
                row.classList.remove('gol-active');
                teams.forEach(t => t.classList.remove('gol-team-highlight'));

                // Add GOL badge
                const badge = document.createElement('span');
                badge.className = 'gol-badge';
                badge.textContent = 'GOL';
                if (playerName) badge.title = playerName;
                teamEl.appendChild(badge);

                // Add red highlights: team name + entire row
                teamEl.classList.add('gol-team-highlight');
                row.classList.add('gol-active');

                // Auto-remove after 60 seconds (1 minute real time)
                setTimeout(() => {{
                    if (badge.parentNode) badge.remove();
                    teamEl.classList.remove('gol-team-highlight');
                    row.classList.remove('gol-active');
                }}, 60000);
            }} catch(err) {{ console.error('[showGoalBadge]', err); }}
        }};
    </script>
</body>
</html>";
        }

        protected override void OnClosed(EventArgs e)
        {
            // Close all per-match detail windows
            foreach (var kvp in _matchDetailWindows)
            {
                try { kvp.Value.Close(); } catch { }
            }
            _matchDetailWindows.Clear();

            // Close all active goal notifications
            foreach (var notif in _activeNotifications.ToList())
            {
                try { notif.Close(); } catch { }
            }
            _activeNotifications.Clear();

            base.OnClosed(e);
        }

        private class WebMessage
        {
            public string? Type { get; set; }
            public string? Href { get; set; }
            public string? MatchId { get; set; }
            public string? TabId { get; set; }
            public string? Name { get; set; }
            public string? MatchUrl { get; set; }
            public List<string>? MatchIds { get; set; }
            public string? Sport { get; set; }
            public bool? Highlighted { get; set; }
        }

        public class SportGroup
        {
            public string Sport { get; set; } = "Otros";
            public List<MatchGroup> Groups { get; set; } = new();
        }

        public class MatchGroup
        {
            public CompetitionData? Competition { get; set; }
            public List<OverlayDisplayData> Matches { get; set; } = new();
            public string? TabId { get; set; }
        }

        public class OverlayDisplayData
        {
            public string? MatchId { get; set; }
            public string? OverlayId { get; set; }
            public string? TimeLabel { get; set; }
            public string? StageLabel { get; set; }
            public string? HomeTeam { get; set; }
            public string? AwayTeam { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public string? HomeHref { get; set; }
            public string? AwayHref { get; set; }
            public int HomeRedCards { get; set; }
            public int AwayRedCards { get; set; }
            public string? MatchHref { get; set; }
            public string? HomeScore { get; set; }
            public string? AwayScore { get; set; }
            public bool HasScore { get; set; }
            public bool IsLive { get; set; }
            public bool IsBlinking { get; set; }
            public string? Url { get; set; }
            public string? Category { get; set; } 
            public string? CompetitionTitle { get; set; }
            
            // Tennis specific
            public string? HomeFlag { get; set; }
            public string? AwayFlag { get; set; }
            public string? HomeSets { get; set; }
            public string? AwaySets { get; set; }
            public string? HomeGamePoints { get; set; }
            public string? AwayGamePoints { get; set; }
            public bool HomeService { get; set; }
            public bool AwayService { get; set; }
            public List<string>? SetScores { get; set; }
        }

        private string? EnsureAbsoluteUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("//")) return "https:" + url;
            if (url.StartsWith("/")) return "https://www.flashscore.es" + url;
            return url;
        }
    }
}