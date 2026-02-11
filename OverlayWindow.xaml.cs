using System;
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
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    link.Click += (s, e) =>
                    {
                        try
                        {
                            var url = competition?.HrefWithParam ?? competition?.Href;
                            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(tabId))
                            {
                                // Set a pending command for the original browser tab so it navigates in-place
                                var main = Application.Current?.MainWindow as MainWindow;
                                main?.SetPendingCommandForTab(tabId, new BrowserCommand { Action = "navigate", Href = url });
                            }
                            else if (!string.IsNullOrWhiteSpace(url))
                            {
                                // fallback: open externally
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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

            return new OverlayDisplayData
            {
                TimeLabel = string.IsNullOrWhiteSpace(timeLabel) ? "—" : timeLabel,
                StageLabel = stageLabel,
                BadgeLabel = badgeLabel,
                HomeTeam = matchData.HomeTeam ?? "Local",
                AwayTeam = matchData.AwayTeam ?? "Visitante",
                HomeLogo = matchData.HomeLogo ?? string.Empty,
                AwayLogo = matchData.AwayLogo ?? string.Empty,
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
                "preview" => "PREVIEW",
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

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var payload = JsonConvert.DeserializeObject<OverlayWebMessage>(e.WebMessageAsJson);
                if (payload == null) return;

                if (payload.Type == "close")
                {
                    Dispatcher.InvokeAsync(Close);
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
        }
    }
}
