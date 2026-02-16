using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
        
        // Used to track which browser tab controls which match, if needed for direct navigation/unchecking
        // Key: MatchId, Value: TabId
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
                    while (httpListener != null && httpListener.IsListening)
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
                            // remove after delivering
                            pendingCommands.Remove(tabId);
                            context.Response.Close();
                            return;
                        }
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
            catch { }
        }

        private void HandleBrowserMessage(BrowserMessage? message)
        {
            if (message?.Action == null) return;

            switch (message.Action)
            {
                case "addMatch":
                    AddMatchToOverlay(message.Data);
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

            // Robust ID generation
            var id = data.Match.OverlayId;
            if (string.IsNullOrWhiteSpace(id)) id = data.Match.MatchId;
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
            
            data.Match.OverlayId = id;
            data.Match.TabId = data.TabId;

            // Handle Competition Data
            if (data.Competition != null)
            {
                // If the competition has no ID but has a title, generate one
                if (string.IsNullOrWhiteSpace(data.Competition.CompetitionId) && !string.IsNullOrWhiteSpace(data.Competition.Title))
                {
                    data.Competition.CompetitionId = $"{data.Competition.Category}-{data.Competition.Title}";
                }

                if (!string.IsNullOrWhiteSpace(data.Competition.CompetitionId))
                {
                    _activeCompetitions[data.Competition.CompetitionId] = data.Competition;
                    data.Match.CompetitionId = data.Competition.CompetitionId;
                }
            }
            // If we still don't have a competition ID on the match, try to infer it or leave null (will be "default")
            
            if (!string.IsNullOrWhiteSpace(data.TabId))
            {
                _matchTabMap[id] = data.TabId!;
            }

            _activeMatches[id] = data.Match;

            EnsureOverlayOpen();
            UpdateOverlayContent();
            UpdateMatchCount();
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

        private void EnsureOverlayOpen()
        {
            if (_unifiedOverlay == null || !_unifiedOverlay.IsLoaded)
            {
                _unifiedOverlay = new OverlayWindow();
                _unifiedOverlay.MatchRemoved += OnMatchRemovedFromOverlayWindow;
                _unifiedOverlay.Closed += (s, e) => { 
                    _unifiedOverlay = null; 
                    _activeMatches.Clear(); 
                    UpdateMatchCount();
                };
                _unifiedOverlay.Show();
            }
        }

        private void OnMatchRemovedFromOverlayWindow(string matchId)
        {
            if (_activeMatches.ContainsKey(matchId))
            {
                _activeMatches.Remove(matchId);
                UpdateOverlayContent();
                UpdateMatchCount();

                // Send 'uncheck' command to browser
                if (_matchTabMap.TryGetValue(matchId, out var tabId))
                {
                    SetPendingCommandForTab(tabId, new BrowserCommand { Action = "uncheck", MatchId = matchId });
                }
            }
        }

        private void UpdateOverlayContent()
        {
            if (_unifiedOverlay != null && _unifiedOverlay.IsLoaded)
            {
                _unifiedOverlay.UpdateMatches(_activeMatches.Values, _activeCompetitions.Values);
                
                // If no matches left, close the overlay? Or keep it open empty?
                // User requirement implies "toggle", so if empty maybe just show empty.
                // But usually overlays close if empty. Let's keep it open for now or close if empty?
                // Let's close if count is 0 to be clean.
                if (_activeMatches.Count == 0)
                {
                    _unifiedOverlay.Close();
                }
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

        [JsonProperty("htmlSource")]
        public string? HtmlSource { get; set; }

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

        [JsonProperty("htmlSource")]
        public string? HtmlSource { get; set; }
        
        [JsonProperty("hrefWithParam")]
        public string? HrefWithParam { get; set; }
    }

    public class BrowserCommand
    {
        [JsonProperty("action")]
        public string? Action { get; set; }

        [JsonProperty("href")]
        public string? Href { get; set; }
        
        [JsonProperty("matchId")]
        public string? MatchId { get; set; }
    }
}
