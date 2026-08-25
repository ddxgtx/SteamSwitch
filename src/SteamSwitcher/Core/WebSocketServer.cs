using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
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
        private readonly string _accessToken;
        private const int MaxClients = 4;
        private const int MaxMessageBytes = 1024 * 1024;

        public event EventHandler<(string action, JsonElement data)>? MessageReceived;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning { get; private set; }
        public int ClientCount => _clients.Count;

        public WebSocketServer(int port, string accessToken)
        {
            _port = port;
            _accessToken = !string.IsNullOrWhiteSpace(accessToken)
                ? accessToken
                : throw new ArgumentException("WebSocket access token is required.", nameof(accessToken));
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

                    if (!IsAuthorized(context.Request))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        context.Response.Close();
                    }
                    else if (_clients.Count >= MaxClients)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                        context.Response.Close();
                    }
                    else if (context.Request.IsWebSocketRequest)
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

                var cancellationToken = _cts?.Token ?? CancellationToken.None;
                while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                        if (ms.Length > MaxMessageBytes)
                        {
                            await ws.CloseAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Message exceeds 1 MB limit.",
                                cancellationToken);
                            break;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                    }
                    else if (ms.Length > 0)
                    {
                        var message = Encoding.UTF8.GetString(ms.ToArray());
                        ProcessMessage(message);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
            {
                // Expected while the local control channel is shutting down.
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
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("action", out var actionElement))
                {
                    var action = actionElement.GetString() ?? "";
                    AppLogger.Info($"WebSocket action received: {action}, bytes={Encoding.UTF8.GetByteCount(message)}");
                    var dataCopy = root.Clone();
                    MessageReceived?.Invoke(this, (action, dataCopy));
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
                        await client.Value.SendAsync(
                            segment,
                            WebSocketMessageType.Text,
                            true,
                            _cts?.Token ?? CancellationToken.None);
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

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var suppliedToken = request.QueryString["token"];
            if (string.IsNullOrEmpty(suppliedToken))
                return false;

            var expectedBytes = Encoding.UTF8.GetBytes(_accessToken);
            var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
            return expectedBytes.Length == suppliedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        public void Stop()
        {
            _cts?.Cancel();
            var listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try { listener.Stop(); } catch (ObjectDisposedException) { }
                try { listener.Close(); } catch (ObjectDisposedException) { }
            }
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
