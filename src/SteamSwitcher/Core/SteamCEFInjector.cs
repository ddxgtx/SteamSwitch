using System;
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

        public bool IsConnected { get; private set; }
        public event EventHandler<string>? StatusChanged;

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
                StatusChanged?.Invoke(this, "正在检测Steam调试端口...");

                // 检查调试端口是否可用
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(3);
                
                var response = await http.GetAsync($"http://localhost:{_debugPort}/json/version");
                if (response.IsSuccessStatusCode)
                {
                    IsConnected = true;
                    StatusChanged?.Invoke(this, "已连接到Steam CEF");
                    return true;
                }

                StatusChanged?.Invoke(this, "Steam调试端口未开放，请重启Steam");
                return false;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"连接失败: {ex.Message}");
                return false;
            }
        }

        public bool InjectCustomFiles(int wsPort = 8081)
        {
            try
            {
                var steamPath = _accountManager.GetSteamService().SteamPath;
                if (string.IsNullOrEmpty(steamPath))
                {
                    StatusChanged?.Invoke(this, "Steam路径未找到");
                    return false;
                }

                var steamuiPath = Path.Combine(steamPath, "steamui");
                if (!Directory.Exists(steamuiPath))
                {
                    Directory.CreateDirectory(steamuiPath);
                }

                // 写入自定义CSS - Steam会自动加载这些文件
                var cssPath = Path.Combine(steamuiPath, "libraryroot.custom.css");
                var css = @"
/* Steam Switch - Custom Styles */
.steamswitch-container {
    position: fixed;
    bottom: 60px;
    right: 20px;
    z-index: 9999;
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.steamswitch-btn {
    background: linear-gradient(135deg, #0A84FF, #0066CC);
    color: white;
    border: none;
    border-radius: 8px;
    padding: 12px 20px;
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    box-shadow: 0 4px 12px rgba(10, 132, 255, 0.3);
    transition: all 0.2s ease;
    font-family: 'Segoe UI', Arial, sans-serif;
}

.steamswitch-btn:hover {
    background: linear-gradient(135deg, #409CFF, #0A84FF);
    transform: translateY(-2px);
    box-shadow: 0 6px 16px rgba(10, 132, 255, 0.4);
}

.steamswitch-btn:active {
    transform: translateY(0);
}

.steamswitch-btn .icon {
    font-size: 18px;
}

.steamswitch-btn.secondary {
    background: #2A475E;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.steamswitch-btn.secondary:hover {
    background: #3D6A8E;
}

.steamswitch-panel {
    background: rgba(27, 40, 56, 0.95);
    border-radius: 12px;
    padding: 16px;
    min-width: 200px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.5);
    backdrop-filter: blur(10px);
}

.steamswitch-panel h3 {
    color: #FFFFFF;
    font-size: 14px;
    margin: 0 0 12px 0;
    font-weight: 600;
}

.steamswitch-account-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 12px;
    border-radius: 6px;
    cursor: pointer;
    transition: background 0.15s;
}

.steamswitch-account-item:hover {
    background: rgba(255, 255, 255, 0.1);
}

.steamswitch-account-item.active {
    background: rgba(10, 132, 255, 0.2);
}

.steamswitch-account-item .avatar {
    width: 32px;
    height: 32px;
    border-radius: 50%;
    background: #2A475E;
}

.steamswitch-account-item .name {
    color: #FFFFFF;
    font-size: 13px;
}

.steamswitch-account-item .status {
    color: #66C0F4;
    font-size: 11px;
}

.steamswitch-badge {
    background: #30D158;
    color: white;
    font-size: 10px;
    padding: 2px 6px;
    border-radius: 4px;
    margin-left: auto;
}
";
                File.WriteAllText(cssPath, css);

                // 写入自定义JS
                var jsPath = Path.Combine(steamuiPath, "libraryroot.custom.js");
                var js = $@"
(function() {{
    'use strict';
    
    if (window.__steamswitch_loaded) return;
    window.__steamswitch_loaded = true;
    
    console.log('[SteamSwitch] Loading...');
    
    // WebSocket连接
    let ws = null;
    let reconnectTimer = null;
    
    function connectWebSocket() {{
        try {{
            ws = new WebSocket('ws://localhost:{wsPort}');
            
            ws.onopen = function() {{
                console.log('[SteamSwitch] Connected to server');
                if (reconnectTimer) {{
                    clearInterval(reconnectTimer);
                    reconnectTimer = null;
                }}
            }};
            
            ws.onmessage = function(e) {{
                console.log('[SteamSwitch] Message:', e.data);
                try {{
                    var msg = JSON.parse(e.data);
                    if (msg.action === 'refreshBindings') {{
                        // 刷新绑定数据
                        loadBindings();
                    }}
                }} catch(err) {{
                    console.error('[SteamSwitch] Parse error:', err);
                }}
            }};
            
            ws.onclose = function() {{
                console.log('[SteamSwitch] Disconnected');
                if (!reconnectTimer) {{
                    reconnectTimer = setInterval(connectWebSocket, 5000);
                }}
            }};
            
            ws.onerror = function(err) {{
                console.log('[SteamSwitch] Error:', err);
            }};
        }} catch(e) {{
            console.log('[SteamSwitch] Connection failed:', e);
        }}
    }}
    
    function sendToServer(action, data) {{
        if (ws && ws.readyState === WebSocket.OPEN) {{
            ws.send(JSON.stringify({{ action: action, ...data }}));
            return true;
        }}
        return false;
    }}
    
    // 获取当前游戏AppID
    function getCurrentAppId() {{
        var url = window.location.href;
        var match = url.match(/app\/(\d+)/);
        return match ? parseInt(match[1]) : null;
    }}
    
    // 获取游戏名称
    function getGameName() {{
        var titleEl = document.querySelector('[class*=""AppName""]') || 
                     document.querySelector('h1') ||
                     document.querySelector('[class*=""title""]');
        return titleEl ? titleEl.textContent.trim() : document.title;
    }}
    
    // 注入按钮到游戏详情页
    function injectGameDetailButton() {{
        var appId = getCurrentAppId();
        if (!appId) return;
        
        // 检查是否已注入
        if (document.getElementById('steamswitch-inject-btn')) return;
        
        // 查找启动按钮区域
        var launchArea = document.querySelector('[class*=""GameDetailsPlayArea""]') ||
                        document.querySelector('[class*=""LaunchButton""]') ||
                        document.querySelector('[class*=""PlayButton""]') ||
                        document.querySelector('[class*=""apphub_OtherSiteAlts""]');
        
        if (!launchArea) return;
        
        // 创建按钮容器
        var container = document.createElement('div');
        container.id = 'steamswitch-inject-btn';
        container.style.cssText = 'display:flex;gap:8px;margin-top:10px;';
        
        // 切换并启动按钮
        var switchBtn = document.createElement('button');
        switchBtn.className = 'steamswitch-btn';
        switchBtn.innerHTML = '<span class=""icon"">⚡</span> 切换账号启动';
        switchBtn.onclick = function() {{
            sendToServer('switchAndLaunch', {{ appId: appId, gameName: getGameName() }});
        }};
        
        // 绑定账号按钮
        var bindBtn = document.createElement('button');
        bindBtn.className = 'steamswitch-btn secondary';
        bindBtn.innerHTML = '<span class=""icon"">📌</span> 绑定账号';
        bindBtn.onclick = function() {{
            sendToServer('showBindingDialog', {{ appId: appId, gameName: getGameName() }});
        }};
        
        container.appendChild(switchBtn);
        container.appendChild(bindBtn);
        
        // 插入到启动按钮后面
        launchArea.parentNode.insertBefore(container, launchArea.nextSibling);
        
        console.log('[SteamSwitch] Injected button for app:', appId);
    }}
    
    // 定期检查并注入
    setInterval(injectGameDetailButton, 1000);
    
    // 初始化
    connectWebSocket();
    injectGameDetailButton();
    
    console.log('[SteamSwitch] Loaded successfully');
}})();
";
                File.WriteAllText(jsPath, js);

                StatusChanged?.Invoke(this, "注入文件已创建，重启Steam库界面生效");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"注入失败: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            IsConnected = false;
        }
    }
}
