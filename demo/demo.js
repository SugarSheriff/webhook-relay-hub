// In-browser replica of Relay Hub's rules (see src/RelayHub in this repo).
// Signatures are real HMAC-SHA256 via Web Crypto; retry timing is sped up 4x.
(() => {
  'use strict';

  const SPEED = 4;                 // simulated seconds per real second
  const MAX_ATTEMPTS = 5;          // same as appsettings.json
  const BASE_DELAY = 2;            // seconds, doubles after each failure
  const MAX_DELAY = 300;

  const SOURCES = {
    github: { secret: 'gh-demo-secret', header: 'X-Hub-Signature-256', targets: ['echo', 'flaky'] },
    stripe: { secret: 'stripe-demo-secret', header: 'Stripe-Signature', targets: ['echo', 'billing'] },
  };

  const state = {
    messages: [],
    seen: new Map(),           // `${source}|${deliveryId}` -> message, like the unique index
    counts: { verified: 0, rejected: 0, stored: 0, dupes: 0 },
    billingOnline: false,
    lastRequest: null,
    simStart: performance.now(),
  };

  const $ = id => document.getElementById(id);
  const now = () => (performance.now() - state.simStart) / 1000 * SPEED;   // sim seconds
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const uuid = () => (crypto.randomUUID ? crypto.randomUUID()
    : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
      }));

  // ---------- signatures (same idea as SignatureVerifier.cs) ----------
  const enc = new TextEncoder();
  async function sign(body, secret) {
    const key = await crypto.subtle.importKey('raw', enc.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const mac = await crypto.subtle.sign('HMAC', key, enc.encode(body));
    return [...new Uint8Array(mac)].map(b => b.toString(16).padStart(2, '0')).join('');
  }
  function fixedTimeEquals(a, b) {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    return diff === 0;
  }

  // ---------- senders ----------
  function payloadFor(source) {
    if (source === 'github') {
      return { event: 'push', body: { ref: 'refs/heads/main', repository: 'acme/storefront', commits: 1 + (Math.random() * 4 | 0), pusher: 'octocat' } };
    }
    return { event: 'payment_intent.succeeded', body: { id: 'pi_' + uuid().slice(0, 12), amount: (5 + Math.random() * 495 | 0) * 100, currency: 'usd' } };
  }

  async function send(source, { forged = false } = {}) {
    const cfg = SOURCES[source];
    const { event, body } = payloadFor(source);
    const raw = JSON.stringify(body);
    const request = {
      source, event, raw,
      deliveryId: uuid(),
      signature: 'sha256=' + await sign(raw, forged ? 'not-the-secret' : cfg.secret),
    };
    if (!forged) {
      state.lastRequest = request;
      $('dup').disabled = false;
    }
    return receive(request);
  }

  // ---------- 1. receive + 2. store (same flow as POST /webhooks/{source}) ----------
  async function receive(req) {
    const cfg = SOURCES[req.source];
    const expected = 'sha256=' + await sign(req.raw, cfg.secret);

    if (!fixedTimeEquals(expected, req.signature)) {
      state.counts.rejected++;
      pulse('st-receive', 'bad');
      log('warn', `Rejected webhook from ${req.source}: bad or missing signature → 401`);
      render();
      return 401;
    }
    state.counts.verified++;
    pulse('st-receive', 'ok');

    const key = `${req.source}|${req.deliveryId}`;
    if (state.seen.has(key)) {
      state.counts.dupes++;
      pulse('st-store', 'warn');
      log('info', `Duplicate ${short(req.deliveryId)} from ${req.source} ignored → 200 {duplicate:true}`);
      render();
      return 200;
    }

    const message = {
      id: uuid(), source: req.source, event: req.event, deliveryId: req.deliveryId,
      receivedAt: now(), payload: req.raw,
      deliveries: cfg.targets.map(t => ({ id: uuid(), target: t, status: 'Pending', attempts: 0, nextAt: now(), lastError: null })),
    };
    state.seen.set(key, message);
    state.messages.unshift(message);
    state.counts.stored++;
    pulse('st-store', 'ok');
    log('info', `Accepted ${short(req.deliveryId)} from ${req.source} for ${message.deliveries.length} target(s) → 202`);
    render();
    return 202;
  }

  // ---------- 3. deliver (same rules as DeliveryWorker.cs) ----------
  function targetResponds(target) {
    if (target === 'echo') return { ok: true };
    if (target === 'flaky') return Math.random() < 0.6 ? { ok: false, error: 'HTTP 503 Service Unavailable' } : { ok: true };
    if (target === 'billing') return state.billingOnline ? { ok: true } : { ok: false, error: 'Connection refused' };
    return { ok: false, error: 'Unknown target' };
  }

  function delayAfter(failedAttempts) {
    return Math.min(BASE_DELAY * 2 ** Math.max(0, failedAttempts - 1), MAX_DELAY);
  }

  function workerTick() {
    const t = now();
    let changed = false;
    for (const m of state.messages) {
      for (const d of m.deliveries) {
        if (d.status !== 'Pending' || d.nextAt > t) continue;
        changed = true;
        d.attempts++;
        const res = targetResponds(d.target);
        hit(d.target, res.ok);
        if (res.ok) {
          d.status = 'Delivered';
          d.lastError = null;
          d.deliveredAt = t;
          pulse('st-deliver', 'ok');
          log('info', `Delivered ${short(m.deliveryId)} to ${d.target} on attempt ${d.attempts}`);
        } else if (d.attempts >= MAX_ATTEMPTS) {
          d.lastError = res.error;
          d.status = 'DeadLettered';
          pulse('st-deliver', 'bad');
          log('error', `Dead-lettered ${short(m.deliveryId)} for ${d.target} after ${d.attempts} attempts: ${res.error}`);
        } else {
          d.lastError = res.error;
          const wait = delayAfter(d.attempts);
          d.nextAt = t + wait;
          pulse('st-deliver', 'warn');
          log('warn', `Delivery of ${short(m.deliveryId)} to ${d.target} failed (${res.error}); retrying in ${wait}s`);
        }
      }
    }
    if (changed) render(); else renderClock();
  }

  function replay(delivery) {
    delivery.status = 'Pending';
    delivery.attempts = 0;
    delivery.nextAt = now();
    delivery.lastError = null;
    log('info', `Replay requested for ${delivery.target}`);
    render();
  }

  function replayAllDead() {
    const dead = allDeliveries().filter(d => d.status === 'DeadLettered');
    dead.forEach(replay);
    return dead.length;
  }

  function setBilling(online) {
    state.billingOnline = online;
    $('billing').setAttribute('aria-pressed', String(online));
    $('billing').querySelector('b').textContent = online ? 'online' : 'offline';
    $('billing-sub').textContent = online ? 'back online' : 'offline';
    $('t-billing').querySelector('.tdot').className = 'tdot ' + (online ? 'up' : 'down');
    log('info', `billing is now ${online ? 'online' : 'offline'}`);
  }

  // ---------- rendering ----------
  const allDeliveries = () => state.messages.flatMap(m => m.deliveries);
  const short = id => id.slice(0, 8);
  const fmt = s => `${s.toFixed(0)}s`;

  function el(tag, props = {}, ...children) {
    const n = Object.assign(document.createElement(tag), props);
    n.append(...children.filter(c => c != null && c !== false));
    return n;
  }

  function pulse(id, kind) {
    const node = $(id);
    node.classList.remove('pulse-ok', 'pulse-warn', 'pulse-bad');
    void node.offsetWidth;
    node.classList.add('pulse-' + kind);
    clearTimeout(node._t);
    node._t = setTimeout(() => node.classList.remove('pulse-' + kind), 700);
  }

  function hit(target, ok) {
    const node = $('t-' + target);
    node.classList.add(ok ? 'hit-ok' : 'hit-bad');
    setTimeout(() => node.classList.remove('hit-ok', 'hit-bad'), 600);
  }

  function log(level, text) {
    const li = el('li', { className: level },
      el('time', {}, fmt(now()).padStart(4, ' ')),
      el('span', { className: 'lvl' }, level === 'info' ? 'info' : level === 'warn' ? 'warn' : 'fail'),
      text);
    const list = $('log');
    list.prepend(li);
    while (list.children.length > 200) list.lastChild.remove();
  }

  const labels = { Pending: 'retrying', Delivered: 'delivered', DeadLettered: 'dead letter' };

  function deliveryDetail(d) {
    if (d.status === 'Delivered') return `attempt ${d.attempts}`;
    if (d.status === 'DeadLettered') return `${d.attempts} attempts · ${d.lastError}`;
    if (d.attempts === 0) return 'queued';
    return `attempt ${d.attempts} · next in ${fmt(Math.max(0, d.nextAt - now()))} · ${d.lastError}`;
  }

  function renderMessage(m) {
    return el('article', { className: 'msg' },
      el('div', { className: 'msg-head' },
        el('strong', {}, m.source),
        el('span', { className: 'badge' }, m.event),
        el('span', { className: 'mono' }, m.deliveryId)),
      ...m.deliveries.map(d => el('div', { className: 'dl' },
        el('span', {}, d.target),
        el('span', { className: d.status }, labels[d.status]),
        el('span', { className: 'err', title: d.lastError || '' }, deliveryDetail(d)),
        el('button', { type: 'button', onclick: () => replay(d) }, 'Replay'))));
  }

  function renderClock() {
    $('clock').textContent = `sim time ${fmt(now())} · ${SPEED}× speed`;
    // keep "next in" countdowns fresh without rebuilding everything every tick
    document.querySelectorAll('#messages .msg').forEach((node, i) => {
      const m = state.messages[i];
      if (!m) return;
      node.querySelectorAll('.dl .err').forEach((span, j) => { span.textContent = deliveryDetail(m.deliveries[j]); });
    });
  }

  function render() {
    const c = state.counts;
    const ds = allDeliveries();
    const by = s => ds.filter(d => d.status === s).length;
    $('c-verified').textContent = c.verified;
    $('c-rejected').textContent = c.rejected;
    $('c-stored').textContent = c.stored;
    $('c-dupes').textContent = c.dupes;
    $('c-delivered').textContent = by('Delivered');
    $('c-pending').textContent = by('Pending');
    $('c-dead').textContent = by('DeadLettered');
    $('replayAll').disabled = by('DeadLettered') === 0;

    const list = $('messages');
    list.replaceChildren(...(state.messages.length
      ? state.messages.slice(0, 30).map(renderMessage)
      : [el('p', { className: 'empty' }, 'No messages yet. Send one above.')]));
    renderClock();
  }

  // ---------- guided tour ----------
  let touring = false;
  const caption = text => { $('caption').textContent = text; };

  async function waitFor(predicate, timeoutMs) {
    const end = performance.now() + timeoutMs;
    while (!predicate() && performance.now() < end) await sleep(200);
  }

  async function tour() {
    if (touring) return;
    touring = true;
    $('tour').disabled = true;
    try {
      caption('1/6 · A GitHub push arrives. The signature checks out, so it\'s stored and fanned out to echo and flaky.');
      await send('github');
      await sleep(4500);

      caption('2/6 · GitHub resends when it isn\'t sure we got it. Same delivery ID, so it\'s recognised and stored only once.');
      await receive(state.lastRequest);
      await sleep(4000);

      caption('3/6 · Someone fakes a webhook without the secret. Rejected with 401 before it touches the database.');
      await send('github', { forged: true });
      await sleep(4000);

      if (state.billingOnline) setBilling(false);
      caption('4/6 · A Stripe payment arrives, but billing is offline. Watch it retry with growing gaps: 2s, 4s, 8s, 16s.');
      await send('stripe');
      const stripeMsg = state.messages[0];
      const billing = stripeMsg.deliveries.find(d => d.target === 'billing');
      await waitFor(() => billing.status === 'DeadLettered', 20000);

      caption('5/6 · After 5 failed attempts it becomes a dead letter. Parked, with its error, instead of disappearing.');
      await sleep(4000);

      caption('6/6 · Billing comes back online. One replay, and the payment is delivered.');
      setBilling(true);
      await sleep(1500);
      replay(billing);
      await waitFor(() => billing.status === 'Delivered', 5000);
      await sleep(1200);

      caption('Nothing lost, nothing doubled, nothing forged. Your turn: use the controls below.');
    } finally {
      touring = false;
      $('tour').disabled = false;
    }
  }

  // ---------- wiring ----------
  function init() {
    if (!window.crypto || !crypto.subtle) {
      caption('This demo needs a secure context (https or localhost) for Web Crypto signatures.');
      document.querySelectorAll('button').forEach(b => { b.disabled = true; });
      return;
    }
    document.querySelectorAll('[data-send]').forEach(b => b.addEventListener('click', () => send(b.dataset.send)));
    $('dup').addEventListener('click', () => state.lastRequest && receive(state.lastRequest));
    $('forge').addEventListener('click', () => send('github', { forged: true }));
    $('billing').addEventListener('click', () => setBilling(!state.billingOnline));
    $('replayAll').addEventListener('click', replayAllDead);
    $('clearLog').addEventListener('click', () => $('log').replaceChildren());
    $('tour').addEventListener('click', tour);

    log('info', 'Relay Hub started · sources: github, stripe · targets: echo, flaky, billing');
    render();
    setInterval(workerTick, 250);
  }

  init();
})();
