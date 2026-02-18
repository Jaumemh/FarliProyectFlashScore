using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace FlashscoreOverlay
{
    public partial class MainWindow : Window
    {
        private HttpListener? httpListener;

        // Unified overlay instance
        private OverlayWindow? _unifiedOverlay;

        // Active matches: Key = OverlayId (or MatchId)
        private readonly Dictionary<string, MatchData> _activeMatches = new();
        private readonly Dictionary<string, CompetitionData> _activeCompetitions = new();

        // Track which browser tab controls which match
        private readonly Dictionary<string, string> _matchTabMap = new();

        private readonly Dictionary<string, BrowserCommand> pendingCommands = new();

        public MainWindow()
        {
            InitializeComponent();
            Hide();
            StartHttpServer();
        }

        private async void StartHttpServer()
        {
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://localhost:8080/");
                httpListener.Start();

                StatusText.Text = "🟢 Servidor activo";

                await Task.Run(async () =>
                {
                    while (httpListener.IsListening)
                    {
                        try
                        {
                            var context = await httpListener.GetContextAsync();
                            _ = Task.Run(() => ProcessRequest(context));
                        }
                        catch (HttpListenerException) { break; }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error en servidor: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al iniciar servidor:\n{ex.Message}\n\nAsegúrate de que el puerto 8080 no está en uso.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "⚠️ Error del servidor";
            }
        }

        private async void ProcessRequest(HttpListenerContext context)
        {
            string? requestBody = null;
            try
            {
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                context.Response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    var json = await reader.ReadToEndAsync();
                    requestBody = json;
                    var message = JsonConvert.DeserializeObject<BrowserMessage>(json);

                    await Dispatcher.InvokeAsync(() => HandleBrowserMessage(message));

                    var response = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                }

                // Support a simple GET endpoint to allow browser tabs to poll for commands
                if (context.Request.HttpMethod == "GET")
                {
                    var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                    if (path.StartsWith("/commands", StringComparison.OrdinalIgnoreCase))
                    {
                        var tabId = context.Request.QueryString["tabId"];
                        if (!string.IsNullOrWhiteSpace(tabId) && pendingCommands.TryGetValue(tabId, out var cmd))
                        {
                            var payload = JsonConvert.SerializeObject(cmd);
                            var buffer = Encoding.UTF8.GetBytes(payload);
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = buffer.Length;
                            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                            pendingCommands.Remove(tabId);
                            context.Response.Close();
                            return;
                        }
                        // empty response
                        var empty = Encoding.UTF8.GetBytes("{}");
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = empty.Length;
                        await context.Response.OutputStream.WriteAsync(empty, 0, empty.Length);
                        context.Response.Close();
                        return;
                    }
                }

                context.Response.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error procesando request: {ex}");
                LogServerError(ex, requestBody);
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private void LogServerError(Exception exception, string? requestBody)
        {
            try
            {
                var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "server-errors.log");
                var builder = new StringBuilder();
                builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}");
                if (!string.IsNullOrWhiteSpace(requestBody))
                {
                    builder.AppendLine("Request body:");
                    builder.AppendLine(requestBody);
                }
                builder.AppendLine(new string('-', 120));
                File.AppendAllText(logPath, builder.ToString());
            }
            catch
            {
                // Don't block the response if logging fails.
            }
        }

        private void HandleBrowserMessage(BrowserMessage? message)
        {
            if (message?.Action == null) return;

            switch (message.Action)
            {
                case "addMatch":
                    AddMatchToOverlay(message.Data);
                    break;
                case "updateMatch":
                    // Check if this is a quarter-score-only update (from match detail page)
                    if (message.Data?.Match != null
                        && !string.IsNullOrWhiteSpace(message.Data.Match.MatchMid)
                        && message.Data.Match.HomeQuarters != null
                        && message.Data.Match.HomeQuarters.Count > 0
                        && string.IsNullOrWhiteSpace(message.Data.Match.OverlayId)
                        && string.IsNullOrWhiteSpace(message.Data.Match.MatchId))
                    {
                        // Find existing match by MatchMid and merge quarters
                        var mid = message.Data.Match.MatchMid;
                        foreach (var kvp in _activeMatches)
                        {
                            if (kvp.Value.MatchMid == mid)
                            {
                                kvp.Value.HomeQuarters = message.Data.Match.HomeQuarters;
                                kvp.Value.AwayQuarters = message.Data.Match.AwayQuarters;
                                UpdateOverlayContent();
                                break;
                            }
                        }
                    }
                    else
                    {
                        AddMatchToOverlay(message.Data);
                    }
                    break;
                case "removeMatch":
                    RemoveMatchFromOverlay(message.Data);
                    break;
                case "showCompetitionLink":
                    ShowCompetitionLink(message.Data);
                    break;
                case "ping":
                    System.Diagnostics.Debug.WriteLine("Ping recibido del navegador");
                    break;
            }
        }

        private void ShowCompetitionLink(MessageData? data)
        {
            try
            {
                var href = data?.Competition?.HrefWithParam ?? data?.Competition?.Href;
                if (!string.IsNullOrWhiteSpace(href))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(href) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error abriendo link de competición: {ex.Message}");
            }
        }

        private void AddMatchToOverlay(MessageData? data)
        {
            if (data?.Match == null || string.IsNullOrWhiteSpace(data.Match.Url)) return;

            var id = data.Match.OverlayId;
            if (string.IsNullOrWhiteSpace(id)) id = data.Match.MatchId;
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");

            data.Match.OverlayId = id;
            data.Match.TabId = data.TabId;

            // Handle Competition Data
            if (data.Competition != null)
            {
                if (string.IsNullOrWhiteSpace(data.Competition.CompetitionId) && !string.IsNullOrWhiteSpace(data.Competition.Title))
                {
                    data.Competition.CompetitionId = $"{data.Competition.Category}:{data.Competition.Title}";
                }

                if (!string.IsNullOrWhiteSpace(data.Competition.CompetitionId))
                {
                    _activeCompetitions[data.Competition.CompetitionId] = data.Competition;
                    data.Match.CompetitionId = data.Competition.CompetitionId;
                }
            }

            if (!string.IsNullOrWhiteSpace(data.TabId))
            {
                _matchTabMap[id] = data.TabId!;
            }

            _activeMatches[id] = data.Match;

            EnsureOverlayOpen();
            UpdateOverlayContent();
            UpdateMatchCount();

            // Auto-fetch quarter scores in the background via hidden WebView2
            if (!string.IsNullOrWhiteSpace(data.Match.Url) && data.Match.Url.Contains("/partido/"))
            {
                _ = FetchQuartersInBackgroundAsync(id, data.Match.Url!);
            }
        }

        /// <summary>
        /// Fetches quarter scores using a hidden offscreen WebView2 window.
        /// Creates a tiny 1x1 Window positioned offscreen, with a WebView2 that
        /// loads the match detail page, waits for JS to render quarter data,
        /// extracts it, and cleans up. Completely invisible to the user.
        /// </summary>
        private async Task FetchQuartersInBackgroundAsync(string matchId, string matchUrl)
        {
            Window? fetchWindow = null;
            try
            {
                // Create a dedicated offscreen window for the WebView2
                var webView = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                fetchWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = -9999,
                    Top = -9999,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Opacity = 0,
                    Content = webView
                };
                fetchWindow.Show();

                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                var tcs = new TaskCompletionSource<bool>();
                var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                timeoutCts.Token.Register(() => tcs.TrySetResult(false));

                webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    if (!e.IsSuccess) { tcs.TrySetResult(false); return; }

                    // Poll for quarter data to appear (JS rendering takes time)
                    for (int attempt = 0; attempt < 30; attempt++)
                    {
                        await Task.Delay(500);
                        if (timeoutCts.IsCancellationRequested) break;

                        try
                        {
                            var jsResult = await webView.CoreWebView2.ExecuteScriptAsync(@"
                                (function() {
                                    var smh = document.querySelector('.smh__template');
                                    if (!smh) return JSON.stringify(null);
                                    var home = [], away = [];
                                    for (var i = 1; i <= 8; i++) {
                                        var h = smh.querySelector('.smh__part.smh__home.smh__part--' + i);
                                        var a = smh.querySelector('.smh__part.smh__away.smh__part--' + i);
                                        var hText = h ? h.textContent.trim() : '';
                                        var aText = a ? a.textContent.trim() : '';
                                        if (hText || aText) {
                                            home.push(hText);
                                            away.push(aText);
                                        } else break;
                                    }
                                    if (home.length === 0) return JSON.stringify(null);
                                    return JSON.stringify({ home: home, away: away });
                                })()
                            ");

                            if (!string.IsNullOrWhiteSpace(jsResult) && jsResult != "null" && jsResult != "\"null\"")
                            {
                                var unescaped = JsonConvert.DeserializeObject<string>(jsResult);
                                if (!string.IsNullOrWhiteSpace(unescaped))
                                {
                                    var quarterData = JsonConvert.DeserializeObject<QuarterResult>(unescaped);
                                    if (quarterData?.Home != null && quarterData.Home.Count > 0)
                                    {
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            if (_activeMatches.TryGetValue(matchId, out var existing))
                                            {
                                                existing.HomeQuarters = quarterData.Home;
                                                existing.AwayQuarters = quarterData.Away;
                                                UpdateOverlayContent();
                                                System.Diagnostics.Debug.WriteLine($"[QuarterFetch] OK: {quarterData.Home.Count} quarters for {matchId}");
                                            }
                                        });
                                        tcs.TrySetResult(true);
                                        return;
                                    }
                                }
                            }
                        }
                        catch { /* continue polling */ }
                    }

                    tcs.TrySetResult(false);
                };

                webView.CoreWebView2.Navigate(matchUrl);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuarterFetch] Error: {ex.Message}");
            }
            finally
            {
                try { fetchWindow?.Close(); } catch { }
            }
        }

        private void RemoveMatchFromOverlay(MessageData? data)
        {
            var id = data?.Match?.OverlayId ?? data?.Match?.MatchId;
            if (string.IsNullOrWhiteSpace(id)) return;

            if (_activeMatches.ContainsKey(id))
            {
                _activeMatches.Remove(id);
                UpdateOverlayContent();
                UpdateMatchCount();
            }
        }

        private void OnMatchRemovedFromOverlay(string matchId)
        {
            if (_activeMatches.ContainsKey(matchId))
            {
                _activeMatches.Remove(matchId);
                UpdateOverlayContent();
                UpdateMatchCount();

                if (_activeMatches.Count == 0)
                {
                    _unifiedOverlay?.Close();
                }
            }
        }

        private void EnsureOverlayOpen()
        {
            if (_unifiedOverlay == null || !_unifiedOverlay.IsLoaded)
            {
                _unifiedOverlay = new OverlayWindow();
                _unifiedOverlay.MatchRemoved += OnMatchRemovedFromOverlay;
                _unifiedOverlay.Closed += (s, e) =>
                {
                    _unifiedOverlay = null;
                    _activeMatches.Clear();
                    UpdateMatchCount();
                };
                _unifiedOverlay.Show();
            }
        }

        private void UpdateOverlayContent()
        {
            if (_unifiedOverlay != null && _unifiedOverlay.IsLoaded)
            {
                _unifiedOverlay.UpdateMatches(_activeMatches.Values, _activeCompetitions.Values);
            }
        }

        public void SetPendingCommandForTab(string tabId, BrowserCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(tabId) || cmd == null) return;
            pendingCommands[tabId] = cmd;
        }

        private void UpdateMatchCount()
        {
            MatchCount.Text = _activeMatches.Count.ToString();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            httpListener?.Stop();
            _unifiedOverlay?.Close();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            httpListener?.Stop();
            _unifiedOverlay?.Close();
            base.OnClosed(e);
        }
    }

    public class BrowserMessage
    {
        [JsonProperty("action")]
        public string? Action { get; set; }

        [JsonProperty("data")]
        public MessageData? Data { get; set; }
    }

    public class MessageData
    {
        [JsonProperty("match")]
        public MatchData? Match { get; set; }

        [JsonProperty("competition")]
        public CompetitionData? Competition { get; set; }

        [JsonProperty("tabId")]
        public string? TabId { get; set; }
    }

    public class MatchData
    {
        [JsonProperty("matchId")]
        public string? MatchId { get; set; }

        [JsonProperty("homeTeam")]
        public string? HomeTeam { get; set; }

        [JsonProperty("awayTeam")]
        public string? AwayTeam { get; set; }

        [JsonProperty("homeScore")]
        public string? HomeScore { get; set; }

        [JsonProperty("awayScore")]
        public string? AwayScore { get; set; }

        [JsonProperty("homeRedCards")]
        public int HomeRedCards { get; set; }

        [JsonProperty("awayRedCards")]
        public int AwayRedCards { get; set; }

        [JsonProperty("time")]
        public string? Time { get; set; }

        [JsonProperty("stage")]
        public string? Stage { get; set; }

        [JsonProperty("homeLogo")]
        public string? HomeLogo { get; set; }

        [JsonProperty("awayLogo")]
        public string? AwayLogo { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("homeHref")]
        public string? HomeHref { get; set; }

        [JsonProperty("awayHref")]
        public string? AwayHref { get; set; }

        [JsonProperty("html")]
        public string? Html { get; set; }

        [JsonProperty("overlayId")]
        public string? OverlayId { get; set; }

        [JsonProperty("matchMid")]
        public string? MatchMid { get; set; }

        [JsonProperty("competitionId")]
        public string? CompetitionId { get; set; }

        [JsonProperty("tabId")]
        public string? TabId { get; set; }

        [JsonProperty("homeQuarters")]
        public List<string>? HomeQuarters { get; set; }

        [JsonProperty("awayQuarters")]
        public List<string>? AwayQuarters { get; set; }
    }

    public class CompetitionData
    {
        [JsonProperty("competitionId")]
        public string? CompetitionId { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("category")]
        public string? Category { get; set; }

        [JsonProperty("logo")]
        public string? Logo { get; set; }

        [JsonProperty("href")]
        public string? Href { get; set; }

        [JsonProperty("hrefWithParam")]
        public string? HrefWithParam { get; set; }
    }

    public class BrowserCommand
    {
        [JsonProperty("action")]
        public string? Action { get; set; }

        [JsonProperty("href")]
        public string? Href { get; set; }
    }

    public class QuarterResult
    {
        [JsonProperty("home")]
        public List<string>? Home { get; set; }

        [JsonProperty("away")]
        public List<string>? Away { get; set; }
    }
}