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
    machines: { search: '', status: 'all', sort: 'status', dir: 'asc', selected: new Set() },
    missing: { search: '' },
    jobs: { state: 'all', search: '' },
    jobTypes: null,
  };

  // ------------------------------------------------------------------ utilitaires

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const enc = encodeURIComponent;
  const plural = (n, one, many) => `${n} ${n > 1 ? many : one}`;
  /** « Microsoft Windows 11 Professionnel 25H2 » → « Windows 11 Professionnel 25H2 » (colonnes étroites). */
  const shortOs = (os) => String(os || '').replace(/^Microsoft\s+/i, '').replace(/\bProfessionnel\b/, 'Pro');

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
    if (m.dangerousDriver) out.push(badge('Pilote dangereux', 'bad'));
    if (m.activeJobs > 0) out.push(badge(`${m.activeJobs} tâche${m.activeJobs > 1 ? 's' : ''} en cours`, 'accent'));
    return `<div class="badges">${out.join('')}</div>`;
  }

  // ------------------------------------------------------------------ santé (objet facultatif « health » des rapports)

  const GRADE_KIND = { A: 'ok', B: 'ok', C: 'warn', D: 'bad', E: 'bad' };
  const scoreKind = (s) => (s >= 80 ? 'ok' : s >= 50 ? 'warn' : 'bad');
  /** Badge « B · 78/100 » ; vide si le poste ne transmet pas de santé. */
  function healthBadge(score, grade) {
    if (score == null && !grade) return '';
    const kind = grade ? GRADE_KIND[grade] : scoreKind(score);
    return badge([grade, score != null ? `${score}/100` : ''].filter(Boolean).join(' · '), kind);
  }
  const SEVERITIES = { critical: ['Critique', 'bad', 0], warning: ['Avertissement', 'warn', 1], info: ['Information', '', 2] };

  // ------------------------------------------------------------------ tâches à distance

  const JOB_STATES = {
    pending: ['En attente', 'accent'],
    running: ['En cours', 'warn'],
    succeeded: ['Réussie', 'ok'],
    failed: ['Échouée', 'bad'],
    expired: ['Expirée', 'bad'],
    cancelled: ['Annulée', ''],
  };
  const jobStateBadge = (s) => { const [label, kind] = JOB_STATES[s] || [s, '']; return badge(label, kind); };
  const REQUESTER_KEY = 'spokapilote.requestedBy';
  const getRequester = () => { try { return localStorage.getItem(REQUESTER_KEY) || ''; } catch { return ''; } };
  const setRequester = (v) => { try { localStorage.setItem(REQUESTER_KEY, v); } catch { /* facultatif */ } };

  /** Types de tâches autorisés (liste blanche du serveur), chargés une fois. */
  async function jobTypes() {
    if (!state.jobTypes) state.jobTypes = await api('/job-types');
    return state.jobTypes;
  }

  /** État de l'agent du poste (dernière interrogation de la file de tâches). */
  function agentInfo(m) {
    if (!m.agentSeen) {
      return `<span class="warn">Agent jamais vu sur ce poste</span> — les tâches attendront qu'il soit activé
        (<code>spokapilote agent enable</code>, console administrateur) ou lancé à la main (<code>spokapilote agent run</code>).`;
    }
    const late = Date.now() - new Date(m.agentSeen).getTime() > 86400000;
    return `<span class="${late ? 'warn' : 'ok'}">Agent ${late ? 'silencieux' : 'actif'}</span> — dernier contact ${esc(fmtAgo(m.agentSeen))}
      (${esc(fmtDate(m.agentSeen))}).`;
  }

  // ------------------------------------------------------------------ fenêtre de dialogue

  /**
   * Ouvre la fenêtre modale. body : HTML déjà échappé. Renvoie une promesse résolue avec le formulaire de la fenêtre
   * (lecture des champs) si l'utilisateur confirme, ou null s'il annule.
   */
  function openModal({ title, body, confirmLabel = 'Confirmer', danger = false, cancelLabel = 'Annuler', wide = false }) {
    const dialog = $('#modal');
    $('#modal-title').textContent = title;
    $('#modal-body').innerHTML = body;
    const ok = $('#modal-ok');
    ok.textContent = confirmLabel;
    ok.hidden = confirmLabel === null;
    ok.classList.toggle('btn-danger-solid', danger);
    $('#modal-cancel').textContent = cancelLabel;
    dialog.classList.toggle('modal-wide', wide);
    return new Promise((resolve) => {
      const form = $('#modal-form');
      const done = (value) => {
        form.removeEventListener('submit', onSubmit);
        $('#modal-cancel').removeEventListener('click', onCancel);
        dialog.removeEventListener('cancel', onCancel);
        if (dialog.open) dialog.close();
        resolve(value);
      };
      const onSubmit = (e) => { e.preventDefault(); done(form); };
      const onCancel = (e) => { e.preventDefault(); done(null); };
      form.addEventListener('submit', onSubmit);
      $('#modal-cancel').addEventListener('click', onCancel);
      dialog.addEventListener('cancel', onCancel);
      dialog.showModal();
      (dialog.querySelector('input:not([type=checkbox]), select') || ok).focus();
    });
  }

  /**
   * Demande confirmation puis crée la tâche (un poste, une sélection ou « tous les postes à traiter »).
   * target : { machines: [...] } ou { target: 'to-process', count }.
   */
  async function requestJob(type, target) {
    const count = target.machines ? target.machines.length : target.count;
    const who = target.machines
      ? (count === 1 ? `le poste <b>${esc(target.machines[0])}</b>` : `<b>${esc(count)} postes</b> sélectionnés`)
      : `<b>tous les postes à traiter</b> (${esc(count)} actuellement)`;
    const form = await openModal({
      title: type.label,
      confirmLabel: count > 1 || !target.machines ? `Lancer sur ${count} postes` : 'Lancer la tâche',
      danger: type.changesSystem,
      body: `
        <p>${esc(type.description)}</p>
        <p>Cible : ${who}.</p>
        ${type.changesSystem ? `<div class="notice notice-warn">Cette action <b>modifie le poste</b>. Elle sera exécutée par l'agent
          SpokaPilote du poste (compte SYSTEM) à son prochain passage, sans intervention de l'utilisateur.</div>` : ''}
        ${type.supportsDryRun ? `<label class="check"><input type="checkbox" name="dryRun"> Simulation : montrer ce qui serait fait,
          sans rien modifier</label>` : ''}
        <label class="field">Demandée par <span class="muted">(facultatif, visible dans l'historique)</span>
          <input type="text" name="requestedBy" maxlength="60" value="${esc(getRequester())}" placeholder="Votre nom"></label>
        <p class="hint">L'agent interroge la console toutes les 15 minutes par défaut. Une tâche non récupérée expire au bout de 7 jours.</p>`,
    });
    if (!form) return null;
    const requestedBy = form.elements.requestedBy.value.trim();
    setRequester(requestedBy);
    try {
      const res = await api('/jobs', {
        method: 'POST',
        body: JSON.stringify({
          type: type.id,
          machines: target.machines,
          target: target.machines ? 'selection' : target.target,
          dryRun: !!(form.elements.dryRun && form.elements.dryRun.checked),
          requestedBy,
        }),
      });
      const created = res.created.length;
      const skipped = res.skipped.length;
      toast(created === 0 ? `Aucune tâche créée : ${res.skipped[0]?.reason || 'rien à faire'}`
        : `${plural(created, 'tâche créée', 'tâches créées')}${skipped ? ` (${skipped} ignorée${skipped > 1 ? 's' : ''} : déjà en attente)` : ''}. `
          + 'Elle sera exécutée au prochain passage de l\'agent.', created === 0);
      return res;
    } catch (e) {
      if (e.status !== 401) toast(e.message, true);
      return null;
    }
  }

  /** Détail d'une tâche (journal d'exécution, lien vers le rapport produit). */
  async function showJob(id) {
    const j = await api(`/jobs/${enc(id)}`);
    const rows = [
      ['Poste', machineLink(j.machine)], ['Action', esc(j.typeLabel) + (j.dryRun ? ` ${badge('simulation', 'accent')}` : '')],
      ['État', jobStateBadge(j.state)], ['Demandée par', esc(j.createdBy)], ['Créée le', esc(fmtDate(j.createdAt))],
      ['Démarrée le', esc(fmtDate(j.startedAt))], ['Terminée le', esc(fmtDate(j.finishedAt))],
      ['Code de sortie', esc(j.exitCode ?? '—')],
      ['Rapport produit', j.reportId ? `<a href="#/postes/${enc(j.machine)}/rapport/${enc(j.reportId)}">Voir le rapport</a>` : '—'],
    ];
    await openModal({
      title: `Tâche n° ${j.id}`,
      confirmLabel: null,
      cancelLabel: 'Fermer',
      wide: true,
      body: `<dl class="facts">${rows.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${v}</dd></div>`).join('')}</dl>
        ${j.message ? `<div class="notice section-tight">${esc(j.message)}</div>` : ''}
        <h3 class="section-tight">Journal d'exécution sur le poste</h3>
        ${j.log ? `<pre class="log">${esc(j.log)}</pre>` : '<p class="muted">Aucun journal (tâche non exécutée ou poste antérieur à la 3.0).</p>'}`,
    });
  }

  /** Tableau de tâches (fiche d'un poste, vue Tâches). */
  function jobsTable(jobs, { showMachine = true, emptyText = 'Aucune tâche.' } = {}) {
    if (jobs.length === 0) return `<div class="card empty">${esc(emptyText)}</div>`;
    return `<div class="table-wrap"><table>
      <thead><tr><th class="num">N°</th><th>Créée</th>${showMachine ? '<th>Poste</th>' : ''}<th>Action</th><th>État</th>
        <th>Demandée par</th><th>Résultat</th><th></th></tr></thead>
      <tbody>${jobs.map((j) => `
        <tr>
          <td class="num">${esc(j.id)}</td>
          <td class="nowrap">${esc(fmtDate(j.createdAt))}<div class="cell-sub">${esc(fmtAgo(j.createdAt))}</div></td>
          ${showMachine ? `<td class="nowrap cell-title">${machineLink(j.machine)}</td>` : ''}
          <td>${esc(j.typeLabel)}${j.dryRun ? ` ${badge('simulation', 'accent')}` : ''}</td>
          <td>${jobStateBadge(j.state)}${j.finishedAt ? `<div class="cell-sub">${esc(fmtAgo(j.finishedAt))}</div>`
            : j.startedAt ? `<div class="cell-sub">depuis ${esc(fmtAgo(j.startedAt).replace('il y a ', ''))}</div>` : ''}</td>
          <td class="small">${esc(j.createdBy)}</td>
          <td class="wrap small">${esc(j.message || '')}${j.reportId ? ` <a href="#/postes/${enc(j.machine)}/rapport/${enc(j.reportId)}">rapport</a>` : ''}</td>
          <td class="nowrap"><button class="btn btn-sm btn-ghost" type="button" data-job-detail="${esc(j.id)}">Détail</button>
            ${j.state === 'pending' ? `<button class="btn btn-sm btn-danger" type="button" data-job-cancel="${esc(j.id)}">Annuler</button>` : ''}</td>
        </tr>`).join('')}</tbody></table></div>`;
  }

  /** Boutons « Détail » et « Annuler » des tableaux de tâches (délégation). */
  document.addEventListener('click', async (e) => {
    const detail = e.target.closest('[data-job-detail]');
    if (detail) {
      try { await showJob(detail.dataset.jobDetail); } catch (err) { if (err.status !== 401) toast(err.message, true); }
      return;
    }
    const cancel = e.target.closest('[data-job-cancel]');
    if (cancel) {
      const form = await openModal({
        title: `Annuler la tâche n° ${cancel.dataset.jobCancel} ?`,
        body: '<p>La tâche est encore en attente : elle ne sera pas exécutée par le poste.</p>',
        confirmLabel: 'Annuler la tâche', cancelLabel: 'Garder', danger: true,
      });
      if (!form) return;
      try {
        await api(`/jobs/${enc(cancel.dataset.jobCancel)}/cancel`, { method: 'POST' });
        toast('Tâche annulée.');
        if (state.page?.refresh) await state.page.refresh();
      } catch (err) { if (err.status !== 401) toast(err.message, true); }
    }
  });

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
    { re: /^\/taches$/, nav: 'jobs', page: jobsPage },
    { re: /^\/parametres$/, nav: 'settings', page: settingsPage },
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
          ${f.averageHealth != null ? kpi('Santé moyenne', `${String(f.averageHealth).replace('.', ',')}/100`,
            { kind: scoreKind(f.averageHealth), href: '#/postes?tri=health', sub: `${plural(f.healthMachines, 'poste évalué', 'postes évalués')}` }) : ''}
          ${f.dangerousDrivers > 0 ? kpi('Pilotes dangereux', f.dangerousDrivers, { kind: 'bad', href: '#/postes?statut=dangerous', sub: 'postes concernés' }) : ''}
          ${kpi('Tâches à distance', f.pendingJobs + f.runningJobs, { href: '#/taches?etat=active',
            sub: `${f.pendingJobs} en attente · ${f.runningJobs} en cours` })}
          ${f.failedJobs24h > 0 ? kpi('Tâches échouées (24 h)', f.failedJobs24h, { kind: 'bad', href: '#/taches?etat=failed', sub: 'voir le détail et le journal' }) : ''}
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
    dangerous: ['Avec pilote dangereux', (m) => m.dangerousDriver],
    jobs: ['Avec tâche à distance en cours', (m) => m.activeJobs > 0],
    selected: ['Sélectionnés', (m) => state.machines.selected.has(m.name)],
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
    health: (m) => (m.healthScore ?? 101),
  };

  async function machinesPage(view, _params, query) {
    const s = state.machines;
    if (query.get('statut') && STATUS_FILTERS[query.get('statut')]) s.status = query.get('statut');
    if (query.get('tri') && SORTS[query.get('tri')]) { s.sort = query.get('tri'); s.dir = 'asc'; }
    let rows = await api('/machines');
    const types = await jobTypes();
    const hasHealth = () => rows.some((m) => m.healthScore != null || m.healthGrade);

    view.innerHTML = `
      <div class="page-head"><div><h1>Postes</h1><p class="muted">Dernier rapport reçu de chaque poste enrôlé.
        Cochez des postes pour leur envoyer une action à distance.</p></div></div>
      <div class="toolbar">
        <input id="m-search" type="search" placeholder="Rechercher un poste, un modèle, un n° de série…" aria-label="Rechercher" value="${esc(s.search)}">
        <select id="m-status" aria-label="Filtrer par état">
          ${Object.entries(STATUS_FILTERS).map(([k, [label]]) => `<option value="${k}"${k === s.status ? ' selected' : ''}>${esc(label)}</option>`).join('')}
        </select>
        <span id="m-count" class="muted small count"></span>
      </div>
      <div class="bulkbar card">
        <span id="m-selected" class="small"></span>
        <select id="m-action" aria-label="Action à distance">
          ${types.map((t) => `<option value="${esc(t.id)}">${esc(t.label)}</option>`).join('')}
        </select>
        <button id="m-run-selection" class="btn btn-primary btn-sm" type="button">Lancer sur la sélection</button>
        <button id="m-run-todo" class="btn btn-sm" type="button" title="Tous les postes actuellement « à traiter », sélectionnés ou non">Lancer sur tous les postes à traiter</button>
        <button id="m-clear" class="btn btn-ghost btn-sm" type="button">Vider la sélection</button>
      </div>
      <div class="table-wrap"><table class="table-compact">
        <thead id="m-head"></thead>
        <tbody id="m-body"></tbody>
      </table></div>`;

    let visibleNames = [];
    function head() {
      $('#m-head').innerHTML = `<tr>
          <th class="check-col"><input id="m-all" type="checkbox" aria-label="Sélectionner les postes affichés"></th>
          <th class="sortable" data-sort="name">Poste</th>
          <th class="sortable" data-sort="model">Modèle</th>
          <th class="sortable" data-sort="os">Windows</th>
          <th class="sortable" data-sort="status">État</th>
          ${hasHealth() ? '<th class="sortable" data-sort="health">Santé</th>' : ''}
          <th class="sortable num" data-sort="driverProblems">Pilotes en erreur</th>
          <th class="sortable num" data-sort="unresolved">Sans solution</th>
          <th class="sortable num" data-sort="proposed">Proposés</th>
          <th class="sortable num" data-sort="failed">Échecs</th>
          <th class="sortable" data-sort="lastReport">Dernier rapport</th>
        </tr>`;
      $$('th.sortable', view).forEach((th) => th.addEventListener('click', () => {
        if (s.sort === th.dataset.sort) s.dir = s.dir === 'asc' ? 'desc' : 'asc';
        else { s.sort = th.dataset.sort; s.dir = ['name', 'model', 'os', 'status', 'health'].includes(s.sort) ? 'asc' : 'desc'; }
        draw();
      }));
      $('#m-all').addEventListener('change', (e) => {
        visibleNames.forEach((n) => (e.target.checked ? s.selected.add(n) : s.selected.delete(n)));
        draw();
      });
    }

    function draw() {
      // La sélection ne garde que des postes encore connus.
      const known = new Set(rows.map((m) => m.name));
      [...s.selected].forEach((n) => { if (!known.has(n)) s.selected.delete(n); });
      const needle = s.search.trim().toLowerCase();
      const filter = STATUS_FILTERS[s.status][1];
      const key = SORTS[s.sort];
      const health = hasHealth();
      const list = rows
        .filter(filter)
        .filter((m) => !needle || [m.name, m.model, m.serialNumber, m.os].some((v) => (v || '').toLowerCase().includes(needle)))
        .sort((a, b) => {
          const x = key(a), y = key(b);
          return (x < y ? -1 : x > y ? 1 : a.name.localeCompare(b.name)) * (s.dir === 'asc' ? 1 : -1);
        });
      visibleNames = list.map((m) => m.name);
      $$('th.sortable', view).forEach((th) => {
        th.classList.toggle('sorted-asc', th.dataset.sort === s.sort && s.dir === 'asc');
        th.classList.toggle('sorted-desc', th.dataset.sort === s.sort && s.dir === 'desc');
      });
      $('#m-all').checked = list.length > 0 && list.every((m) => s.selected.has(m.name));
      $('#m-count').textContent = `${list.length} / ${plural(rows.length, 'poste', 'postes')}`;
      const todo = rows.filter((m) => !m.healthy).length;
      $('#m-selected').textContent = s.selected.size ? `${plural(s.selected.size, 'poste sélectionné', 'postes sélectionnés')}` : 'Aucun poste sélectionné';
      $('#m-run-selection').disabled = s.selected.size === 0;
      $('#m-clear').disabled = s.selected.size === 0;
      $('#m-run-todo').disabled = todo === 0;
      $('#m-run-todo').textContent = `Lancer sur tous les postes à traiter (${todo})`;
      const cols = health ? 11 : 10;
      $('#m-body').innerHTML = list.length === 0
        ? `<tr><td colspan="${cols}" class="empty">${rows.length === 0 ? 'Aucun poste enrôlé pour l\'instant (voir <a href="#/enrolement">Enrôlement</a>).' : 'Aucun poste ne correspond à ces critères.'}</td></tr>`
        : list.map((m) => `
          <tr class="clickable${s.selected.has(m.name) ? ' is-selected' : ''}" data-href="#/postes/${enc(m.name)}">
            <td class="check-col"><input type="checkbox" data-select="${esc(m.name)}" aria-label="Sélectionner ${esc(m.name)}"${s.selected.has(m.name) ? ' checked' : ''}></td>
            <td class="nowrap"><div class="cell-title">${machineLink(m.name)}</div>${m.serialNumber ? `<div class="cell-sub">S/N ${esc(m.serialNumber)}</div>` : ''}</td>
            <td class="wrap">${esc(m.model)}</td>
            <td>${esc(shortOs(m.os))}<div class="cell-sub">${esc(m.osVersion || '')} ${esc(m.architecture || '')}</div></td>
            <td>${machineBadges(m)}</td>
            ${health ? `<td>${healthBadge(m.healthScore, m.healthGrade) || '<span class="muted">—</span>'}${m.healthCritical ? `<div class="cell-sub bad">${esc(plural(m.healthCritical, 'constat critique', 'constats critiques'))}</div>` : ''}</td>` : ''}
            ${num(m.driverProblems, 'warn')}${num(m.unresolved, 'bad')}${num(m.proposed)}${num(m.failed, 'bad')}
            <td class="nowrap">${esc(fmtDate(m.lastReport))}<div class="cell-sub">${esc(fmtAgo(m.lastReport))}</div></td>
          </tr>`).join('');
      $$('[data-select]', view).forEach((box) => box.addEventListener('change', () => {
        if (box.checked) s.selected.add(box.dataset.select); else s.selected.delete(box.dataset.select);
        draw();
      }));
    }

    const selectedType = () => types.find((t) => t.id === $('#m-action').value);
    $('#m-search').addEventListener('input', (e) => { s.search = e.target.value; draw(); });
    $('#m-status').addEventListener('change', (e) => { s.status = e.target.value; draw(); });
    $('#m-clear').addEventListener('click', () => { s.selected.clear(); draw(); });
    $('#m-run-selection').addEventListener('click', async () => {
      if (await requestJob(selectedType(), { machines: [...s.selected] })) { s.selected.clear(); await refresh(); }
    });
    $('#m-run-todo').addEventListener('click', async () => {
      if (await requestJob(selectedType(), { target: 'to-process', count: rows.filter((m) => !m.healthy).length })) await refresh();
    });
    let healthShown = hasHealth();
    head();
    draw();
    async function refresh() {
      rows = await api('/machines');
      if (hasHealth() !== healthShown) { healthShown = hasHealth(); head(); }
      draw();
    }
    return { refresh };
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

  function healthSection(h) {
    if (!h) return '';
    const findings = [...(h.findings || [])].sort((a, b) => (SEVERITIES[a.severity]?.[2] ?? 2) - (SEVERITIES[b.severity]?.[2] ?? 2));
    const critical = findings.filter((f) => f.severity === 'critical').length;
    const warnings = findings.filter((f) => f.severity === 'warning').length;
    return `
      <section class="section">
        <div class="section-head"><h2>Santé du poste</h2><span class="muted small">${esc(plural(findings.length, 'constat', 'constats'))}</span></div>
        <div class="card card-pad health-card">
          <div class="health-score is-${esc(h.grade ? GRADE_KIND[h.grade] : scoreKind(h.score ?? 0))}">
            <div class="health-grade">${esc(h.grade || '—')}</div>
            <div class="muted small">${h.score != null ? `${esc(h.score)} / 100` : 'score inconnu'}</div>
          </div>
          <div class="health-summary">
            <div class="badges">${critical ? badge(plural(critical, 'critique', 'critiques'), 'bad') : ''}${warnings ? badge(plural(warnings, 'avertissement', 'avertissements'), 'warn') : ''}
              ${critical + warnings === 0 ? badge('Aucun problème signalé', 'ok') : ''}</div>
            ${findings.some((f) => f.isDangerousDriver) ? '<p class="bad"><b>Pilote dangereux détecté</b> : un pilote connu pour provoquer des pannes (écrans bleus) est présent.</p>' : ''}
          </div>
        </div>
        ${findings.length === 0 ? '' : `
        <div class="table-wrap section-tight"><table>
          <thead><tr><th>Gravité</th><th>Constat</th><th>Détail</th><th>Périphérique</th></tr></thead>
          <tbody>${findings.map((f) => {
            const [label, kind] = SEVERITIES[f.severity] || SEVERITIES.info;
            return `<tr><td>${badge(label, kind)}</td>
              <td class="wrap"><div class="cell-title">${esc(f.title)}</div><code class="cell-sub">${esc(f.code)}</code></td>
              <td class="wrap small">${esc(f.detail || '')}</td><td class="wrap"><code class="small">${esc(f.deviceId || '')}</code></td></tr>`;
          }).join('')}</tbody>
        </table></div>`}
      </section>`;
  }

  async function machinePage(view, [name, requestedHistoryId]) {
    const data = await api(`/machines/${enc(name)}`);
    const latestId = data.history.length ? data.history[0].id : null;
    // Lien vers le rapport le plus récent (depuis une tâche) : c'est l'état actuel, pas un rapport antérieur.
    const historyId = requestedHistoryId && String(requestedHistoryId) !== String(latestId) ? requestedHistoryId : undefined;
    const historic = historyId ? await api(`/machines/${enc(name)}/reports/${enc(historyId)}`) : null;
    const r = historic || data.report;
    const m = data.machine;
    const sys = r.system;
    const types = await jobTypes();

    const facts = [
      ['Constructeur', sys.manufacturer], ['Modèle', sys.model], ['Nom commercial', sys.friendlyModel],
      ['N° de série', sys.serialNumber], ['BIOS', sys.biosVersion],
      ['Windows', `${sys.osCaption || ''} ${sys.osDisplayVersion || ''}`.trim()], ['Version', sys.osVersion], ['Architecture', sys.architecture],
      ['SpokaPilote', r.appVersion], ['Rapport du', fmtDate(r.timestamp)], ['Dernier contact', `${fmtDate(m.lastSeen)} (${fmtAgo(m.lastSeen)})`],
      ['Identifiant machine', m.machineId],
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
          <div class="badges section-tight">${machineBadges({ ...m, healthy: r.healthy, hardwareFaults: r.hardwareFaults, rebootRequired: r.rebootRequired })}
            ${r.health ? healthBadge(r.health.score, r.health.grade) : ''}</div>
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

      ${healthSection(r.health)}

      <section class="section">
        <div class="section-head"><h2>Actions à distance</h2>
          <span class="small">${agentInfo(m)}</span></div>
        <div class="card card-pad">
          <div class="actions">${types.map((t) => `<button class="btn${t.changesSystem ? '' : ' btn-primary'}" type="button" data-run-job="${esc(t.id)}"
            title="${esc(t.description)}">${esc(t.label)}</button>`).join('')}</div>
          <p class="hint">Chaque action est confirmée avant d'être envoyée, puis exécutée par l'agent du poste à son prochain passage.
            Liste fermée : aucune commande ni script libre ne peut être envoyé.</p>
        </div>
        <div id="d-jobs" class="section-tight">${jobsTable(data.jobs, { showMachine: false, emptyText: 'Aucune tâche à distance pour ce poste.' })}</div>
      </section>

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
    async function refreshJobs() {
      const fresh = await api(`/machines/${enc(name)}`);
      $('#d-jobs').innerHTML = jobsTable(fresh.jobs, { showMachine: false, emptyText: 'Aucune tâche à distance pour ce poste.' });
    }
    $$('[data-run-job]', view).forEach((b) => b.addEventListener('click', async () => {
      const type = types.find((t) => t.id === b.dataset.runJob);
      if (await requestJob(type, { machines: [m.name] })) await refreshJobs();
    }));
    return { refresh: refreshJobs };
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
& $exe scan --quiet                 # premier rapport : le poste apparaît dans la console
& $exe agent enable --every 15      # actions à distance : la console est interrogée toutes les 15 min
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
          <h2>Activer les actions à distance</h2>
          <p>Crée la tâche planifiée <code>\\SpokaPilote\\Agent</code> (compte SYSTEM) qui récupère les actions demandées depuis cette
            console (analyse, installation, imprimantes, annulation, mise à jour). Console <b>administrateur</b> :</p>
          ${codeBlock('spokapilote agent enable --every 15')}
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

  // ------------------------------------------------------------------ tâches à distance

  const JOB_FILTERS = {
    all: 'Toutes les tâches',
    active: 'En attente ou en cours',
    pending: 'En attente',
    running: 'En cours',
    finished: 'Terminées',
    succeeded: 'Réussies',
    failed: 'Échouées',
    expired: 'Expirées',
    cancelled: 'Annulées',
  };

  async function jobsPage(view, _params, query) {
    const s = state.jobs;
    if (query.get('etat') && JOB_FILTERS[query.get('etat')]) s.state = query.get('etat');
    const load = () => api(`/jobs?state=${enc(s.state)}&limit=500`);
    let jobs = await load();

    view.innerHTML = `
      <div class="page-head"><div><h1>Tâches à distance</h1>
        <p class="muted">Actions demandées aux postes depuis la console. Chaque poste les récupère à son prochain passage
          (<code>spokapilote agent enable</code> : toutes les 15 minutes par défaut) et renvoie le résultat et son rapport.</p></div></div>
      <div class="toolbar">
        <input id="j-search" type="search" placeholder="Rechercher un poste, une action, un message…" aria-label="Rechercher" value="${esc(s.search)}">
        <select id="j-state" aria-label="Filtrer par état">
          ${Object.entries(JOB_FILTERS).map(([k, label]) => `<option value="${k}"${k === s.state ? ' selected' : ''}>${esc(label)}</option>`).join('')}
        </select>
        <span id="j-count" class="muted small count"></span>
      </div>
      <div id="j-table"></div>
      <p class="muted small">Une tâche jamais récupérée expire au bout de 7 jours ; une tâche « en cours » sans résultat après 6 heures
        est déclarée expirée (poste éteint ou redémarré pendant l'exécution). Les tâches terminées sont conservées 90 jours.</p>`;

    function draw() {
      const needle = s.search.trim().toLowerCase();
      const list = jobs.filter((j) => !needle || [j.machine, j.typeLabel, j.type, j.message, j.createdBy].some((v) => (v || '').toLowerCase().includes(needle)));
      $('#j-count').textContent = `${list.length} / ${plural(jobs.length, 'tâche', 'tâches')}`;
      $('#j-table').innerHTML = jobsTable(list, {
        emptyText: jobs.length === 0 ? 'Aucune tâche. Lancez une action depuis la fiche d\'un poste ou la liste des postes.' : 'Aucune tâche ne correspond.',
      });
    }
    $('#j-search').addEventListener('input', (e) => { s.search = e.target.value; draw(); });
    $('#j-state').addEventListener('change', async (e) => { s.state = e.target.value; jobs = await load(); draw(); });
    draw();
    return { refresh: async () => { jobs = await load(); draw(); } };
  }

  // ------------------------------------------------------------------ paramètres (alertes)

  const TRIGGERS = [
    ['machineToProcess', 'Un poste devient « à traiter »', 'pilote en erreur ou échec d\'installation, alors qu\'il était sain'],
    ['installFailed', 'Échec d\'installation', 'au moins un pilote n\'a pas pu être installé'],
    ['jobFailed', 'Tâche à distance échouée', 'ou restée sans résultat'],
    ['machineStale', 'Poste non vu', 'aucun rapport depuis le délai ci-dessous ; une alerte par période d\'absence'],
    ['dangerousDriver', 'Pilote dangereux détecté', 'constat critique « known-bad-driver » dans la santé du poste'],
  ];
  const ALERT_STATUS = { 'envoyée': 'ok', partielle: 'warn', 'échec': 'bad', 'aucun canal': '' };

  async function settingsPage(view) {
    let cfg = await api('/settings/alerts');
    let events = await api('/alerts/events');
    const isLocked = (field) => cfg.locked.includes(field);
    const lockNote = '<span class="lock" title="Défini par variable d\'environnement sur le serveur : modifiable uniquement là">variable d\'environnement</span>';
    const input = (id, field, value, { type = 'text', placeholder = '', attrs = '' } = {}) =>
      `<input id="${id}" type="${type}" value="${esc(value ?? '')}" placeholder="${esc(placeholder)}" ${isLocked(field) ? 'disabled' : ''} ${attrs}>`;
    const label = (text, field, forId) => `<label for="${forId}">${esc(text)} ${isLocked(field) ? lockNote : ''}</label>`;

    function webhookRow(w) {
      return `<div class="webhook${w.locked ? ' is-locked' : ''}" data-id="${esc(w.id || '')}">
        <input type="text" class="w-name" value="${esc(w.name)}" placeholder="Nom (ex. Équipe IT)" aria-label="Nom" ${w.locked ? 'disabled' : ''}>
        <select class="w-format" aria-label="Plateforme" ${w.locked ? 'disabled' : ''}>
          ${[['teams', 'Microsoft Teams'], ['discord', 'Discord'], ['slack', 'Slack'], ['json', 'JSON générique']]
            .map(([v, l]) => `<option value="${v}"${v === w.format ? ' selected' : ''}>${l}</option>`).join('')}
        </select>
        <input type="url" class="w-url" value="" placeholder="${esc(w.url ? `${w.url} (enregistrée — laisser vide pour la garder)` : 'https://…')}" aria-label="Adresse" ${w.locked ? 'disabled' : ''}>
        <label class="check"><input type="checkbox" class="w-enabled"${w.enabled ? ' checked' : ''} ${w.locked ? 'disabled' : ''}> actif</label>
        ${w.locked ? lockNote : '<button class="btn btn-sm btn-danger w-remove" type="button">Retirer</button>'}
        ${w.insecure ? '<div class="small warn w-note">Adresse en HTTP : le message (et le jeton de l\'adresse) circulent en clair.</div>' : ''}
      </div>`;
    }

    function render() {
      const smtp = cfg.smtp;
      view.innerHTML = `
        <div class="page-head"><div><h1>Paramètres</h1>
          <p class="muted">Alertes envoyées par la console : e-mail et webhooks (Microsoft Teams, Discord, Slack, JSON générique).
            Une même alerte n'est jamais envoyée deux fois pour le même poste en moins de 24 heures.</p></div>
          <div class="actions"><button id="s-test" class="btn" type="button">Envoyer une alerte de test</button>
            <button id="s-save" class="btn btn-primary" type="button">Enregistrer</button></div>
        </div>
        ${cfg.hasChannel ? '' : '<div class="notice notice-warn">Aucun canal configuré : aucune alerte n\'est envoyée. Renseignez un serveur e-mail ou un webhook.</div>'}
        <div id="s-test-result"></div>

        <section class="section">
          <div class="section-head"><h2>E-mail (SMTP)</h2>${smtp.configured ? badge('configuré', 'ok') : badge('non configuré')}</div>
          <div class="card card-pad form-grid">
            <div>${label('Serveur SMTP', 'smtp.host', 's-host')}${input('s-host', 'smtp.host', smtp.host, { placeholder: 'smtp.office365.com' })}</div>
            <div>${label('Port', 'smtp.port', 's-port')}${input('s-port', 'smtp.port', smtp.port, { type: 'number', attrs: 'min="1" max="65535"' })}</div>
            <div>${label('Sécurité', 'smtp.security', 's-security')}
              <select id="s-security" ${isLocked('smtp.security') ? 'disabled' : ''}>
                ${[['starttls', 'STARTTLS (port 587, recommandé)'], ['ssl', 'SSL/TLS implicite (port 465)'], ['none', 'Aucune (relais interne, port 25)']]
                  .map(([v, l]) => `<option value="${v}"${v === smtp.security ? ' selected' : ''}>${l}</option>`).join('')}
              </select></div>
            <div>${label('Identifiant (facultatif)', 'smtp.username', 's-user')}${input('s-user', 'smtp.username', smtp.username, { attrs: 'autocomplete="off"' })}</div>
            <div>${label('Mot de passe', 'smtp.password', 's-pass')}${input('s-pass', 'smtp.password', '', { type: 'password',
              placeholder: smtp.passwordSet ? '•••••••• (enregistré — laisser vide pour le garder)' : '', attrs: 'autocomplete="new-password"' })}
              ${smtp.passwordSet && !isLocked('smtp.password') ? '<label class="check small"><input id="s-pass-clear" type="checkbox"> effacer le mot de passe enregistré</label>' : ''}</div>
            <div>${label('Expéditeur', 'smtp.from', 's-from')}${input('s-from', 'smtp.from', smtp.from, { type: 'email', placeholder: 'spokapilote@mon-entreprise.fr' })}</div>
            <div class="span-2">${label('Destinataires (séparés par des virgules)', 'smtp.to', 's-to')}${input('s-to', 'smtp.to', (smtp.to || []).join(', '), { placeholder: 'support-it@mon-entreprise.fr' })}</div>
          </div>
        </section>

        <section class="section">
          <div class="section-head"><h2>Webhooks</h2><button id="s-add-hook" class="btn btn-sm" type="button">Ajouter un webhook</button></div>
          <div class="card card-pad">
            <div id="s-hooks">${cfg.webhooks.length ? cfg.webhooks.map(webhookRow).join('') : '<p class="muted" id="s-no-hook">Aucun webhook.</p>'}</div>
            <p class="hint">Teams : flux « Publier dans un canal lorsqu'une requête webhook est reçue » (application Workflows) ou webhook entrant.
              Discord : Paramètres du salon › Intégrations › Webhooks. Slack : application « Incoming Webhooks ».
              JSON générique : un objet <code>{ event, severity, title, message, machine, url, details }</code> envoyé en POST.
              Les adresses contiennent un secret : elles ne sont jamais réaffichées.</p>
          </div>
        </section>

        <section class="section">
          <div class="section-head"><h2>Déclencheurs</h2></div>
          <div class="card card-pad">
            ${TRIGGERS.map(([k, t, d]) => `<label class="check block"><input type="checkbox" data-trigger="${k}"${cfg.triggers[k] ? ' checked' : ''}>
              <b>${esc(t)}</b> <span class="muted small">— ${esc(d)}</span></label>`).join('')}
            <div class="form-grid section-tight">
              <div>${label('Poste « non vu » après (jours, 0 = jamais)', 'staleDays', 's-stale')}${input('s-stale', 'staleDays', cfg.staleDays, { type: 'number', attrs: 'min="0" max="3650"' })}</div>
              <div>${label('Résumé quotidien à (vide = aucun)', 'dailySummaryTime', 's-daily')}${input('s-daily', 'dailySummaryTime', cfg.dailySummaryTime, { type: 'time' })}</div>
              <div class="span-2">${label('Adresse publique de la console (liens des alertes)', 'consoleUrl', 's-url')}
                <div class="inline">${input('s-url', 'consoleUrl', cfg.consoleUrl, { type: 'url', placeholder: 'https://spokapilote.mon-entreprise.fr' })}
                ${isLocked('consoleUrl') ? '' : '<button id="s-url-here" class="btn btn-sm" type="button">Adresse actuelle</button>'}</div></div>
            </div>
          </div>
        </section>

        <section class="section">
          <div class="section-head"><h2>Dernières alertes</h2></div>
          ${events.length === 0 ? '<div class="card empty">Aucune alerte envoyée pour l\'instant.</div>' : `
          <div class="table-wrap"><table>
            <thead><tr><th>Date</th><th>Motif</th><th>Alerte</th><th>Envoi</th><th>Détail</th></tr></thead>
            <tbody>${events.map((ev) => `<tr><td class="nowrap">${esc(fmtDate(ev.createdAt))}</td><td>${esc(ev.reasonLabel)}</td>
              <td class="wrap">${ev.machine ? `${machineLink(ev.machine)} — ` : ''}${esc(ev.title)}</td>
              <td>${badge(ev.status, ALERT_STATUS[ev.status] ?? '')}</td><td class="wrap small muted">${esc(ev.detail || '')}</td></tr>`).join('')}</tbody>
          </table></div>`}
        </section>`;

      $('#s-add-hook').addEventListener('click', () => {
        $('#s-no-hook')?.remove();
        $('#s-hooks').insertAdjacentHTML('beforeend', webhookRow({ id: '', name: '', format: 'teams', url: '', enabled: true }));
        $('#s-hooks .webhook:last-child .w-name').focus();
      });
      $('#s-hooks').addEventListener('click', (e) => { if (e.target.closest('.w-remove')) e.target.closest('.webhook').remove(); });
      $('#s-url-here')?.addEventListener('click', () => {
        $('#s-url').value = `${location.origin}${location.pathname.replace(/[^/]*$/, '')}`.replace(/\/$/, '');
      });
      $('#s-save').addEventListener('click', save);
      $('#s-test').addEventListener('click', test);
    }

    function collect() {
      const v = (id) => $(id).value.trim();
      const pass = $('#s-pass').value;
      return {
        smtp: {
          host: v('#s-host'),
          port: Number(v('#s-port')) || 587,
          security: $('#s-security').value,
          username: v('#s-user'),
          // null = mot de passe inchangé ; '' = effacé.
          password: $('#s-pass-clear')?.checked ? '' : pass === '' ? null : pass,
          from: v('#s-from'),
          to: v('#s-to').split(/[,;\s]+/).filter(Boolean),
        },
        webhooks: $$('#s-hooks .webhook:not(.is-locked)').map((row) => ({
          id: row.dataset.id || null,
          name: $('.w-name', row).value.trim(),
          format: $('.w-format', row).value,
          url: $('.w-url', row).value.trim() || null,
          enabled: $('.w-enabled', row).checked,
        })),
        triggers: Object.fromEntries($$('[data-trigger]').map((c) => [c.dataset.trigger, c.checked])),
        staleDays: Number(v('#s-stale')) || 0,
        dailySummaryTime: v('#s-daily') || null,
        consoleUrl: v('#s-url') || null,
      };
    }

    async function save() {
      try {
        cfg = await api('/settings/alerts', { method: 'PUT', body: JSON.stringify(collect()) });
        events = await api('/alerts/events');
        render();
        toast('Paramètres enregistrés.');
        return true;
      } catch (e) {
        if (e.status !== 401) toast(e.message, true);
        return false;
      }
    }

    async function test() {
      const out = $('#s-test-result');
      out.innerHTML = '<div class="notice">Envoi de l\'alerte de test…</div>';
      try {
        const res = await api('/alerts/test', { method: 'POST' });
        out.innerHTML = `<div class="notice">${res.results.map((r) => `<div>${badge(r.success ? 'OK' : 'Échec', r.success ? 'ok' : 'bad')}
          <b>${esc(r.channel)}</b> — ${esc(r.message)}</div>`).join('')}
          <div class="hint">Le test utilise les paramètres enregistrés : enregistrez d'abord vos modifications.</div></div>`;
        events = await api('/alerts/events');
      } catch (e) {
        out.innerHTML = '';
        if (e.status !== 401) toast(e.message, true);
      }
    }

    render();
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
