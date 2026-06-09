using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamSwitcher.Core
{
    public class SteamCEFInjector : IDisposable
    {
        private readonly GameAccountBinding _binding;
        private readonly AccountManager _accountManager;
        private int _debugPort;

        public bool IsConnected { get; private set; }
        public event EventHandler<string>? StatusChanged;

        public SteamCEFInjector(GameAccountBinding binding, AccountManager accountManager)
        {
            _binding = binding;
            _accountManager = accountManager;
        }

        public async Task<bool> InjectAsync(int wsPort)
        {
            try
            {
                StatusChanged?.Invoke(this, "正在检测Steam调试端口...");

                // 查找可用的调试端口
                bool found = false;
                for (int port = 8080; port <= 8090; port++)
                {
                    if (await CheckPortAsync(port))
                    {
                        _debugPort = port;
                        found = true;
                        StatusChanged?.Invoke(this, $"找到调试端口: {port}");
                        break;
                    }
                }

                if (!found)
                {
                    StatusChanged?.Invoke(this, "未找到调试端口，正在启动Steam调试模式...");
                    if (!StartSteamWithDebug())
                    {
                        StatusChanged?.Invoke(this, "无法启动Steam，请手动启动Steam后再试");
                        return false;
                    }
                    
                    StatusChanged?.Invoke(this, "等待Steam启动...");
                    await Task.Delay(8000);
                    
                    for (int port = 8080; port <= 8090; port++)
                    {
                        if (await CheckPortAsync(port))
                        {
                            _debugPort = port;
                            found = true;
                            StatusChanged?.Invoke(this, $"找到调试端口: {port}");
                            break;
                        }
                    }
                }

                if (!found)
                {
                    StatusChanged?.Invoke(this, "无法连接Steam调试端口，请确保Steam正在运行");
                    return false;
                }

                // 获取所有页面
                StatusChanged?.Invoke(this, "正在获取Steam页面...");
                JsonElement pages;
                try
                {
                    pages = await GetPagesAsync();
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, $"获取页面失败: {ex.Message}");
                    return false;
                }

                // 读取注入脚本
                var css = LoadResource("inject.css");
                var js = LoadResource("inject.js").Replace("__WS_PORT__", wsPort.ToString());

                if (string.IsNullOrEmpty(js))
                {
                    StatusChanged?.Invoke(this, "注入脚本为空");
                    return false;
                }

                // 注入到所有页面
                int injected = 0;
                int failed = 0;
                foreach (var page in pages.EnumerateArray())
                {
                    var wsUrl = "";
                    var title = "";
                    
                    try
                    {
                        wsUrl = page.GetProperty("webSocketDebuggerUrl").GetString() ?? "";
                        title = page.GetProperty("title").GetString() ?? "unknown";
                    }
                    catch { continue; }
                    
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        try
                        {
                            await InjectViaCDP(wsUrl, css, js);
                            injected++;
                            StatusChanged?.Invoke(this, $"已注入: {title}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            StatusChanged?.Invoke(this, $"注入失败 {title}: {ex.Message}");
                        }
                    }
                }

                IsConnected = injected > 0;
                StatusChanged?.Invoke(this, $"注入完成！成功: {injected}, 失败: {failed}");
                
                return injected > 0;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"错误: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> CheckPortAsync(int port)
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(1);
                var resp = await http.GetAsync($"http://localhost:{port}/json/version");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private bool StartSteamWithDebug()
        {
            var exe = _accountManager.GetSteamService().SteamExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            try
            {
                // 先关闭Steam
                foreach (var p in Process.GetProcessesByName("steam"))
                {
                    try { p.Kill(); } catch { }
                }

                // 带调试参数启动
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-cef-enable-debugging",
                    UseShellExecute = true
                });
                return true;
            }
            catch { return false; }
        }

        private async Task<JsonElement> GetPagesAsync()
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            var json = await http.GetStringAsync($"http://localhost:{_debugPort}/json");
            return JsonDocument.Parse(json).RootElement;
        }

        private async Task InjectViaCDP(string wsUrl, string css, string js)
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            if (ws.State != System.Net.WebSockets.WebSocketState.Open)
            {
                throw new Exception("WebSocket连接失败");
            }

            // 注入CSS
            if (!string.IsNullOrEmpty(css))
            {
                var cssCmd = JsonSerializer.Serialize(new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new
                    {
                        expression = "(function(){if(document.getElementById('ss-css'))return;var s=document.createElement('style');s.id='ss-css';s.textContent='" + css.Replace("'", "\\'").Replace("\n", " ") + "';document.head.appendChild(s)})()",
                        returnByValue = true
                    }
                });
                await SendCDP(ws, cssCmd, cts.Token);
            }

            // 注入JS
            var jsCmd = JsonSerializer.Serialize(new
            {
                id = 2,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = "(function(){if(window.__ss_loaded)return;window.__ss_loaded=true;" + js + "})()",
                    returnByValue = true
                }
            });
            await SendCDP(ws, jsCmd, cts.Token);

            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 
                    "", System.Threading.CancellationToken.None);
            }
        }

        private async Task SendCDP(System.Net.WebSockets.ClientWebSocket ws, string cmd, 
            System.Threading.CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(cmd);
            await ws.SendAsync(new ArraySegment<byte>(bytes), 
                System.Net.WebSockets.WebSocketMessageType.Text, true, ct);

            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            
            // 检查响应
            var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (response.Contains("\"error\""))
            {
                throw new Exception("CDP执行错误: " + response.Substring(0, Math.Min(200, response.Length)));
            }
        }

        private string LoadResource(string name)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "scripts", name);
            if (File.Exists(path))
                return File.ReadAllText(path, Encoding.UTF8);
            
            // 尝试从嵌入资源加载
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = $"SteamSwitcher.Resources.scripts.{name}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            
            return "";
        }

        public void Dispose()
        {
            IsConnected = false;
        }
    }
}
