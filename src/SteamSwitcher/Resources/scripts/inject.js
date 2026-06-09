var wsPort = __WS_PORT__;
var ws = null;
var accounts = [];
var currentAccount = null;

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
                    updatePanel();
                } else if (msg.action === 'switchResult') {
                    if (msg.success) {
                        showNotification('已切换到 ' + msg.accountName);
                        currentAccount = msg.accountName;
                        ws.send(JSON.stringify({ action: 'getAccounts' }));
                    }
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
    n.style.cssText = 'position:fixed;top:20px;right:20px;z-index:999999;background:#1B2838;color:white;padding:16px 24px;border-radius:10px;font-size:14px;box-shadow:0 8px 30px rgba(0,0,0,0.5);';
    n.textContent = text;
    document.body.appendChild(n);
    setTimeout(function() { n.remove(); }, 3000);
}

function updatePanel() {
    var list = document.querySelector('.ss-list');
    if (!list) return;
    list.innerHTML = '';
    accounts.forEach(function(acc) {
        var item = document.createElement('div');
        item.className = 'steamswitch-acc' + (acc.name === currentAccount ? ' active' : '');
        var html = '<div><div class="ss-name">' + acc.name + '</div><div class="ss-user">' + acc.username + '</div></div>';
        if (acc.name === currentAccount) html += '<span class="ss-badge">当前</span>';
        item.innerHTML = html;
        item.onclick = function() { sendMsg('switchAccount', { steamId: acc.steamId }); };
        list.appendChild(item);
    });
}

function createUI() {
    if (document.getElementById('steamswitch-float-btn')) return;
    var btn = document.createElement('button');
    btn.id = 'steamswitch-float-btn';
    btn.className = 'steamswitch-float-btn';
    btn.innerHTML = '⚡ Steam Switch';
    btn.onclick = function(e) {
        e.stopPropagation();
        var panel = document.getElementById('steamswitch-panel');
        if (panel) panel.classList.toggle('show');
    };
    document.body.appendChild(btn);

    var panel = document.createElement('div');
    panel.id = 'steamswitch-panel';
    panel.className = 'steamswitch-panel';
    panel.innerHTML = '<h3>⚡ 快速切换账号</h3><div class="ss-list"></div>';
    document.body.appendChild(panel);

    document.addEventListener('click', function(e) {
        if (!panel.contains(e.target) && e.target !== btn) panel.classList.remove('show');
    });
    updatePanel();
}

createUI();
connectWS();
