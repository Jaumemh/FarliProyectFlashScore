using System;
using System.Net.Http;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AngleSharp.Dom;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

namespace FlashscoreOverlay
{
    public partial class OverlayWindow : Window
    {
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly MatchData match;
        private readonly CompetitionData? competition;
        private readonly string overlayKey;
        private readonly string? tabId;
        private CancellationTokenSource? pollingCts;

        public event Action<string>? OverlayClosed;

        public OverlayWindow(MatchData match, CompetitionData? competition, string? tabId = null)
        {
            InitializeComponent();

            this.match = match ?? throw new ArgumentNullException(nameof(match));
            this.competition = competition;
            this.tabId = tabId;
            overlayKey = !string.IsNullOrWhiteSpace(match.OverlayId)
                ? match.OverlayId!
                : match.MatchId ?? Guid.NewGuid().ToString("N");

            // Build title with clickable competition name when available
            BuildOverlayTitleWithLink();
            Topmost = true;
            ShowInTaskbar = false;

            _ = InitializeAsync();
        }

        private string? ResolveMatchHrefFromData(string? html)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(html)) return null;
                var pattern = "<a[^>]+href=[\\'\\\"](?<h>[^\\'\\\"]*(?:/partido/)[^\\'\\\"]*)[\\'\\\"][^>]*>";
                var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var m = rx.Match(html);
                if (m.Success)
                {
                    var found = m.Groups["h"].Value;
                    return found.StartsWith("http") ? found : "https://www.flashscore.es" + found;
                }
            }
            catch { }
            return null;
        }

        private void BuildOverlayTitleWithLink()
        {
            try
            {
                MatchTitle.Inlines.Clear();
                var teams = $"{match.HomeTeam ?? "Partido"} - {match.AwayTeam ?? "Flashscore"}";
                MatchTitle.Inlines.Add(new System.Windows.Documents.Run(teams + " ")); 
                if (!string.IsNullOrWhiteSpace(competition?.Title))
                {
                    MatchTitle.Inlines.Add(new System.Windows.Documents.Run("· "));
                    var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(competition.Title))
                    {
                        Foreground = MatchTitle.Foreground,
                        TextDecorations = new System.Windows.TextDecorationCollection()
                    };
                    link.Click += async (s, e) =>
                    {
                        try
                        {
                            var url = competition?.HrefWithParam ?? competition?.Href;
                            if (string.IsNullOrWhiteSpace(url)) return;

                            // Try to resolve a better tab (clasificación -> cuadro -> tabla en directo) before navigating
                            string chosen = url;
                            try
                            {
                                var resolved = await FindBestCompetitionTabAsync(url);
                                if (!string.IsNullOrWhiteSpace(resolved)) chosen = resolved;
                            }
                            catch { /* don't block navigation on errors */ }

                            if (!string.IsNullOrWhiteSpace(chosen) && !string.IsNullOrWhiteSpace(tabId))
                            {
                                var main = Application.Current?.MainWindow as MainWindow;
                                main?.SetPendingCommandForTab(tabId, new BrowserCommand { Action = "navigate", Href = chosen });
                            }
                            else if (!string.IsNullOrWhiteSpace(chosen))
                            {
                                Process.Start(new ProcessStartInfo(chosen) { UseShellExecute = true });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error abriendo link desde overlay: {ex.Message}");
                        }
                    };
                    MatchTitle.Inlines.Add(link);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error construyendo title con link: {ex.Message}");
                MatchTitle.Text = BuildSimpleTitle();
            }
        }

        private string BuildSimpleTitle()
        {
            var teams = $"{match.HomeTeam ?? "Partido"} - {match.AwayTeam ?? "Flashscore"}";
            if (!string.IsNullOrWhiteSpace(competition?.Title))
            {
                return $"{teams} · {competition!.Title}";
            }
            return teams;
        }

        private async Task InitializeAsync()
        {
            try
            {
                await MatchWebView.EnsureCoreWebView2Async();

                MatchWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                MatchWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                MatchWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                var html = BuildOverlayHtml();
                MatchWebView.NavigateToString(html);

                // Try an immediate refresh so team hrefs extracted from the live page are available
                var initialPayload = await RefreshMatchDataAsync(CancellationToken.None);
                if (initialPayload != null)
                {
                    UpdateOverlayPayload(initialPayload);
                }

                StartPollingMatch();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir el overlay:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private string BuildOverlayHtml()
        {
            var initialData = CreateDisplayData(match, match.Time, match.Stage, match.HomeScore, match.AwayScore);
            var payloadJson = JsonConvert.SerializeObject(initialData);

            var html = @"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width,initial-scale=1'>
    <title>Flashscore Overlay</title>
    <style>
        :root {
            font-family: 'Segoe UI', 'Helvetica Neue', Arial, sans-serif;
            background: transparent;
        }

        * {
            box-sizing: border-box;
        }

        body {
            margin: 0;
            background: transparent;
            min-height: 0;
            color: #f5f5f5;
        }

        .overlay-shell {
            width: 100%;
            height: 100%;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 6px;
        }

        .match-card {
            width: 420px;
            min-height: 80px;
            display: flex;
            align-items: center;
            gap: 16px;
            padding: 12px 18px;
            border-radius: 10px;
            border: 1px solid rgba(255, 255, 255, 0.12);
            background: linear-gradient(90deg, #02090f, #031026);
            position: relative;
            overflow: hidden;
            box-shadow: 0 18px 38px rgba(0, 0, 0, 0.7);
            transition: background 0.35s ease, border-color 0.35s ease, transform 0.25s ease;
        }

        .match-card::after {
            content: '';
            position: absolute;
            right: 0;
            top: 12px;
            bottom: 12px;
            width: 1px;
            background: rgba(255, 255, 255, 0.08);
        }

        .match-card[data-state='preview'] {
            border-color: rgba(255, 255, 255, 0.12);
            background: linear-gradient(90deg, #02060e, #031427);
        }

        .match-card[data-state='live'] {
            border-color: #ff2f46;
            background: linear-gradient(90deg, #031125, #041534);
        }

        .match-card[data-state='finished'] {
            border-color: rgba(255, 255, 255, 0.08);
            background: linear-gradient(90deg, #07080e, #0b0b12);
        }

        .match-card.goal-flash {
            background: linear-gradient(90deg, #5d071a, #3d0111);
            border-color: #ff6f83;
            box-shadow: 0 30px 60px rgba(93, 7, 25, 0.9);
        }

        .time-block {
            display: flex;
            flex-direction: column;
            align-items: flex-start;
            gap: 4px;
            width: 90px;
        }

        .time-value {
            font-size: 20px;
            font-weight: 600;
            color: #fdfdfd;
        }

        .stage-label {
            font-size: 12px;
            letter-spacing: 0.3em;
            color: rgba(255, 255, 255, 0.5);
            text-transform: uppercase;
            opacity: 0;
            transition: opacity 0.25s ease;
        }

        .teams-block {
            flex: 1;
            display: flex;
            flex-direction: column;
            gap: 6px;
            justify-content: center;
        }

        .team-row {
            display: flex;
            align-items: center;
            gap: 10px;
            font-size: 16px;
            font-weight: 600;
            color: #fbfbfb;
            text-shadow: 0 1px 2px rgba(0, 0, 0, 0.35);
            cursor: pointer;
        }

        /* Team name should look like normal text but allow clicks */
        .team-row span, #homeName, #awayName {
            color: inherit;
            text-decoration: none;
            outline: none;
            cursor: inherit;
        }

        .team-logo {
            width: 28px;
            height: 28px;
            border-radius: 50%;
            background: #0a121f;
            border: 1px solid rgba(255, 255, 255, 0.16);
            object-fit: contain;
        }

        .score-section {
            width: 110px;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 6px;
            z-index: 1;
        }

        .score-grid {
            display: flex;
            align-items: center;
            gap: 8px;
            font-size: 26px;
            font-weight: 700;
            color: #d3d6ec;
        }

        .score-grid[data-empty='true'] .score-digit {
            opacity: 0.4;
        }

        .score-digit {
            width: 36px;
            text-align: center;
        }

        .score-separator {
            font-size: 22px;
            color: rgba(255, 255, 255, 0.45);
        }

        .match-card[data-state='live'] .score-digit {
            color: #ff4c58;
        }

        .match-card[data-state='finished'] .score-digit {
            color: #ffffff;
        }

        .goal-flash .score-digit {
            color: #ffe7eb;
            text-shadow: 0 0 18px rgba(255, 255, 255, 0.65);
        }

        .status-badge {
            font-size: 11px;
            letter-spacing: 0.35em;
            text-transform: uppercase;
            padding: 3px 12px;
            border-radius: 999px;
            border: 1px solid rgba(255, 255, 255, 0.15);
            color: #fde4e9;
            background: rgba(255, 255, 255, 0.04);
            opacity: 0;
            transition: opacity 0.25s ease, transform 0.25s ease;
            transform: translateY(6px);
        }

        .status-badge.visible {
            opacity: 1;
            transform: translateY(0);
        }

        .goal-flash .status-badge {
            border-color: rgba(255, 255, 255, 0.35);
            background: rgba(255, 255, 255, 0.1);
            color: #fff7fb;
        }
    </style>
</head>
<body>
    <div class='overlay-shell'>
        <div class='match-card' data-state='preview'>
            <div class='time-block'>
                <span class='time-value' id='timeLabel'></span>
                <span class='stage-label' id='stageLabel'></span>
            </div>
            <div class='teams-block'>
                <div class='team-row'>
                    <img id='homeLogo' class='team-logo' alt='Local'>
                    <span id='homeName'>Local</span>
                </div>
                <div class='team-row'>
                    <img id='awayLogo' class='team-logo' alt='Visitante'>
                    <span id='awayName'>Visitante</span>
                </div>
            </div>
            <div class='score-section'>
                <div class='score-grid' data-empty='true'>
                    <span class='score-digit' id='homeScore'>-</span>
                    <span class='score-separator'>-</span>
                    <span class='score-digit' id='awayScore'>-</span>
                </div>
                <div class='status-badge' id='statusBadge'></div>
            </div>
        </div>
    </div>
    <script>
        const initialData = " + payloadJson + @";
        const root = document.querySelector('.match-card');
        const timeLabelEl = document.getElementById('timeLabel');
        const stageLabelEl = document.getElementById('stageLabel');
        const badgeEl = document.getElementById('statusBadge');
        const homeNameEl = document.getElementById('homeName');
        const awayNameEl = document.getElementById('awayName');
        const homeLogoEl = document.getElementById('homeLogo');
        const awayLogoEl = document.getElementById('awayLogo');
        const homeScoreEl = document.getElementById('homeScore');
        const awayScoreEl = document.getElementById('awayScore');
        const scoreGrid = document.querySelector('.score-grid');

        // Click handlers for team area — send navigate message to host (no visual changes)
        try {
            function sendNavigate(href, name) {
                // Prefer navigating to the exact match page first so the page can auto-click
                // the team anchor (fc_open_team). If match href isn't available, fall back
                // to navigating directly to the team's href when present.
                try {
                    const matchHref = root?.getAttribute('data-match-href') || '';
                    if (matchHref) {
                        try {
                            const u = new URL(matchHref, window.location.origin);
                            if (name) u.searchParams.set('fc_open_team', name);
                            if (href) u.searchParams.set('fc_open_href', href);
                            const target = u.toString();
                            if (window.chrome?.webview) {
                                window.chrome.webview.postMessage({ type: 'navigate', href: target });
                            } else {
                                window.location.href = target;
                            }
                            return;
                        } catch (e) { /* fallthrough */ }
                    }
                } catch (e) { /* ignore */ }

                // If no match page available, navigate directly to team href if provided
                if (href) {
                    if (window.chrome?.webview) {
                        window.chrome.webview.postMessage({ type: 'navigate', href: href });
                    } else {
                        try { window.open(href, '_blank'); } catch (e) {}
                    }
                    return;
                }

                // fallback: request host to open team by name (will perform search)
                if (!name) return;
                if (window.chrome?.webview) {
                    window.chrome.webview.postMessage({ type: 'openTeam', name: name });
                } else {
                    try {
                        const q = encodeURIComponent(name.trim());
                        window.open('https://www.flashscore.es/search/?q=' + q, '_blank');
                    } catch (e) {}
                }
            }

            // prefer data-href attribute (more robust) and avoid bubbling to card click
            homeNameEl.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(homeNameEl.getAttribute('data-href'), homeNameEl.textContent); });
            awayNameEl.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(awayNameEl.getAttribute('data-href'), awayNameEl.textContent); });

            const homeRow = homeNameEl.closest('.team-row');
            const awayRow = awayNameEl.closest('.team-row');
            if (homeRow) homeRow.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(homeNameEl.getAttribute('data-href'), homeNameEl.textContent); });
            if (awayRow) awayRow.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(awayNameEl.getAttribute('data-href'), awayNameEl.textContent); });

            if (homeLogoEl) homeLogoEl.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(homeNameEl.getAttribute('data-href'), homeNameEl.textContent); });
            if (awayLogoEl) awayLogoEl.addEventListener('click', (ev) => { ev.stopPropagation(); sendNavigate(awayNameEl.getAttribute('data-href'), awayNameEl.textContent); });

            // click on the card navigates to the exact match page
            root.addEventListener('click', () => {
                try {
                    const m = root.getAttribute('data-match-href');
                    if (m) {
                        if (window.chrome?.webview) {
                            window.chrome.webview.postMessage({ type: 'navigate', href: m });
                        } else {
                            window.location.href = m;
                        }
                    }
                } catch (e) { }
            });
        } catch (e) {
            // ignore if elements not available
        }

        let badgeDefault = '';
        let previousScores = { home: '', away: '' };
        let goalTimeout;
        let goalActive = false;
        let renderedOnce = false;

        function formatValue(value, fallback) {
            const clean = value?.trim();
            return clean && clean.length ? clean : fallback;
        }

        function setBadgeText(text) {
            if (!text) {
                badgeEl.classList.remove('visible');
                badgeEl.textContent = '';
                return;
            }
            badgeEl.textContent = text;
            badgeEl.classList.add('visible');
        }

        function applyBadge(text) {
            badgeDefault = text || '';
            if (!goalActive) {
                setBadgeText(badgeDefault);
            }
        }

        function clearGoalFlash() {
            goalActive = false;
            root?.classList.remove('goal-flash');
            if (goalTimeout) {
                clearTimeout(goalTimeout);
                goalTimeout = undefined;
            }
            setBadgeText(badgeDefault);
        }

        function triggerGoalFlash() {
            goalActive = true;
            root?.classList.add('goal-flash');
            setBadgeText('GOL');
            if (goalTimeout) {
                clearTimeout(goalTimeout);
            }
            goalTimeout = setTimeout(() => {
                goalActive = false;
                root?.classList.remove('goal-flash');
                setBadgeText(badgeDefault);
            }, 4200);
        }

        function render(data) {
            const state = (data?.state || 'preview').toLowerCase();
            if (root) {
                root.setAttribute('data-state', state);
                if (state !== 'live') {
                    clearGoalFlash();
                }
            }

            timeLabelEl.textContent = data?.timeLabel || '—';

            if (data?.stageLabel) {
                stageLabelEl.textContent = data.stageLabel;
                stageLabelEl.style.opacity = '1';
            }
            else {
                stageLabelEl.style.opacity = '0';
            }

            homeNameEl.textContent = data?.homeTeam || 'Local';
            awayNameEl.textContent = data?.awayTeam || 'Visitante';
            // expose current hrefs as data attributes so click handlers can use them
            try { homeNameEl.setAttribute('data-href', data?.homeHref || ''); } catch (e) {}
            try { awayNameEl.setAttribute('data-href', data?.awayHref || ''); } catch (e) {}
            try { root.setAttribute('data-match-href', data?.matchHref || ''); } catch (e) {}
            homeLogoEl.src = data?.homeLogo || '';
            awayLogoEl.src = data?.awayLogo || '';

            const homeScore = formatValue(data?.homeScore, '-');
            const awayScore = formatValue(data?.awayScore, '-');
            const hasScore = !!data?.hasScore;

            homeScoreEl.textContent = homeScore;
            awayScoreEl.textContent = awayScore;
            if (scoreGrid) {
                scoreGrid.setAttribute('data-empty', hasScore ? 'false' : 'true');
            }

            applyBadge(data?.badgeLabel);

            if (renderedOnce && state === 'live' && hasScore) {
                const changed = homeScore !== previousScores.home || awayScore !== previousScores.away;
                if (changed && (previousScores.home || previousScores.away)) {
                    triggerGoalFlash();
                }
            }

            previousScores.home = homeScore;
            previousScores.away = awayScore;
            renderedOnce = true;
        }

        function registerMessages() {
            if (!window.chrome?.webview) return;
            window.chrome.webview.addEventListener('message', event => {
                const payload = event.data;
                if (!payload?.type) return;
                if (payload.type === 'update') {
                    render(payload.payload);
                }
                else if (payload.type === 'close') {
                    window.chrome.webview.postMessage({ type: 'close' });
                }
            });
        }

        document.addEventListener('contextmenu', event => {
            event.preventDefault();
            if (window.chrome?.webview) {
                window.chrome.webview.postMessage({ type: 'close' });
            }
        });

        render(initialData);
        registerMessages();
    </script>
</body>
</html>";
            // replace preview placeholder with actual initial state
            try { html = html.Replace("data-state='preview'", "data-state='" + initialData.State + "'"); } catch { }
            return html;
        }

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
            var state = DetermineOverlayState(isLive, stageLabel, hasScore);
            var badgeLabel = DetermineBadgeLabel(state);

            // Fallback: if user script didn't provide team hrefs, try to extract from the captured HTML
            string ResolveHref(string? href, string? teamName)
            {
                if (!string.IsNullOrWhiteSpace(href)) return href!;
                try
                {
                    var html = matchData.Html ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(teamName)) return string.Empty;
                    // find anchors with /equipo/ or /team/ and check if the anchor text contains the team name
                    var pattern = "<a[^>]+href=[\'\"](?<h>[^\'\"]*(?:/equipo/|/team/)[^\'\"]*)[\'\"][^>]*>(?<t>.*?)</a>";
                    var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    foreach (Match m in rx.Matches(html))
                    {
                        var text = Regex.Replace(m.Groups["t"].Value, "<.*?>", string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(text) && text.IndexOf(teamName!, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var found = m.Groups["h"].Value;
                            return found.StartsWith("http") ? found : "https://www.flashscore.es" + found;
                        }
                    }
                    // last resort: return first match href
                    var first = rx.Match(html);
                    if (first.Success)
                    {
                        var found = first.Groups["h"].Value;
                        return found.StartsWith("http") ? found : "https://www.flashscore.es" + found;
                    }
                }
                catch { }
                return string.Empty;
            }

            // Ensure we expose the match URL (matchHref) so overlay can navigate to the match page
            string ResolveMatchHref()
            {
                if (!string.IsNullOrWhiteSpace(matchData.Url)) return matchData.Url!;
                try
                {
                    var html = matchData.Html ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(html)) return string.Empty;
                    var pattern = "<a[^>]+href=[\\'\\\"](?<h>[^\\'\\\"]*(?:/partido/)[^\\'\\\"]*)[\\'\\\"][^>]*>";
                    var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    var m = rx.Match(html);
                    if (m.Success)
                    {
                        var found = m.Groups["h"].Value;
                        return found.StartsWith("http") ? found : "https://www.flashscore.es" + found;
                    }
                }
                catch { }
                return string.Empty;
            }

            return new OverlayDisplayData
            {
                TimeLabel = string.IsNullOrWhiteSpace(timeLabel) ? "—" : timeLabel,
                StageLabel = stageLabel,
                BadgeLabel = badgeLabel,
                HomeTeam = matchData.HomeTeam ?? "Local",
                AwayTeam = matchData.AwayTeam ?? "Visitante",
                HomeLogo = matchData.HomeLogo ?? string.Empty,
                AwayLogo = matchData.AwayLogo ?? string.Empty,
                HomeHref = ResolveHref(matchData.HomeHref, matchData.HomeTeam) ?? string.Empty,
                AwayHref = ResolveHref(matchData.AwayHref, matchData.AwayTeam) ?? string.Empty,
                MatchHref = ResolveMatchHref() ?? string.Empty,
                HomeScore = string.IsNullOrWhiteSpace(homeScoreSource) ? string.Empty : homeScoreSource.Trim(),
                AwayScore = string.IsNullOrWhiteSpace(awayScoreSource) ? string.Empty : awayScoreSource.Trim(),
                HasScore = hasScore,
                IsLive = isLive,
                State = state
            };
        }

        private static string DetermineOverlayState(bool isLive, string stageLabel, bool hasScore)
        {
            var normalizedStage = (stageLabel ?? string.Empty).Trim().ToLowerInvariant();

            if (normalizedStage.Contains("finalizado") || normalizedStage.Contains("final") || normalizedStage.Contains("terminado") || normalizedStage.Contains("descansado"))
            {
                return "finished";
            }

            if (isLive)
            {
                return "live";
            }

            return "preview";
        }

        private static string DetermineBadgeLabel(string state)
        {
            return state switch
            {
                "finished" => "FINALIZADO",
                _ => string.Empty
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

            if (firstLetter <= 0)
            {
                return (trimmed, string.Empty);
            }

            var timePart = trimmed.Substring(0, firstLetter).Trim();
            var trailing = trimmed.Substring(firstLetter).Trim();
            return (timePart, trailing);
        }

        private static bool DetermineLiveState(string? timeSource, string? stageSource)
        {
            var stage = (stageSource ?? string.Empty).Trim().ToLowerInvariant();
            var time = (timeSource ?? string.Empty).Trim().ToLowerInvariant();

            if (stage.Contains("preview") || stage.Contains("finalizado") || stage.Contains("final"))
            {
                return false;
            }

            if (stage.Contains("penaltis") || stage.Contains("terminado") || stage.Contains("descansado"))
            {
                return false;
            }

            if (stage.Contains("descanso") || stage.Contains("en directo") || stage.Contains("en curso"))
            {
                return true;
            }

            if (stage.Contains("'") || time.Contains("'"))
            {
                return true;
            }

            return false;
        }

        private void StartPollingMatch()
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
                    var payload = await RefreshMatchDataAsync(token);
                    if (payload != null)
                    {
                        await Dispatcher.InvokeAsync(() => UpdateOverlayPayload(payload));
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Overlay refresh error: {ex.Message}");
                }
            }
        }

        private async Task<OverlayDisplayData?> RefreshMatchDataAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(match.Url)) return null;

            try
            {
                var response = await HttpClient.GetAsync(match.Url, token);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(token);
                var parser = new HtmlParser();
                var document = await parser.ParseDocumentAsync(html, token);
                var matchElement = FindMatchElement(document);
                if (matchElement == null) return null;

                var timeText = matchElement.QuerySelector(".event__time")?.TextContent;
                var stageText = matchElement.QuerySelector(".event__stage")?.TextContent;
                var homeScore = matchElement.QuerySelector(".event__score--home")?.TextContent;
                var awayScore = matchElement.QuerySelector(".event__score--away")?.TextContent;

                // Try to extract team links from the match element (more reliable than userscript sometimes)
                try
                {
                    var homeAnchor = matchElement.QuerySelector(".event__participant--home a[href]") ?? matchElement.QuerySelector(".event__participant--home [href]");
                    var awayAnchor = matchElement.QuerySelector(".event__participant--away a[href]") ?? matchElement.QuerySelector(".event__participant--away [href]");
                    if (homeAnchor != null)
                    {
                        var h = homeAnchor.GetAttribute("href");
                        if (!string.IsNullOrWhiteSpace(h))
                        {
                            match.HomeHref = h.StartsWith("http") ? h : "https://www.flashscore.es" + h;
                        }
                    }
                    if (awayAnchor != null)
                    {
                        var h2 = awayAnchor.GetAttribute("href");
                        if (!string.IsNullOrWhiteSpace(h2))
                        {
                            match.AwayHref = h2.StartsWith("http") ? h2 : "https://www.flashscore.es" + h2;
                        }
                    }
                }
                catch { }

                return CreateDisplayData(match, timeText, stageText, homeScore, awayScore);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Overlay refresh error: {ex.Message}");
                return null;
            }
        }

        private IElement? FindMatchElement(IDocument document)
        {
            if (!string.IsNullOrWhiteSpace(match.OverlayId))
            {
                var byId = document.QuerySelector($"#{match.OverlayId}");
                if (byId != null) return byId;
            }

            if (!string.IsNullOrWhiteSpace(match.MatchMid))
            {
                var byMid = document.QuerySelector($"[data-mid=\"{match.MatchMid}\"]");
                if (byMid != null) return byMid;
            }

            if (!string.IsNullOrWhiteSpace(match.MatchId))
            {
                var byMatch = document.QuerySelector($"[data-event-id=\"{match.MatchId}\"]");
                if (byMatch != null) return byMatch;
            }

            return document.QuerySelector(".event__match");
        }

        private async Task<string?> ResolveTeamHrefAsync(string teamName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(teamName)) return null;

                // If match already has hrefs, prefer them when the text matches
                if (!string.IsNullOrWhiteSpace(match.HomeHref) && match.HomeTeam != null && match.HomeTeam.IndexOf(teamName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return match.HomeHref;
                }
                if (!string.IsNullOrWhiteSpace(match.AwayHref) && match.AwayTeam != null && match.AwayTeam.IndexOf(teamName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return match.AwayHref;
                }

                // If match page available, fetch it and look for /equipo/ or /team/ anchors that contain the team name
                if (string.IsNullOrWhiteSpace(match.Url)) return null;

                using var req = new HttpRequestMessage(HttpMethod.Get, match.Url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
                var resp = await HttpClient.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                var parser = new HtmlParser();
                var doc = await parser.ParseDocumentAsync(html);

                var rx = new Regex("<a[^>]+href=[\\'\\\"](?<h>[^\\'\\\"]*(?:/equipo/|/team/)[^\\'\\\"]*)[\\'\\\"][^>]*>(?<t>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match m in rx.Matches(html))
                {
                    var text = Regex.Replace(m.Groups["t"].Value, "<.*?>", string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(text) && text.IndexOf(teamName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var found = m.Groups["h"].Value;
                        return found.StartsWith("http") ? found : "https://www.flashscore.es" + found;
                    }
                }

                // As last resort, scan anchors in parsed document
                var anchors = doc.QuerySelectorAll("a[href]");
                foreach (var a in anchors)
                {
                    try
                    {
                        var h = a.GetAttribute("href") ?? string.Empty;
                        var t = (a.TextContent ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(h)) continue;
                        if (!Regex.IsMatch(h, "/(equipo|team)/", RegexOptions.IgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(t) && t.IndexOf(teamName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return h.StartsWith("http") ? h : "https://www.flashscore.es" + h;
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resolviendo href de equipo: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> FindBestCompetitionTabAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // set a browser-like user-agent and headers to improve compatibility with Flashscore
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
                req.Headers.AcceptLanguage.ParseAdd("es-ES,es;q=0.9,en;q=0.8");
                var response = await HttpClient.SendAsync(req);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync();
                var parser = new HtmlParser();
                var doc = await parser.ParseDocumentAsync(html);

                // Normalize search tokens in order of preference
                var searchGroups = new[]
                {
                    new[] { "clasific" }, // clasificación / clasificaciôn
                    new[] { "cuadro", "bracket" }, // bracket / cuadro
                    new[] { "tabla en directo", "tabla en vivo", "tabla" }, // tabla en directo / tabla
                    new[] { "standings", "table" }
                };

                string MakeAbsolute(string href)
                {
                    if (string.IsNullOrWhiteSpace(href)) return href;
                    href = href.Trim();
                    if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return string.Empty;
                    if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
                    try
                    {
                        var baseUri = new Uri(url);
                        var abs = new Uri(baseUri, href).ToString();
                        return abs;
                    }
                    catch { return href; }
                }

                var anchors = doc.QuerySelectorAll("a[href]");
                foreach (var group in searchGroups)
                {
                    foreach (var a in anchors)
                    {
                        try
                        {
                            var href = a.GetAttribute("href") ?? string.Empty;
                            var text = (a.TextContent ?? string.Empty).Trim();
                            var combined = (href + " " + text).ToLowerInvariant();
                            foreach (var token in group)
                            {
                                if (combined.Contains(token, StringComparison.OrdinalIgnoreCase))
                                {
                                    var abs = MakeAbsolute(href);
                                    if (!string.IsNullOrWhiteSpace(abs)) return abs;
                                }
                            }
                        }
                        catch { }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resolviendo pestañas de competición: {ex.Message}");
                return null;
            }
        }

        private void UpdateOverlayPayload(OverlayDisplayData data)
        {
            try
            {
                var envelope = new
                {
                    type = "update",
                    payload = data
                };
                var json = JsonConvert.SerializeObject(envelope);
                MatchWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update overlay: {ex.Message}");
            }
        }

        public void OpenCompetitionLink(CompetitionData? competitionData)
        {
            try
            {
                var url = competitionData?.HrefWithParam ?? competitionData?.Href ?? competition?.HrefWithParam ?? competition?.Href;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error abriendo link de competición desde overlay: {ex.Message}");
            }
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var payload = JsonConvert.DeserializeObject<OverlayWebMessage>(e.WebMessageAsJson);
                if (payload == null) return;

                if (payload.Type == "close")
                {
                    Dispatcher.InvokeAsync(Close);
                }
                else if (payload.Type == "navigate")
                {
                    try
                    {
                        var url = payload.Href;
                        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(tabId))
                        {
                            var main = Application.Current?.MainWindow as MainWindow;
                            main?.SetPendingCommandForTab(tabId, new BrowserCommand { Action = "navigate", Href = url });
                        }
                        else if (!string.IsNullOrWhiteSpace(url))
                        {
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error navegando desde overlay: {ex.Message}");
                    }
                }
                else if (payload.Type == "openTeam")
                {
                    try
                    {
                        var name = payload.Name?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) return;
                        // Prefer navigating to the exact match page (if available) and instruct it to auto-click
                        // the team anchor via query params. This avoids opening the competition clasificación.
                        string matchTarget = null;
                        try
                        {
                            // prefer the explicit match URL if we have it
                            if (!string.IsNullOrWhiteSpace(match.Url)) matchTarget = match.Url;
                            // otherwise try to extract from captured HTML
                            if (string.IsNullOrWhiteSpace(matchTarget))
                            {
                                var resolved = ResolveMatchHrefFromData(match.Html);
                                if (!string.IsNullOrWhiteSpace(resolved)) matchTarget = resolved;
                            }
                        }
                        catch { }

                        if (!string.IsNullOrWhiteSpace(matchTarget))
                        {
                            try
                            {
                                var u = new UriBuilder(matchTarget);
                                var qp = System.Web.HttpUtility.ParseQueryString(u.Query);
                                qp["fc_open_team"] = name;
                                // optional: provide fallback direct href if already known
                                var direct = await ResolveTeamHrefAsync(name);
                                if (!string.IsNullOrWhiteSpace(direct)) qp["fc_open_href"] = direct;
                                u.Query = qp.ToString();
                                var target = u.ToString();

                                if (!string.IsNullOrWhiteSpace(tabId))
                                {
                                    var main = Application.Current?.MainWindow as MainWindow;
                                    main?.SetPendingCommandForTab(tabId, new BrowserCommand { Action = "navigate", Href = target });
                                }
                                else
                                {
                                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                                }
                                return;
                            }
                            catch { /* fallthrough to resolve directly */ }
                        }

                        // Fallback: try to resolve direct team href and navigate
                        var teamHref = await ResolveTeamHrefAsync(name);
                        if (string.IsNullOrWhiteSpace(teamHref))
                        {
                            teamHref = "https://www.flashscore.es/search/?q=" + Uri.EscapeDataString(name);
                        }

                        if (!string.IsNullOrWhiteSpace(tabId))
                        {
                            var main = Application.Current?.MainWindow as MainWindow;
                            main?.SetPendingCommandForTab(tabId, new BrowserCommand { Action = "navigate", Href = teamHref });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo(teamHref) { UseShellExecute = true });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error abriendo ficha del equipo: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView message error: {ex.Message}");
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            var payload = await RefreshMatchDataAsync(CancellationToken.None);
            if (payload != null)
            {
                UpdateOverlayPayload(payload);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            pollingCts?.Cancel();
            pollingCts?.Dispose();
            pollingCts = null;

            base.OnClosed(e);
            OverlayClosed?.Invoke(overlayKey);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
        }

        private class OverlayDisplayData
        {
            [JsonProperty("timeLabel")]
            public string TimeLabel { get; set; } = string.Empty;

            [JsonProperty("stageLabel")]
            public string StageLabel { get; set; } = string.Empty;

            [JsonProperty("badgeLabel")]
            public string BadgeLabel { get; set; } = string.Empty;

            [JsonProperty("homeTeam")]
            public string HomeTeam { get; set; } = string.Empty;

            [JsonProperty("awayTeam")]
            public string AwayTeam { get; set; } = string.Empty;

            [JsonProperty("homeLogo")]
            public string HomeLogo { get; set; } = string.Empty;

            [JsonProperty("homeHref")]
            public string HomeHref { get; set; } = string.Empty;

            [JsonProperty("matchHref")]
            public string MatchHref { get; set; } = string.Empty;

            [JsonProperty("awayHref")]
            public string AwayHref { get; set; } = string.Empty;

            [JsonProperty("awayLogo")]
            public string AwayLogo { get; set; } = string.Empty;

            [JsonProperty("homeScore")]
            public string HomeScore { get; set; } = string.Empty;

            [JsonProperty("awayScore")]
            public string AwayScore { get; set; } = string.Empty;

            [JsonProperty("hasScore")]
            public bool HasScore { get; set; }

            [JsonProperty("isLive")]
            public bool IsLive { get; set; }

            [JsonProperty("state")]
            public string State { get; set; } = "preview";
        }

        private class OverlayWebMessage
        {
            [JsonProperty("type")]
            public string? Type { get; set; }
            [JsonProperty("href")]
            public string? Href { get; set; }
            [JsonProperty("name")]
            public string? Name { get; set; }
        }
    }
}
