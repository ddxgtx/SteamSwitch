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
                        break;
                    }
                }

                if (!found)
                {
                    StatusChanged?.Invoke(this, "正在启动Steam调试模式...");
                    if (!StartSteamWithDebug())
                    {
                        StatusChanged?.Invoke(this, "无法启动Steam");
                        return false;
                    }
                    
                    await Task.Delay(6000);
                    
                    for (int port = 8080; port <= 8090; port++)
                    {
                        if (await CheckPortAsync(port))
                        {
                            _debugPort = port;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    StatusChanged?.Invoke(this, "无法连接Steam调试端口");
                    return false;
                }

                StatusChanged?.Invoke(this, $"已连接端口: {_debugPort}，正在注入...");

                // 获取所有页面
                JsonElement pages;
                try
                {
                    pages = await GetPagesAsync();
                }
                catch
                {
                    StatusChanged?.Invoke(this, "未找到Steam页面");
                    return false;
                }

                // 读取注入脚本
                var css = LoadResource("inject.css");
                var js = LoadResource("inject.js").Replace("__WS_PORT__", wsPort.ToString());

                // 注入到所有页面
                int injected = 0;
                foreach (var page in pages.EnumerateArray())
                {
                    var wsUrl = page.GetProperty("webSocketDebuggerUrl").GetString();
                    var title = page.GetProperty("title").GetString();
                    
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        try
                        {
                            await InjectViaCDP(wsUrl, css, js);
                            injected++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"注入失败 {title}: {ex.Message}");
                        }
                    }
                }

                IsConnected = injected > 0;
                StatusChanged?.Invoke(this, injected > 0 
                    ? $"注入完成！{injected} 个页面" 
                    : "注入失败");
                
                return injected > 0;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"错误: {ex.Message}");
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
            await ws.ConnectAsync(new Uri(wsUrl), System.Threading.CancellationToken.None);

            // 注入CSS
            var cssCmd = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = $@"(function() {{
                        if (document.getElementById('ss-css')) return;
                        var s = document.createElement('style');
                        s.id = 'ss-css';
                        s.textContent = {JsonSerializer.Serialize(css)};
                        document.head.appendChild(s);
                    }})()",
                    returnByValue = true
                }
            });
            await SendCDP(ws, cssCmd);

            // 注入JS
            var jsCmd = JsonSerializer.Serialize(new
            {
                id = 2,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = $@"(function() {{
                        if (window.__ss_loaded) return;
                        window.__ss_loaded = true;
                        {js}
                    }})()",
                    returnByValue = true
                }
            });
            await SendCDP(ws, jsCmd);

            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 
                "", System.Threading.CancellationToken.None);
        }

        private async Task SendCDP(System.Net.WebSockets.ClientWebSocket ws, string cmd)
        {
            var bytes = Encoding.UTF8.GetBytes(cmd);
            await ws.SendAsync(new ArraySegment<byte>(bytes), 
                System.Net.WebSockets.WebSocketMessageType.Text, true, 
                System.Threading.CancellationToken.None);

            var buffer = new byte[8192];
            await ws.ReceiveAsync(new ArraySegment<byte>(buffer), 
                System.Threading.CancellationToken.None);
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
