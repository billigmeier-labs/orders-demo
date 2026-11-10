// The console's only script: polls live regions, posts actions, opens explainers.
(() => {
  'use strict';

  const fetchHeaders = { 'X-Console-Fetch': '1' };

  function localTimes(root) {
    root.querySelectorAll('time[datetime]').forEach(t => {
      const d = new Date(t.getAttribute('datetime'));
      if (!isNaN(d.getTime())) {
        t.textContent = d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
        t.title = d.toLocaleString('en-US', { hour12: true });
      }
    });
  }

  async function refresh(el) {
    try {
      const res = await fetch(el.dataset.pollUrl, { headers: fetchHeaders, cache: 'no-store' });
      if (res.status === 401) { location.reload(); return; }
      if (!res.ok) { el.dataset.pollError = String(res.status); return; }
      // Keep expanded <details data-keep-open="key"> open across the refresh.
      const open = new Set(Array.from(el.querySelectorAll('details[data-keep-open][open]')).map(d => d.dataset.keepOpen));
      // Keep what was typed in <input data-keep-value="key"> (and its focus) across the refresh too.
      const values = new Map(Array.from(el.querySelectorAll('input[data-keep-value]')).map(i => [i.dataset.keepValue, i.value]));
      const focused = document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.keepValue : undefined;
      el.innerHTML = await res.text();
      el.querySelectorAll('details[data-keep-open]').forEach(d => { if (open.has(d.dataset.keepOpen)) d.open = true; });
      el.querySelectorAll('input[data-keep-value]').forEach(i => {
        if (values.has(i.dataset.keepValue)) i.value = values.get(i.dataset.keepValue);
        if (focused && i.dataset.keepValue === focused) i.focus();
      });
      // The header's "Step N of M" is outside the polled region; copy it from the fresh content.
      const counter = el.querySelector('[data-step-counter]');
      const header = document.querySelector('[data-header-step]');
      if (counter && header) header.textContent = counter.textContent;
      delete el.dataset.pollError;
      el.dataset.pollAt = new Date().toISOString();
      localTimes(el);
    } catch (err) {
      el.dataset.pollError = 'network';
    }
  }

  const regions = () => Array.from(document.querySelectorAll('[data-poll-url]'));
  const refreshAll = () => Promise.all(regions().map(refresh));
  regions().forEach(el => {
    const seconds = Math.max(1, Number(el.dataset.pollSeconds || 5));
    setInterval(() => { if (!document.hidden) refresh(el); }, seconds * 1000);
  });
  localTimes(document);

  const toastEl = document.getElementById('toast');
  let toastTimer;
  function toast(message, isError) {
    if (!toastEl) return;
    toastEl.textContent = message;
    toastEl.classList.toggle('error', !!isError);
    toastEl.dataset.kind = isError ? 'error' : 'info';
    toastEl.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toastEl.hidden = true; }, isError ? 8000 : 4000);
  }

  // data-minutes-input="id" adds durationMinutes from that input to the action's JSON body.
  function actionBody(btn) {
    const body = btn.dataset.actionBody || '{}';
    const input = btn.dataset.minutesInput && document.getElementById(btn.dataset.minutesInput);
    if (!input) return body;
    const parsed = JSON.parse(body);
    parsed.durationMinutes = Math.min(120, Math.max(1, Math.round(Number(input.value)) || 15));
    return JSON.stringify(parsed);
  }

  async function runAction(btn) {
    if (btn.dataset.busy) return;
    if (btn.dataset.confirm && !window.confirm(btn.dataset.confirm)) return;
    btn.dataset.busy = 'true';
    btn.disabled = true;
    try {
      const res = await fetch(btn.dataset.actionUrl, {
        method: 'POST',
        headers: { ...fetchHeaders, 'Content-Type': 'application/json', 'X-Console-Action': '1' },
        body: actionBody(btn),
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        toast(body.detail || body.title || ('Failed with ' + res.status), true);
      } else if (body.lines && body.lines.length) {
        toast(body.lines.map(l => l.text).join(' · '));
      }
    } catch (err) {
      toast('Request failed: ' + err.message, true);
    } finally {
      delete btn.dataset.busy;
      btn.disabled = false;
      await refreshAll();
    }
  }

  const drawer = document.getElementById('explainer');
  let lastFocus = null;
  function openExplainer(id) {
    const tpl = document.getElementById('explain-' + id);
    if (!drawer || !tpl) return;
    lastFocus = document.activeElement;
    document.getElementById('explainer-title').textContent = tpl.dataset.name;
    const body = document.getElementById('explainer-body');
    body.replaceChildren(tpl.content.cloneNode(true));
    drawer.hidden = false;
    drawer.dataset.open = 'true';
    drawer.dataset.explainId = id;
    drawer.querySelector('[data-explain-close]').focus();
  }
  function closeExplainer() {
    if (!drawer || drawer.hidden) return;
    drawer.hidden = true;
    drawer.dataset.open = 'false';
    drawer.dataset.explainId = '';
    if (lastFocus && lastFocus.focus) lastFocus.focus();
  }

  document.addEventListener('click', e => {
    const action = e.target.closest('[data-action-url]');
    if (action) { e.preventDefault(); runAction(action); return; }
    const explain = e.target.closest('[data-explain]');
    if (explain) { e.preventDefault(); openExplainer(explain.dataset.explain); return; }
    if (e.target.closest('[data-explain-close]') || e.target === drawer) closeExplainer();
  });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeExplainer(); });
})();
