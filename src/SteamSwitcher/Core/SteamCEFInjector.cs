using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamSwitcher.Core
{
    public class SteamCEFInjector : IDisposable
    {
        private readonly GameAccountBinding _binding;
        private readonly AccountManager _accountManager;

        public bool IsConnected { get; private set; }
        public event EventHandler<string>? StatusChanged;

        // 注入标记，用于识别和移除注入内容
        private const string INJECTION_START = "/* === STEAM SWITCH INJECT START === */";
        private const string INJECTION_END = "/* === STEAM SWITCH INJECT END === */";

        public SteamCEFInjector(GameAccountBinding binding, AccountManager accountManager)
        {
            _binding = binding;
            _accountManager = accountManager;
        }

        public bool InjectAndRestart(int wsPort = 8081)
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

                // 查找Steam的库CSS文件
                var cssFiles = Directory.GetFiles(steamuiPath, "*.css", SearchOption.TopDirectoryOnly)
                    .Where(f => f.Contains("library") || f.Contains("Library"))
                    .ToList();

                // 查找库JS文件
                var jsFiles = Directory.GetFiles(steamuiPath, "*.js", SearchOption.TopDirectoryOnly)
                    .Where(f => f.Contains("library") || f.Contains("Library"))
                    .ToList();

                StatusChanged?.Invoke(this, $"找到 {cssFiles.Count} 个CSS, {jsFiles.Count} 个JS文件");

                // 方法1: 尝试修改libraryroot相关文件
                var injected = false;

                // 查找并注入CSS
                foreach (var cssFile in cssFiles)
                {
                    if (InjectIntoFile(cssFile, GetCssContent()))
                    {
                        injected = true;
                        StatusChanged?.Invoke(this, $"已注入CSS: {Path.GetFileName(cssFile)}");
                        break;
                    }
                }

                // 如果没找到特定文件，尝试注入到所有小CSS文件
                if (!injected)
                {
                    foreach (var cssFile in cssFiles.Where(f => new FileInfo(f).Length < 100000))
                    {
                        if (InjectIntoFile(cssFile, GetCssContent()))
                        {
                            injected = true;
                            StatusChanged?.Invoke(this, $"已注入CSS: {Path.GetFileName(cssFile)}");
                            break;
                        }
                    }
                }

                // 注入JS
                var jsContent = GetJsContent().Replace("__WS_PORT__", wsPort.ToString());
                injected = false;

                foreach (var jsFile in jsFiles)
                {
                    if (InjectIntoFile(jsFile, jsContent))
                    {
                        injected = true;
                        StatusChanged?.Invoke(this, $"已注入JS: {Path.GetFileName(jsFile)}");
                        break;
                    }
                }

                // 重启Steam库界面
                StatusChanged?.Invoke(this, "正在重启Steam库界面...");
                RestartSteamLibrary();

                StatusChanged?.Invoke(this, $"注入完成！端口: {wsPort}");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"注入失败: {ex.Message}");
                return false;
            }
        }

        private bool InjectIntoFile(string filePath, string content)
        {
            try
            {
                var fileContent = File.ReadAllText(filePath, Encoding.UTF8);

                // 移除旧的注入内容
                var startIndex = fileContent.IndexOf(INJECTION_START);
                var endIndex = fileContent.IndexOf(INJECTION_END);
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    fileContent = fileContent.Remove(startIndex, endIndex - startIndex + INJECTION_END.Length);
                }

                // 添加新的注入内容
                var injection = $"\n{INJECTION_START}\n{content}\n{INJECTION_END}\n";
                fileContent += injection;

                File.WriteAllText(filePath, fileContent, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool RemoveInjection()
        {
            try
            {
                var steamPath = _accountManager.GetSteamService().SteamPath;
                if (string.IsNullOrEmpty(steamPath)) return false;

                var steamuiPath = Path.Combine(steamPath, "steamui");
                var allFiles = Directory.GetFiles(steamuiPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".css") || f.EndsWith(".js"));

                foreach (var file in allFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file, Encoding.UTF8);
                        var startIndex = content.IndexOf(INJECTION_START);
                        var endIndex = content.IndexOf(INJECTION_END);

                        if (startIndex >= 0 && endIndex > startIndex)
                        {
                            content = content.Remove(startIndex, endIndex - startIndex + INJECTION_END.Length);
                            File.WriteAllText(file, content, Encoding.UTF8);
                        }
                    }
                    catch { }
                }

                StatusChanged?.Invoke(this, "已移除注入内容");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool RestartSteamLibrary()
        {
            try
            {
                var processes = Process.GetProcessesByName("steamwebhelper");
                foreach (var proc in processes)
                {
                    try { proc.Kill(); } catch { }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetCssContent()
        {
            return @"
/* Steam Switch Styles */
.steamswitch-float-btn {
    position: fixed !important;
    bottom: 80px !important;
    right: 30px !important;
    z-index: 99999 !important;
    background: linear-gradient(135deg, #0A84FF, #0066CC) !important;
    color: white !important;
    border: none !important;
    border-radius: 12px !important;
    padding: 14px 24px !important;
    font-size: 15px !important;
    font-weight: 600 !important;
    cursor: pointer !important;
    display: flex !important;
    align-items: center !important;
    gap: 10px !important;
    box-shadow: 0 6px 20px rgba(10, 132, 255, 0.4) !important;
    transition: all 0.2s ease !important;
    font-family: 'Segoe UI', Arial, sans-serif !important;
}
.steamswitch-float-btn:hover {
    transform: translateY(-3px) scale(1.02) !important;
    box-shadow: 0 8px 25px rgba(10, 132, 255, 0.5) !important;
}
.steamswitch-float-btn:active {
    transform: translateY(0) scale(0.98) !important;
}
.steamswitch-float-btn .ss-icon { font-size: 20px !important; }
.steamswitch-panel {
    position: fixed !important;
    bottom: 140px !important;
    right: 30px !important;
    z-index: 99998 !important;
    background: rgba(27, 40, 56, 0.97) !important;
    border-radius: 14px !important;
    padding: 18px !important;
    min-width: 260px !important;
    box-shadow: 0 12px 40px rgba(0,0,0,0.6) !important;
    backdrop-filter: blur(12px) !important;
    border: 1px solid rgba(102,192,244,0.15) !important;
    display: none !important;
    font-family: 'Segoe UI', Arial, sans-serif !important;
}
.steamswitch-panel.show { display: block !important; }
.steamswitch-panel h3 {
    color: #FFFFFF !important;
    font-size: 15px !important;
    margin: 0 0 14px 0 !important;
    font-weight: 600 !important;
    padding-bottom: 10px !important;
    border-bottom: 1px solid rgba(255,255,255,0.08) !important;
}
.steamswitch-acc {
    display: flex !important;
    align-items: center !important;
    gap: 12px !important;
    padding: 10px 14px !important;
    border-radius: 8px !important;
    cursor: pointer !important;
    transition: background 0.15s !important;
    margin-bottom: 4px !important;
}
.steamswitch-acc:hover { background: rgba(255,255,255,0.08) !important; }
.steamswitch-acc.active { background: rgba(10,132,255,0.25) !important; }
.steamswitch-acc .ss-avatar {
    width: 36px !important;
    height: 36px !important;
    border-radius: 8px !important;
    background: #2A475E !important;
    object-fit: cover !important;
}
.steamswitch-acc .ss-name { color: #FFF !important; font-size: 14px !important; font-weight: 500 !important; }
.steamswitch-acc .ss-user { color: #8F98A0 !important; font-size: 11px !important; margin-top: 2px !important; }
.steamswitch-acc .ss-badge {
    background: #30D158 !important;
    color: white !important;
    font-size: 10px !important;
    padding: 3px 8px !important;
    border-radius: 6px !important;
    margin-left: auto !important;
    font-weight: 600 !important;
}
";
        }

        private string GetJsContent()
        {
            return @"
(function() {
    if (window.__steamswitch_loaded) return;
    window.__steamswitch_loaded = true;
    console.log('[SteamSwitch] Loading...');

    var wsPort = __WS_PORT__;
    var ws = null;
    var reconnectTimer = null;
    var accounts = [];
    var currentAccount = null;

    function connectWS() {
        try {
            ws = new WebSocket('ws://localhost:' + wsPort);
            ws.onopen = function() {
                console.log('[SteamSwitch] WS connected');
                if (reconnectTimer) { clearInterval(reconnectTimer); reconnectTimer = null; }
                ws.send(JSON.stringify({ action: 'getAccounts' }));
            };
            ws.onmessage = function(e) {
                console.log('[SteamSwitch] WS msg:', e.data);
                try {
                    var msg = JSON.parse(e.data);
                    if (msg.action === 'accountsData') {
                        accounts = msg.accounts || [];
                        currentAccount = msg.current || null;
                        updatePanel();
                    } else if (msg.action === 'switchResult') {
                        if (msg.success) {
                            showNotification('已切换到 ' + msg.accountName);
                            currentAccount = msg.accountName;
                            ws.send(JSON.stringify({ action: 'getAccounts' }));
                        } else {
                            showNotification('切换失败: ' + (msg.error || '未知错误'));
                        }
                    }
                } catch(err) {
                    console.error('[SteamSwitch] Parse error:', err);
                }
            };
            ws.onclose = function() {
                console.log('[SteamSwitch] WS disconnected');
                if (!reconnectTimer) reconnectTimer = setInterval(connectWS, 5000);
            };
        } catch(e) {
            console.log('[SteamSwitch] WS error:', e);
        }
    }

    function sendMsg(action, data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ action: action, ...data }));
            return true;
        }
        return false;
    }

    function showNotification(text) {
        var n = document.createElement('div');
        n.style.cssText = 'position:fixed;top:20px;right:20px;z-index:999999;background:#1B2838;color:white;padding:16px 24px;border-radius:10px;font-size:14px;font-family:Segoe UI,Arial,sans-serif;box-shadow:0 8px 30px rgba(0,0,0,0.5);border:1px solid rgba(102,192,244,0.2);';
        n.textContent = text;
        document.body.appendChild(n);
        setTimeout(function() { n.remove(); }, 3000);
    }

    function updatePanel() {
        var panel = document.getElementById('steamswitch-panel');
        if (!panel) return;
        var list = panel.querySelector('.ss-list');
        if (!list) return;
        list.innerHTML = '';
        accounts.forEach(function(acc) {
            var item = document.createElement('div');
            item.className = 'steamswitch-acc' + (acc.name === currentAccount ? ' active' : '');
            var html = '<div>';
            html += '<div class=""ss-name"">' + acc.name + '</div>';
            html += '<div class=""ss-user"">' + acc.username + '</div>';
            html += '</div>';
            if (acc.name === currentAccount) {
                html += '<span class=""ss-badge"">当前</span>';
            }
            item.innerHTML = html;
            item.onclick = function() {
                sendMsg('switchAccount', { steamId: acc.steamId });
            };
            list.appendChild(item);
        });
    }

    function createUI() {
        if (document.getElementById('steamswitch-float-btn')) return;

        var btn = document.createElement('button');
        btn.id = 'steamswitch-float-btn';
        btn.className = 'steamswitch-float-btn';
        btn.innerHTML = '<span class=""ss-icon"">⚡</span> Steam Switch';
        btn.onclick = function(e) {
            e.stopPropagation();
            var panel = document.getElementById('steamswitch-panel');
            if (panel) panel.classList.toggle('show');
        };
        document.body.appendChild(btn);

        var panel = document.createElement('div');
        panel.id = 'steamswitch-panel';
        panel.className = 'steamswitch-panel';
        panel.innerHTML = '<h3>⚡ 快速切换账号</h3><div class=""ss-list""></div>';
        document.body.appendChild(panel);

        document.addEventListener('click', function(e) {
            if (!panel.contains(e.target) && e.target !== btn) {
                panel.classList.remove('show');
            }
        });

        updatePanel();
    }

    setTimeout(function() {
        createUI();
        connectWS();
    }, 2000);
})();
";
        }

        public void Dispose()
        {
            IsConnected = false;
        }
    }
}
