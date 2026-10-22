// Run with: node tests/cef-session-regression.cjs
// Executes the shipped injection script in an isolated fake DOM; no browser or Steam is opened.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/SteamSwitcher/Core/SteamCEFInjector.cs'), 'utf8').replace(/\r\n/g, '\n');
const start = source.indexOf('var js = @"') + 'var js = @"'.length;
const end = source.indexOf('"\n                .Replace("__ACCOUNTS__"', start);
assert.ok(start > 0 && end > start, 'Locate the production C# verbatim script');
const template = source.slice(start, end).replaceAll('""', '"');
const timers = new Map();
const frames = [];
const sockets = [];
let nextTimer = 0;
class Events {
    listeners = new Map();
    addEventListener(name, handler) { this.listeners.set(handler, name); }
    removeEventListener(name, handler) { this.listeners.delete(handler); }
}
class Element extends Events {
    children = []; parentNode = null; textContent = ''; className = ''; id = ''; style = {}; dataset = {};
    constructor(tag) {
        super(); this.tagName = tag;
        this.classList = {
            contains: value => this.className.split(' ').includes(value),
            add: value => { if (!this.classList.contains(value)) this.className += ' ' + value; },
            remove: value => { this.className = this.className.split(' ').filter(x => x !== value).join(' '); },
            toggle: (value, on) => on ? this.classList.add(value) : this.classList.remove(value)
        };
    }
    get parentElement() { return this.parentNode; }
    get nextSibling() { return this.parentNode?.children[this.parentNode.children.indexOf(this) + 1] ?? null; }
    get previousElementSibling() { return this.parentNode?.children[this.parentNode.children.indexOf(this) - 1] ?? null; }
    appendChild(child) { child.remove(); this.children.push(child); child.parentNode = this; }
    insertBefore(child, anchor) {
        child.remove(); const index = this.children.indexOf(anchor);
        this.children.splice(index < 0 ? this.children.length : index, 0, child); child.parentNode = this;
    }
    remove() {
        if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1);
        this.parentNode = null;
    }
    all() { return this.children.flatMap(child => [child, ...child.all()]); }
    querySelectorAll(selector) { return this.all().filter(node => selector[0] === '.' && node.classList.contains(selector.slice(1))); }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
    closest(selector) { return this.classList.contains(selector.slice(1)) ? this : this.parentNode?.closest(selector) ?? null; }
    contains(target) { return target === this || this.all().includes(target); }
    getBoundingClientRect() { return { left: 10, right: 100, top: 20, bottom: 60, width: 90, height: 40 }; }
}
const document = new Events();
document.body = new Element('body'); document.head = new Element('head'); document.documentElement = new Element('html');
document.title = 'Example Game';
const button = new Element('button'); button.textContent = 'Play'; document.body.appendChild(button);
const title = new Element('h1'); title.textContent = 'Example Game';
document.createElement = tag => new Element(tag);
document.getElementById = id => [...document.body.all(), ...document.head.all()].find(node => node.id === id) ?? null;
document.querySelectorAll = selector => selector.includes('AppStateButton') ? [button] : [];
document.querySelector = selector => selector.includes('AppName') ? title : null;
class FakeSocket {
    static OPEN = 1; static CLOSED = 3;
    readyState = 0; sent = []; closed = false;
    constructor(url, protocols) { this.url = url; this.protocols = protocols; sockets.push(this); }
    send(message) { this.sent.push(JSON.parse(message)); }
    open() { this.readyState = FakeSocket.OPEN; this.onopen?.(); }
    close() { this.closed = true; this.readyState = FakeSocket.CLOSED; this.onclose?.(); }
}
const window = new Events(); window.location = { href: 'https://steamloopback.host/library/app/123' };
const context = vm.createContext({ window, document, WebSocket: FakeSocket, console: { log() {} },
    MutationObserver: class { constructor(callback) { this.callback = callback; } observe() {} disconnect() {} },
    setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; },
    setInterval: callback => { timers.set(++nextTimer, callback); return nextTimer; },
    clearTimeout: id => timers.delete(id), clearInterval: id => timers.delete(id),
    requestAnimationFrame: callback => { frames.push(callback); return frames.length; }
});
const account = name => ({ steamId: name, accountName: name, displayName: name });
function inject(session, token, accounts = [account('alice')]) {
    let script = template;
    for (const [key, value] of Object.entries({ __TARGET_APP_ID__: '123', __SESSION_ID__: JSON.stringify(session),
        __WS_URL__: JSON.stringify('ws://localhost:8081/'), __WS_TOKEN__: JSON.stringify(token),
        __ACCOUNTS__: JSON.stringify(accounts), __BINDINGS__: '[]', __STYLE__: '""' })) script = script.replaceAll(key, value);
    vm.runInContext(script, context);
}
inject('session-one', 'token-one'); sockets[0].open();
assert.equal(sockets.length, 1);
assert.ok(sockets[0].sent.some(message => message.action === 'hello' && message.sessionId === 'session-one'));
assert.equal(document.listeners.size + window.listeners.size, 3);
inject('session-two', 'token-two'); sockets[1].open();
assert.equal(sockets.length, 2);
assert.ok(sockets[0].closed, 'A new host session closes the old authenticated socket');
assert.deepEqual(Array.from(sockets[1].protocols), ['token-two']);
assert.equal(document.listeners.size + window.listeners.size, 3, 'No duplicate document/window listeners');
inject('session-two', 'token-two', [account('bob')]);
assert.equal(sockets.length, 2, 'Same-session refresh reuses its authenticated channel');
assert.equal(document.getElementById('steamswitch-launch-menu').querySelectorAll('.steamswitch-menu-item')[0].dataset.steamId, 'bob');
assert.equal(document.listeners.size + window.listeners.size, 3, 'Account refresh replaces obsolete menu listeners');
window.__steamswitch_cleanup();
assert.ok(sockets[1].closed);
assert.equal(document.listeners.size + window.listeners.size, 0);
assert.equal(timers.size, 0);
assert.equal(document.getElementById('steamswitch-launch-wrap'), null);
assert.equal(document.getElementById('steamswitch-launch-menu'), null);
assert.equal(document.getElementById('steamswitch-style'), null);
assert.equal(window.__steamswitch_session_id, undefined);
console.log('CEF session regression: old token replacement, authenticated hello, same-session refresh, DOM/listener/timer cleanup passed.');
