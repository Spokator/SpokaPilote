/* SpokaPilote Server — console web (JavaScript sans framework ni dépendance).
 * Le jeton administrateur est conservé dans sessionStorage : il disparaît à la fermeture de l'onglet.
 * Aucun HTML n'est construit sans échappement (fonction esc) ; aucun script ni style en ligne (CSP stricte). */
'use strict';

(() => {
  const TOKEN_KEY = 'spokapilote.adminToken';
  const REFRESH_MS = 30000;

  const state = {
    token: null,
    version: '',
    page: null,          // page affichée : { refresh?, canLeave? }
    timer: null,
    machines: { search: '', status: 'all', sort: 'status', dir: 'asc' },
    missing: { search: '' },
  };

  // ------------------------------------------------------------------ utilitaires

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const enc = encodeURIComponent;
  const plural = (n, one, many) => `${n} ${n > 1 ? many : one}`;
  /** « Microsoft Windows 11 Professionnel 25H2 » → « Windows 11 Professionnel 25H2 » (colonnes étroites). */
  const shortOs = (os) => String(os || '').replace(/^Microsoft\s+/i, '');

  const dateFmt = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
  const timeFmt = new Intl.DateTimeFormat('fr-FR', { timeStyle: 'medium' });
  const fmtDate = (iso) => (iso ? dateFmt.format(new Date(iso)) : '—');
  function fmtAgo(iso) {
    if (!iso) return '';
    const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
    if (s < 60) return 'à l\'instant';
    if (s < 3600) return `il y a ${Math.floor(s / 60)} min`;
    if (s < 86400) return `il y a ${Math.floor(s / 3600)} h`;
    const d = Math.floor(s / 86400);
    return `il y a ${plural(d, 'jour', 'jours')}`;
  }

  class HttpError extends Error {
    constructor(status, message) { super(message); this.status = status; }
  }

  /** Appel de l'API (chemin relatif : la console fonctionne aussi publiée sous un sous-dossier du reverse proxy). */
  async function api(path, { method = 'GET', body, raw = false } = {}) {
    const headers = { Authorization: `Bearer ${state.token}` };
    if (body !== undefined) headers['Content-Type'] = 'application/json; charset=utf-8';
    let res;
    try {
      res = await fetch(`api${path}`, { method, headers, body, cache: 'no-store' });
    } catch {
      throw new HttpError(0, 'Console injoignable : vérifiez la connexion réseau.');
    }
    if (res.status === 401) {
      logout('Session expirée ou jeton refusé : reconnectez-vous.');
      throw new HttpError(401, 'Jeton refusé.');
    }
    if (!res.ok) {
      let message = `Erreur ${res.status}`;
      try { message = (await res.json()).error || message; } catch { /* corps non JSON */ }
      throw new HttpError(res.status, message);
    }
    if (raw) return { status: res.status, text: res.status === 204 ? null : await res.text(), lastModified: res.headers.get('Last-Modified'), response: res };
    return res.status === 204 ? null : res.json();
  }

  let toastTimer;
  function toast(message, bad = false) {
    const el = $('#toast');
    el.textContent = message;
    el.classList.toggle('is-bad', bad);
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.hidden = true; }, bad ? 6000 : 3000);
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Contexte non sécurisé (HTTP hors localhost) : repli sur la sélection classique.
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.className = 'offscreen';
      document.body.appendChild(area);
      area.select();
      document.execCommand('copy');
      area.remove();
    }
    toast('Copié dans le presse-papiers.');
  }

  function download(name, blob) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  const badge = (text, kind = '') => `<span class="badge${kind ? ` badge-${kind}` : ''}">${esc(text)}</span>`;
  const num = (n, kind) => `<td class="num${n > 0 && kind ? ` ${kind}` : ''}">${esc(n)}</td>`;
  const kpi = (label, value, { kind = '', href = '', sub = '' } = {}) => {
    const cls = `kpi${kind ? ` is-${kind}` : ''}`;
    const inner = `<div class="kpi-label">${esc(label)}</div><div class="kpi-value">${esc(value)}</div>${sub ? `<div class="kpi-sub">${esc(sub)}</div>` : ''}`;
    return href ? `<a class="${cls}" href="${esc(href)}">${inner}</a>` : `<div class="${cls}">${inner}</div>`;
  };
  const machineLink = (name) => `<a href="#/postes/${enc(name)}">${esc(name)}</a>`;
  const codeBlock = (text) => `<div class="code"><pre>${esc(text)}</pre><button class="btn btn-sm" type="button" data-copy="${esc(text)}">Copier</button></div>`;

  function machineBadges(m) {
    const out = [m.healthy ? badge('Sain', 'ok') : badge('À traiter', 'warn')];
    if (m.stale) out.push(badge('Non vu', 'bad'));
    if (m.hardwareFaults > 0) out.push(badge('Panne matérielle', 'warn'));
    if (m.rebootRequired) out.push(badge('Redémarrage requis', 'accent'));
    return `<div class="badges">${out.join('')}</div>`;
  }

  // ------------------------------------------------------------------ connexion

  function showLogin(message) {
    stopTimer();
    $('#app').hidden = true;
    $('#login').hidden = false;
    const err = $('#login-error');
    err.textContent = message || '';
    err.hidden = !message;
    $('#login-token').value = '';
    $('#login-token').focus();
  }

  function logout(message) {
    state.token = null;
    try { sessionStorage.removeItem(TOKEN_KEY); } catch { /* stockage indisponible */ }
    state.page = null;
    showLogin(message);
  }

  /** Vérifie le jeton auprès du serveur ; renvoie un message d'erreur ou null. */
  async function verifyToken(token) {
    let res;
    try {
      res = await fetch('api/whoami', { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' });
    } catch {
      return 'Console injoignable.';
    }
    if (res.status === 401) return 'Jeton refusé.';
    if (!res.ok) return `Erreur ${res.status}.`;
    const who = await res.json();
    if (who.access !== 'admin') return 'Ce jeton est celui d\'enrôlement des postes : utilisez le jeton administrateur.';
    state.version = who.version;
    return null;
  }

  async function login(token, target) {
    const error = await verifyToken(token);
    if (error) { showLogin(error); return; }
    state.token = token;
    try { sessionStorage.setItem(TOKEN_KEY, token); } catch { /* onglet sans stockage : session en mémoire */ }
    $('#login').hidden = true;
    $('#app').hidden = false;
    $('#server-version').textContent = `SpokaPilote Server ${state.version}`;
    if (target !== undefined) location.hash = `#${target}`;
    route();
  }

  // ------------------------------------------------------------------ routage

  function parseHash() {
    const raw = location.hash.replace(/^#/, '') || '/';
    const [path, query = ''] = raw.split('?');
    return { path: path || '/', query: new URLSearchParams(query) };
  }

  const routes = [
    { re: /^\/$/, nav: 'overview', page: overviewPage },
    { re: /^\/postes$/, nav: 'machines', page: machinesPage },
    { re: /^\/postes\/([^/]+)(?:\/rapport\/(\d+))?$/, nav: 'machines', page: machinePage },
    { re: /^\/materiel$/, nav: 'missing', page: missingPage },
    { re: /^\/politique$/, nav: 'policy', page: () => documentPage(DOCUMENTS.policy) },
    { re: /^\/imprimantes$/, nav: 'printers', page: () => documentPage(DOCUMENTS.printers) },
    { re: /^\/enrolement$/, nav: 'enroll', page: enrollPage },
  ];

  let currentHash = location.hash;
  async function route() {
    if (!state.token) return;
    const { path, query } = parseHash();
    const match = routes.map((r) => ({ r, m: path.match(r.re) })).find((x) => x.m) || { r: routes[0], m: [] };
    $$('#nav a').forEach((a) => a.classList.toggle('active', a.dataset.route === match.r.nav));
    $('#nav').classList.remove('open');
    $('#menu-toggle').setAttribute('aria-expanded', 'false');

    stopTimer();
    const view = $('#view');
    view.innerHTML = '<div class="loading">Chargement…</div>';
    currentHash = location.hash;
    try {
      state.page = await match.r.page(view, match.m.slice(1).map((x) => (x === undefined ? x : decodeURIComponent(x))), query) || {};
      if (state.page.refresh) startTimer();
      markRefreshed();
    } catch (e) {
      if (e.status === 401) return;
      state.page = {};
      view.innerHTML = `<div class="notice notice-bad">${esc(e.message)}</div><p><a href="#/">Retour à la vue d'ensemble</a></p>`;
    }
    view.focus({ preventScroll: true });
  }

  function startTimer() {
    stopTimer();
    state.timer = setInterval(async () => {
      if (document.visibilityState !== 'visible' || !state.page?.refresh) return;
      try { await state.page.refresh(); markRefreshed(); } catch { /* nouvel essai au prochain tour */ }
    }, REFRESH_MS);
  }
  function stopTimer() { if (state.timer) clearInterval(state.timer); state.timer = null; }
  function markRefreshed() { $('#refresh-info').textContent = `Actualisé à ${timeFmt.format(new Date())}`; }

  window.addEventListener('hashchange', () => {
    if (state.page?.canLeave && !state.page.canLeave()) {
      history.replaceState(null, '', currentHash || '#/');
      return;
    }
    route();
  });
  window.addEventListener('beforeunload', (e) => {
    if (state.page?.canLeave && !state.page.canLeave(true)) { e.preventDefault(); e.returnValue = ''; }
  });

  // Actions génériques (délégation) : copie, téléchargement du rapport brut.
  document.addEventListener('click', async (e) => {
    const copy = e.target.closest('[data-copy]');
    if (copy) { copyText(copy.dataset.copy); return; }
    const row = e.target.closest('tr[data-href]');
    if (row && !e.target.closest('a, button')) location.hash = row.dataset.href;
  });

  // ------------------------------------------------------------------ vue d'ensemble

  async function overviewPage(view) {
    async function render() {
      const f = await api('/fleet');
      if (f.machineCount === 0) {
        view.innerHTML = `
          <div class="page-head"><div><h1>Vue d'ensemble</h1></div></div>
          <div class="notice">Aucun poste n'a encore envoyé de rapport. Consultez la page
            <a href="#/enrolement">Enrôlement</a> pour connecter les postes à cette console.</div>`;
        return;
      }
      const missingKinds = f.missingDrivers.length;
      const todo = f.toProcessMachines.slice(0, 12);
      view.innerHTML = `
        <div class="page-head">
          <div><h1>Vue d'ensemble</h1><p class="muted">${esc(plural(f.machineCount, 'poste', 'postes'))} ·
            ${esc(plural(f.modelCount, 'modèle', 'modèles'))} · synthèse du ${esc(fmtDate(f.generatedAt))}</p></div>
        </div>
        <div class="kpis">
          ${kpi('Postes', f.machineCount, { href: '#/postes' })}
          ${kpi('Sains', f.healthy, { kind: 'ok', href: '#/postes?statut=healthy' })}
          ${kpi('À traiter', f.toProcess, { kind: f.toProcess ? 'warn' : 'ok', href: '#/postes?statut=todo' })}
          ${kpi(`Non vus depuis ${f.staleDays} j`, f.stale, { kind: f.stale ? 'bad' : '', href: '#/postes?statut=stale' })}
          ${kpi('Matériel sans pilote', missingKinds, { kind: missingKinds ? 'bad' : 'ok', href: '#/materiel', sub: `${plural(f.unresolved, 'périphérique', 'périphériques')} en tout` })}
          ${kpi('Pannes matérielles', f.hardwareFaults, { kind: f.hardwareFaults ? 'warn' : '', href: '#/postes?statut=hardware', sub: 'aucun pilote ne peut les corriger' })}
          ${kpi('Pilotes proposés', f.proposed, { sub: f.blocked ? `${f.blocked} bloqué(s) par la politique` : 'aucun bloqué' })}
          ${kpi('Échecs d\'installation', f.failed, { kind: f.failed ? 'bad' : '', href: '#/postes?statut=failed', sub: `${f.installed} pilote(s) installé(s)` })}
        </div>

        <section class="section">
          <div class="section-head"><h2>Postes à traiter</h2>
            ${f.toProcess > todo.length ? `<a href="#/postes?statut=todo">Voir les ${esc(f.toProcess)} postes</a>` : ''}</div>
          ${todo.length === 0 ? '<div class="card empty">Tous les postes sont sains.</div>' : `
          <div class="table-wrap"><table>
            <thead><tr><th>Poste</th><th>Modèle</th><th class="num">Pilotes en erreur</th><th class="num">Sans solution</th>
              <th class="num">Échecs</th><th>État</th><th>Dernier rapport</th></tr></thead>
            <tbody>${todo.map((m) => `
              <tr class="clickable" data-href="#/postes/${enc(m.name)}">
                <td class="cell-title nowrap">${machineLink(m.name)}</td><td class="wrap">${esc(m.model)}</td>
                ${num(m.driverProblems, 'warn')}${num(m.unresolved, 'bad')}${num(m.failed, 'bad')}
                <td>${machineBadges(m)}</td>
                <td class="nowrap">${esc(fmtDate(m.lastReport))}<div class="cell-sub">${esc(fmtAgo(m.lastReport))}</div></td>
              </tr>`).join('')}</tbody>
          </table></div>`}
        </section>

        <div class="split section">
          <section>
            <div class="section-head"><h2>Matériel sans pilote (le plus répandu)</h2><a href="#/materiel">Tout voir</a></div>
            ${f.missingDrivers.length === 0 ? '<div class="card empty">Aucun matériel sans pilote sur le parc.</div>' : `
            <div class="table-wrap"><table>
              <thead><tr><th>Périphérique</th><th class="num">Postes</th></tr></thead>
              <tbody>${f.missingDrivers.slice(0, 6).map((h) => `
                <tr><td class="wrap"><div class="cell-title">${esc(h.name)}</div><code class="cell-sub">${esc(h.hardwareId)}</code></td>
                <td class="num">${esc(h.count)}</td></tr>`).join('')}</tbody>
            </table></div>`}
          </section>
          <section>
            <div class="section-head"><h2>Modèles</h2></div>
            <div class="table-wrap"><table>
              <thead><tr><th>Modèle</th><th class="num">Postes</th><th class="num">À traiter</th></tr></thead>
              <tbody>${f.models.map((m) => `<tr><td class="wrap">${esc(m.model)}</td><td class="num">${esc(m.count)}</td>${num(m.toProcess, 'warn')}</tr>`).join('')}</tbody>
            </table></div>
          </section>
        </div>

        ${f.staleMachines.length === 0 ? '' : `
        <section class="section">
          <div class="section-head"><h2>Non vus depuis ${esc(f.staleDays)} jours</h2></div>
          <div class="table-wrap"><table>
            <thead><tr><th>Poste</th><th>Modèle</th><th>Dernier contact</th></tr></thead>
            <tbody>${f.staleMachines.map((m) => `
              <tr class="clickable" data-href="#/postes/${enc(m.name)}"><td class="cell-title nowrap">${machineLink(m.name)}</td>
              <td class="wrap">${esc(m.model)}</td><td>${esc(fmtDate(m.lastSeen))} <span class="muted">(${esc(fmtAgo(m.lastSeen))})</span></td></tr>`).join('')}</tbody>
          </table></div>
        </section>`}`;
    }
    await render();
    return { refresh: render };
  }

  // ------------------------------------------------------------------ liste des postes

  const STATUS_FILTERS = {
    all: ['Tous les postes', () => true],
    todo: ['À traiter', (m) => !m.healthy],
    healthy: ['Sains', (m) => m.healthy],
    stale: ['Non vus récemment', (m) => m.stale],
    unresolved: ['Avec matériel sans pilote', (m) => m.unresolved > 0],
    hardware: ['Avec panne matérielle', (m) => m.hardwareFaults > 0],
    failed: ['Avec échec d\'installation', (m) => m.failed > 0],
    blocked: ['Avec pilote bloqué', (m) => m.blocked > 0],
  };

  const SORTS = {
    name: (m) => m.name.toLowerCase(),
    model: (m) => m.model.toLowerCase(),
    os: (m) => `${m.os} ${m.osVersion}`.toLowerCase(),
    status: (m) => (m.healthy ? 1 : 0) * 1e6 - m.failed * 1000 - m.driverProblems,
    driverProblems: (m) => m.driverProblems,
    unresolved: (m) => m.unresolved,
    proposed: (m) => m.proposed,
    failed: (m) => m.failed,
    lastReport: (m) => new Date(m.lastReport).getTime(),
  };

  async function machinesPage(view, _params, query) {
    const s = state.machines;
    if (query.get('statut') && STATUS_FILTERS[query.get('statut')]) s.status = query.get('statut');
    let rows = await api('/machines');

    view.innerHTML = `
      <div class="page-head"><div><h1>Postes</h1><p class="muted">Dernier rapport reçu de chaque poste enrôlé.</p></div></div>
      <div class="toolbar">
        <input id="m-search" type="search" placeholder="Rechercher un poste, un modèle, un n° de série…" aria-label="Rechercher" value="${esc(s.search)}">
        <select id="m-status" aria-label="Filtrer par état">
          ${Object.entries(STATUS_FILTERS).map(([k, [label]]) => `<option value="${k}"${k === s.status ? ' selected' : ''}>${esc(label)}</option>`).join('')}
        </select>
        <span id="m-count" class="muted small count"></span>
      </div>
      <div class="table-wrap"><table>
        <thead><tr>
          <th class="sortable" data-sort="name">Poste</th>
          <th class="sortable" data-sort="model">Modèle</th>
          <th class="sortable" data-sort="os">Windows</th>
          <th class="sortable" data-sort="status">État</th>
          <th class="sortable num" data-sort="driverProblems">Pilotes en erreur</th>
          <th class="sortable num" data-sort="unresolved">Sans solution</th>
          <th class="sortable num" data-sort="proposed">Proposés</th>
          <th class="sortable num" data-sort="failed">Échecs</th>
          <th class="sortable" data-sort="lastReport">Dernier rapport</th>
        </tr></thead>
        <tbody id="m-body"></tbody>
      </table></div>`;

    function draw() {
      const needle = s.search.trim().toLowerCase();
      const filter = STATUS_FILTERS[s.status][1];
      const key = SORTS[s.sort];
      const list = rows
        .filter(filter)
        .filter((m) => !needle || [m.name, m.model, m.serialNumber, m.os].some((v) => (v || '').toLowerCase().includes(needle)))
        .sort((a, b) => {
          const x = key(a), y = key(b);
          return (x < y ? -1 : x > y ? 1 : a.name.localeCompare(b.name)) * (s.dir === 'asc' ? 1 : -1);
        });
      $$('th.sortable', view).forEach((th) => {
        th.classList.toggle('sorted-asc', th.dataset.sort === s.sort && s.dir === 'asc');
        th.classList.toggle('sorted-desc', th.dataset.sort === s.sort && s.dir === 'desc');
      });
      $('#m-count').textContent = `${list.length} / ${plural(rows.length, 'poste', 'postes')}`;
      $('#m-body').innerHTML = list.length === 0
        ? `<tr><td colspan="9" class="empty">${rows.length === 0 ? 'Aucun poste enrôlé pour l\'instant (voir <a href="#/enrolement">Enrôlement</a>).' : 'Aucun poste ne correspond à ces critères.'}</td></tr>`
        : list.map((m) => `
          <tr class="clickable" data-href="#/postes/${enc(m.name)}">
            <td class="nowrap"><div class="cell-title">${machineLink(m.name)}</div>${m.serialNumber ? `<div class="cell-sub">S/N ${esc(m.serialNumber)}</div>` : ''}</td>
            <td class="wrap">${esc(m.model)}</td>
            <td>${esc(shortOs(m.os))}<div class="cell-sub">${esc(m.osVersion || '')} ${esc(m.architecture || '')}</div></td>
            <td>${machineBadges(m)}</td>
            ${num(m.driverProblems, 'warn')}${num(m.unresolved, 'bad')}${num(m.proposed)}${num(m.failed, 'bad')}
            <td class="nowrap">${esc(fmtDate(m.lastReport))}<div class="cell-sub">${esc(fmtAgo(m.lastReport))}</div></td>
          </tr>`).join('');
    }

    $('#m-search').addEventListener('input', (e) => { s.search = e.target.value; draw(); });
    $('#m-status').addEventListener('change', (e) => { s.status = e.target.value; draw(); });
    $$('th.sortable', view).forEach((th) => th.addEventListener('click', () => {
      if (s.sort === th.dataset.sort) s.dir = s.dir === 'asc' ? 'desc' : 'asc';
      else { s.sort = th.dataset.sort; s.dir = ['name', 'model', 'os', 'status'].includes(s.sort) ? 'asc' : 'desc'; }
      draw();
    }));
    draw();
    return { refresh: async () => { rows = await api('/machines'); draw(); } };
  }

  // ------------------------------------------------------------------ détail d'un poste

  const OUTCOMES = {
    Installed: ['Installé', 'ok'],
    NotApplicable: ['Non applicable', ''],
    Skipped: ['Ignoré', ''],
    Failed: ['Échec', 'bad'],
    Blocked: ['Bloqué', 'bad'],
  };

  function deviceSituation(d) {
    if (d.isHardwareFault) return badge('Panne matérielle', 'warn');
    if (d.hasCandidate) return badge('Pilote proposé', 'accent');
    if (d.onlyBlockedCandidates) return badge('Pilote bloqué par la politique', 'bad');
    return badge('Aucun pilote trouvé', 'bad');
  }

  async function machinePage(view, [name, historyId]) {
    const data = await api(`/machines/${enc(name)}`);
    const historic = historyId ? await api(`/machines/${enc(name)}/reports/${enc(historyId)}`) : null;
    const r = historic || data.report;
    const m = data.machine;
    const sys = r.system;
    const latestId = data.history.length ? data.history[0].id : null;

    const facts = [
      ['Constructeur', sys.manufacturer], ['Modèle', sys.model], ['Nom commercial', sys.friendlyModel],
      ['N° de série', sys.serialNumber], ['BIOS', sys.biosVersion],
      ['Windows', `${sys.osCaption || ''} ${sys.osDisplayVersion || ''}`.trim()], ['Version', sys.osVersion], ['Architecture', sys.architecture],
      ['SpokaPilote', r.appVersion], ['Rapport du', fmtDate(r.timestamp)], ['Dernier contact', `${fmtDate(m.lastSeen)} (${fmtAgo(m.lastSeen)})`],
      ['Durée de l\'analyse', r.durationSeconds ? `${Math.round(r.durationSeconds)} s` : null],
      ['Sources interrogées', (r.sources || []).join(', ')],
    ].filter(([, v]) => v);
    const sourceErrors = Object.entries(r.sourceErrors || {});

    view.innerHTML = `
      ${historic ? `<div class="notice notice-warn">Vous consultez un rapport antérieur (${esc(fmtDate(r.timestamp))}).
        <a href="#/postes/${enc(name)}">Revenir au dernier rapport</a></div>` : ''}
      <div class="page-head">
        <div>
          <h1>${esc(m.name)}</h1>
          <p class="muted">${esc(m.model)}${m.serialNumber ? ` · S/N ${esc(m.serialNumber)}` : ''}</p>
          <div class="badges section-tight">${machineBadges({ ...m, healthy: r.healthy, hardwareFaults: r.hardwareFaults, rebootRequired: r.rebootRequired })}</div>
        </div>
        <div class="actions">
          <button id="d-raw" class="btn" type="button" title="Rapport complet du dernier envoi, utilisable avec « spokapilote pack »">Rapport complet (JSON)</button>
          <button id="d-delete" class="btn btn-danger" type="button">Supprimer le poste</button>
        </div>
      </div>

      <div class="kpis">
        ${kpi('Périphériques', r.deviceCount)}
        ${kpi('Pilotes en erreur', r.driverProblems, { kind: r.driverProblems ? 'warn' : 'ok' })}
        ${kpi('Sans solution', r.unresolved, { kind: r.unresolved ? 'bad' : '' })}
        ${kpi('Pannes matérielles', r.hardwareFaults, { kind: r.hardwareFaults ? 'warn' : '' })}
        ${kpi('Pilotes proposés', r.proposed, { sub: r.blocked ? `${r.blocked} bloqué(s)` : '' })}
        ${kpi(r.installation ? 'Installés / échecs' : 'Installation', r.installation ? `${r.installed} / ${r.failed}` : '—',
          { kind: r.failed ? 'bad' : '', sub: r.installation ? '' : 'analyse seule' })}
      </div>

      <section class="section">
        <div class="section-head"><h2>Système</h2></div>
        <div class="card card-pad"><dl class="facts">${facts.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join('')}</dl></div>
        ${sourceErrors.length ? `<div class="notice notice-warn section-tight"><b>Sources indisponibles lors de l'analyse :</b>
          <ul class="list-plain">${sourceErrors.map(([k, v]) => `<li><b>${esc(k)}</b> — ${esc(v)}</li>`).join('')}</ul></div>` : ''}
      </section>

      <section class="section">
        <div class="section-head"><h2>Périphériques en erreur</h2><span class="muted small">${esc(plural(r.problemDevices.length, 'périphérique', 'périphériques'))}</span></div>
        ${r.problemDevices.length === 0 ? '<div class="card empty">Aucun périphérique en erreur.</div>' : `
        <div class="table-wrap"><table>
          <thead><tr><th>Périphérique</th><th>ID matériel</th><th>Pilote actuel</th><th>Situation</th><th>Diagnostic</th></tr></thead>
          <tbody>${r.problemDevices.map((d) => `
            <tr>
              <td class="wrap"><div class="cell-title">${esc(d.name)}</div><div class="cell-sub">${esc(d.statusText || '')} (code ${esc(d.errorCode)})</div></td>
              <td class="wrap"><code>${esc(d.hardwareIds[0] || d.instanceId)}</code>${d.hardwareIds.length > 1 ? `<div class="cell-sub">+ ${esc(d.hardwareIds.length - 1)} autre(s)</div>` : ''}</td>
              <td>${esc(d.driverVersion || '—')}<div class="cell-sub">${esc(d.driverProvider || '')}</div></td>
              <td>${deviceSituation(d)}</td>
              <td class="wrap muted">${esc(d.hint || '')}</td>
            </tr>`).join('')}</tbody>
        </table></div>`}
      </section>

      <section class="section">
        <div class="section-head"><h2>Pilotes proposés</h2><span class="muted small">${esc(plural(r.candidates.length, 'pilote', 'pilotes'))}</span></div>
        ${r.candidates.length === 0 ? '<div class="card empty">Aucun pilote manquant ou à mettre à jour.</div>' : `
        <div class="table-wrap"><table>
          <thead><tr><th>Pilote</th><th>Version</th><th>Type</th><th>Source</th><th>Périphériques</th><th>Politique</th></tr></thead>
          <tbody>${r.candidates.map((c) => `
            <tr>
              <td class="wrap cell-title">${esc(c.title)}</td><td class="mono">${esc(c.version || '')}</td>
              <td>${esc(c.kind || '')}</td><td>${esc(c.source || '')}</td>
              <td class="wrap small">${esc(c.devices.slice(0, 3).join(', '))}${c.devices.length > 3 ? ` <span class="muted">+ ${esc(c.devices.length - 3)}</span>` : ''}</td>
              <td>${c.isBlocked ? `${badge('Bloqué', 'bad')}<div class="cell-sub">${esc(c.blockedReason)}</div>` : badge('Autorisé', 'ok')}</td>
            </tr>`).join('')}</tbody>
        </table></div>`}
      </section>

      <section class="section">
        <div class="section-head"><h2>Résultats d'installation</h2></div>
        ${!r.installation ? '<div class="card empty">Analyse seule : aucune installation lors de ce rapport.</div>'
          : r.installation.length === 0 ? '<div class="card empty">Rien n\'était à installer.</div>' : `
        <div class="table-wrap"><table>
          <thead><tr><th>Pilote</th><th>Version</th><th>Résultat</th><th>Message</th></tr></thead>
          <tbody>${r.installation.map((o) => {
            const [label, kind] = OUTCOMES[o.status] || [o.status, ''];
            return `<tr><td class="wrap cell-title">${esc(o.title)}</td><td class="mono">${esc(o.version || '')}</td>
              <td>${badge(label, kind)}${o.rebootRequired ? ` ${badge('Redémarrage', 'accent')}` : ''}</td><td class="wrap">${esc(o.message || '')}</td></tr>`;
          }).join('')}</tbody>
        </table></div>`}
      </section>

      <section class="section">
        <div class="section-head"><h2>Historique</h2><span class="muted small">${esc(plural(data.history.length, 'rapport conservé', 'rapports conservés'))}</span></div>
        <div class="table-wrap"><table>
          <thead><tr><th>Date du rapport</th><th>Type</th><th class="num">Pilotes en erreur</th><th class="num">Sans solution</th>
            <th class="num">Proposés</th><th class="num">Installés</th><th class="num">Échecs</th><th>État</th></tr></thead>
          <tbody>${data.history.map((h) => {
            const current = historyId ? String(h.id) === String(historyId) : h.id === latestId;
            const href = h.id === latestId ? `#/postes/${enc(name)}` : `#/postes/${enc(name)}/rapport/${h.id}`;
            return `<tr class="clickable" data-href="${esc(href)}">
              <td>${esc(fmtDate(h.timestamp))} ${current ? badge('affiché', 'accent') : ''}</td>
              <td>${h.analysisOnly ? 'Analyse' : 'Installation'}</td>
              ${num(h.driverProblems, 'warn')}${num(h.unresolved, 'bad')}${num(h.proposed)}${num(h.installed, 'ok')}${num(h.failed, 'bad')}
              <td>${h.healthy ? badge('Sain', 'ok') : badge('À traiter', 'warn')}</td></tr>`;
          }).join('')}</tbody>
        </table></div>
      </section>`;

    $('#d-raw').addEventListener('click', async () => {
      try {
        const { text } = await api(`/machines/${enc(name)}/raw`, { raw: true });
        download(`${m.name}.json`, new Blob([text], { type: 'application/json' }));
      } catch (e) { if (e.status !== 401) toast(e.message, true); }
    });
    $('#d-delete').addEventListener('click', async () => {
      if (!confirm(`Supprimer « ${m.name} » et tout son historique de la console ?\n\nLe poste réapparaîtra à son prochain rapport s'il est toujours enrôlé.`)) return;
      try {
        await api(`/machines/${enc(name)}`, { method: 'DELETE' });
        toast(`${m.name} supprimé de la console.`);
        location.hash = '#/postes';
      } catch (e) { if (e.status !== 401) toast(e.message, true); }
    });
    return {};
  }

  // ------------------------------------------------------------------ matériel sans pilote

  async function missingPage(view) {
    const s = state.missing;
    let list = (await api('/fleet')).missingDrivers;

    view.innerHTML = `
      <div class="page-head">
        <div><h1>Matériel sans pilote</h1>
          <p class="muted">Périphériques en erreur pour lesquels aucune source n'a proposé de pilote autorisé, regroupés par ID matériel
            sur tout le parc. Les pannes matérielles (USB qui ne répond pas…) sont exclues : aucun pilote ne peut les corriger.</p></div>
        <div class="actions"><button id="x-csv" class="btn" type="button">Exporter (CSV)</button></div>
      </div>
      <div class="toolbar">
        <input id="x-search" type="search" placeholder="Rechercher un périphérique, un ID matériel, un poste…" aria-label="Rechercher" value="${esc(s.search)}">
        <span id="x-count" class="muted small count"></span>
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>Périphérique</th><th>ID matériel</th><th class="num">Postes</th><th>Postes concernés</th><th>Modèles</th><th>Remarque</th></tr></thead>
        <tbody id="x-body"></tbody>
      </table></div>
      <p class="muted small">Piste : ajoutez le pilote validé au dépôt interne (<code>spokapilote export</code> depuis un poste de référence),
        ou autorisez sa source dans la politique.</p>`;

    const visible = () => {
      const needle = s.search.trim().toLowerCase();
      return list.filter((h) => !needle || [h.name, h.hardwareId, ...h.machines, ...h.models].some((v) => (v || '').toLowerCase().includes(needle)));
    };
    function draw() {
      const rows = visible();
      $('#x-count').textContent = `${rows.length} / ${plural(list.length, 'type de matériel', 'types de matériel')}`;
      $('#x-body').innerHTML = rows.length === 0
        ? `<tr><td colspan="6" class="empty">${list.length === 0 ? 'Aucun matériel sans pilote sur le parc.' : 'Aucun résultat.'}</td></tr>`
        : rows.map((h) => `
          <tr>
            <td class="wrap"><div class="cell-title">${esc(h.name)}</div><div class="cell-sub">${esc(h.statusText || '')}</div></td>
            <td class="wrap"><code>${esc(h.hardwareId)}</code> <button class="btn btn-sm btn-ghost" type="button" data-copy="${esc(h.hardwareId)}" title="Copier l'ID matériel">Copier</button></td>
            <td class="num bad">${esc(h.count)}</td>
            <td class="wrap"><ul class="list-inline">${h.machines.slice(0, 12).map((n) => `<li>${machineLink(n)}</li>`).join('')}${h.machines.length > 12 ? `<li class="muted">+ ${esc(h.machines.length - 12)}</li>` : ''}</ul></td>
            <td class="wrap small">${esc(h.models.join(', '))}</td>
            <td class="wrap small muted">${h.blockedOn ? `Pilote bloqué par la politique sur ${esc(plural(h.blockedOn, 'poste', 'postes'))}. ` : ''}${esc(h.hint || '')}</td>
          </tr>`).join('');
    }
    $('#x-search').addEventListener('input', (e) => { s.search = e.target.value; draw(); });
    $('#x-csv').addEventListener('click', () => {
      const cell = (v) => `"${String(v ?? '').replace(/"/g, '""')}"`;
      const lines = [['Périphérique', 'ID matériel', 'Nombre de postes', 'Postes', 'Modèles', 'Bloqué sur'].map(cell).join(';')]
        .concat(visible().map((h) => [h.name, h.hardwareId, h.count, h.machines.join(', '), h.models.join(', '), h.blockedOn].map(cell).join(';')));
      download('materiel-sans-pilote.csv', new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    });
    draw();
    return { refresh: async () => { list = (await api('/fleet')).missingDrivers; draw(); } };
  }

  // ------------------------------------------------------------------ documents JSON (politique, profil d'imprimantes)

  const DOCUMENTS = {
    policy: {
      route: '/policy',
      title: 'Politique de pilotes',
      intro: 'Règles appliquées par les postes (pilotes approuvés, bloqués…). Les postes la récupèrent avec leur jeton d\'enrôlement '
        + '(GET /api/policy). Le format est défini par le moteur SpokaPilote : la console vérifie seulement que le JSON est valide.',
      template: { version: 1, name: 'Politique du parc', rules: [] },
      saved: 'Politique de pilotes enregistrée.',
      removeQuestion: 'Supprimer la politique de pilotes de la console ? Les postes ne la recevront plus.',
    },
    printers: {
      route: '/printer-profile',
      title: 'Profil d\'imprimantes',
      intro: 'Imprimantes et pilotes de l\'organisation, récupérés par les postes avec leur jeton d\'enrôlement '
        + '(GET /api/printer-profile). La console vérifie seulement que le JSON est valide.',
      template: { version: 1, name: 'Imprimantes du siège' },
      saved: 'Profil d\'imprimantes enregistré.',
      removeQuestion: 'Supprimer le profil d\'imprimantes de la console ? Les postes ne le recevront plus.',
    },
  };

  /** Position (ligne, colonne) d'une erreur JSON.parse, quel que soit le navigateur. */
  function jsonErrorLocation(message, text) {
    const lc = message.match(/line (\d+) column (\d+)/i);
    if (lc) return { line: +lc[1], col: +lc[2] };
    const pos = message.match(/position (\d+)/i);
    if (!pos) return null;
    const before = text.slice(0, +pos[1]);
    const lines = before.split('\n');
    return { line: lines.length, col: lines[lines.length - 1].length + 1 };
  }

  function describeJson(value) {
    if (Array.isArray(value)) return `tableau de ${plural(value.length, 'élément', 'éléments')}`;
    if (value === null || typeof value !== 'object') return `valeur ${typeof value}`;
    const parts = [];
    if (value.name !== undefined) parts.push(`« ${value.name} »`);
    if (value.version !== undefined) parts.push(`version ${value.version}`);
    for (const [k, v] of Object.entries(value)) if (Array.isArray(v)) parts.push(`${k} : ${v.length}`);
    return parts.length ? parts.join(' · ') : `objet (${plural(Object.keys(value).length, 'clé', 'clés')})`;
  }

  async function documentPage(doc) {
    const view = $('#view');
    let saved = null;        // texte enregistré sur le serveur (null = aucun document)
    let updated = null;

    view.innerHTML = `
      <div class="page-head">
        <div><h1>${esc(doc.title)}</h1><p class="muted">${esc(doc.intro)}</p></div>
      </div>
      <div class="editor">
        <div class="toolbar">
          <button id="e-format" class="btn" type="button">Formater</button>
          <button id="e-template" class="btn" type="button">Insérer un modèle</button>
          <button id="e-revert" class="btn" type="button">Annuler les modifications</button>
          <button id="e-delete" class="btn btn-danger" type="button">Supprimer</button>
          <span class="count"></span>
          <button id="e-save" class="btn btn-primary" type="button">Enregistrer</button>
        </div>
        <textarea id="e-text" spellcheck="false" autocapitalize="off" autocomplete="off" aria-label="${esc(doc.title)} (JSON)"></textarea>
        <div id="e-status" class="editor-status" role="status"></div>
        <p id="e-meta" class="muted small"></p>
      </div>`;

    const area = $('#e-text');
    const status = $('#e-status');
    const dirty = () => area.value !== (saved ?? '');

    function validate() {
      const text = area.value;
      let ok = true;
      if (text.trim() === '') {
        status.innerHTML = saved === null ? '<span class="muted">Aucun document enregistré : les postes reçoivent « 204 Aucun contenu ».</span>'
          : '<span class="warn">Document vide : utilisez « Supprimer » pour retirer le document.</span>';
        ok = false;
      } else {
        try {
          const value = JSON.parse(text);
          status.innerHTML = `${badge('JSON valide', 'ok')} <span class="muted">${esc(describeJson(value))}</span>`;
        } catch (e) {
          const where = jsonErrorLocation(e.message, text);
          const prefix = where && !/line \d+/i.test(e.message) ? `ligne ${where.line}, colonne ${where.col} — ` : '';
          status.innerHTML = `${badge('JSON invalide', 'bad')} <span class="bad">${esc(prefix)}${esc(e.message)}</span>`;
          ok = false;
        }
      }
      area.classList.toggle('invalid', !ok && text.trim() !== '');
      $('#e-save').disabled = !ok || !dirty();
      $('#e-revert').disabled = !dirty();
      $('#e-delete').disabled = saved === null;
      $('#e-template').hidden = text.trim() !== '';
      if (dirty()) status.insertAdjacentHTML('beforeend', ` ${badge('modifié', 'accent')}`);
      return ok;
    }

    function showMeta() {
      $('#e-meta').textContent = saved === null ? 'Aucun document enregistré sur la console.'
        : `Enregistré le ${fmtDate(updated)} · ${new Blob([saved]).size.toLocaleString('fr-FR')} octets.`;
    }

    async function load() {
      const res = await api(doc.route, { raw: true });
      saved = res.text;
      updated = res.lastModified ? new Date(res.lastModified).toISOString() : null;
      area.value = saved ?? '';
      showMeta();
      validate();
    }

    async function save() {
      if (!validate() || !dirty()) return;
      try {
        const res = await api(doc.route, { method: 'PUT', body: area.value });
        saved = area.value;
        updated = res.updatedAt;
        showMeta();
        validate();
        toast(doc.saved);
      } catch (e) { if (e.status !== 401) toast(e.message, true); }
    }

    area.addEventListener('input', validate);
    area.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
      if (e.key === 'Tab' && !e.shiftKey && !e.ctrlKey) {
        // Tabulation = deux espaces (sans perdre l'annulation du navigateur).
        e.preventDefault();
        document.execCommand('insertText', false, '  ');
      }
    });
    $('#e-format').addEventListener('click', () => {
      try { area.value = JSON.stringify(JSON.parse(area.value), null, 2) + '\n'; validate(); } catch { validate(); toast('Corrigez d\'abord le JSON.', true); }
    });
    $('#e-template').addEventListener('click', () => { area.value = JSON.stringify(doc.template, null, 2) + '\n'; validate(); area.focus(); });
    $('#e-revert').addEventListener('click', () => { area.value = saved ?? ''; validate(); });
    $('#e-save').addEventListener('click', save);
    $('#e-delete').addEventListener('click', async () => {
      if (!confirm(doc.removeQuestion)) return;
      try {
        await api(doc.route, { method: 'DELETE' });
        saved = null; updated = null; area.value = '';
        showMeta(); validate();
        toast('Document supprimé.');
      } catch (e) { if (e.status !== 401) toast(e.message, true); }
    });

    await load();
    return { canLeave: (silent) => !dirty() || (!silent && confirm('Des modifications ne sont pas enregistrées. Quitter quand même ?')) };
  }

  // ------------------------------------------------------------------ enrôlement

  async function enrollPage(view) {
    const info = await api('/enrollment');
    const base = `${location.origin}${location.pathname.replace(/[^/]*$/, '')}`.replace(/\/$/, '');
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
    const insecure = location.protocol !== 'https:' && !local;
    const masked = '•'.repeat(24);
    let shown = false;

    const commands = (token) => `spokapilote config set ServerUrl ${base}\nspokapilote config set ServerToken ${token}`;
    const script = (token) => `# Enrôlement SpokaPilote (Intune, RMM, GPO : exécuter en SYSTEM ou administrateur)
$exe = "$env:ProgramFiles\\SpokaPilote\\spokapilote.exe"
& $exe config set ServerUrl "${base}"
& $exe config set ServerToken "${token}"
& $exe server status
# Analyse hebdomadaire, rapport envoyé automatiquement à la console
schtasks /Create /F /TN "SpokaPilote\\Analyse" /SC WEEKLY /D MON /ST 09:00 /RU SYSTEM /TR "\\"$exe\\" scan --quiet"`;

    view.innerHTML = `
      <div class="page-head"><div><h1>Enrôlement des postes</h1>
        <p class="muted">Un poste enrôlé envoie le rapport de chaque <code>spokapilote scan</code> ou <code>install</code> à cette console
        et y récupère la politique de pilotes.</p></div></div>
      ${insecure ? `<div class="notice notice-bad"><b>Console servie en HTTP.</b> Les postes refusent d'envoyer leurs rapports hors HTTPS
        (sauf <code>localhost</code>). Publiez la console derrière un reverse proxy HTTPS (voir <code>docs/CONSOLE-CENTRALE.md</code>).</div>` : ''}

      <div class="card card-pad section-tight">
        <dl class="facts">
          <div><dt>Adresse de la console (ServerUrl)</dt><dd><code>${esc(base)}</code></dd></div>
          <div><dt>Jeton d'enrôlement (ServerToken)</dt><dd class="secret">
            <code id="t-value">${masked}</code>
            <button id="t-show" class="btn btn-sm" type="button">Afficher</button>
            <button class="btn btn-sm" type="button" data-copy="${esc(info.agentToken)}">Copier</button></dd></div>
          <div><dt>Rétention</dt><dd>${esc(info.historyPerMachine)} rapports par poste · « non vu » après ${esc(info.staleDays)} jours</dd></div>
        </dl>
        <p class="hint">Le jeton d'enrôlement permet uniquement d'envoyer des rapports et de lire la politique et le profil d'imprimantes.
          Il est différent du jeton administrateur, qui ne doit jamais être copié sur les postes.</p>
      </div>

      <div class="steps section">
        <div class="card step">
          <h2>Configurer le poste</h2>
          <p>Dans une console <b>administrateur</b> (la configuration est commune à tout le poste : <code>%ProgramData%\\SpokaPilote\\settings.json</code>) :</p>
          <div id="c-commands">${codeBlock(commands(masked))}</div>
        </div>
        <div class="card step">
          <h2>Vérifier la connexion</h2>
          <p>Teste l'adresse, le certificat HTTPS et le jeton.</p>
          ${codeBlock('spokapilote server status')}
        </div>
        <div class="card step">
          <h2>Envoyer un premier rapport</h2>
          <p>Chaque analyse ou installation envoie ensuite son rapport automatiquement (échec d'envoi non bloquant ;
            <code>--no-upload</code> pour ne pas l'envoyer).</p>
          ${codeBlock('spokapilote scan')}
        </div>
        <div class="card step">
          <h2>Déployer sur le parc</h2>
          <p>Script à distribuer par Intune (script de plateforme), RMM ou GPO de démarrage, avec une analyse hebdomadaire planifiée :</p>
          <div id="c-script">${codeBlock(script(masked))}</div>
        </div>
        <div class="card step">
          <h2>Reprendre les rapports existants (facultatif)</h2>
          <p>Les rapports déjà déposés sur un partage avec <code>--output-dir</code> peuvent être importés depuis n'importe quel poste enrôlé :</p>
          ${codeBlock('spokapilote server upload \\\\srv\\rapports')}
        </div>
      </div>`;

    const renderSecrets = () => {
      const token = shown ? info.agentToken : masked;
      $('#t-value').textContent = token;
      $('#t-show').textContent = shown ? 'Masquer' : 'Afficher';
      // Les boutons « Copier » copient toujours la vraie valeur, même masquée à l'écran.
      $('#c-commands').innerHTML = codeBlock(commands(token));
      $('#c-script').innerHTML = codeBlock(script(token));
      $('#c-commands [data-copy]').dataset.copy = commands(info.agentToken);
      $('#c-script [data-copy]').dataset.copy = script(info.agentToken);
    };
    $('#t-show').addEventListener('click', () => { shown = !shown; renderSecrets(); });
    renderSecrets();
    return {};
  }

  // ------------------------------------------------------------------ démarrage

  function init() {
    $('#login-form').addEventListener('submit', (e) => {
      e.preventDefault();
      const token = $('#login-token').value.trim();
      if (token) login(token);
    });
    $('#logout').addEventListener('click', () => { if (!state.page?.canLeave || state.page.canLeave()) logout(); });
    $('#refresh').addEventListener('click', () => route());
    $('#menu-toggle').addEventListener('click', () => {
      const open = $('#nav').classList.toggle('open');
      $('#menu-toggle').setAttribute('aria-expanded', String(open));
    });

    // Lien de connexion directe « #jeton=<jeton>&vue=/postes » (automatisation, captures) : le jeton est retiré
    // de l'adresse immédiatement ; le fragment n'est jamais envoyé au serveur.
    if (location.hash.startsWith('#jeton=')) {
      const params = new URLSearchParams(location.hash.slice(1));
      const token = params.get('jeton');
      const target = params.get('vue') || '/';
      history.replaceState(null, '', `${location.pathname}${location.search}#${target}`);
      currentHash = location.hash;
      if (token) { login(token); return; }
    }

    let saved = null;
    try { saved = sessionStorage.getItem(TOKEN_KEY); } catch { /* stockage indisponible */ }
    if (saved) login(saved);
    else showLogin();
  }

  init();
})();
