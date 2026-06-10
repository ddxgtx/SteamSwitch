using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class WebSocketServer : IDisposable
    {
        private HttpListener? _listener;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private CancellationTokenSource? _cts;
        private readonly int _port;

        public event EventHandler<(string action, JsonElement data)>? MessageReceived;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning { get; private set; }
        public int ClientCount => _clients.Count;

        public WebSocketServer(int port = 8081)
        {
            _port = port;
        }

        public Task StartAsync()
        {
            if (IsRunning) return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");

            try
            {
                _listener.Start();
                IsRunning = true;
                AppLogger.Info($"WebSocketServer started on port {_port}.");
                _ = ListenAsync(_cts.Token);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"WebSocketServer failed to start on port {_port}.", ex);
                throw new InvalidOperationException($"无法启动 WebSocket 服务: {ex.Message}", ex);
            }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var context = await _listener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleWebSocketAsync(context);
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    AppLogger.Error("WebSocket listen error.", ex);
                    System.Diagnostics.Debug.WriteLine($"WebSocket listen error: {ex.Message}");
                }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;
            var clientId = Guid.NewGuid().ToString();

            _clients.TryAdd(clientId, ws);
            AppLogger.Info($"WebSocket client connected: {clientId}");
            ClientConnected?.Invoke(this, clientId);

            try
            {
                var buffer = new byte[16384];

                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    else
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessMessage(message);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"WebSocket client handler failed: {clientId}", ex);
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                AppLogger.Info($"WebSocket client disconnected: {clientId}");
                ClientDisconnected?.Invoke(this, clientId);
                ws.Dispose();
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                AppLogger.Info($"WebSocket message: {message}");
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("action", out var actionElement))
                {
                    var action = actionElement.GetString() ?? "";
                    MessageReceived?.Invoke(this, (action, root));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("ProcessMessage error.", ex);
                System.Diagnostics.Debug.WriteLine($"ProcessMessage error: {ex.Message}");
            }
        }

        public async Task BroadcastAsync(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(data);

            foreach (var client in _clients)
            {
                try
                {
                    if (client.Value.State == WebSocketState.Open)
                    {
                        await client.Value.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Broadcast failed for client {client.Key}.", ex);
                }
            }
        }

        public async Task SendToAllAsync(string action, object data)
        {
            var message = JsonSerializer.Serialize(new { action, data });
            await BroadcastAsync(message);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            IsRunning = false;
            AppLogger.Info($"WebSocketServer stopped on port {_port}.");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();

            foreach (var client in _clients)
            {
                client.Value.Dispose();
            }
            _clients.Clear();
        }
    }
}
