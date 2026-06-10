using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class SteamCEFInjector : IDisposable
    {
        private readonly int _debugPort;
        private readonly GameAccountBinding _binding;
        private readonly AccountManager _accountManager;
        private CancellationTokenSource? _scanCts;
        private string? _script;
        private bool _isConnected;

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
                StatusChanged?.Invoke(this, "正在连接 Steam CEF 调试端口...");
                AppLogger.Info($"SteamCEFInjector.ConnectAsync started. DebugPort={_debugPort}");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var versionUrl = $"http://127.0.0.1:{_debugPort}/json/version";
                AppLogger.Info($"Checking DevTools version endpoint: {versionUrl}");

                var response = await http.GetAsync(versionUrl);
                if (!response.IsSuccessStatusCode)
                {
                    AppLogger.Info($"DevTools version endpoint failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    StatusChanged?.Invoke(this, "Steam 调试端口未开放");
                    return false;
                }

                var versionJson = await response.Content.ReadAsStringAsync();
                AppLogger.Info($"DevTools version response length: {versionJson.Length}");

                _script = await BuildAndWriteInjectedAssetsAsync();
                var injectedCount = await InjectIntoTargetsAsync(http, _script);
                _isConnected = injectedCount > 0;
                StartTargetRescan();

                StatusChanged?.Invoke(this, _isConnected
                    ? $"已注入 {injectedCount} 个 Steam CEF 页面"
                    : "已连接调试端口，但没有找到可注入的 Steam CEF 页面");

                AppLogger.Info($"SteamCEFInjector.ConnectAsync result: injectedCount={injectedCount}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("SteamCEFInjector.ConnectAsync failed.", ex);
                StatusChanged?.Invoke(this, $"连接失败: {ex.Message}");
                return false;
            }
        }

        private void StartTargetRescan()
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            _ = RescanTargetsAsync(_scanCts.Token);
        }

        private async Task RescanTargetsAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_script))
                return;

            AppLogger.Info("Steam CEF target rescan loop started.");

            for (var i = 0; i < 40 && !cancellationToken.IsCancellationRequested; i++)
            {
                try
                {
                    var delay = i < 10 ? 500 : 2500;
                    await Task.Delay(delay, cancellationToken);
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var injectedCount = await InjectIntoTargetsAsync(http, _script);
                    if (injectedCount > 0)
                    {
                        _isConnected = true;
                        StatusChanged?.Invoke(this, $"已刷新注入 {injectedCount} 个 Steam CEF 页面");
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Steam CEF target rescan failed.", ex);
                }
            }

            AppLogger.Info("Steam CEF target rescan loop stopped.");
        }

        private async Task<string> BuildAndWriteInjectedAssetsAsync()
        {
            var steamPath = _accountManager.GetSteamService().SteamPath;
            if (string.IsNullOrEmpty(steamPath))
                throw new InvalidOperationException("Steam path is empty.");

            var steamuiPath = Path.Combine(steamPath, "steamui");
            var jsPath = Path.Combine(steamuiPath, "steamswitch.inject.js");
            var cssPath = Path.Combine(steamuiPath, "steamswitch.inject.css");

            AppLogger.Info($"Writing inject assets. jsPath={jsPath}, cssPath={cssPath}");

            var css = @"
.steamswitch-wrap {
    position: relative;
    display: inline-flex;
    margin-left: 8px;
    vertical-align: middle;
    z-index: 9999;
    height: 36px;
    filter: drop-shadow(0 4px 10px rgba(0,0,0,0.35));
}
.steamswitch-btn {
    background: #0A84FF;
    color: white;
    border: none;
    border-radius: 8px 0 0 8px;
    padding: 0 14px;
    min-width: 52px;
    height: 36px;
    font-size: 13px;
    font-weight: 600;
    line-height: 18px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    transition: background 0.15s;
}
.steamswitch-menu-btn {
    background: #0066CC;
    color: white;
    border: none;
    border-left: 1px solid rgba(255,255,255,0.18);
    border-radius: 0 8px 8px 0;
    width: 28px;
    height: 36px;
    padding: 0;
    font-size: 12px;
    line-height: 18px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    transition: background 0.15s;
}
.steamswitch-btn:hover {
    background: #409CFF;
}
.steamswitch-menu-btn:hover {
    background: #0A84FF;
}
.steamswitch-menu-btn.steamswitch-has-account {
    background: #30D158;
}
.steamswitch-menu {
    display: none;
    position: fixed;
    min-width: 220px;
    max-width: 300px;
    padding: 6px;
    background: rgba(28, 28, 30, 0.98);
    border: 1px solid rgba(44, 44, 46, 0.8);
    border-radius: 8px;
    box-shadow: 0 8px 24px rgba(0,0,0,0.4);
    z-index: 2147483647;
    backdrop-filter: blur(12px);
}
.steamswitch-menu.steamswitch-open {
    display: block;
}
.steamswitch-menu-header {
    padding: 8px 10px 8px;
    border-bottom: 1px solid rgba(56, 56, 58, 0.8);
    margin-bottom: 5px;
}
.steamswitch-menu-game {
    color: #ffffff;
    font-size: 13px;
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.steamswitch-menu-subtitle {
    color: #98989D;
    font-size: 11px;
    margin-top: 4px;
}
.steamswitch-menu-item {
    display: flex;
    gap: 8px;
    align-items: flex-start;
    width: 100%;
    padding: 8px 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: #f2f2f2;
    text-align: left;
    font-size: 13px;
    cursor: pointer;
    transition: background 0.1s;
}
.steamswitch-menu-check {
    width: 14px;
    flex: 0 0 14px;
    color: #30D158;
    font-weight: 700;
    line-height: 18px;
}
.steamswitch-menu-item-main {
    min-width: 0;
    flex: 1;
}
.steamswitch-menu-account {
    color: #f2f2f2;
    font-size: 13px;
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.steamswitch-menu-game-line {
    color: #98989D;
    font-size: 11px;
    margin-top: 3px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.steamswitch-menu-item:hover {
    background: rgba(255,255,255,0.08);
}
.steamswitch-menu-item.steamswitch-selected {
    background: rgba(10, 132, 255, 0.2);
    color: #ffffff;
}
.steamswitch-menu-empty {
    padding: 8px 10px;
    color: #98989D;
    font-size: 12px;
}
";
            await File.WriteAllTextAsync(cssPath, css);

            var accountDtos = new List<object>();
            foreach (var account in _accountManager.Accounts)
            {
                accountDtos.Add(new
                {
                    steamId = account.SteamId,
                    accountName = account.AccountName,
                    displayName = string.IsNullOrWhiteSpace(account.PersonaName)
                        ? account.AccountName
                        : account.PersonaName
                });
            }

            var bindingDtos = new List<object>();
            foreach (var binding in _binding.GetAllBindings().Values)
            {
                bindingDtos.Add(new
                {
                    appId = binding.AppId,
                    gameName = binding.GameName,
                    accountSteamId = binding.AccountSteamId,
                    accountName = binding.AccountName
                });
            }

            var accountsJson = JsonSerializer.Serialize(accountDtos);
            var bindingsJson = JsonSerializer.Serialize(bindingDtos);
            var cssJson = JsonSerializer.Serialize(css);
            var js = @"
(function() {
    const injectedTargetAppId = '__TARGET_APP_ID__';
    if (injectedTargetAppId) {
        window.__steamswitch_target_app_id = injectedTargetAppId;
    }

    if (window.__steamswitch_injected_v12) {
        if (typeof window.__steamswitch_forceInjectGameDetail === 'function') {
            window.__steamswitch_forceInjectGameDetail();
        }
        return;
    }
    window.__steamswitch_injected_v12 = true;

    const WS_URL = 'ws://localhost:8081';
    const ACCOUNTS = __ACCOUNTS__;
    const BINDINGS = __BINDINGS__;
    const STYLE_TEXT = __STYLE__;
    function getTargetAppId() {
        return window.__steamswitch_target_app_id || '';
    }
    let ws = null;
    let pendingMessages = [];
    let missingDetailLogAt = 0;
    let injectScheduled = false;
    let bindingMap = new Map(BINDINGS.map(function(item) { return [String(item.appId), item]; }));

    function log(message) {
        console.log('[SteamSwitch] ' + message);
        sendToApp('log', { message: message });
    }

    function ensureStyles() {
        let style = document.getElementById('steamswitch-style');
        if (!style) {
            style = document.createElement('style');
            style.id = 'steamswitch-style';
            document.head.appendChild(style);
        }
        style.textContent = STYLE_TEXT;
    }

    function connectWS() {
        try {
            ws = new WebSocket(WS_URL);
            ws.onopen = function() {
                console.log('[SteamSwitch] WebSocket connected');
                const queued = pendingMessages;
                pendingMessages = [];
                sendToApp('log', { message: 'WebSocket connected' });
                for (const item of queued) {
                    sendToApp(item.action, item.data);
                }
            };
            ws.onmessage = function(e) { console.log('[SteamSwitch] Received:', e.data); };
            ws.onclose = function() {
                console.log('[SteamSwitch] WebSocket closed, retrying');
                setTimeout(connectWS, 3000);
            };
            ws.onerror = function() {
                console.log('[SteamSwitch] WebSocket error');
            };
        } catch(e) {
            console.log('[SteamSwitch] WebSocket connect failed:', e);
            setTimeout(connectWS, 3000);
        }
    }

    function sendToApp(action, data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ action, ...data }));
        } else {
            pendingMessages.push({ action: action, data: data });
        }
    }

    function extractAppId(value) {
        if (!value) return null;
        const text = String(value);
        const patterns = [
            /(?:app|details)\/(\d{2,})/i,
            /steam:\/\/(?:run|rungameid)\/(\d{2,})/i,
            /(?:appid|app_id|gameid|game_id)[""'\s:=]+(\d{2,})/i
        ];

        for (const pattern of patterns) {
            const match = text.match(pattern);
            if (match) return match[1];
        }

        return null;
    }

    function getAppId(startBtn) {
        const fromUrl = window.location.href.match(/(?:app|details|library\/app)\/(\d+)/);
        if (fromUrl) return fromUrl[1];

        const targetAppId = getTargetAppId();
        if (targetAppId) return targetAppId;

        const roots = [];
        if (startBtn) {
            let node = startBtn;
            for (let i = 0; i < 8 && node; i++) {
                roots.push(node);
                node = node.parentElement;
            }
        }

        for (const root of roots) {
            if (root.dataset) {
                for (const key of Object.keys(root.dataset)) {
                    const found = extractAppId(root.dataset[key]);
                    if (found) return found;
                }
            }

            if (root.attributes) {
                for (const attr of root.attributes) {
                    const found = extractAppId(attr.value);
                    if (found) return found;
                }
            }

            const html = root.outerHTML;
            const found = extractAppId(html && html.slice(0, 50000));
            if (found) return found;
        }

        const candidates = document.querySelectorAll('[data-appid], [data-ds-appid], [data-gameid], [href*=""store.steampowered.com/app/""], [href*=""steam://run""], [href*=""steam://rungameid""]');
        for (const candidate of candidates) {
            const found = extractAppId(candidate.outerHTML);
            if (found) return found;
        }

        const appLink = document.querySelector('a[href*=""store.steampowered.com/app/""]');
        const fromLink = appLink && appLink.href.match(/app\/(\d+)/);
        return fromLink ? fromLink[1] : null;
    }

    function normalizeText(text) {
        return (text || '').replace(/\s+/g, ' ').trim();
    }

    function isStatusOrNavigationText(text) {
        const value = normalizeText(text);
        if (!value || value.length < 2 || value.length > 80) return true;
        if (isLibraryActionText(value)) return true;
        if (/^[\d\s()\/.,:%+\-]+$/.test(value)) return true;
        return value === 'Steam' ||
            value === '库' ||
            value === '商店' ||
            value === '社区' ||
            value === '主页' ||
            value === '联机' ||
            value === '成就' ||
            value === '管理' ||
            value.indexOf('云状态') >= 0 ||
            value.indexOf('已是最新') >= 0 ||
            value.indexOf('正在检查') >= 0 ||
            value.indexOf('正在上传') >= 0 ||
            value.indexOf('正在下载') >= 0 ||
            value.indexOf('AppID') === 0;
    }

    function cleanGameNameCandidate(text) {
        let value = normalizeText(text);
        const quoted = value.match(/《([^》]{2,80})》/);
        if (quoted) return quoted[1].trim();

        value = value
            .replace(/开始游戏|Play|启动|▾|云状态|已是最新|正在检查…?|正在上传…?|正在下载…?|联机/g, ' ')
            .replace(/成就\s*\d+\s*\/\s*\d+/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();

        return value;
    }

    function isGameNameCandidate(text) {
        const value = cleanGameNameCandidate(text);
        return value.length >= 2 && value.length <= 80 && !isStatusOrNavigationText(value);
    }

    function getNearbyGameName(startBtn) {
        if (!startBtn) return null;
        const startRect = startBtn.getBoundingClientRect();
        const candidates = Array.from(document.querySelectorAll('span, div, a'))
            .map(function(el) {
                const rect = el.getBoundingClientRect();
                const rawText = normalizeText(el.textContent || el.getAttribute('aria-label') || '');
                const cleanText = cleanGameNameCandidate(rawText);
                if (!cleanText || !isGameNameCandidate(cleanText)) return null;
                if (rect.width <= 1 || rect.height <= 1) return null;

                let score = 0;
                const sameActionRow = rect.top >= startRect.top - 24 &&
                    rect.top <= startRect.bottom + 16 &&
                    rect.left >= startRect.right - 16;
                const detailHeader = rect.top >= startRect.top - 32 &&
                    rect.top <= startRect.bottom + 80 &&
                    rect.left >= startRect.left;
                const titleBelow = rect.top >= startRect.bottom &&
                    rect.top <= startRect.bottom + 120 &&
                    rect.left >= startRect.left;

                if (sameActionRow) score += 80;
                if (detailHeader) score += 45;
                if (titleBelow) score += 25;
                if (rawText.indexOf('《' + cleanText + '》') >= 0) score += 35;
                if (rawText === cleanText) score += 20;
                score -= Math.abs(rect.top - startRect.top) / 8;
                score -= Math.max(0, rect.left - startRect.right) / 120;

                return { text: cleanText, score: score };
            })
            .filter(Boolean)
            .sort(function(a, b) { return b.score - a.score; });

        return candidates.length > 0 ? candidates[0].text : null;
    }

    function getGameName(startBtn) {
        const title = document.querySelector('h1, [class*=""AppName""], [class*=""game_title""], [class*=""GameName""]');
        const titleText = cleanGameNameCandidate(title && title.textContent);
        if (titleText && titleText !== 'Steam' && isGameNameCandidate(titleText)) return titleText;

        const nearbyName = getNearbyGameName(startBtn);
        if (nearbyName) return nearbyName;

        const actionText = startBtn && normalizeText(startBtn.textContent || '');
        if (actionText && document.body && document.body.innerText) {
            const lines = document.body.innerText
                .split('\n')
                .map(function(line) { return normalizeText(line); })
                .filter(Boolean);

            for (let i = lines.length - 2; i >= 0; i--) {
                if (lines[i] === actionText) {
                    const candidate = cleanGameNameCandidate(lines[i + 1]);
                    if (isGameNameCandidate(candidate)) {
                        return candidate;
                    }
                }
            }
        }

        const docTitle = cleanGameNameCandidate(document.title);
        return docTitle && docTitle !== 'Steam' ? docTitle : '当前游戏';
    }

    function isLibraryActionText(text) {
        return text === '开始游戏' ||
            text === 'Play' ||
            text === '启动' ||
            text === '借用' ||
            text === '运行' ||
            text === '购买' ||
            text === 'Purchase' ||
            text === 'Buy' ||
            text === '安装' ||
            text === 'Install';
    }

    function getStartButton() {
        const candidates = Array.from(document.querySelectorAll('[class*=""AppStateButton""], [class*=""LaunchButton""], button[class*=""Play""], button, [role=""button""]'))
            .filter(function(btn) {
                if (btn.closest('.steamswitch-wrap')) return false;
                const text = (btn.textContent || '').trim();
                if (!isLibraryActionText(text)) return false;
                const rect = btn.getBoundingClientRect();
                return rect.width > 20 && rect.height > 20;
            })
            .sort(function(a, b) {
                const ar = a.getBoundingClientRect();
                const br = b.getBoundingClientRect();
                return (br.top - ar.top) || (br.left - ar.left);
            });

        return candidates[0] || null;
    }

    function closeMenu(wrap, menu) {
        if (wrap) wrap.classList.remove('steamswitch-open');
        if (menu) menu.classList.remove('steamswitch-open');
    }

    function openMenu(wrap, menu, anchor) {
        const rect = anchor.getBoundingClientRect();
        const gap = 8;

        menu.style.position = 'fixed';
        menu.style.left = '0px';
        menu.style.top = '0px';
        menu.style.zIndex = '2147483647';
        menu.style.visibility = 'hidden';
        wrap.classList.add('steamswitch-open');
        menu.classList.add('steamswitch-open');

        const menuRect = menu.getBoundingClientRect();
        const menuWidth = Math.max(menuRect.width, 230);
        const menuHeight = menuRect.height || 40;
        const viewportWidth = window.innerWidth || document.documentElement.clientWidth;
        const viewportHeight = window.innerHeight || document.documentElement.clientHeight;

        let left = rect.right - menuWidth;
        left = Math.max(8, Math.min(left, viewportWidth - menuWidth - 8));

        let top = rect.bottom + gap;
        const belowFits = top + menuHeight <= viewportHeight - 8;
        const aboveTop = rect.top - menuHeight - gap;
        if (!belowFits && aboveTop >= 8) {
            top = aboveTop;
        } else {
            top = Math.max(8, Math.min(top, viewportHeight - menuHeight - 8));
        }

        menu.style.left = Math.round(left) + 'px';
        menu.style.top = Math.round(top) + 'px';
        menu.style.visibility = 'visible';
        log('Account menu opened at x=' + Math.round(left) + ', y=' + Math.round(top));
    }

    function setButtonLabel(btn, appId) {
        if (!btn) return;
        const binding = bindingMap.get(String(appId));
        btn.textContent = '启动';
        btn.title = binding && binding.accountName
            ? '使用账号 ' + binding.accountName + ' 启动'
            : '选择账号后启动';

        const menuBtn = document.getElementById('steamswitch-menu-btn');
        if (menuBtn) {
            menuBtn.classList.toggle('steamswitch-has-account', Boolean(binding && binding.accountSteamId));
            menuBtn.title = binding && binding.accountName
                ? '当前账号: ' + binding.accountName + '。点击更换账号'
                : '选择启动账号';
        }
    }

    function setMenuSelection(menu, appId, gameName) {
        const binding = bindingMap.get(String(appId));
        const selectedSteamId = binding && binding.accountSteamId;
        for (const item of menu.querySelectorAll('.steamswitch-menu-item')) {
            const selected = item.dataset.steamId === selectedSteamId;
            item.classList.toggle('steamswitch-selected', selected);
            const check = item.querySelector('.steamswitch-menu-check');
            const gameLine = item.querySelector('.steamswitch-menu-game-line');
            if (check) check.textContent = selected ? '✓' : '';
            if (gameLine) gameLine.textContent = '用于：' + (gameName || '当前游戏') + ' · AppID ' + appId;
        }
    }

    function setMenuContext(menu, appId, gameName) {
        const title = menu.querySelector('.steamswitch-menu-game');
        const subtitle = menu.querySelector('.steamswitch-menu-subtitle');
        if (title) title.textContent = gameName || '当前游戏';
        if (subtitle) subtitle.textContent = 'AppID ' + appId + ' · 选择启动账号';
    }

    function bindAccount(appId, gameName, account) {
        const binding = {
            appId: parseInt(appId, 10),
            gameName: gameName,
            accountSteamId: account.steamId,
            accountName: account.accountName
        };
        bindingMap.set(String(appId), binding);

        sendToApp('setBinding', {
            appId: binding.appId,
            gameName: gameName,
            steamId: account.steamId,
            accountName: account.accountName
        });
    }

    function injectGameDetail() {
        injectScheduled = false;
        const startBtn = getStartButton();
        const appId = getAppId(startBtn);
        if (!startBtn || !appId) {
            const now = Date.now();
            if (now - missingDetailLogAt > 10000) {
                missingDetailLogAt = now;
                log('Waiting for game detail controls. hasStartBtn=' + Boolean(startBtn) + ', appId=' + appId + ', title=' + document.title + ', url=' + window.location.href);
            }
            return;
        }

        let wrap = document.getElementById('steamswitch-launch-wrap');
        let bodyMenu = document.getElementById('steamswitch-launch-menu');
        if (!wrap && bodyMenu) {
            bodyMenu.remove();
            bodyMenu = null;
        }

        if (wrap && (!wrap.querySelector('.steamswitch-menu-btn') || !bodyMenu)) {
            wrap.remove();
            if (bodyMenu) bodyMenu.remove();
            wrap = null;
            bodyMenu = null;
        }

        if (wrap && wrap.previousElementSibling !== startBtn) {
            startBtn.parentNode?.insertBefore(wrap, startBtn.nextSibling);
            const rect = startBtn.getBoundingClientRect();
            log('Button moved for app ' + appId + ' next to start button at x=' + Math.round(rect.left) + ', y=' + Math.round(rect.top));
        }

        if (!wrap) {
            wrap = document.createElement('span');
            wrap.id = 'steamswitch-launch-wrap';
            wrap.className = 'steamswitch-wrap';

            const btn = document.createElement('button');
            btn.id = 'steamswitch-launch-btn';
            btn.className = 'steamswitch-btn';
            btn.type = 'button';

            const menuBtn = document.createElement('button');
            menuBtn.id = 'steamswitch-menu-btn';
            menuBtn.className = 'steamswitch-menu-btn';
            menuBtn.type = 'button';
            menuBtn.textContent = '▾';
            menuBtn.title = '选择启动账号';

            const menu = document.createElement('div');
            menu.id = 'steamswitch-launch-menu';
            menu.className = 'steamswitch-menu';
            menu.addEventListener('click', function(event) {
                event.preventDefault();
                event.stopPropagation();
            });
            menu.addEventListener('pointerdown', function(event) {
                event.stopPropagation();
            });

            const menuHeader = document.createElement('div');
            menuHeader.className = 'steamswitch-menu-header';
            const menuGame = document.createElement('div');
            menuGame.className = 'steamswitch-menu-game';
            menuGame.textContent = '当前游戏';
            const menuSubtitle = document.createElement('div');
            menuSubtitle.className = 'steamswitch-menu-subtitle';
            menuSubtitle.textContent = '选择启动账号';
            menuHeader.appendChild(menuGame);
            menuHeader.appendChild(menuSubtitle);
            menu.appendChild(menuHeader);

            if (ACCOUNTS.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'steamswitch-menu-empty';
                empty.textContent = '未找到 Steam 账号';
                menu.appendChild(empty);
            } else {
                for (const account of ACCOUNTS) {
                    const item = document.createElement('button');
                    item.type = 'button';
                    item.className = 'steamswitch-menu-item';
                    item.dataset.steamId = account.steamId;
                    item.dataset.label = account.displayName + ' (' + account.accountName + ')';

                    const itemCheck = document.createElement('span');
                    itemCheck.className = 'steamswitch-menu-check';

                    const itemMain = document.createElement('span');
                    itemMain.className = 'steamswitch-menu-item-main';

                    const accountLine = document.createElement('span');
                    accountLine.className = 'steamswitch-menu-account';
                    accountLine.textContent = item.dataset.label;

                    const gameLine = document.createElement('span');
                    gameLine.className = 'steamswitch-menu-game-line';
                    gameLine.textContent = '用于：当前游戏';

                    itemMain.appendChild(accountLine);
                    itemMain.appendChild(gameLine);
                    item.appendChild(itemCheck);
                    item.appendChild(itemMain);

                    item.addEventListener('click', function(event) {
                        event.preventDefault();
                        event.stopPropagation();
                        event.stopImmediatePropagation();
                        const currentAppId = getAppId(btn);
                        if (!currentAppId) {
                            log('No appId found when account menu clicked');
                            return;
                        }
                        const currentStartBtn = getStartButton();
                        const currentGameName = getGameName(currentStartBtn);
                        log('Selected account ' + account.accountName + ' for app ' + currentAppId + ', launching game ' + currentGameName);
                        bindAccount(currentAppId, currentGameName, account);
                        setMenuContext(menu, currentAppId, currentGameName);
                        setButtonLabel(btn, currentAppId);
                        setMenuSelection(menu, currentAppId, currentGameName);
                        closeMenu(wrap, menu);
                        sendToApp('switchAndLaunch', {
                            appId: parseInt(currentAppId, 10),
                            gameName: currentGameName,
                            steamId: account.steamId,
                            accountName: account.accountName
                        });
                    });
                    menu.appendChild(item);
                }
            }

            btn.addEventListener('click', function(event) {
                event.preventDefault();
                event.stopPropagation();
                const currentAppId = getAppId(btn);
                log('SteamSwitch button clicked. appId=' + currentAppId);
                if (!currentAppId) {
                    log('No appId found when SteamSwitch button clicked');
                    return;
                }

                const binding = bindingMap.get(String(currentAppId));
                if (binding && binding.accountSteamId) {
                    log('Launching app ' + currentAppId + ' with bound account ' + binding.accountName);
                    const currentStartBtn = getStartButton();
                    sendToApp('switchAndLaunch', {
                        appId: parseInt(currentAppId, 10),
                        gameName: getGameName(currentStartBtn),
                        steamId: binding.accountSteamId,
                        accountName: binding.accountName
                    });
                } else {
                    log('No binding for app ' + currentAppId + ', opening account menu');
                    const currentGameName = getGameName(getStartButton());
                    setMenuContext(menu, currentAppId, currentGameName);
                    setMenuSelection(menu, currentAppId, currentGameName);
                    openMenu(wrap, menu, menuBtn);
                }
            });

            menuBtn.addEventListener('click', function(event) {
                event.preventDefault();
                event.stopPropagation();
                const currentAppId = getAppId(btn);
                if (currentAppId) {
                    const currentGameName = getGameName(getStartButton());
                    setMenuContext(menu, currentAppId, currentGameName);
                    setMenuSelection(menu, currentAppId, currentGameName);
                }
                if (wrap.classList.contains('steamswitch-open')) {
                    closeMenu(wrap, menu);
                } else {
                    openMenu(wrap, menu, menuBtn);
                }
            });

            document.addEventListener('click', function(event) {
                if (!wrap.contains(event.target) && !menu.contains(event.target)) {
                    closeMenu(wrap, menu);
                }
            });
            document.addEventListener('scroll', function() {
                closeMenu(wrap, menu);
            }, true);
            window.addEventListener('resize', function() {
                closeMenu(wrap, menu);
            });

            wrap.appendChild(btn);
            wrap.appendChild(menuBtn);
            document.body.appendChild(menu);
            startBtn.parentNode?.insertBefore(wrap, startBtn.nextSibling);
            const rect = startBtn.getBoundingClientRect();
            log('Button injected for app ' + appId + ' next to start button at x=' + Math.round(rect.left) + ', y=' + Math.round(rect.top));
        }

        const btn = document.getElementById('steamswitch-launch-btn');
        const menu = document.getElementById('steamswitch-launch-menu');
        setButtonLabel(btn, appId);
        if (menu) {
            const gameName = getGameName(startBtn);
            setMenuContext(menu, appId, gameName);
            setMenuSelection(menu, appId, gameName);
        }
    }

    function scheduleInjectGameDetail() {
        if (injectScheduled) return;
        injectScheduled = true;
        requestAnimationFrame(injectGameDetail);
    }

    function startFastDomWatch() {
        const observer = new MutationObserver(scheduleInjectGameDetail);
        observer.observe(document.documentElement || document.body, {
            childList: true,
            subtree: true
        });

        let ticks = 0;
        const fastTimer = setInterval(function() {
            ticks++;
            scheduleInjectGameDetail();
            if (document.getElementById('steamswitch-launch-wrap') || ticks >= 40) {
                clearInterval(fastTimer);
            }
        }, 250);

        setTimeout(function() {
            observer.disconnect();
        }, 15000);
    }

    window.__steamswitch_forceInjectGameDetail = injectGameDetail;

    ensureStyles();
    injectGameDetail();
    startFastDomWatch();
    setInterval(scheduleInjectGameDetail, 1500);
    connectWS();
    log('Script loaded at ' + window.location.href);
})();
"
                .Replace("__ACCOUNTS__", accountsJson)
                .Replace("__BINDINGS__", bindingsJson)
                .Replace("__STYLE__", cssJson);

            await File.WriteAllTextAsync(jsPath, js);
            AppLogger.Info($"Inject assets written. Accounts={accountDtos.Count}, Bindings={bindingDtos.Count}");
            return js;
        }

        private async Task<int> InjectIntoTargetsAsync(HttpClient http, string script)
        {
            var targetsUrl = $"http://127.0.0.1:{_debugPort}/json/list";
            AppLogger.Info($"Loading DevTools targets: {targetsUrl}");

            var response = await http.GetAsync(targetsUrl);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Info($"DevTools targets endpoint failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                return 0;
            }

            var targetsJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(targetsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                AppLogger.Info("DevTools targets response is not an array.");
                return 0;
            }

            var injectedCount = 0;
            var targetAppId = FindTargetAppId(doc.RootElement);
            AppLogger.Info($"Detected target appId from DevTools targets: {targetAppId ?? "(none)"}");

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                var type = GetString(target, "type");
                var url = GetString(target, "url");
                var title = GetString(target, "title");
                var wsUrl = GetString(target, "webSocketDebuggerUrl");

                AppLogger.Info($"DevTools target: type={type}, title={title}, url={url}, hasWs={BooleanText(!string.IsNullOrEmpty(wsUrl))}");

                if (string.IsNullOrEmpty(wsUrl))
                    continue;

                if (!string.IsNullOrEmpty(type) && type != "page" && type != "webview")
                    continue;

                var targetScript = script.Replace("__TARGET_APP_ID__", EscapeForJavaScriptString(targetAppId ?? ""));
                if (await EvaluateScriptAsync(wsUrl, targetScript, url, title))
                    injectedCount++;
            }

            return injectedCount;
        }

        private static string? FindTargetAppId(JsonElement targets)
        {
            foreach (var target in targets.EnumerateArray())
            {
                var url = GetString(target, "url");
                var title = GetString(target, "title");
                var found = ExtractAppId(url) ?? ExtractAppId(title);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }

            return null;
        }

        private static string? ExtractAppId(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var decoded = Uri.UnescapeDataString(value);
            var match = System.Text.RegularExpressions.Regex.Match(decoded, @"(?:app|details|library/app)/(\d{2,})");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string EscapeForJavaScriptString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        private static async Task<bool> EvaluateScriptAsync(string webSocketUrl, string script, string url, string title)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(webSocketUrl), cts.Token);

                var payload = JsonSerializer.Serialize(new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new
                    {
                        expression = script,
                        awaitPromise = false,
                        returnByValue = true
                    }
                });

                var bytes = Encoding.UTF8.GetBytes(payload);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);

                var buffer = new byte[8192];
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var success = !response.Contains("\"error\"");

                AppLogger.Info($"Runtime.evaluate result: success={BooleanText(success)}, title={title}, url={url}, response={TrimForLog(response)}");
                return success;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Runtime.evaluate failed. title={title}, url={url}", ex);
                return false;
            }
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static string BooleanText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string TrimForLog(string value)
        {
            return value.Length <= 600 ? value : value.Substring(0, 600) + "...";
        }

        public void Dispose()
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null;
            _isConnected = false;
        }
    }
}
