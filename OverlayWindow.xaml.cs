using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Microsoft.Web.WebView2.Wpf;
using System.Collections.Generic;

namespace FlashscoreOverlay
{
    public partial class OverlayWindow : Window
    {
        private Dictionary<string, StackPanel> competitionGroups = new Dictionary<string, StackPanel>();

        public OverlayWindow()
        {
            InitializeComponent();
        }

        public void AddMatch(MatchData match, CompetitionData competition)
        {
            // Verificar si el partido ya existe
            foreach (var group in competitionGroups.Values)
            {
                var existingMatch = group.Children
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Tag?.ToString() == match.MatchId);
                
                if (existingMatch != null)
                    return;
            }

            // Buscar o crear grupo de competición
            if (!competitionGroups.ContainsKey(competition.CompetitionId ?? ""))
            {
                CreateCompetitionGroup(competition);
            }

            var matchPanel = CreateMatchPanel(match, competition);
            competitionGroups[competition.CompetitionId ?? ""].Children.Add(matchPanel);
        }

        public void RemoveMatch(string matchId)
        {
            foreach (var kvp in competitionGroups.ToList())
            {
                var group = kvp.Value;
                var matchToRemove = group.Children
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Tag?.ToString() == matchId);
                
                if (matchToRemove != null)
                {
                    group.Children.Remove(matchToRemove);
                    
                    // Si no quedan partidos en esta competición, eliminar el grupo completo
                    if (group.Children.Count == 0)
                    {
                        var competitionContainer = MatchesPanel.Children
                            .OfType<StackPanel>()
                            .FirstOrDefault(sp => sp.Tag?.ToString() == kvp.Key);
                        
                        if (competitionContainer != null)
                        {
                            MatchesPanel.Children.Remove(competitionContainer);
                        }
                        
                        competitionGroups.Remove(kvp.Key);
                    }
                    
                    break;
                }
            }

            if (competitionGroups.Count == 0)
            {
                Close();
            }
        }

        private void CreateCompetitionGroup(CompetitionData competition)
        {
            var competitionContainer = new StackPanel
            {
                Tag = competition.CompetitionId,
                Margin = new Thickness(0, 0, 0, 15)
            };

            // HEADER DE COMPETICIÓN
            var competitionHeader = new Grid
            {
                Margin = new Thickness(0, 0, 0, 5),
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26))
            };

            var competitionText = new TextBlock
            {
                Text = $"{competition.Category} - {competition.Title}",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 5, 8, 5)
            };
            competitionHeader.Children.Add(competitionText);
            competitionContainer.Children.Add(competitionHeader);

            // StackPanel para los partidos de esta competición
            var matchesStack = new StackPanel();
            competitionContainer.Children.Add(matchesStack);

            competitionGroups[competition.CompetitionId ?? ""] = matchesStack;
            MatchesPanel.Children.Add(competitionContainer);
        }

        private Border CreateMatchPanel(MatchData match, CompetitionData competition)
        {
            var border = new Border
            {
                Tag = match.MatchId,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = Cursors.Hand
            };

            // Click izquierdo para abrir el partido
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (!string.IsNullOrEmpty(match.Url))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = match.Url,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
                e.Handled = true;
            };
            
            // Click derecho para eliminar
            border.MouseRightButtonDown += (s, e) =>
            {
                RemoveMatch(match.MatchId ?? "");
                e.Handled = true;
            };

            // WebView2 para mostrar el HTML del partido
            if (!string.IsNullOrEmpty(match.Html))
            {
                var webView = new WebView2
                {
                    Height = 80,
                    Margin = new Thickness(0)
                };

                border.Child = webView;

                // Crear HTML completo con estilos de Flashscore
                var htmlContent = @"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }
        body {
            margin: 0;
            padding: 0;
            background-color: #000000;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            overflow: hidden;
        }
        .event__match {
            background-color: #000000;
            color: white;
            position: relative;
            display: grid !important;
            grid-template-columns: 60px 1fr 50px;
            grid-template-rows: 1fr 1fr;
            min-height: 55px;
            padding: 6px 10px;
            gap: 8px;
            align-items: center;
        }
        .event__time {
            grid-column: 1;
            grid-row: 1 / span 2;
            display: flex !important;
            align-items: center;
            justify-content: center;
            text-align: center;
            color: #ffffff !important;
            font-weight: 500;
            font-size: 12px !important;
            line-height: 1;
        }
        .event__match--live .event__time {
            color: #ff0046 !important;
        }
        .event__homeParticipant {
            grid-column: 2;
            grid-row: 1;
            align-self: center;
        }
        .event__awayParticipant {
            grid-column: 2;
            grid-row: 2;
            align-self: center;
        }
        .wcl-participant_bctDY {
            display: flex !important;
            align-items: center;
            padding: 0;
            margin: 0;
        }
        .wcl-participants_ASufu {
            display: flex !important;
            align-items: center;
            width: 100%;
        }
        .wcl-item_DKWjj {
            display: flex !important;
            align-items: center;
            gap: 8px;
            width: 100%;
        }
        .wcl-logo_UrSpU {
            width: 20px !important;
            height: 20px !important;
            object-fit: contain;
            flex-shrink: 0;
        }
        .wcl-name_jjfMf {
            color: #ffffff !important;
            font-size: 13px !important;
            font-weight: 400;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .wcl-matchRowScore_fWR-Z,
        .event__score {
            grid-column: 3;
            display: flex !important;
            align-items: center;
            justify-content: center;
            color: #ffffff !important;
            font-weight: bold;
            font-size: 15px !important;
            text-align: center;
        }
        .event__score--home,
        .wcl-matchRowScore_fWR-Z[data-side=""1""] {
            grid-row: 1;
            align-self: center;
        }
        .event__score--away,
        .wcl-matchRowScore_fWR-Z[data-side=""2""] {
            grid-row: 2;
            align-self: center;
        }
        .wcl-isPreMatch_FgNtO {
            color: #888888 !important;
        }
        .eventRowLink, .icon--preview, .event__icon, .liveBetWrapper, 
        .wcl-favorite_ggUc2, .fc-overlay-btn, 
        .wcl-badgePreview_cjWs9, .wcl-badgeLiveBet_8XqLc {
            display: none !important;
        }
    </style>
</head>
<body>
    " + match.Html + @"
    <script>
        (function() {
            var timeElements = document.querySelectorAll('.event__time');
            timeElements.forEach(function(el) {
                var text = el.textContent.trim();
                var isNumber = /^\d+$/.test(text) || text.includes('" + "'" + @"');
                if (isNumber) {
                    el.closest('.event__match').classList.add('event__match--live');
                }
            });
        })();
    </script>
</body>
</html>";

                // Inicializar WebView2 de forma asíncrona
                _ = InitializeWebView(webView, htmlContent);
            }

            return border;
        }

        private async System.Threading.Tasks.Task InitializeWebView(WebView2 webView, string htmlContent)
        {
            try
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.NavigateToString(htmlContent);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error inicializando WebView2: {ex.Message}");
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Permitir arrastrar desde cualquier parte
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
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
        
        [JsonProperty("html")]
        public string? Html { get; set; }
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
    }
}
