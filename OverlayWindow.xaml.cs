using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        
        // Key: MatchId (or OverlayId)
        private readonly Dictionary<string, MatchData> _matches = new();
        private readonly Dictionary<string, CompetitionData> _competitions = new();
        
        private CancellationTokenSource? pollingCts;

        // Action to notify MainWindow to remove a match (and uncheck on web)
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
            // Sync current IDs
            var currentIds = new HashSet<string>();
            
            foreach (var comp in competitions)
            {
                if (comp.CompetitionId != null)
                {
                    _competitions[comp.CompetitionId] = comp;
                }
            }

            foreach (var m in matches)
            {
                var id = m.OverlayId ?? m.MatchId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                
                currentIds.Add(id);
                _matches[id] = m;
            }

            // Remove matches that are no longer present
            var toRemove = _matches.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var k in toRemove) _matches.Remove(k);

            // Re-render
            RenderOverlay();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await MatchWebView.EnsureCoreWebView2Async();

                MatchWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true; // Enabled for debugging
                MatchWebView.CoreWebView2.Settings.AreDevToolsEnabled = true; // Enabled for debugging
                MatchWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Initial empty render
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
                            // Try to navigate in the specific tab if provided
                            if (!string.IsNullOrWhiteSpace(msg.TabId))
                            {
                                var main = Application.Current.MainWindow as MainWindow;
                                main?.SetPendingCommandForTab(msg.TabId, new BrowserCommand { Action = "navigate", Href = msg.Href });
                            }
                            else 
                            {
                                // Fallback: open externally
                                try { Process.Start(new ProcessStartInfo(msg.Href) { UseShellExecute = true }); } catch { }
                            }
                        }
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

        // Event Handlers
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
             // Already initialized in Constructor/InitializeAsync
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            RenderOverlay();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RenderOverlay()
        {
            if (MatchWebView.CoreWebView2 == null) return;

            // Group matches by CompetitionId (or key)
            var groups = _matches.Values
                .GroupBy(m => GetCompetitionId(m))
                .Select(g => 
                {
                    // Resolve competition data
                    CompetitionData? comp = null;
                    if (_competitions.TryGetValue(g.Key, out var c))
                    {
                        comp = c;
                    }
                    else
                    {
                        // Fallback logic for name
                        var displayTitle = g.Key == "default" ? "Otros" : g.Key;
                        // Try to parse Category-Title from ID
                        if (g.Key.Contains("-"))
                        {
                             var parts = g.Key.Split(new[] { '-' }, 2);
                             if (parts.Length == 2)
                             {
                                 comp = new CompetitionData { Category = parts[0], Title = parts[1], CompetitionId = g.Key };
                             }
                        }
                        
                        if (comp == null)
                        {
                            comp = new CompetitionData { Title = displayTitle, CompetitionId = g.Key };
                        }
                    }

                    var sampleMatch = g.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.TabId));
                    var tabId = sampleMatch?.TabId;
                    
                    return new MatchGroup 
                    {
                        Competition = comp,
                        Matches = g.Select(m => CreateDisplayData(m, m.Time, m.Stage, m.HomeScore, m.AwayScore)).ToList(),
                        TabId = tabId
                    };
                })
                .OrderBy(g => g.Competition?.Title)
                .ToList();

            var settings = new JsonSerializerSettings 
            { 
                ContractResolver = new CamelCasePropertyNamesContractResolver() 
            };
            var payload = JsonConvert.SerializeObject(groups, settings);
            var html = BuildOverlayHtml(payload);
            MatchWebView.NavigateToString(html); 
            
            MatchTitle.Text = $"Flashscore ({_matches.Count})";
        }

        private string GetCompetitionId(MatchData m)
        {
            return m.CompetitionId ?? m.Stage ?? "default";
        }

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

                    var tasks = currentMatches.Select(m => RefreshSingleMatchAsync(m, token));
                    var results = await Task.WhenAll(tasks);
                    
                    bool anyUpdated = false;
                    foreach (var res in results)
                    {
                        if (res != null)
                        {
                            if (_matches.TryGetValue(res.OverlayId ?? res.MatchId!, out var existing))
                            {
                                existing.Time = res.Time;
                                existing.Stage = res.Stage;
                                existing.HomeScore = res.HomeScore;
                                existing.AwayScore = res.AwayScore;
                                existing.Html = res.Html; 
                                anyUpdated = true;
                            }
                        }
                    }

                    if (anyUpdated)
                    {
                        await Dispatcher.InvokeAsync(RenderOverlay);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Polling loop error: {ex}");
                }
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
                var matchElement = FindMatchElement(match, document);
                if (matchElement == null) return null;

                var timeText = matchElement.QuerySelector(".event__time")?.TextContent;
                var stageText = matchElement.QuerySelector(".event__stage")?.TextContent;
                var homeScore = matchElement.QuerySelector(".event__score--home")?.TextContent;
                var awayScore = matchElement.QuerySelector(".event__score--away")?.TextContent;

                return new MatchData
                {
                    MatchId = match.MatchId,
                    OverlayId = match.OverlayId,
                    Time = timeText,
                    Stage = stageText,
                    HomeScore = homeScore,
                    AwayScore = awayScore,
                    Html = html 
                };
            }
            catch 
            {
                return null;
            }
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
        
        // Helper Methods for Display Data
        private OverlayDisplayData CreateDisplayData(MatchData matchData, string? timeSource, string? stageSource, string? homeScoreSource, string? awayScoreSource)
        {
             var (timeLabel, trailingText) = SplitTimeSegment(timeSource ?? matchData.Time);
            var stageLabel = (stageSource ?? matchData.Stage)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stageLabel))
            {
                stageLabel = trailingText;
            }

            var hasScore = !string.IsNullOrWhiteSpace(homeScoreSource) || !string.IsNullOrWhiteSpace(awayScoreSource);
            var isLive = DetermineLiveState(timeSource ?? matchData.Time, stageSource ?? matchData.Stage);

            return new OverlayDisplayData
            {
                MatchId = matchData.MatchId,
                OverlayId = matchData.OverlayId,
                TimeLabel = string.IsNullOrWhiteSpace(timeLabel) ? "—" : timeLabel,
                StageLabel = stageLabel,
                HomeTeam = matchData.HomeTeam ?? "Local",
                AwayTeam = matchData.AwayTeam ?? "Visitante",
                HomeLogo = matchData.HomeLogo ?? string.Empty,
                AwayLogo = matchData.AwayLogo ?? string.Empty,
                HomeScore = string.IsNullOrWhiteSpace(homeScoreSource) ? string.Empty : homeScoreSource.Trim(),
                AwayScore = string.IsNullOrWhiteSpace(awayScoreSource) ? string.Empty : awayScoreSource.Trim(),
                HasScore = hasScore,
                IsLive = isLive,
                HtmlSource = matchData.HtmlSource,
                Url = matchData.Url
            };
        }

        private static (string time, string trailing) SplitTimeSegment(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, string.Empty);
            var trimmed = raw.Trim();
            var firstLetter = -1;
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (char.IsLetter(trimmed[i]))
                {
                    firstLetter = i;
                    break;
                }
            }
            if (firstLetter <= 0) return (trimmed, string.Empty);
            return (trimmed.Substring(0, firstLetter).Trim(), trimmed.Substring(firstLetter).Trim());
        }

        private static bool DetermineLiveState(string? timeSource, string? stageSource)
        {
            var stage = (stageSource ?? string.Empty).Trim().ToLowerInvariant();
            var time = (timeSource ?? string.Empty).Trim().ToLowerInvariant();
            if (stage.Contains("preview") || stage.Contains("finalizado") || stage.Contains("final") || stage.Contains("terminado") || stage.Contains("descansado")) return false;
            
            if (stage.Contains("descanso") || stage.Contains("en directo") || stage.Contains("en curso")) return true;
            if (stage.Contains("'") || time.Contains("'")) return true;
            return false;
        }

        private string BuildOverlayHtml(string initialGroupsJson)
        {
            // Note: Single quotes are used for HTML attributes to avoid conflict with C# double quote string termination.
            // JavaScript template literals use backticks.
            // CSS and JS braces are escaped as {{ and }}.
            
            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width,initial-scale=1'>
    <style>
        :root {{
            /* Color System - Consolidado */
            --bg-color: #eeeeee;
            --header-bg: #ffffff;
            --text-main: #1a1a1a;
            --text-secondary: #808080;
            --accent-yellow: #f7db00;
            --border-color: #dbdbdb;
            --hover-bg: rgba(0, 0, 0, 0.05);

            --row-bg: #ffffff;
            --row-hover: #fbfbfb;
            --text-time: #555555;
            --star-gray: #e8e8e8;
            --star-hover: #666;
            --star-active: #ffc400;
        }}

        body {{
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
            background: transparent;
            margin: 0;
            padding: 0;
            user-select: none;
            overflow-y: auto; 
            /* Added overflow to ensure scroll if needed, though usually fixed height window */
        }}

        /* --- COMPONENT: Header League --- */
        .competition-container {{
            margin-bottom: 8px;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
            border-radius: 8px;
            overflow: hidden;
            background: var(--header-bg);
        }}

        .headerLeague__wrapper {{
            background: var(--header-bg);
            overflow: hidden;
            transition: box-shadow 0.2s ease;
        }}

        .headerLeague {{
            display: flex;
            align-items: center;
            padding: 12px 16px;
            min-height: 56px;
        }}

        .icon-btn {{
            background: none;
            border: none;
            cursor: pointer;
            padding: 8px;
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--text-secondary);
            border-radius: 50%;
            transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
            outline: none;
        }}

        .icon-btn:hover {{
            color: var(--text-main);
            background-color: var(--hover-bg);
            transform: translateY(-1px);
        }}

        .icon-btn.active {{
            color: var(--accent-yellow);
        }}

        .headerLeague__body {{
            display: flex;
            align-items: center;
            flex-grow: 1;
            margin-left: 12px;
            overflow: hidden;
        }}

        .headerLeague__flag {{
            width: 20px;
            height: 14px;
            margin-right: 12px;
            border-radius: 2px;
            box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.05);
            flex-shrink: 0;
            object-fit: cover;
        }}

        .headerLeague__titleWrapper {{
            display: flex;
            flex-direction: column;
            justify-content: center;
            line-height: 1.2;
        }}

        .headerLeague__title {{
            text-decoration: none;
            color: var(--text-main);
            font-weight: 700;
            font-size: 15px;
            text-transform: uppercase;
            letter-spacing: 0.02em;
            transition: color 0.15s ease;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }}

        .headerLeague__title:hover {{
            color: #000;
            text-decoration: underline;
            text-decoration-thickness: 2px;
            text-decoration-color: var(--accent-yellow);
        }}

        .headerLeague__meta {{
            font-size: 11px;
            color: var(--text-secondary);
            font-weight: 600;
            display: flex;
            align-items: center;
            margin-top: 2px;
        }}

        .headerLeague__category {{
            text-transform: uppercase;
        }}

        .headerLeague__actions {{
            display: flex;
            align-items: center;
            gap: 8px;
            margin-left: 12px;
        }}
        
        .headerLeague__link {{
            text-decoration: none;
            color: var(--text-main);
            font-size: 13px;
            font-weight: 600;
            padding: 6px 10px;
            border-radius: 4px;
            transition: background-color 0.2s ease;
            white-space: nowrap;
        }}
        
        .headerLeague__link:hover {{
            background-color: var(--hover-bg);
        }}
        
        .svg-icon {{
            fill: currentColor;
            display: block;
        }}

        /* --- COMPONENT: Match Row --- */
        .match-row-wrapper {{
            display: flex; 
            flex-direction: column;
        }}

        .match-row {{
            display: flex;
            align-items: center;
            background: var(--row-bg);
            border-bottom: 1px solid var(--border-color);
            padding: 12px 16px;
            position: relative;
            text-decoration: none;
            color: inherit;
            transition: background 0.15s ease-out;
            min-height: 48px;
            cursor: pointer;
        }}

        .match-row:hover {{
            background: var(--row-hover);
        }}
        
        .match-row:last-child {{
            border-bottom: none;
        }}

        .favorite-btn {{
            background: none;
            border: none;
            cursor: pointer;
            padding: 4px;
            margin-right: 12px;
            color: var(--star-gray);
            z-index: 2;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: color 0.2s;
            border-radius: 50%;
        }}

        .favorite-btn:hover {{
            color: var(--star-hover);
            background-color: rgba(0, 0, 0, 0.03);
        }}

        .favorite-btn.active {{
            color: var(--accent-yellow);
        }}

        .event__time {{
            font-size: 12px;
            color: var(--text-time);
            min-width: 45px;
            font-weight: 400;
            letter-spacing: 0.02em;
        }}

        .teams-container {{
            display: flex;
            flex-direction: column;
            flex-grow: 1;
            gap: 6px;
            justify-content: center;
        }}

        .team-item {{
            display: flex;
            align-items: center;
            gap: 10px;
        }}

        .team-logo {{
            width: 20px;
            height: 20px;
            object-fit: contain;
            flex-shrink: 0;
        }}

        .team-name {{
            font-size: 13px;
            font-weight: 600;
            color: var(--text-main);
            line-height: 1.2;
        }}
        
        .score-val {{
            font-weight: 700;
            font-size: 13px;
            margin-left: auto;
            color: #ff2f46;
        }}

        /* --- Empty State --- */
        .empty-state {{
            padding: 40px 20px;
            text-align: center;
            color: var(--text-secondary);
            font-size: 14px;
            background: var(--header-bg);
            border-radius: 8px;
            margin-top: 10px;
        }}
    </style>
</head>
<body>
    <div id='app-container'></div>
    <script>
        const initialGroups = {initialGroupsJson}; 
        const container = document.getElementById('app-container');

        // Debug logging to Verify Data
        console.log('Flashscore Overlay Loaded');
        console.log('Initial Groups:', initialGroups);

        function send(type, data) {{
             if (window.chrome?.webview) window.chrome.webview.postMessage({{ type, ...data }});
        }}

        document.addEventListener('click', (e) => {{
            const link = e.target.closest('a');
            if (link && link.href && !link.href.endsWith('#')) {{
                e.preventDefault();
                send('navigate', {{ href: link.href }});
            }}
        }});
        
        document.addEventListener('contextmenu', (e) => {{
            const matchRow = e.target.closest('.match-row');
            if (matchRow) {{
                e.preventDefault();
                const id = matchRow.getAttribute('data-id');
                if (id) {{
                    matchRow.style.opacity = '0.5';
                    send('remove', {{ MatchId: id }});
                }}
            }}
        }});

        function createElementFromHTML(htmlString) {{
            const div = document.createElement('div');
            div.innerHTML = htmlString.trim();
            return div.firstChild;
        }}

        function renderMatch(data) {{
            const homeScoreHtml = data.homeScore ? `<span class='score-val'>${{data.homeScore}}</span>` : '';
            const awayScoreHtml = data.awayScore ? `<span class='score-val'>${{data.awayScore}}</span>` : '';

            return `
            <div class='match-row' data-id='${{data.id}}'>
                <a href='${{data.url || '#' }}' class='row-link' style='position:absolute;top:0;left:0;right:0;bottom:0;z-index:1;'></a>
                
                <button class='favorite-btn' type='button'>
                    <svg class='svg-icon' fill='currentColor' viewBox='0 0 20 20' width='18' height='18'>
                        <path fill-rule='evenodd' d='m9.35 0-2.1 6.85L.4 6.83 0 8.14l5.54 4.21-2.13 6.84 1.06.81L10 15.76 15.53 20l1.05-.8-2.12-6.85L20 8.15l-.4-1.32-6.85.02L10.65 0h-1.3ZM8.4 7.8 10 2.57l1.6 5.21.66.5h5.2l-4.22 3.2-.25.81 1.63 5.21-4.22-3.23h-.8l-4.22 3.23 1.63-5.2-.26-.82-4.22-3.2h5.21l.66-.5Z'></path>
                    </svg>
                </button>

                <div class='event__time'>${{data.time}}</div>

                <div class='teams-container'>
                    <div class='team-item'>
                        <img class='team-logo' src='${{data.homeLogo}}' alt=''>
                        <span class='team-name'>${{data.homeTeam}}</span>
                        ${{homeScoreHtml}}
                    </div>
                    <div class='team-item'>
                        <img class='team-logo' src='${{data.awayLogo}}' alt=''>
                        <span class='team-name'>${{data.awayTeam}}</span>
                        ${{awayScoreHtml}}
                    </div>
                </div>
            </div>`;
        }}

        function renderHeader(competition) {{
            // competition object from JSON (camelCase)
            // Expects: title, category, logo, hrefWithParam, href
            
            const logoSrc = competition.logo || ''; 
            const logoHtml = logoSrc ? `<img class='headerLeague__flag' src='${{logoSrc}}' alt='Flag'>` : '<span class=\'headerLeague__flag\'></span>';
            const url = competition.hrefWithParam || competition.href || '#';
            const title = competition.title || 'Unknown Competition';
            const category = competition.category || '';

            return `
            <div class='headerLeague__wrapper'>
                <header class='headerLeague'>
                    <div class='headerLeague__star'>
                        <button class='icon-btn' type='button'>
                            <svg class='svg-icon' width='18' height='18' viewBox='0 0 20 20'><path d='m9.35 0-2.1 6.85L.4 6.83 0 8.14l5.54 4.21-2.13 6.84 1.06.81L10 15.76 15.53 20l1.05-.8-2.12-6.85L20 8.15l-.4-1.32-6.85.02L10.65 0h-1.3ZM8.4 7.8 10 2.57l1.6 5.21.66.5h5.2l-4.22 3.2-.25.81 1.63 5.21-4.22-3.23h-.8l-4.22 3.23 1.63-5.2-.26-.82-4.22-3.2h5.21l.66-.5Z'></path></svg>
                        </button>
                    </div>

                    <div class='headerLeague__body'>
                        ${{logoHtml}}
                        <div class='headerLeague__titleWrapper'>
                            <a href='${{url}}' class='headerLeague__title'>${{title}}</a>
                            <div class='headerLeague__meta'>
                                <span class='headerLeague__category'>${{category}}</span>
                            </div>
                        </div>
                    </div>
                </header>
            </div>`;
        }}

        function render(groups) {{
             if (!groups || groups.length === 0) {{
                 container.innerHTML = `<div class='empty-state'>No hay partidos fijados.<br>Agrega partidos desde Flashscore.</div>`;
                 return;
             }}
             
             // Clear for simple re-render (or implement diffing if flickering is an issue)
             // For strict correctness, we'll clear first or use ID checking.
             // Using ID checking similar to before.
             
             // Remove groups not present? 
             // Simplification: We will just append/update.
             
             groups.forEach(group => {{
                 // group.competition (camelCase)
                 const comp = group.competition;
                 if (!comp) return;

                 // Safely handle potential missing properties
                 const safeTitle = comp.title || 'Unknown';
                 const compIdBase = comp.competitionId || comp.title || 'default';
                 let compId = 'comp-' + compIdBase.replace(/[^a-zA-Z0-9]/g, '');
                 
                 let compContainer = document.getElementById(compId);
                 
                 if (!compContainer) {{
                     compContainer = document.createElement('div');
                     compContainer.id = compId;
                     compContainer.className = 'competition-container';
                     
                     // Insert Header
                     compContainer.innerHTML = renderHeader(comp);
                     
                     // Create matches container
                     const matchesWrapper = document.createElement('div');
                     matchesWrapper.className = 'match-row-wrapper';
                     compContainer.appendChild(matchesWrapper);

                     container.appendChild(compContainer);
                 }}

                 const matchesWrapper = compContainer.querySelector('.match-row-wrapper');

                 group.matches.forEach(match => {{
                     // match propertis (camelCase)
                     const matchId = match.matchId || match.overlayId;
                     let matchEl = matchesWrapper.querySelector(`[data-id='${{matchId}}']`);
                     
                     const matchData = {{
                         id: matchId,
                         homeTeam: match.homeTeam,
                         awayTeam: match.awayTeam,
                         homeLogo: match.homeLogo,
                         awayLogo: match.awayLogo,
                         time: match.timeLabel,
                         homeScore: match.homeScore,
                         awayScore: match.awayScore,
                         url: match.url || '#'
                     }};
                     
                     const matchHtml = renderMatch(matchData);
                     
                     if (matchEl) {{
                         matchEl.outerHTML = matchHtml;
                     }} else {{
                         const div = createElementFromHTML(matchHtml);
                         matchesWrapper.appendChild(div);
                     }}
                 }});
             }});
        }}

        try {{
            render(initialGroups);
        }} catch(e) {{
            console.error('Render error:', e);
            document.body.innerHTML += '<div style=""color:red;background:white;padding:10px"">JS Error: ' + e.message + '</div>';
        }}

        window.chrome.webview.addEventListener('message', event => {{
            if (event.data.type === 'render') {{
                try {{
                    render(event.data.payload);
                }} catch(e) {{
                    console.error('Update render error:', e);
                }}
            }}
        }});
    </script>
</body>
</html>";
        }

        // Helper Classes
        private class WebMessage { public string? Type { get; set; } public string? Href { get; set; } public string? MatchId { get; set; } public string? TabId { get; set; } }

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
            public string? BadgeLabel { get; set; }
            public string? HomeTeam { get; set; }
            public string? AwayTeam { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public string? HomeHref { get; set; }
            public string? AwayHref { get; set; }
            public string? HomeScore { get; set; }
            public string? AwayScore { get; set; }
            public bool HasScore { get; set; }
            public bool IsLive { get; set; }
            public string? State { get; set; }
            public string? HtmlSource { get; set; } // Raw HTML from MatchData
            public string? Url { get; set; }
        }
    }
}
