using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FlashscoreOverlay
{
    public partial class OverlayWindow : Window
    {
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

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

        private CancellationTokenSource? pollingCts;
        private bool _webViewReady;
        private bool _scrollEnabled;

        public event Action<string>? MatchRemoved;

        public OverlayWindow()
        {
            InitializeComponent();
            Topmost = true;
            ShowInTaskbar = false;
            Title = "Flashscore Overlay";

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
                await MatchWebView.EnsureCoreWebView2Async();
                MatchWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                MatchWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                MatchWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                MatchWebView.NavigationCompleted += MatchWebView_NavigationCompleted;
                _webViewReady = true;

                RenderOverlay();
                StartPollingMatches();
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
                }
            }
            catch { }
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

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
        }

        private void PruneHighlightedMatches()
        {
            var toRemove = _highlightedMatches.Where(id => !_matches.ContainsKey(id)).ToList();
            foreach (var id in toRemove)
            {
                _highlightedMatches.Remove(id);
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

        private void StartPollingMatches()
        {
            pollingCts?.Cancel();
            pollingCts?.Dispose();
            pollingCts = new CancellationTokenSource();
            _ = Task.Run(async () => await PollLoopAsync(pollingCts.Token));
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), token);
                    var currentMatches = _matches.Values.ToList();
                    if (currentMatches.Count == 0) continue;

                    var results = await Task.WhenAll(currentMatches.Select(m => RefreshSingleMatchAsync(m, token)));

                    bool anyUpdated = false;
                    foreach (var res in results)
                    {
                        if (res != null && _matches.TryGetValue(res.OverlayId ?? res.MatchId!, out var existing))
                        {
                            if (!string.IsNullOrWhiteSpace(res.Time)) existing.Time = res.Time;
                            if (!string.IsNullOrWhiteSpace(res.Stage)) existing.Stage = res.Stage;
                            if (!string.IsNullOrWhiteSpace(res.HomeScore)) existing.HomeScore = res.HomeScore;
                            if (!string.IsNullOrWhiteSpace(res.AwayScore)) existing.AwayScore = res.AwayScore;
                            
                            // Update logos if we found them
                            if (!string.IsNullOrWhiteSpace(res.HomeLogo)) existing.HomeLogo = res.HomeLogo;
                            if (!string.IsNullOrWhiteSpace(res.AwayLogo)) existing.AwayLogo = res.AwayLogo;

                            // Update Tennis specific fields
                            if (!string.IsNullOrWhiteSpace(res.HomeFlag)) existing.HomeFlag = res.HomeFlag;
                            if (!string.IsNullOrWhiteSpace(res.AwayFlag)) existing.AwayFlag = res.AwayFlag;
                            if (res.HomeSets != null) existing.HomeSets = res.HomeSets;
                            if (res.AwaySets != null) existing.AwaySets = res.AwaySets;
                            if (!string.IsNullOrEmpty(res.HomeGamePoints)) existing.HomeGamePoints = res.HomeGamePoints;
                            if (!string.IsNullOrEmpty(res.AwayGamePoints)) existing.AwayGamePoints = res.AwayGamePoints;
                            existing.HomeService = res.HomeService;
                            existing.AwayService = res.AwayService;
                            if (res.SetScores != null && res.SetScores.Count > 0) existing.SetScores = res.SetScores;

                            existing.Html = res.Html;
                            anyUpdated = true;
                        }
                    }

                    if (anyUpdated) await Dispatcher.InvokeAsync(RenderOverlay);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"Polling loop error: {ex}"); }
            }
        }

        private async Task<MatchData?> RefreshSingleMatchAsync(MatchData match, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(match.Url)) return null;
            try
            {
                var response = await HttpClient.GetAsync(match.Url, token);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(token);
                var parser = new HtmlParser();
                var document = await parser.ParseDocumentAsync(html, token);
                var el = FindMatchElement(match, document);
                
                // --- Tennis Data Extraction ---
                string? homeLogo = match.HomeLogo;
                string? awayLogo = match.AwayLogo;
                string? homeFlag = match.HomeFlag; 
                string? awayFlag = match.AwayFlag;
                string? homeSets = match.HomeSets;
                string? awaySets = match.AwaySets;
                string? homeGamePoints = match.HomeGamePoints;
                string? awayGamePoints = match.AwayGamePoints;
                bool homeService = match.HomeService;
                bool awayService = match.AwayService;
                var setScores = match.SetScores != null ? new List<string>(match.SetScores) : new List<string>();

                // 1. Logos & Flags
                var homeImg = document.QuerySelector(".participant__home .participant__image img") 
                           ?? document.QuerySelector(".duelParticipant__home .participant__image img");
                var awayImg = document.QuerySelector(".participant__away .participant__image img")
                           ?? document.QuerySelector(".duelParticipant__away .participant__image img");

                if (homeImg != null) homeLogo = homeImg.GetAttribute("src");
                if (awayImg != null) awayLogo = awayImg.GetAttribute("src");

                var homeFlagImg = document.QuerySelector(".duelParticipant__home .participant__image--country") 
                               ?? document.QuerySelector(".duelParticipant__home .participant__country img");
                var awayFlagImg = document.QuerySelector(".duelParticipant__away .participant__image--country")
                               ?? document.QuerySelector(".duelParticipant__away .participant__country img");

                if (homeFlagImg != null) homeFlag = EnsureAbsoluteUrl(homeFlagImg.GetAttribute("src"));
                if (awayFlagImg != null) awayFlag = EnsureAbsoluteUrl(awayFlagImg.GetAttribute("src"));

                if (homeFlag == null)
                {
                    var homeFlagSpan = document.QuerySelector(".duelParticipant__home .flag");
                    if (homeFlagSpan != null)
                    {
                         var classes = homeFlagSpan.ClassName;
                         var matchFlag = System.Text.RegularExpressions.Regex.Match(classes, @"fl_(\d+)");
                         if (matchFlag.Success)
                         {
                             homeFlag = $"https://static.flashscore.com/res/image/data/flags/24x18/{matchFlag.Groups[1].Value}.png";
                         }
                    }
                }
                
                if (awayFlag == null)
                {
                    var awayFlagSpan = document.QuerySelector(".duelParticipant__away .flag");
                    if (awayFlagSpan != null)
                    {
                         var classes = awayFlagSpan.ClassName;
                         var matchFlag = System.Text.RegularExpressions.Regex.Match(classes, @"fl_(\d+)");
                         if (matchFlag.Success)
                         {
                             awayFlag = $"https://static.flashscore.com/res/image/data/flags/24x18/{matchFlag.Groups[1].Value}.png";
                         }
                    }
                }

                // 2. Scores (Sets & Points) in Detail Page
                var detailScore = document.QuerySelector(".detailScore__matchInfo");
                if (detailScore != null)
                {
                    var rows = detailScore.QuerySelectorAll(".detailScore__row");
                    if (rows.Length >= 2)
                    {
                        var homeRow = rows[0];
                        var awayRow = rows[1];

                        homeSets = homeRow.QuerySelector(".detailScore__score")?.TextContent;
                        awaySets = awayRow.QuerySelector(".detailScore__score")?.TextContent;

                        homeService = homeRow.QuerySelector(".detailScore__serving") != null;
                        awayService = awayRow.QuerySelector(".detailScore__serving") != null;
                        
                        var homeParts = homeRow.QuerySelectorAll(".detailScore__part");
                        var awayParts = awayRow.QuerySelectorAll(".detailScore__part");

                        int count = Math.Max(homeParts.Length, awayParts.Length);
                        var tempSetScores = new List<string>();

                        for(int i=0; i<count; i++)
                        {
                            var h = i < homeParts.Length ? homeParts[i].TextContent : "-";
                            var a = i < awayParts.Length ? awayParts[i].TextContent : "-";
                            
                            if (!string.IsNullOrWhiteSpace(h) || !string.IsNullOrWhiteSpace(a))
                            {
                                h = h?.Trim() ?? "-";
                                a = a?.Trim() ?? "-";
                                
                                bool isPoints = (h == "0" || h=="15" || h=="30" || h=="40" || h=="Ad" || a=="0" || a=="15" || a=="30" || a=="40" || a=="Ad");
                                
                                if (isPoints && i == count - 1) 
                                {
                                    homeGamePoints = h;
                                    awayGamePoints = a;
                                }
                                else
                                {
                                     tempSetScores.Add($"{h} {a}");
                                }
                            }
                        }
                        
                        if (tempSetScores.Count > 0)
                        {
                             setScores = tempSetScores;
                        }
                    }
                }

                if (el == null) 
                {
                     return new MatchData
                    {
                        MatchId = match.MatchId,
                        OverlayId = match.OverlayId,
                        HomeLogo = homeLogo,
                        AwayLogo = awayLogo,
                        HomeFlag = homeFlag,
                        AwayFlag = awayFlag, 
                        Html = html,
                        
                        HomeSets = homeSets,
                        AwaySets = awaySets,
                        HomeGamePoints = homeGamePoints,
                        AwayGamePoints = awayGamePoints,
                        HomeService = homeService,
                        AwayService = awayService,
                        SetScores = setScores
                    };
                }

                return new MatchData
                {
                    MatchId = match.MatchId,
                    OverlayId = match.OverlayId,
                    Time = el.QuerySelector(".event__time")?.TextContent,
                    Stage = el.QuerySelector(".event__stage")?.TextContent,
                    HomeScore = el.QuerySelector(".event__score--home")?.TextContent,
                    AwayScore = el.QuerySelector(".event__score--away")?.TextContent,
                    HomeLogo = homeLogo,
                    AwayLogo = awayLogo,
                    Html = html,
                    
                    HomeFlag = homeFlag,
                    AwayFlag = awayFlag,
                    HomeSets = homeSets,
                    AwaySets = awaySets,
                    HomeGamePoints = homeGamePoints,
                    AwayGamePoints = awayGamePoints,
                    HomeService = homeService,
                    AwayService = awayService,
                    SetScores = setScores
                };
            }
            catch { return null; }
        }

        private IElement? FindMatchElement(MatchData m, IDocument document)
        {
            if (!string.IsNullOrWhiteSpace(m.OverlayId))
            {
                var byId = document.QuerySelector($"#{m.OverlayId}");
                if (byId != null) return byId;
            }
            if (!string.IsNullOrWhiteSpace(m.MatchMid))
            {
                var byMid = document.QuerySelector($"[data-mid=\"{m.MatchMid}\"]");
                if (byMid != null) return byMid;
            }
            if (!string.IsNullOrWhiteSpace(m.MatchId))
            {
                var byMatch = document.QuerySelector($"[data-event-id=\"{m.MatchId}\"]");
                if (byMatch != null) return byMatch;
            }
            return document.QuerySelector(".event__match");
        }

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

        .red-card-icon {{
            display: inline-block;
            width: 8px;
            height: 10px;
            background-color: #d20000;
            margin-left: 4px;
            border-radius: 1px;
            vertical-align: middle;
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

            let headerContent = '';
            if (category) {{
                headerContent = `<span class='headerLeague__category'>${{category}}</span><span class='headerLeague__separator'>:</span><a href='${{url}}' class='headerLeague__title'>${{title}}</a>`;
            }} else {{
                headerContent = `<a href='${{url}}' class='headerLeague__title'>${{title}}</a>`;
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
                if (match.stageLabel) {{
                    inner += `<div class='stage-label'>${{match.stageLabel}}</div>`;
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
    </script>
</body>
</html>";
        }

        protected override void OnClosed(EventArgs e)
        {
            pollingCts?.Cancel();
            pollingCts?.Dispose();
            pollingCts = null;
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