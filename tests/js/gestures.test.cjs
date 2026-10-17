const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

function setup() {
    const handlers = {}, frames = [], timers = new Map();
    let nextTimer = 0, now = 0;
    const windowHandlers = {};
    const element = () => ({
        classList: { toggle() {}, contains() { return false; }, add() {}, remove() {} },
        style: {}, dataset: {}, addEventListener() {}, setAttribute() {}, appendChild() {},
        querySelector() { return element(); }, querySelectorAll() { return []; }
    });
    const document = {
        documentElement: element(), body: element(), activeElement: null,
        addEventListener(name, callback) { (handlers[name] ??= []).push(callback); },
        createElement: element, querySelector() { return null; }, querySelectorAll() { return []; }
    };
    const window = { addEventListener(name, callback) { (windowHandlers[name] ??= []).push(callback); }, matchMedia() { return { matches: false }; }, scrollY: 0 };
    vm.runInNewContext(readFileSync(join(__dirname, '../../src/KeepmarkMobile/wwwroot/js/gestures.js'), 'utf8'), {
        window, document, navigator: {}, performance: { now: () => now }, Date: { now: () => now },
        requestAnimationFrame(callback) { frames.push(callback); return frames.length; },
        setTimeout(callback) { const id = ++nextTimer; timers.set(id, callback); return id; },
        clearTimeout(id) { timers.delete(id); }
    });
    const emit = (name, event) => handlers[name]?.forEach(fn => fn(event));
    const row = element();
    row.dataset = { rowid: '7', nodeid: '42', contentVersion: '19' };
    row.isConnected = true;
    const classes = new Set();
    row.classList = { add: name => classes.add(name), remove: name => classes.delete(name), contains: name => classes.has(name) };
    const event = x => ({ target: { closest(selector) { return selector === '.xbs-row' ? row : null; } },
        clientX: x, clientY: 0, pointerId: 1, cancelable: true, preventDefault() {} });
    const swipe = () => { emit('pointerdown', event(0)); emit('pointermove', event(90)); emit('pointerup', event(90)); };
    return { api: window.xbsGestures, frames, timers, emit, event, swipe, row,
        setNow(value) { now = value; },
        emitWindow(name) { windowHandlers[name]?.forEach(fn => fn()); },
        fireTimers() { const pending = [...timers.values()]; timers.clear(); pending.forEach(fn => fn()); }
    };
}

test('late disposal does not release the new Home; current disposal stops gestures', () => {
    const ui = setup(), calls = [];
    ui.api.init({ invokeMethodAsync() { calls.push('old'); return Promise.resolve(); } }, 'old');
    ui.api.init({ invokeMethodAsync(method, id) { calls.push([method, id]); return Promise.resolve(); } }, 'new');
    ui.api.release('old');
    ui.swipe();
    assert.deepEqual(calls, [['OnRowSwipeRight', 7]]);
    ui.api.release('new');
    ui.swipe();
    assert.equal(calls.length, 1);
});

test('disposal cancels an outstanding long press', () => {
    const ui = setup();
    ui.api.init({ invokeMethodAsync() { throw Error('disposed'); } }, 'page');
    ui.emit('pointerdown', ui.event(0));
    assert.equal(ui.timers.size, 1);
    ui.api.release('page');
    assert.equal(ui.timers.size, 0);
});

test('multiple layout requests are coalesced into one animation frame', () => {
    const ui = setup();
    for (let i = 0; i < 20; i++) ui.api.layoutBrowse();
    assert.equal(ui.frames.length, 1);
    ui.frames[0]();
    ui.api.layoutBrowse();
    assert.equal(ui.frames.length, 2);
});

function recording(ui) {
    const calls = [];
    ui.api.init({ invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } }, 'page');
    return calls;
}

test('long press captures stable identity and content version and removes press highlight', () => {
    const ui = setup(), calls = recording(ui);
    ui.emit('pointerdown', ui.event(0));
    assert.equal(ui.row.classList.contains('xbs-pressing'), true);
    ui.fireTimers();
    assert.deepEqual(calls, [['OnRowLongPress', 7, 42, 19]]);
    assert.equal(ui.row.classList.contains('xbs-pressing'), false);
});

test('a row reused or changed after pointerdown never invokes long press', () => {
    for (const change of [row => row.dataset.nodeid = '43', row => row.dataset.contentVersion = '20', row => row.isConnected = false]) {
        const ui = setup(), calls = recording(ui);
        ui.emit('pointerdown', ui.event(0));
        change(ui.row);
        ui.fireTimers();
        assert.deepEqual(calls, []);
    }
});

test('holding beyond initial suppression still suppresses the finger-up click', () => {
    const ui = setup(); recording(ui);
    ui.emit('pointerdown', ui.event(0));
    ui.setNow(500); ui.fireTimers();
    ui.setNow(2500); ui.emit('pointerup', ui.event(0));
    let stopped = false, prevented = false;
    ui.emit('click', { target: { closest() { return ui.row; } },
        stopPropagation() { stopped = true; }, preventDefault() { prevented = true; } });
    assert.equal(stopped, true);
    assert.equal(prevented, true);
});

test('selection disables swipe and long press without preventing vertical scroll', () => {
    const ui = setup(), calls = recording(ui);
    ui.row.dataset.selection = '1';
    let prevented = false;
    const move = { ...ui.event(90), clientY: 100, preventDefault() { prevented = true; } };
    ui.emit('pointerdown', ui.event(0)); ui.emit('pointermove', move); ui.emit('pointerup', move);
    ui.fireTimers();
    assert.deepEqual(calls, []);
    assert.equal(prevented, false);
    ui.emit('pointerdown', ui.event(0)); ui.fireTimers();
    assert.deepEqual(calls, []);
});

test('pointer cancellation and scroll clear pending presses; cancelled swipes never execute', () => {
    for (const cancel of [ui => ui.emit('pointercancel', ui.event(0)), ui => ui.emitWindow('scroll')]) {
        const ui = setup(), calls = recording(ui);
        ui.emit('pointerdown', ui.event(0)); cancel(ui); ui.fireTimers();
        assert.deepEqual(calls, []);
        assert.equal(ui.row.classList.contains('xbs-pressing'), false);
    }
    const ui = setup(), calls = recording(ui);
    ui.emit('pointerdown', ui.event(0)); ui.emit('pointermove', ui.event(90));
    ui.emit('pointercancel', ui.event(90)); ui.emit('pointerup', ui.event(90));
    assert.deepEqual(calls, []);
});
