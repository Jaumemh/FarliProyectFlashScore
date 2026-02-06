using System;
using System.Collections.Generic;
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
        private OverlayWindow? overlayWindow = null;
        private int totalMatches = 0;

        public MainWindow()
        {
            InitializeComponent();
            this.Hide(); // Ocultar ventana al inicio
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
                StatusText.Text = "🔴 Error del servidor";
            }
        }

        private async void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                // Configurar CORS
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
                    using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    var json = await reader.ReadToEndAsync();
                    
                    var message = JsonConvert.DeserializeObject<BrowserMessage>(json);
                    
                    await Dispatcher.InvokeAsync(() => HandleBrowserMessage(message));

                    var response = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                }

                context.Response.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error procesando request: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private void HandleBrowserMessage(BrowserMessage? message)
        {
            if (message == null) return;

            switch (message.Action)
            {
                case "addMatch":
                    AddMatchToOverlay(message.Data);
                    break;
                case "removeMatch":
                    RemoveMatchFromOverlay(message.Data);
                    break;
                case "ping":
                    System.Diagnostics.Debug.WriteLine("Ping recibido del navegador");
                    break;
            }

            UpdateMatchCount();
        }

        private void AddMatchToOverlay(MessageData? data)
        {
            if (data?.Match == null || data?.Competition == null) return;

            // Crear la ventana overlay si no existe
            if (overlayWindow == null || !overlayWindow.IsLoaded)
            {
                overlayWindow = new OverlayWindow();
                overlayWindow.Closed += (s, e) => 
                {
                    overlayWindow = null;
                    totalMatches = 0;
                    UpdateMatchCount();
                };
                overlayWindow.Show();
            }

            overlayWindow.AddMatch(data.Match, data.Competition);
            totalMatches++;
        }

        private void RemoveMatchFromOverlay(MessageData? data)
        {
            if (data?.Match == null) return;

            if (overlayWindow != null && overlayWindow.IsLoaded)
            {
                overlayWindow.RemoveMatch(data.Match.MatchId ?? "");
                totalMatches = Math.Max(0, totalMatches - 1);
            }
        }

        private void UpdateMatchCount()
        {
            MatchCount.Text = totalMatches.ToString();
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
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            httpListener?.Stop();
            if (overlayWindow != null && overlayWindow.IsLoaded)
            {
                overlayWindow.Close();
            }
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
    }
}
