/* ============================================================
   JELLYFIN RATINGS CACHE — client (injecté automatiquement par le plugin)
   Réglages : Tableau de bord > Ratings Cache. Rien à ajouter dans index.html ni dans le CSS.

   - Notes lues depuis /JellyfinRatingsCache/ratings (cache serveur), gardées en mémoire ET en localStorage
   - Page film / série : notes, certification, pays/langues, liens externes
   - Cartes : "logo note - année" sur la ligne choisie (1re ou 2e ligne de texte)
   - Page épisode et épisodes d'une saison : note TMDb
   - Rendu via MutationObserver + requestAnimationFrame (avant l'affichage)
   Debug : jellyfinRatingsCacheDebug() dans la console (F12)
   ============================================================ */
;(function () {
  'use strict';
  if (window.__jrcLoaded) return;
  window.__jrcLoaded = true;

  var DEBUG = false;
  var REFRESH_MS = 5 * 60 * 1000;
  var EP_TTL = 24 * 3600 * 1000;
  var SRC_BASE = '';
  try {
    var cs = document.currentScript;
    if (cs && cs.src) SRC_BASE = cs.src.replace(/\/JellyfinRatingsCache\/client\.js.*$/i, '');
  } catch (e) { /* ignore */ }

  function log() { if (DEBUG) console.log.apply(console, ['[JRC]'].concat([].slice.call(arguments))); }
  function warn() { console.warn.apply(console, ['[JRC]'].concat([].slice.call(arguments))); }

  function serverBase() {
    var c = window.ApiClient;
    try { var a = c && typeof c.serverAddress === 'function' ? c.serverAddress() : ''; if (a) return a.replace(/\/+$/, ''); } catch (e) { /* ignore */ }
    return SRC_BASE || location.origin;
  }
  function dataUrl() { return serverBase() + '/JellyfinRatingsCache/ratings'; }

  var CDN = 'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/';
  var LOGO = {
    imdb: CDN + 'IMDb.png', tmdb: CDN + 'TMDB.png',
    tomatoes: CDN + 'Rotten_Tomatoes.png', tomatoes_rotten: CDN + 'Rotten_Tomatoes_rotten.png',
    tomatoes_certified: CDN + 'rotten-tomatoes-certified.png',
    audience: CDN + 'Rotten_Tomatoes_positive_audience.png', audience_rotten: CDN + 'Rotten_Tomatoes_negative_audience.png',
    rotten_ver: CDN + 'roten_tomatoes_ver.png', letterboxd: CDN + 'letterboxd.png'
  };
  Object.keys(LOGO).forEach(function (k) { new Image().src = LOGO[k]; });
  var NAMES = { imdb: 'IMDb', rt: 'Rotten Tomatoes', aud: 'Popcorn', tmdb: 'TMDb', lb: 'Letterboxd' };
  var LABELS = { imdb: 'IMDb', rt: 'RT', aud: 'Pop', tmdb: 'TMDb', lb: 'LB' };

  /* ---------------------------------------------------------- RÉGLAGES (défauts, remplacés par ceux du serveur) */
  var DEFAULT_CFG = {
    detail: { enabled: true, show: { imdb: true, rt: true, aud: true, tmdb: true, lb: true }, order: ['imdb', 'rt', 'aud', 'tmdb', 'lb'],
      cert: true, countries: true, languages: true, links: true, seasonEpisodes: true, episodePage: true, logoSize: 150, position: 'afterOfficialRating' },
    cards: { enabled: true, movies: true, series: true, episodes: false, line: 'secondary', sources: ['lb', 'imdb', 'tmdb', 'rt', 'aud'],
      max: 1, sep: '-', logo: true, logoSize: 120, position: 'before' },
    hideStar: true
  };
  var CFG = DEFAULT_CFG;

  /* ---------------------------------------------------------- CSS */
  var styleEl = document.createElement('style');
  (document.head || document.documentElement).appendChild(styleEl);
  function applyStyles() {
    styleEl.textContent = [
      '.jrc-ratings{display:inline-flex;align-items:center;flex-wrap:wrap;vertical-align:middle;margin-left:6px;}',
      '.jrc-ratings img{margin-right:4px;vertical-align:middle;}',
      '.jrc-val{margin-right:8px;vertical-align:middle;}',
      '.jrc-pg{display:inline-flex;align-items:center;margin-left:2px;vertical-align:middle;border-radius:4px;overflow:hidden;font-size:0.82em;font-weight:700;line-height:1;white-space:nowrap;}',
      '.jrc-pg-age{padding:4px 7px;letter-spacing:0.3px;}',
      '.jrc-pg-cat{padding:4px 8px;background:rgba(0,0,0,0.35);letter-spacing:0.5px;text-transform:uppercase;font-size:0.85em;}',
      '.jrc-ep{display:inline-flex;align-items:center;margin-left:6px;}',
      '.jrc-ep img{height:1.2em;margin-right:3px;vertical-align:middle;}',
      '.jrc-ep span{font-size:0.9em;vertical-align:middle;}',
      '.jrc-card{display:inline-flex;align-items:center;vertical-align:middle;white-space:nowrap;}',
      '.jrc-card img{margin-right:3px;vertical-align:middle;}',
      '.jrc-card-val{margin-right:4px;}',
      '.jrc-card-lbl{margin-right:3px;font-weight:600;opacity:0.85;}',
      '.jrc-card-sep{white-space:pre;}',
      '.jrc-flag-img{height:0.9em;margin-right:4px;vertical-align:middle;border-radius:2px;}',
      CFG.hideStar ? '.starRatingContainer{display:none !important;}' : ''
    ].join('\n');
  }
  applyStyles();

  /* ---------------------------------------------------------- DONNÉES */
  var LS_DATA = 'jrc2_data', LS_VER = 'jrc2_ver', LS_ETAG = 'jrc2_etag', LS_EP = 'jrc2_ep';
  var DATA = {}, VER = null, ETAG = null, ready = false, loading = false, lastError = null;
  var EP = {};   // épisode -> [note TMDb (0 = aucune), horodatage, id de la série]

  try {
    var raw = localStorage.getItem(LS_DATA);
    if (raw) {
      var saved = JSON.parse(raw);
      DATA = saved.m || {}; if (saved.cfg) CFG = saved.cfg;
      VER = localStorage.getItem(LS_VER); ready = true; applyStyles();
    }
    ETAG = localStorage.getItem(LS_ETAG);
  } catch (e) { DATA = {}; }
  try { EP = JSON.parse(localStorage.getItem(LS_EP) || '{}') || {}; } catch (e) { EP = {}; }

  function norm(id) { return (id || '').replace(/-/g, '').toLowerCase(); }
  function token() {
    var c = window.ApiClient;
    try { return c && typeof c.accessToken === 'function' ? c.accessToken() : null; } catch (e) { return null; }
  }
  function authHeaders() {
    var t = token();
    return t ? { 'Authorization': 'MediaBrowser Client="Jellyfin Ratings Cache", Token="' + t + '"', 'Accept': 'application/json' } : {};
  }

  function loadData() {
    if (loading || !token()) return Promise.resolve(false);
    loading = true;
    return fetch(dataUrl(), { headers: authHeaders(), cache: 'no-cache' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      var tag = r.headers.get('ETag');
      if (tag && tag === ETAG && ready) return false;           // identique : on ne re-parse même pas
      return r.json().then(function (j) {
        var changed = !ready || j.v !== VER;
        DATA = j.m || {}; VER = j.v || null; ETAG = tag; ready = true;
        if (j.cfg) { CFG = j.cfg; applyStyles(); }
        try {
          localStorage.setItem(LS_DATA, JSON.stringify({ m: DATA, cfg: CFG }));
          localStorage.setItem(LS_VER, VER || ''); localStorage.setItem(LS_ETAG, tag || '');
        } catch (e) { /* quota */ }
        return changed;
      });
    }).then(function (c) { lastError = null; return c; })
      .catch(function (e) { lastError = e.message; log('chargement KO', e.message); return false; })
      .then(function (c) { loading = false; return c; });
  }
  function refresh() { loadData().then(function (c) { if (c) schedule(); }); }

  /* ---------------------------------------------------------- INFOS D'ÉPISODES (API Jellyfin, requêtes groupées) */
  var epPending = {}, epInflight = {}, epTimer = 0;
  function epWant(id) {
    var e = EP[id];
    if ((e && Date.now() - e[1] < EP_TTL) || epPending[id] || epInflight[id]) return;
    epPending[id] = 1;
    clearTimeout(epTimer);
    epTimer = setTimeout(epFlush, 20);
  }
  function epFlush() {
    var ids = Object.keys(epPending).slice(0, 50);
    if (!ids.length) return;
    if (!token()) { epTimer = setTimeout(epFlush, 300); return; }
    ids.forEach(function (i) { delete epPending[i]; epInflight[i] = 1; });
    fetch(serverBase() + '/Items?Ids=' + ids.join(',') + '&Fields=CommunityRating&EnableImages=false&EnableUserData=false', { headers: authHeaders() })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (j) {
        var now = Date.now();
        (j.Items || []).forEach(function (it) {
          EP[norm(it.Id)] = [it.CommunityRating > 0 ? it.CommunityRating : 0, now, it.SeriesId ? norm(it.SeriesId) : ''];
        });
        if (Object.keys(EP).length > 8000) EP = {};
        try { localStorage.setItem(LS_EP, JSON.stringify(EP)); } catch (e) { /* quota */ }
        schedule();
      })
      .catch(function (e) { log('épisodes KO', e.message); })
      .then(function () { ids.forEach(function (i) { delete epInflight[i]; }); });
    if (Object.keys(epPending).length) epTimer = setTimeout(epFlush, 20);
  }

  /* ---------------------------------------------------------- HELPERS DOM */
  function getVisiblePage() {
    var pages = document.querySelectorAll('.mainAnimatedPage:not(.hide)');
    for (var i = 0; i < pages.length; i++) {
      if (pages[i].querySelector('#itemDetailPage') || pages[i].offsetParent !== null || pages[i].offsetWidth > 0) return pages[i];
    }
    return document;
  }
  function getItemId() {
    var m = (window.location.hash || '').match(/[?&]id=([a-f0-9-]+)/i) || window.location.href.match(/[?&]id=([a-f0-9-]+)/i);
    return m ? norm(m[1]) : null;
  }
  function mk(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function findAnchorIn(box, position) {
    var isYear = function (t) { return /^\d{4}$/.test(t) || /^\d{4}\s*[–—-]/.test(t); };
    var isRuntime = function (t) { return /^\d+\s*(?:h|m|min)\b/i.test(t); };
    var items = Array.prototype.slice.call(box.querySelectorAll('.mediaInfoItem'));
    var runtime = items.filter(function (el) { return isRuntime((el.textContent || '').trim()); })[0];
    if (position === 'end') return null;
    if (position === 'afterRuntime' && runtime) return runtime;
    return box.querySelector('.mediaInfoItem.mediaInfoText.mediaInfoOfficialRating')
      || items.filter(function (el) { return isYear((el.textContent || '').trim()); })[0]
      || runtime || items[items.length - 1] || null;
  }
  function logoKey(k, rec, value) {
    if (k === 'imdb') return 'imdb';
    if (k === 'rt') return rec.cert ? 'tomatoes_certified' : (value < 60 ? 'tomatoes_rotten' : 'tomatoes');
    if (k === 'aud') return rec.ver ? 'rotten_ver' : (value < 60 ? 'audience_rotten' : 'audience');
    if (k === 'tmdb') return 'tmdb';
    return 'letterboxd';
  }
  function fmt(v) { return String(v); }

  /* ---------------------------------------------------------- PAGE DÉTAIL */
  function certInfo(age) {
    if (age <= 0) return { label: 'Tous publics', bg: '#2e7d32', fg: '#fff' };
    if (age <= 7) return { label: 'Familial', bg: '#558b2f', fg: '#fff' };
    if (age <= 10) return { label: 'Jeune public', bg: '#9e9d24', fg: '#fff' };
    if (age <= 13) return { label: 'Ados', bg: '#f9a825', fg: '#000' };
    if (age <= 16) return { label: 'Déconseillé', bg: '#e65100', fg: '#fff' };
    return { label: 'Adulte', bg: '#b71c1c', fg: '#fff' };
  }
  function addRating(box, key, src, value, name, sizePct) {
    var img = mk('img');
    img.src = LOGO[key]; img.alt = name; img.title = name + ': ' + value; img.dataset.source = key;
    img.style.height = (sizePct / 100) + 'em';
    box.appendChild(img);
    box.appendChild(mk('span', 'jrc-val', fmt(value)));
  }
  function buildRatings(rec) {
    var d = CFG.detail, r = rec.r || {}, box = mk('div', 'jrc-ratings'), n = 0;
    d.order.forEach(function (k) {
      if (!d.show[k] || r[k] == null) return;
      addRating(box, logoKey(k, rec, r[k]), k, r[k], NAMES[k], d.logoSize); n++;
    });
    if (d.cert && rec.age != null) {
      var info = certInfo(rec.age), txt = rec.age > 0 ? rec.age + '+' : '0+';
      var pg = mk('span', 'jrc-pg');
      pg.style.cssText = 'background:' + info.bg + ';color:' + info.fg + ';';
      pg.title = info.label + ' (' + txt + ')';
      pg.appendChild(mk('span', 'jrc-pg-age', txt));
      pg.appendChild(mk('span', 'jrc-pg-cat', info.label));
      box.appendChild(pg); n++;
    }
    return n ? box : null;
  }
  function scanDetail() {
    var id = getItemId();
    if (!id || !ready) return;
    var page = getVisiblePage();
    var misc = page.querySelector('.itemMiscInfo.itemMiscInfo-primary');
    if (!misc) return;
    var d = CFG.detail;
    var rec = DATA[id] || null;
    var sig = id + '|' + (VER || '') + '|' + (rec ? 1 : 0);
    var old = misc.parentElement && misc.parentElement.querySelector('.jrc-ratings');
    if (!d.enabled) { if (old) old.remove(); return; }
    if (old && old.dataset.sig === sig) { if (rec) { scanExtras(page, rec, id); scanLinks(page, rec, id); } return; }
    if (old) old.remove();

    var box = null;
    if (rec) {
      box = buildRatings(rec);
    } else if (d.episodePage) {
      var src = misc.querySelector('.starRatingContainer') || page.querySelector('.starRatingContainer');
      var val = src ? parseFloat((src.textContent || '').replace(',', '.')) : NaN;
      if (!isNaN(val) && val > 0) {
        box = mk('div', 'jrc-ratings');
        addRating(box, 'tmdb', 'tmdb', val.toFixed(1), 'TMDb', d.logoSize);
      }
    }
    if (box) {
      box.dataset.sig = sig;
      var anchor = findAnchorIn(misc, d.position);
      if (anchor) anchor.insertAdjacentElement('afterend', box); else misc.appendChild(box);
    }
    if (rec) { scanExtras(page, rec, id); scanLinks(page, rec, id); }
  }

  /* ---------------------------------------------------------- CARTES : "logo note - texte" */
  function cardId(el) {
    var root = el.closest('.card') || el.closest('[data-id]');
    if (!root) return { id: null, root: null };
    var id = root.getAttribute('data-id');
    if (!id) {
      var l = root.querySelector('a[href*="id="]');
      var m = l && (l.getAttribute('href') || '').match(/[?&]id=([a-f0-9-]+)/i);
      id = m ? m[1] : null;
    }
    return { id: norm(id), root: root };
  }
  function cardRecord(id, root) {
    var rec = DATA[id];
    if (rec) return (rec.t === 'movie' ? CFG.cards.movies : CFG.cards.series) ? rec : null;
    var type = root ? root.getAttribute('data-type') : null;
    if (type === 'Episode' && CFG.cards.episodes) {
      var e = EP[id];
      if (!e) { epWant(id); return undefined; }
      if (Date.now() - e[1] >= EP_TTL) epWant(id);
      return e[2] && DATA[e[2]] ? DATA[e[2]] : null;
    }
    return null;
  }
  function buildCardRating(rec, c) {
    var r = rec.r || {}, picks = [];
    for (var i = 0; i < c.sources.length && picks.length < c.max; i++) if (r[c.sources[i]] != null) picks.push(c.sources[i]);
    if (!picks.length) return null;
    var box = mk('span', 'jrc-card');
    picks.forEach(function (k, idx) {
      if (c.logo) {
        var img = mk('img');
        img.src = LOGO[logoKey(k, rec, r[k])]; img.alt = NAMES[k]; img.title = NAMES[k] + ': ' + r[k];
        img.style.height = (c.logoSize / 100) + 'em';
        box.appendChild(img);
      } else {
        box.appendChild(mk('span', 'jrc-card-lbl', LABELS[k]));
      }
      box.appendChild(mk('span', 'jrc-card-val', fmt(r[k])));
    });
    return box;
  }
  function scanCards() {
    var c = CFG.cards;
    if (!ready) return;
    var key = String(VER) + '|' + (c.enabled ? 1 : 0);
    var sel = c.line === 'first' ? '.cardText.cardText-first, .cardText:not(.cardText-secondary)' : '.cardText.cardText-secondary';
    var els = document.querySelectorAll(sel);
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      if (el.dataset.jrcCard === key) continue;
      var old = el.querySelector(':scope > .jrc-card, :scope > .jrc-card-sep');
      while (old) { old.remove(); old = el.querySelector(':scope > .jrc-card, :scope > .jrc-card-sep'); }
      if (!c.enabled) { el.dataset.jrcCard = key; continue; }
      var ci = cardId(el);
      if (!ci.id) continue;
      var rec = cardRecord(ci.id, ci.root);
      if (rec === undefined) continue;                         // infos d'épisode en cours de chargement
      el.dataset.jrcCard = key;
      var box = rec ? buildCardRating(rec, c) : null;
      if (!box) continue;
      var sep = mk('span', 'jrc-card-sep', ' ' + c.sep + ' ');
      if (c.position === 'after') { el.appendChild(sep); el.appendChild(box); }
      else { el.insertBefore(sep, el.firstChild); el.insertBefore(box, sep); }
    }
  }

  /* ---------------------------------------------------------- ÉPISODES D'UNE SAISON */
  function domRating(el) {
    var s = el.querySelector('.starRatingContainer');
    var v = s ? parseFloat((s.textContent || '').replace(',', '.')) : NaN;
    return isNaN(v) ? null : v;
  }
  function attachEp(el, rating) {
    if (el.querySelector('.jrc-ep')) return true;
    var target;
    if (el.classList.contains('listItem')) {
      target = el.querySelector('.secondary.listItemBodyText, .listItemMediaInfo');
    } else {
      var texts = el.querySelectorAll('.cardFooter .cardText');
      target = texts.length > 1 ? texts[1] : texts[0];
    }
    if (!target) return false;
    var rc = mk('span', 'jrc-ep');
    var img = mk('img');
    img.src = LOGO.tmdb; img.alt = 'TMDb'; img.title = 'TMDb: ' + rating.toFixed(1);
    rc.appendChild(img);
    rc.appendChild(mk('span', null, rating.toFixed(1)));
    target.appendChild(rc);
    return true;
  }
  function scanEpisodes() {
    if (!CFG.detail.enabled || !CFG.detail.seasonEpisodes) return;
    var page = getVisiblePage();
    var els = page.querySelectorAll('#childrenContent .listItem[data-type="Episode"], #childrenContent .card[data-type="Episode"]');
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      if (el.dataset.jrcEp) continue;
      var id = norm(el.getAttribute('data-id'));
      if (!id) continue;
      var r = domRating(el);
      if (r == null) {
        var c = EP[id];
        if (!c) { epWant(id); continue; }
        r = c[0]; epWant(id);
      }
      if (r > 0 && !attachEp(el, r)) continue;
      el.dataset.jrcEp = '1';
    }
  }

  /* ---------------------------------------------------------- PAYS / LANGUES */
  var LANG_TO_COUNTRY = {
    en: 'gb', ja: 'jp', ko: 'kr', zh: 'cn', ar: 'sa', he: 'il', hi: 'in', bn: 'bd', ur: 'pk', fa: 'ir', ta: 'in', te: 'in',
    ml: 'in', kn: 'in', pa: 'in', gu: 'in', mr: 'in', ne: 'np', si: 'lk', sw: 'tz', am: 'et', ka: 'ge', hy: 'am', uk: 'ua',
    be: 'by', sr: 'rs', bs: 'ba', sq: 'al', ms: 'my', tl: 'ph', vi: 'vn', my: 'mm', km: 'kh', lo: 'la', da: 'dk', sv: 'se',
    nb: 'no', nn: 'no', cs: 'cz', el: 'gr', et: 'ee', ga: 'ie', cy: 'gb', eu: 'es', ca: 'es', gl: 'es', af: 'za', zu: 'za',
    la: 'va', jv: 'id', su: 'id', kk: 'kz', tk: 'tm', ky: 'kg', tg: 'tj', ps: 'af', ku: 'iq'
  };
  function langFlag(code) {
    if (!code) return null;
    var lc = code.toLowerCase().split('-')[0];
    return LANG_TO_COUNTRY[lc] || (lc.length === 2 ? lc : null);
  }
  var dnRegion = null, dnLang = null;
  function countryName(c) {
    try { dnRegion = dnRegion || new Intl.DisplayNames([document.documentElement.lang || 'fr'], { type: 'region' }); return dnRegion.of(c.toUpperCase()) || c; } catch (e) { return c; }
  }
  function langName(c) {
    try { dnLang = dnLang || new Intl.DisplayNames([document.documentElement.lang || 'fr'], { type: 'language' }); var n = dnLang.of(c) || c; return n.charAt(0).toUpperCase() + n.slice(1); } catch (e) { return c; }
  }
  function flagImg(cc) {
    if (!cc) return null;
    var img = mk('img', 'jrc-flag-img');
    img.src = 'https://flagcdn.com/w40/' + cc.toLowerCase() + '.png';
    img.alt = cc.toUpperCase();
    img.onerror = function () { this.style.display = 'none'; };
    return img;
  }
  function detailGroup(kind, label, list, toFlag, toName) {
    var div = mk('div', 'detailsGroupItem ' + kind + 'Group jrc-extra-detail');
    div.appendChild(mk('div', kind + 'GroupLabel label', label));
    var content = mk('div', kind + 'Group content focuscontainer-x');
    list.forEach(function (c, i) {
      if (i > 0) content.appendChild(document.createTextNode(', '));
      var f = flagImg(toFlag(c));
      if (f) content.appendChild(f);
      content.appendChild(document.createTextNode(toName(c)));
    });
    div.appendChild(content);
    return div;
  }
  function scanExtras(page, rec, id) {
    var d = CFG.detail;
    var list = page.querySelector('.itemDetailsGroup');
    if (!list) return;
    var countries = d.countries ? rec.c : null, langs = d.languages ? rec.l : null;
    var sig = id + '|' + VER + '|' + JSON.stringify([countries, langs]);
    if (list.dataset.jrcSig === sig && (list.querySelector('.jrc-extra-detail') || (!countries && !langs))) return;
    list.querySelectorAll('.jrc-extra-detail').forEach(function (o) { o.remove(); });
    var anchor = list.querySelector('.genresGroup');
    if (countries && countries.length) {
      var d1 = detailGroup('countries', document.documentElement.lang && /^fr/i.test(document.documentElement.lang) ? 'Pays' : 'Countries', countries, function (c) { return c; }, countryName);
      if (anchor) anchor.insertAdjacentElement('afterend', d1); else list.insertBefore(d1, list.firstChild);
      anchor = d1;
    }
    if (langs && langs.length) {
      var d2 = detailGroup('languages', document.documentElement.lang && /^fr/i.test(document.documentElement.lang) ? 'Langues' : 'Languages', langs, langFlag, langName);
      if (anchor) anchor.insertAdjacentElement('afterend', d2); else list.appendChild(d2);
    }
    list.dataset.jrcSig = sig;
  }

  /* ---------------------------------------------------------- LIENS EXTERNES */
  function extLink(label, href) {
    var a = document.createElement('a');
    a.setAttribute('is', 'emby-linkbutton');
    a.className = 'button-link emby-button';
    a.href = href; a.target = '_blank'; a.rel = 'noopener';
    a.textContent = label;
    return a;
  }
  function scanLinks(page, rec, id) {
    if (!CFG.detail.links) return;
    var c = page.querySelector('.itemExternalLinks.focuscontainer-x') || page.querySelector('.itemExternalLinks');
    if (!c || (!rec.im && !rec.tm)) return;
    var sig = [id, rec.im || '', rec.tm || '', rec.t, rec.rt_url || ''].join('|');
    if (c.dataset.jrcSig === sig) return;
    if (c.dataset.jrcSnap !== id) {
      c._jrcOthers = []; c._jrcKeep = [];
      for (var n = c.firstChild; n; n = n.nextSibling) {
        if (n.nodeType !== 1) continue;
        if (n.tagName === 'A') {
          var h = (n.href || '').toLowerCase();
          var managed = h.indexOf('imdb.com/title') !== -1 || h.indexOf('themoviedb.org/') !== -1 ||
            h.indexOf('rottentomatoes.com/') !== -1 || h.indexOf('letterboxd.com/') !== -1;
          if (!managed) c._jrcOthers.push(n.cloneNode(true));
        } else c._jrcKeep.push(n.cloneNode(true));
      }
      c.dataset.jrcSnap = id;
    }
    while (c.firstChild) c.removeChild(c.firstChild);
    var items = [];
    if (rec.im) items.push(['IMDb', 'https://www.imdb.com/title/' + rec.im]);
    if (rec.rt_url) items.push(['Rotten Tomatoes', rec.rt_url]);
    if (rec.tm) items.push(['TMDB', 'https://www.themoviedb.org/' + (rec.t === 'show' ? 'tv' : 'movie') + '/' + rec.tm]);
    if (rec.im) items.push(['Letterboxd', 'https://letterboxd.com/imdb/' + rec.im + '/']);
    items.forEach(function (it, j) {
      if (j > 0) c.appendChild(document.createTextNode(', '));
      c.appendChild(extLink(it[0], it[1]));
    });
    (c._jrcOthers || []).forEach(function (o) { c.appendChild(document.createTextNode(', ')); c.appendChild(o.cloneNode(true)); });
    (c._jrcKeep || []).forEach(function (p) { c.appendChild(p.cloneNode(true)); });
    c.dataset.jrcSig = sig;
  }

  /* ---------------------------------------------------------- MOTEUR */
  var raf = 0;
  function schedule() {
    if (raf) return;
    raf = requestAnimationFrame(function () {
      raf = 0;
      if (!ready && token()) refresh();
      try { scanDetail(); scanCards(); scanEpisodes(); } catch (e) { warn(e); }
    });
  }
  function start() {
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('hashchange', schedule);
    document.addEventListener('viewshow', schedule, true);
    document.addEventListener('visibilitychange', function () { if (!document.hidden) refresh(); });
    setInterval(refresh, REFRESH_MS);
    setInterval(function () { if (!ready) refresh(); }, 2000);   // attend la connexion de l'utilisateur
    schedule();
    refresh();
  }
  if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);

  window.jellyfinRatingsCacheDebug = function () {
    DEBUG = true;
    var id = getItemId();
    console.table({
      url: dataUrl(), itemId: id, dataItems: Object.keys(DATA).length, version: VER, ready: ready, lastError: lastError,
      inData: id ? !!DATA[id] : null, tokenPresent: !!token(),
      cardsEnabled: CFG.cards.enabled, cardLine: CFG.cards.line, cardTexts: document.querySelectorAll('.cardText').length,
      miscInfoFound: !!getVisiblePage().querySelector('.itemMiscInfo.itemMiscInfo-primary'), episodesCached: Object.keys(EP).length
    });
    schedule();
  };
})();
