using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamSwitcher.Core
{
    public class SteamCEFInjector : IDisposable
    {
        private readonly int _debugPort;
        private readonly GameAccountBinding _binding;
        private readonly AccountManager _accountManager;
        private bool _isConnected;
        private System.Threading.Timer? _checkTimer;

        public bool IsConnected => _isConnected;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<(int appId, string gameName, string steamId, string accountName)>? GamePlayRequested;

        public SteamCEFInjector(GameAccountBinding binding, AccountManager accountManager, int debugPort = 8080)
        {
            _binding = binding;
            _accountManager = accountManager;
            _debugPort = debugPort;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                StatusChanged?.Invoke(this, "正在连接Steam...");

                // 检查调试端口是否可用
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(3);
                
                var response = await http.GetAsync($"http://localhost:{_debugPort}/json/version");
                if (!response.IsSuccessStatusCode)
                {
                    StatusChanged?.Invoke(this, "Steam调试端口未开放");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out _))
                {
                    _isConnected = true;
                    StatusChanged?.Invoke(this, "已连接到Steam CEF");
                    
                    // 注入JS到Steam的steamloopback.host
                    await InjectScriptAsync();
                    
                    return true;
                }

                StatusChanged?.Invoke(this, "无法获取WebSocket地址");
                return false;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"连接失败: {ex.Message}");
                return false;
            }
        }

        private async Task InjectScriptAsync()
        {
            try
            {
                // 创建注入脚本到Steam的steamui目录
                var steamPath = _accountManager.GetSteamService().SteamPath;
                if (string.IsNullOrEmpty(steamPath)) return;

                var steamuiPath = Path.Combine(steamPath, "steamui");
                var jsPath = Path.Combine(steamuiPath, "steamswitch.inject.js");
                var cssPath = Path.Combine(steamuiPath, "steamswitch.inject.css");

                // 写入CSS
                var css = @"
.steamswitch-btn {
    background: linear-gradient(135deg, #0A84FF, #0066CC);
    color: white;
    border: none;
    border-radius: 4px;
    padding: 8px 16px;
    font-size: 14px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    margin-left: 8px;
    transition: all 0.2s;
}
.steamswitch-btn:hover {
    background: linear-gradient(135deg, #409CFF, #0A84FF);
    transform: translateY(-1px);
}
.steamswitch-badge {
    background: #30D158;
    color: white;
    font-size: 11px;
    padding: 2px 6px;
    border-radius: 4px;
    margin-left: 4px;
}
";
                await File.WriteAllTextAsync(cssPath, css);

                // 写入JS
                var js = $@"
(function() {{
    if (window.__steamswitch_injected) return;
    window.__steamswitch_injected = true;

    const WS_URL = 'ws://localhost:8081';
    let ws = null;

    function connectWS() {{
        try {{
            ws = new WebSocket(WS_URL);
            ws.onopen = function() {{
                console.log('[SteamSwitch] Connected');
            }};
            ws.onmessage = function(e) {{
                console.log('[SteamSwitch] Received:', e.data);
                const msg = JSON.parse(e.data);
                if (msg.action === 'refreshBindings') {{
                    loadBindings();
                }}
            }};
            ws.onclose = function() {{
                setTimeout(connectWS, 3000);
            }};
        }} catch(e) {{
            setTimeout(connectWS, 3000);
        }}
    }}

    function sendToApp(action, data) {{
        if (ws && ws.readyState === WebSocket.OPEN) {{
            ws.send(JSON.stringify({{ action, ...data }}));
        }}
    }}

    // 监听游戏详情页
    function injectGameDetail() {{
        const startBtn = document.querySelector('[class*=""AppStateButton""]') || 
                        document.querySelector('[class*=""LaunchButton""]') ||
                        document.querySelector('button[class*=""Play""]');
        
        if (startBtn && !document.getElementById('steamswitch-launch-btn')) {{
            const btn = document.createElement('button');
            btn.id = 'steamswitch-launch-btn';
            btn.className = 'steamswitch-btn';
            btn.innerHTML = '⚡ 切换账号启动';
            
            btn.addEventListener('click', function() {{
                const url = window.location.href;
                const appMatch = url.match(/app\/(\d+)/);
                if (appMatch) {{
                    const appId = appMatch[1];
                    const gameName = document.title || 'Unknown Game';
                    sendToApp('switchAndLaunch', {{ 
                        appId: parseInt(appId), 
                        gameName: gameName 
                    }});
                }}
            }});
            
            startBtn.parentNode?.insertBefore(btn, startBtn.nextSibling);
        }}
    }}

    setInterval(injectGameDetail, 2000);
    connectWS();
    console.log('[SteamSwitch] Script loaded');
}})();
";
                await File.WriteAllTextAsync(jsPath, js);

                StatusChanged?.Invoke(this, "注入脚本已创建，重新加载Steam库界面生效");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"注入脚本创建失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _checkTimer?.Dispose();
            _isConnected = false;
        }
    }
}
