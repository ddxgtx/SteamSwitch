using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

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

        public async Task StartAsync()
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");

            try
            {
                _listener.Start();
                IsRunning = true;
                _ = ListenAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法启动WebSocket服务器: {ex.Message}", ex);
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
                        // 处理普通HTTP请求（用于健康检查）
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
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
            ClientConnected?.Invoke(this, clientId);

            try
            {
                var buffer = new byte[4096];

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
            catch { }
            finally
            {
                _clients.TryRemove(clientId, out _);
                ClientDisconnected?.Invoke(this, clientId);
                ws.Dispose();
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("action", out var actionElement))
                {
                    var action = actionElement.GetString() ?? "";
                    MessageReceived?.Invoke(this, (action, root));
                }
            }
            catch (Exception ex)
            {
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
                catch { }
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
