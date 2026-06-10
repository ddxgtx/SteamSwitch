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
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inject_log.txt");
                File.WriteAllText(logPath, $"=== Steam Switch 注入日志 {DateTime.Now} ==={Environment.NewLine}");
                File.AppendAllText(logPath, $"调试端口: {_debugPort}{Environment.NewLine}");
                File.AppendAllText(logPath, $"WebSocket端口: {wsPort}{Environment.NewLine}");
                File.AppendAllText(logPath, $"CSS长度: {css?.Length ?? 0}{Environment.NewLine}");
                File.AppendAllText(logPath, $"JS长度: {js?.Length ?? 0}{Environment.NewLine}{Environment.NewLine}");

                foreach (var page in pages.EnumerateArray())
                {
                    var wsUrl = "";
                    var title = "";
                    var url = "";
                    
                    try
                    {
                        wsUrl = page.GetProperty("webSocketDebuggerUrl").GetString() ?? "";
                        title = page.GetProperty("title").GetString() ?? "unknown";
                        url = page.GetProperty("url").GetString() ?? "";
                    }
                    catch { continue; }
                    
                    File.AppendAllText(logPath, $"页面: {title} | URL: {url}{Environment.NewLine}");
                    File.AppendAllText(logPath, $"  WebSocket: {wsUrl}{Environment.NewLine}");
                    
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        try
                        {
                            await InjectViaCDP(wsUrl, css, js);
                            injected++;
                            StatusChanged?.Invoke(this, $"已注入: {title}");
                            File.AppendAllText(logPath, $"  结果: 成功{Environment.NewLine}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            StatusChanged?.Invoke(this, $"注入失败 {title}: {ex.Message}");
                            File.AppendAllText(logPath, $"  结果: 失败 - {ex.Message}{Environment.NewLine}");
                        }
                    }
                    else
                    {
                        File.AppendAllText(logPath, $"  结果: 跳过(无WebSocket){Environment.NewLine}");
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
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"http://localhost:{port}/json/version");
                
                // 记录结果到日志
                var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] CheckPort {port}: StatusCode={resp.StatusCode}{Environment.NewLine}");
                
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] CheckPort {port}: Failed={ex.Message}{Environment.NewLine}");
                return false;
            }
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
            http.Timeout = TimeSpan.FromSeconds(10);
            var url = $"http://localhost:{_debugPort}/json";
            var json = await http.GetStringAsync(url);
            
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] GetPages from {url}: {json.Length} chars{Environment.NewLine}");
            
            return JsonDocument.Parse(json).RootElement;
        }

        private async Task InjectViaCDP(string wsUrl, string css, string js)
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"[InjectViaCDP] 开始注入: {wsUrl}");

            using var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            
            log.AppendLine("[InjectViaCDP] 正在连接WebSocket...");
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            log.AppendLine($"[InjectViaCDP] WebSocket状态: {ws.State}");

            if (ws.State != System.Net.WebSockets.WebSocketState.Open)
            {
                var err = log.ToString();
                System.Diagnostics.Debug.WriteLine(err);
                throw new Exception($"WebSocket连接失败，状态: {ws.State}");
            }

            // 测试连接
            log.AppendLine("[InjectViaCDP] 测试CDP连接...");
            var testCmd = JsonSerializer.Serialize(new
            {
                id = 99,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = "document.title",
                    returnByValue = true
                }
            });
            var testResult = await SendCDP(ws, testCmd, cts.Token);
            log.AppendLine($"[InjectViaCDP] 测试结果: {testResult}");

            // 注入CSS
            if (!string.IsNullOrEmpty(css))
            {
                log.AppendLine("[InjectViaCDP] 注入CSS...");
                var cssScript = "(function(){try{if(document.getElementById('ss-css'))return 'skip';var s=document.createElement('style');s.id='ss-css';s.textContent='" + EscapeJs(css) + "';document.head.appendChild(s);return 'ok'}catch(e){return 'err:'+e.message}})()";
                
                var cssCmd = JsonSerializer.Serialize(new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new
                    {
                        expression = cssScript,
                        returnByValue = true
                    }
                });
                var cssResult = await SendCDP(ws, cssCmd, cts.Token);
                log.AppendLine($"[InjectViaCDP] CSS结果: {cssResult}");
            }

            // 注入JS
            log.AppendLine("[InjectViaCDP] 注入JS...");
            var jsScript = "(function(){try{if(window.__ss_loaded)return 'skip';window.__ss_loaded=true;" + js + ";return 'ok'}catch(e){return 'err:'+e.message}})()";
            
            var jsCmd = JsonSerializer.Serialize(new
            {
                id = 2,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsScript,
                    returnByValue = true
                }
            });
            var jsResult = await SendCDP(ws, jsCmd, cts.Token);
            log.AppendLine($"[InjectViaCDP] JS结果: {jsResult}");

            // 关闭连接
            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 
                    "", System.Threading.CancellationToken.None);
            }

            log.AppendLine("[InjectViaCDP] 注入完成");
            System.Diagnostics.Debug.WriteLine(log.ToString());
            
            // 写入日志文件
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inject_log.txt");
                File.AppendAllText(logPath, log.ToString() + Environment.NewLine);
            }
            catch { }
        }

        private string EscapeJs(string js)
        {
            return js.Replace("\\", "\\\\")
                     .Replace("'", "\\'")
                     .Replace("\n", "\\n")
                     .Replace("\r", "\\r")
                     .Replace("\t", "\\t");
        }

        private async Task<string> SendCDP(System.Net.WebSockets.ClientWebSocket ws, string cmd, 
            System.Threading.CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(cmd);
            await ws.SendAsync(new ArraySegment<byte>(bytes), 
                System.Net.WebSockets.WebSocketMessageType.Text, true, ct);

            var buffer = new byte[16384];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            
            System.Diagnostics.Debug.WriteLine($"[CDP Response] {response}");
            return response;
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
