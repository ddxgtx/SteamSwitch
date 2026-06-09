using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace SteamSwitcher.Core
{
    public class WebSocketServer : IDisposable
    {
        private TcpListener? _listener;
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

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                IsRunning = true;
                _ = ListenAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法启动服务器(端口{_port}): {ex.Message}", ex);
            }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"Listen error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            WebSocket? ws = null;
            string clientId = Guid.NewGuid().ToString();

            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                
                // 读取HTTP升级请求
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (request.Contains("Upgrade: websocket"))
                {
                    // 完成WebSocket握手
                    var response = CreateWebSocketAcceptResponse(request);
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, ct);

                    // 创建WebSocket
                    ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromMinutes(30));
                    _clients.TryAdd(clientId, ws);
                    ClientConnected?.Invoke(this, clientId);

                    // 接收消息
                    var msgBuffer = new byte[4096];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(msgBuffer), ct);
                        
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var message = Encoding.UTF8.GetString(msgBuffer, 0, result.Count);
                            ProcessMessage(message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                ClientDisconnected?.Invoke(this, clientId);
                ws?.Dispose();
                client.Dispose();
            }
        }

        private string CreateWebSocketAcceptResponse(string request)
        {
            // 从请求中提取Sec-WebSocket-Key
            var keyMatch = System.Text.RegularExpressions.Regex.Match(
                request, @"Sec-WebSocket-Key:\s*(\S+)");
            
            var key = keyMatch.Groups[1].Value.Trim();
            var acceptKey = Convert.ToBase64String(
                System.Security.Cryptography.SHA1.Create().ComputeHash(
                    Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            return $"HTTP/1.1 101 Switching Protocols\r\n" +
                   $"Upgrade: websocket\r\n" +
                   $"Connection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
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
