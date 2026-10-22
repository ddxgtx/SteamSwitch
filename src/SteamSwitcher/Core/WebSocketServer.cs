using System;
using System.Collections.Concurrent;
using System.Linq;
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
        private readonly ConcurrentDictionary<string, Task> _clientTasks = new();
        private readonly SemaphoreSlim _clientSlots = new(MaxClients, MaxClients);
        private readonly SemaphoreSlim _broadcastGate = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
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
                _listenerTask = ListenAsync(_cts.Token);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                // Roll back the half-initialized listener so a later StartAsync can retry
                // cleanly instead of leaking an open HttpListener / CTS.
                try { _listener?.Stop(); } catch (ObjectDisposedException) { }
                try { _listener?.Close(); } catch (ObjectDisposedException) { }
                _listener = null;
                _cts?.Dispose();
                _cts = null;
                IsRunning = false;
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
                    else if (context.Request.IsWebSocketRequest)
                    {
                        if (!_clientSlots.Wait(0))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                            context.Response.Close();
                            continue;
                        }

                        var operationId = Guid.NewGuid().ToString("N");
                        var clientTask = HandleWebSocketAsync(context, operationId);
                        _clientTasks[operationId] = clientTask;
                        _ = clientTask.ContinueWith(
                            completed =>
                            {
                                _clientTasks.TryRemove(operationId, out _);
                                if (completed.IsFaulted)
                                    AppLogger.Error($"WebSocket client task failed: {operationId}", completed.Exception);
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("WebSocket listen error.", ex);
                    System.Diagnostics.Debug.WriteLine($"WebSocket listen error: {ex.Message}");
                }
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, string operationId)
        {
            WebSocket? ws = null;
            var clientId = operationId;

            try
            {
                // Echo the auth subprotocol when present so browsers that require
                // protocol negotiation complete the handshake cleanly.
                string? selectedProtocol = null;
                if (context.Request.IsWebSocketRequest)
                {
                    var header = context.Request.Headers[SecWebSocketProtocolHeader];
                    if (!string.IsNullOrEmpty(header))
                    {
                        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (raw.StartsWith(AuthProtocolPrefix, StringComparison.Ordinal))
                            {
                                selectedProtocol = raw;
                                break;
                            }
                        }
                    }
                }

                var wsContext = await context.AcceptWebSocketAsync(selectedProtocol);
                ws = wsContext.WebSocket;
                if (!_clients.TryAdd(clientId, ws))
                    throw new InvalidOperationException("Unable to register WebSocket client.");

                AppLogger.Info($"WebSocket client connected: {clientId}");
                ClientConnected?.Invoke(this, clientId);
                var buffer = new byte[16384];

                var cancellationToken = _cts?.Token ?? CancellationToken.None;
                while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    var tooBig = false;
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
                            tooBig = true;
                            await ws.CloseAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Message exceeds 1 MB limit.",
                                cancellationToken);
                            break;
                        }
                    }
                    while (!result.EndOfMessage);

                    // Oversized payloads must never reach ProcessMessage; leave the
                    // receive loop entirely so the finally block releases the slot.
                    if (tooBig)
                        break;

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
                if (_clients.TryRemove(clientId, out _))
                {
                    AppLogger.Info($"WebSocket client disconnected: {clientId}");
                    ClientDisconnected?.Invoke(this, clientId);
                }
                ws?.Dispose();
                _clientSlots.Release();
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
            await _broadcastGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
            try
            {
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
                    catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Broadcast failed for client {client.Key}.", ex);
                    }
                }
            }
            finally
            {
                _broadcastGate.Release();
            }
        }

        public async Task SendToAllAsync(string action, object data)
        {
            var message = JsonSerializer.Serialize(new { action, data });
            await BroadcastAsync(message);
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            // Prefer the WebSocket subprotocol so the token never appears in the
            // URL (keeps it out of CEF/network access logs). Query string remains
            // as a fallback for older injected scripts.
            var suppliedToken = ExtractSubProtocolToken(request) ?? request.QueryString["token"];
            if (string.IsNullOrEmpty(suppliedToken))
                return false;

            var expectedBytes = Encoding.UTF8.GetBytes(_accessToken);
            var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
            return expectedBytes.Length == suppliedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        private const string AuthProtocolPrefix = "ss.auth.";
        private const string SecWebSocketProtocolHeader = "Sec-WebSocket-Protocol";

        private static string? ExtractSubProtocolToken(HttpListenerRequest request)
        {
            try
            {
                if (!request.IsWebSocketRequest)
                    return null;

                var header = request.Headers[SecWebSocketProtocolHeader];
                if (string.IsNullOrEmpty(header))
                    return null;

                foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (raw.StartsWith(AuthProtocolPrefix, StringComparison.Ordinal))
                        return raw[AuthProtocolPrefix.Length..];
                }
            }
            catch
            {
                // Header may be absent or malformed; fall through to query fallback.
            }
            return null;
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
            foreach (var client in _clients.Values)
            {
                try { client.Abort(); } catch (ObjectDisposedException) { }
            }
            IsRunning = false;
            AppLogger.Info($"WebSocketServer stopped on port {_port}.");
        }

        public void Dispose()
        {
            Stop();
            try { _listenerTask?.Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException ex) { AppLogger.Warn($"WebSocket listener stopped with an error: {ex.GetBaseException().Message}"); }
            try { Task.WaitAll(_clientTasks.Values.ToArray(), TimeSpan.FromSeconds(1)); }
            catch (AggregateException ex) { AppLogger.Warn($"WebSocket clients stopped with an error: {ex.GetBaseException().Message}"); }
            _cts?.Dispose();

            foreach (var client in _clients)
            {
                client.Value.Dispose();
            }
            _clients.Clear();

            // The semaphores are created once in the constructor and never released
            // otherwise; dispose them here so their wait handles are not leaked.
            _clientSlots.Dispose();
            _broadcastGate.Dispose();
        }
    }
}
