using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
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
        private const double EmptyStateHeight = 80;

        // Key: MatchId (or OverlayId)
        private readonly Dictionary<string, MatchData> _matches = new();
        private readonly Dictionary<string, CompetitionData> _competitions = new();

        private CancellationTokenSource? pollingCts;
        private bool _webViewReady;

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

        private void AdjustWindowHeight(int groupCount, int totalMatchCount)
        {
            double contentHeight;
            if (groupCount == 0)
            {
                contentHeight = EmptyStateHeight + PaddingTotal;
            }
            else
            {
                contentHeight = PaddingTotal
                    + (groupCount * (HeaderHeight + GroupMargin))
                    + (totalMatchCount * MatchRowHeight);
            }

            var idealHeight = TitleBarHeight + BorderExtra + contentHeight;

            var screenHeight = SystemParameters.WorkArea.Height;
            var maxAllowed = screenHeight * 0.85;

            Height = idealHeight > maxAllowed ? maxAllowed : idealHeight;
        }

        private void RenderOverlay()
        {
            if (!_webViewReady || MatchWebView.CoreWebView2 == null) return;

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

            var totalMatches = groups.Sum(g => g.Matches.Count);
            AdjustWindowHeight(groups.Count, totalMatches);

            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            var payload = JsonConvert.SerializeObject(groups, settings);
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

            // Enable scroll when more than 1 match total
            var enableScroll = totalMatches > 1;

            var html = BuildOverlayHtml(payloadBase64, enableScroll);
            MatchWebView.NavigateToString(html);

            MatchTitle.Text = $"Flashscore ({_matches.Count})";
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
                // Initialize with existing data to prevent overwriting with nulls if parsing fails
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
                
                // Flag to track if we found new set scores to replace old ones
                bool foundNewSets = false;

                // 1. Logos & Flags
                // Try standard participant image selectors (Tennis uses participant__image)
                var homeImg = document.QuerySelector(".participant__home .participant__image img") 
                           ?? document.QuerySelector(".duelParticipant__home .participant__image img");
                var awayImg = document.QuerySelector(".participant__away .participant__image img")
                           ?? document.QuerySelector(".duelParticipant__away .participant__image img");

                if (homeImg != null) homeLogo = homeImg.GetAttribute("src");
                if (awayImg != null) awayLogo = awayImg.GetAttribute("src");

                // Try country flags
                // Selector 1: .duelParticipant__home .participant__image--country (img)
                // Selector 2: .duelParticipant__home .participant__country (often div with bg image?)
                // Selector 3: check inside participant__participantNameWrapper for a flag
                
                var homeFlagImg = document.QuerySelector(".duelParticipant__home .participant__image--country") 
                               ?? document.QuerySelector(".duelParticipant__home .participant__country img");
                var awayFlagImg = document.QuerySelector(".duelParticipant__away .participant__image--country")
                               ?? document.QuerySelector(".duelParticipant__away .participant__country img");

                if (homeFlagImg != null) homeFlag = EnsureAbsoluteUrl(homeFlagImg.GetAttribute("src"));
                if (awayFlagImg != null) awayFlag = EnsureAbsoluteUrl(awayFlagImg.GetAttribute("src"));

                // Fallback: Try to parse "fl_XXX" class from spans (User reported structure)
                // <span class="... flag fl_200" title="...">
                if (homeFlag == null)
                {
                    var homeFlagSpan = document.QuerySelector(".duelParticipant__home .flag");
                    if (homeFlagSpan != null)
                    {
                         var classes = homeFlagSpan.ClassName;
                         var matchFlag = System.Text.RegularExpressions.Regex.Match(classes, @"fl_(\d+)");
                         if (matchFlag.Success)
                         {
                             // Construct URL. Typical Flashscore structure:
                             // https://static.flashscore.com/res/image/data/flags/24x18/{id}.png
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

                        // Service indicator
                        homeService = homeRow.QuerySelector(".detailScore__serving") != null;
                        awayService = awayRow.QuerySelector(".detailScore__serving") != null;
                        
                        // Game Points
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
                                
                                // Check if game points: 0, 15, 30, 40, Ad
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
                
                // If detailed parsing failed but it's clearly tennis (e.g. from competition name which we don't have here easily without map, 
                // but we can check if we found sets in the main event__score--home if detail was missing)
                
                // If we didn't find specific sets but found a score in the main element, use that as sets for tennis if we can infer tennis
                // But RefreshSingleMatchAsync returns MatchData, the rendering decides.
                // We should pass "HomeScore" as "Sets" fallback in UI if tennis is detected via other means.

                if (el == null) 
                {
                    // If match element not found in list (common in detail page parsing), return partial data with images
                     return new MatchData
                    {
                        MatchId = match.MatchId,
                        OverlayId = match.OverlayId,
                        HomeLogo = homeLogo,
                        AwayLogo = awayLogo,
                        HomeFlag = homeFlag,
                        AwayFlag = awayFlag, 
                        Html = html,
                        
                        // New fields
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
                    
                     // New fields (if found in detail parsing)
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

            // Blinking only when actively playing (minute contains ')
            var shouldBlink = (md.Time?.Contains("'") == true || md.Stage?.Contains("'") == true);

            // Handle "Descanso" state: show as red label but no blinking
            var isDescanso = stage.ToLowerInvariant().Contains("descanso");
            var displayTimeLabel = isDescanso
                ? "Descanso"
                : (string.IsNullOrWhiteSpace(timeLabel) ? "\u2014" : timeLabel);
            var displayStageLabel = isDescanso ? string.Empty : stage;

            // For tennis, use the full stage as the time label (e.g. "2º Set" instead of "2")
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
                Category = null, // Will be filled in RenderOverlay
                CompetitionTitle = null, 
                
                // Tennis
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

            // Explicit NOT live states
            if (stage.Contains("preview") || stage.Contains("finalizado") || stage.Contains("final") || stage.Contains("terminado")) return false;
            if (stage.Contains("penaltis") || stage.Contains("postergado") || stage.Contains("aplazado") || stage.Contains("cancelado")) return false;

            // Explicit LIVE states (Descanso uses red style too as requested)
            if (stage.Contains("descanso") || stage.Contains("en directo") || stage.Contains("en curso") || stage.Contains("juego") || stage.Contains("gol")) return true;
            if (stage.Contains("parte") || stage.Contains("tiempo") || stage.Contains("prórroga") || stage.Contains("extra")) return true;
            if (stage.Contains("set")) return true; // Tennis: "1º Set", "2º Set", etc.
            if (stage.Contains("'") || time.Contains("'")) return true;

            // Heuristic for time: if starts with digit and NO colon/dot (distinguish from 15:00 or 15.05.)
            if (time.Length > 0 && char.IsDigit(time[0]) && !time.Contains(":") && !time.Contains(".")) return true;

            return false;
        }

        private string BuildOverlayHtml(string initialGroupsBase64, bool enableScroll)
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
            overflow: hidden;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
            background: var(--header-bg);
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
            transition: background 0.12s ease-out;
            min-height: 48px;
            cursor: pointer;
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
            font-size: 12px;
            color: var(--text-time);
            min-width: 46px;
            font-weight: 500;
            letter-spacing: 0.02em;
            text-align: center;
            flex-shrink: 0;
        }}

        .event__stage {{
            color: var(--color-primary);
            font-weight: 700;
            font-size: 12px;
            min-width: 46px;
            text-align: center;
            flex-shrink: 0;
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
            margin-left: 10px;
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
            width: 16px;
            height: 12px;
            object-fit: cover;
            flex-shrink: 0;
            border-radius: 1px;
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
            transition: background 0.12s ease-out;
            min-height: 52px;
            cursor: pointer;
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

        function send(type, data) {{
            if (window.chrome?.webview) window.chrome.webview.postMessage(JSON.stringify({{ type, ...data }}));
        }}

        // Single-click: prevent navigation on links
        document.addEventListener('click', (e) => {{

            const link = e.target.closest('a');
            if (link && link.href && !link.href.endsWith('#')) {{
                e.preventDefault();
                // Do NOT navigate on single click
            }}
        }});

        // Double-click: navigate
        document.addEventListener('dblclick', (e) => {{
            // Check if a team-item or tennis-player was clicked
            const teamItem = e.target.closest('.team-item, .tennis-player');
            if (teamItem) {{
                e.preventDefault();
                e.stopPropagation();
                const teamName = teamItem.getAttribute('data-team-name') || '';
                const teamHref = teamItem.getAttribute('data-team-href') || '';
                const matchUrl = teamItem.getAttribute('data-match-url') || '';
                if (teamHref) {{
                    // Navigate directly to team href via fc_open_team
                    send('openTeam', {{ Name: teamName, Href: teamHref, MatchUrl: matchUrl }});
                }} else if (teamName) {{
                    // Fallback: search by name
                    send('openTeam', {{ Name: teamName, MatchUrl: matchUrl }});
                }}
                return;
            }}

            const link = e.target.closest('a');
            if (link && link.href && !link.href.endsWith('#')) {{
                e.preventDefault();
                e.stopPropagation();
                send('navigate', {{ href: link.href }});
            }}
        }});

        // Right-click on match rows → remove immediately
        document.addEventListener('contextmenu', (e) => {{
            const row = e.target.closest('.match-row, .tennis-row');
            if (row) {{
                e.preventDefault();
                const matchId = row.getAttribute('data-id') || '';
                if (matchId) send('remove', {{ MatchId: matchId }});
            }}
        }});

        function renderHeader(comp) {{
            const url = comp.hrefWithParam || comp.href || '#';
            const title = comp.title || 'Competición';
            const category = comp.category || '';

            // Build inline: CATEGORY: TITLE
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
        
        function safeStr(s) {{ return s || ''; }}

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
            // Heuristic updates: check category or title
            const cat = (match.category || '').toUpperCase();
            const title = (match.competitionTitle || '').toUpperCase();
            const url = (match.url || '').toLowerCase();
            
            let isTennis = (!!match.homeFlag || !!match.awayFlag || !!match.homeSets || (match.setScores && match.setScores.length > 0));
            if (!isTennis && (url.includes('/tenis/') || url.includes('/tennis/'))) isTennis = true;
            
            // Console debug
            console.log('Match Debug:', match.matchId, 'Flags:', match.homeFlag, match.awayFlag, 'Sets:', match.setScores, 'IsTennis:', isTennis, 'Cat:', cat);
            
            if (!isTennis) {{
                // Fallback detection by name
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

                // Sets won (total)
                let homeSetsVal = match.homeSets;
                let awaySetsVal = match.awaySets;
                if (!homeSetsVal && !awaySetsVal && (match.homeScore || match.awayScore)) {{
                    homeSetsVal = match.homeScore || '0';
                    awaySetsVal = match.awayScore || '0';
                }}
                homeSetsVal = homeSetsVal || '0';
                awaySetsVal = awaySetsVal || '0';

                // Individual set scores
                const setScores = match.setScores || [];
                const parsedSets = setScores.map(s => {{
                    const parts = s.split(' ');
                    return {{ h: parts[0] || '-', a: parts[1] || '-' }};
                }});

                // Game points (only use real values, not empty strings)
                const homePts = match.homeGamePoints || '';
                const awayPts = match.awayGamePoints || '';
                const hasGamePoints = (homePts !== '' && homePts !== '0') || (awayPts !== '' && awayPts !== '0') || (homePts === '0' && awayPts !== '') || (awayPts === '0' && homePts !== '');

                // Debug tennis data
                console.log('Tennis Data:', match.matchId, 'Sets:', homeSetsVal, awaySetsVal, 'SetScores:', setScores, 'GamePts:', homePts, awayPts, 'Service:', match.homeService, match.awayService, 'HasPts:', hasGamePoints);

                // Service indicator HTML (small circle, like Flashscore)
                const homeServiceHtml = match.homeService ? `<span class='service-icon'>⊕</span>` : '';
                const awayServiceHtml = match.awayService ? `<span class='service-icon'>⊕</span>` : '';

                // Build score columns: [Sets Won] [Set1] [Set2] ... [Points]
                let scoreColumnsHtml = '';

                // 1. Sets won column
                scoreColumnsHtml += `<div class='tennis-score-col sets-won'>
                    <span class='sc-val bold'>${{homeSetsVal}}</span>
                    <span class='sc-val bold'>${{awaySetsVal}}</span>
                </div>`;

                // 2. Individual set score columns
                parsedSets.forEach(s => {{
                    scoreColumnsHtml += `<div class='tennis-score-col set-score'>
                        <span class='sc-val'>${{s.h}}</span>
                        <span class='sc-val'>${{s.a}}</span>
                    </div>`;
                }});

                // 3. Game points column (only if live AND we have real data)
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

        function render(groups) {{
            if (!groups || groups.length === 0) {{
                container.innerHTML = `<div class='empty-state'>No hay partidos fijados.<br>Agrega partidos desde Flashscore.</div>`;
                return;
            }}

            container.innerHTML = '';

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