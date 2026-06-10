// Steam Switch - 仅在库界面游戏详情页注入
(function() {
    if (window.__steamswitch_loaded) return;
    window.__steamswitch_loaded = true;

    var wsPort = __WS_PORT__;
    var ws = null;
    var accounts = [];
    var currentAccount = null;
    var currentAppId = null;

    // WebSocket
    function connectWS() {
        try {
            ws = new WebSocket('ws://localhost:' + wsPort);
            ws.onopen = function() {
                ws.send(JSON.stringify({ action: 'getAccounts' }));
            };
            ws.onmessage = function(e) {
                try {
                    var msg = JSON.parse(e.data);
                    if (msg.action === 'accountsData') {
                        accounts = msg.accounts || [];
                        currentAccount = msg.current || null;
                    } else if (msg.action === 'switchResult') {
                        if (msg.success) {
                            showNotification('已切换到 ' + msg.accountName);
                            currentAccount = msg.accountName;
                            ws.send(JSON.stringify({ action: 'getAccounts' }));
                        } else {
                            showNotification('切换失败: ' + (msg.error || '请关闭Steam'));
                        }
                    } else if (msg.action === 'bindingSaved') {
                        showNotification('已绑定账号');
                    }
                } catch(err) {}
            };
            ws.onclose = function() { setTimeout(connectWS, 3000); };
        } catch(e) {}
    }

    function sendMsg(action, data) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ action: action, ...data }));
        }
    }

    function showNotification(text) {
        var n = document.createElement('div');
        n.style.cssText = 'position:fixed;top:20px;right:20px;z-index:999999;background:#1B2838;color:white;padding:16px 24px;border-radius:10px;font-size:14px;font-family:Segoe UI,Arial,sans-serif;box-shadow:0 8px 30px rgba(0,0,0,0.5);';
        n.textContent = text;
        document.body.appendChild(n);
        setTimeout(function() { n.remove(); }, 3000);
    }

    // 获取当前页面的AppID - 必须是 /app/数字 格式
    function getAppId() {
        var url = window.location.href;
        // 只匹配游戏详情页URL
        var match = url.match(/\/app\/(\d+)/);
        return match ? match[1] : null;
    }

    // 检查是否在Steam库界面
    function isInSteamLibrary() {
        // 检查URL是否包含库路径
        var url = window.location.href;
        if (url.includes('/library/app/') || url.includes('steam://openurl/')) {
            return true;
        }
        // 检查是否有库界面的元素
        var libEl = document.querySelector('[class*="Library"]') || 
                    document.querySelector('[class*="library"]');
        return libEl !== null;
    }

    // 查找"开始游戏"按钮 - 在游戏详情页中
    function findPlayButton() {
        // 在库界面中查找开始游戏按钮
        var selectors = [
            '[class*="GameDetailsPlayArea"] button',
            '[class*="PlayButton"]',
            '[class*="LaunchButton"]',
            'button[class*="Play"]',
            'div[class*="PlayButton"]'
        ];
        
        for (var i = 0; i < selectors.length; i++) {
            var el = document.querySelector(selectors[i]);
            if (el) return el;
        }
        
        // 通过文本查找
        var allBtns = document.querySelectorAll('button');
        for (var j = 0; j < allBtns.length; j++) {
            var text = allBtns[j].textContent.trim();
            if (text === '开始游戏' || text === 'Play' || text === '运行' || text === '安装') {
                return allBtns[j];
            }
        }
        
        return null;
    }

    // 移除注入
    function removeInjection() {
        var old = document.getElementById('ss-game-btn');
        if (old) old.remove();
        currentAppId = null;
    }

    // 主注入逻辑
    function injectIfNeeded() {
        var appId = getAppId();
        
        // 如果没有AppID，不在游戏详情页，移除注入
        if (!appId) {
            removeInjection();
            return;
        }
        
        // 如果AppID没变且已注入，跳过
        if (appId === currentAppId && document.getElementById('ss-game-btn')) {
            return;
        }
        
        // 移除旧注入
        removeInjection();
        currentAppId = appId;

        // 查找开始游戏按钮
        var playBtn = findPlayButton();
        if (!playBtn) return;

        // 创建容器
        var container = document.createElement('div');
        container.id = 'ss-game-btn';
        container.style.cssText = 'display:inline-block;position:relative;margin-left:8px;vertical-align:middle;';

        // 按钮
        var btn = document.createElement('button');
        btn.style.cssText = 'background:linear-gradient(135deg,#0A84FF,#0066CC);color:white;border:none;border-radius:4px;padding:8px 16px;font-size:13px;font-weight:600;cursor:pointer;font-family:Segoe UI,Arial,sans-serif;height:36px;';
        btn.textContent = '⚡ 切换账号';
        btn.onclick = function(e) {
            e.stopPropagation();
            e.preventDefault();
            var menu = document.getElementById('ss-game-menu');
            if (menu) {
                menu.style.display = menu.style.display === 'block' ? 'none' : 'block';
            }
        };
        container.appendChild(btn);

        // 下拉菜单
        var menu = document.createElement('div');
        menu.id = 'ss-game-menu';
        menu.style.cssText = 'display:none;position:absolute;top:100%;left:0;margin-top:4px;background:#1B2838;border:1px solid #2A475E;border-radius:8px;min-width:200px;box-shadow:0 8px 30px rgba(0,0,0,0.5);z-index:99999;overflow:hidden;';

        // 标题
        var header = document.createElement('div');
        header.style.cssText = 'padding:10px 14px;font-size:12px;color:#8F98A0;border-bottom:1px solid #2A475E;font-weight:600;';
        header.textContent = '选择账号启动';
        menu.appendChild(header);

        // 账号列表
        accounts.forEach(function(acc) {
            var item = document.createElement('div');
            item.style.cssText = 'padding:10px 14px;cursor:pointer;font-size:13px;color:#C7D5E0;transition:background 0.15s;' + (acc.name === currentAccount ? 'color:#66C0F4;' : '');
            item.textContent = (acc.name === currentAccount ? '✓ ' : '') + acc.name;
            item.onclick = function(e) {
                e.stopPropagation();
                menu.style.display = 'none';
                sendMsg('switchAndLaunch', {
                    appId: parseInt(appId),
                    gameName: getGameName(),
                    targetSteamId: acc.steamId
                });
            };
            item.onmouseover = function() { this.style.background = 'rgba(255,255,255,0.08)'; };
            item.onmouseout = function() { this.style.background = 'transparent'; };
            menu.appendChild(item);
        });

        // 分隔线
        var sep = document.createElement('div');
        sep.style.cssText = 'height:1px;background:#2A475E;margin:4px 0;';
        menu.appendChild(sep);

        // 绑定按钮
        var bindBtn = document.createElement('div');
        bindBtn.style.cssText = 'padding:10px 14px;cursor:pointer;color:#66C0F4;font-size:12px;';
        bindBtn.textContent = '📌 绑定当前账号到此游戏';
        bindBtn.onclick = function(e) {
            e.stopPropagation();
            menu.style.display = 'none';
            sendMsg('bindGame', {
                appId: parseInt(appId),
                gameName: getGameName()
            });
        };
        bindBtn.onmouseover = function() { this.style.background = 'rgba(102,192,244,0.1)'; };
        bindBtn.onmouseout = function() { this.style.background = 'transparent'; };
        menu.appendChild(bindBtn);

        container.appendChild(menu);

        // 插入到开始游戏按钮后面
        if (playBtn.nextSibling) {
            playBtn.parentNode.insertBefore(container, playBtn.nextSibling);
        } else {
            playBtn.parentNode.appendChild(container);
        }

        // 点击其他地方关闭菜单
        document.addEventListener('click', function() {
            var m = document.getElementById('ss-game-menu');
            if (m) m.style.display = 'none';
        });
    }

    // 获取游戏名称
    function getGameName() {
        var el = document.querySelector('[class*="AppName"]') ||
                 document.querySelector('[class*="apphub_AppName"]') ||
                 document.querySelector('h1');
        return el ? el.textContent.trim() : 'Unknown Game';
    }

    // 定期检查
    setInterval(injectIfNeeded, 1000);

    // 初始化
    connectWS();
})();
