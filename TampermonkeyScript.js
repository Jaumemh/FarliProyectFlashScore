// ==UserScript==
// @name         Flashcore Overlay - Windows App Integration
// @namespace    http://tampermonkey.net/
// @version      12.2
// @description  Overlay que envía datos a la aplicación WPF local
// @author       TuNombre
// @match        https://www.flashscore.es/*
// @grant        GM_addStyle
// @run-at       document-end
// ==/UserScript==

(function () {
    'use strict';

    const SERVER_URL = 'http://localhost:8080';
    // Ensure a stable tab id for this browser tab so the native app can target this tab
    let FC_TAB_ID = sessionStorage.getItem('fc_tab_id');
    if (!FC_TAB_ID) {
        FC_TAB_ID = 'tab-' + Math.random().toString(36).slice(2, 10);
        sessionStorage.setItem('fc_tab_id', FC_TAB_ID);
    }

    // Estilos CSS
    GM_addStyle(`
        .fc-overlay-btn {
            position: absolute;
            right: 10px;
            bottom: 10px;
            width: 30px;
            height: 30px;
            border-radius: 50%;
            background-color: #C80037;
            color: white;
            border: none;
            cursor: pointer;
            font-size: 16px;
            font-weight: bold;
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
            box-shadow: 0 2px 5px rgba(0,0,0,0.3);
            transition: background-color 0.3s;
        }
        .fc-overlay-btn:hover {
            background-color: #a0002b;
        }
        .fc-overlay-btn.active {
            background-color: #0787FA;
        }
        .fc-overlay-btn.active:hover {
            background-color: #0567ca;
        }
    `);

    // ===== Team Index =====
    const TEAM_INDEX_KEY = 'fc_team_index';
    const TRACKED_MATCHES_KEY = 'fc_tracked_matches';

    function loadTrackedMatches() {
        try { return JSON.parse(sessionStorage.getItem(TRACKED_MATCHES_KEY) || '[]'); } catch (e) { return []; }
    }
    function saveTrackedMatches(list) {
        sessionStorage.setItem(TRACKED_MATCHES_KEY, JSON.stringify(list));
    }
    function trackMatch(id) {
        const list = loadTrackedMatches();
        if (!list.includes(id)) { list.push(id); saveTrackedMatches(list); }
    }
    function untrackMatch(id) {
        const list = loadTrackedMatches();
        const n = list.filter(x => x !== id);
        saveTrackedMatches(n);
    }

    function normalizeKey(s) {
        return (s || '').toString().normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[^a-z0-9]/gi, '').toLowerCase();
    }

    function normalizeTeamHref(href) {
        if (!href) return href;

        // 1. Extract path part starting from /equipo/ or /team/
        // This effectively strips any malformed domain prefixes like www.flashscore.es/www.flashscore.es
        const match = href.toString().match(/(\/(?:equipo|team)\/.*)/i);
        if (!match) return href; // Return original if pattern not found

        let path = match[1];

        // 2. Fix multi-segment slugs: /equipo/part1/part2/ID -> /equipo/part1-part2/ID
        // Heuristic: If we have 4+ segments (equipo/a/b/ID...), assume a/b should be a-b
        const segments = path.split('/').filter(p => p && !/flashscore/i.test(p));
        // segments[0] is 'equipo' or 'team'

        if (segments.length >= 4) {
            // Check if 4th segment looks like an ID (min 4 chars)
            const idCandidate = segments[3];
            if (idCandidate && idCandidate.length >= 4) {
                const newSlug = `${segments[1]}-${segments[2]}`;
                // Reconstruct path: /equipo/new-slug/id/suffix
                // Use the rest of segments from index 3 onwards
                const rest = segments.slice(3).join('/');
                path = `/${segments[0]}/${newSlug}/${rest}`;
            }
        }

        // 3. Clean slashes and prepend single domain
        path = path.replace(/\/+/g, '/');
        if (!path.startsWith('/')) path = '/' + path;

        return `https://www.flashscore.es${path}`;
    }

    function loadTeamIndex() {
        try {
            return JSON.parse(sessionStorage.getItem(TEAM_INDEX_KEY) || '{}');
        } catch (e) { return {}; }
    }

    function saveTeamIndex(idx) {
        try { sessionStorage.setItem(TEAM_INDEX_KEY, JSON.stringify(idx)); } catch (e) { }
    }

    function indexTeam(name, href) {
        try {
            if (!name || !href) return;
            const k = normalizeKey(name);
            if (!k) return;
            const idx = loadTeamIndex();
            let full;
            // Handle absolute URLs
            if (href.startsWith('http://') || href.startsWith('https://')) {
                full = href;
            }
            // Handle URLs that already have the domain without protocol
            else if (href.startsWith('www.flashscore.es')) {
                full = 'https://' + href;
            }
            // Handle protocol-relative URLs
            else if (href.startsWith('//')) {
                full = 'https:' + href;
            }
            // Handle path-relative URLs
            else {
                full = 'https://www.flashscore.es' + (href.startsWith('/') ? href : '/' + href);
            }
            full = normalizeTeamHref(full);
            idx[k] = full;
            saveTeamIndex(idx);
        } catch (e) { }
    }

    function scanAndIndexTeams() {
        try {
            const anchors = Array.from(document.querySelectorAll('a[href]'));
            const idx = loadTeamIndex();
            for (const a of anchors) {
                try {
                    const h = a.getAttribute('href') || '';
                    if (!/(equipo|team)/i.test(h)) continue;
                    const t = (a.textContent || '').trim();
                    if (t) {
                        let fullHref;
                        // Handle absolute URLs
                        if (h.startsWith('http://') || h.startsWith('https://')) {
                            fullHref = h;
                        }
                        // Handle URLs that already have the domain without protocol
                        else if (h.startsWith('www.flashscore.es')) {
                            fullHref = 'https://' + h;
                        }
                        // Handle protocol-relative URLs
                        else if (h.startsWith('//')) {
                            fullHref = 'https:' + h;
                        }
                        // Handle path-relative URLs
                        else {
                            fullHref = 'https://www.flashscore.es' + (h.startsWith('/') ? h : '/' + h);
                        }
                        fullHref = normalizeTeamHref(fullHref);
                        idx[normalizeKey(t)] = fullHref;
                    }
                } catch (e) { }
            }
            saveTeamIndex(idx);
        } catch (e) { }
    }

    // Enviar datos al servidor local
    async function sendToApp(action, data) {
        const payloadData = (data && typeof data === 'object') ? Object.assign({}, data, { tabId: FC_TAB_ID }) : data;
        const response = await fetch(SERVER_URL + '/', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({
                action: action,
                data: payloadData
            })
        });

        if (!response.ok) {
            throw new Error(`Servidor devolvió ${response.status}`);
        }

        return response;
    }

    // Poll native app for commands targeted to this browser tab
    let pollIntervalId;
    async function pollForCommands() {
        try {
            const resp = await fetch(`${SERVER_URL}/commands?tabId=${FC_TAB_ID}`);
            if (!resp.ok) return;
            const json = await resp.json();
            if (json && json.action === 'navigate' && json.href) {
                // clear flag and navigate in this same tab
                try { sessionStorage.removeItem('fc_open_tab'); sessionStorage.removeItem('fc_open_href'); } catch { }
                window.location.href = json.href;
            }
        } catch (e) {
            // ignore network errors
        }
    }

    function startPollingCommands() {
        if (pollIntervalId) return;
        pollIntervalId = setInterval(pollForCommands, 1000);
    }

    function stopPollingCommands() {
        if (pollIntervalId) clearInterval(pollIntervalId);
        pollIntervalId = null;
    }

    function getMatchIdentifier(matchElement) {
        const id = matchElement.id;
        if (id) return id;
        const homeTeam = matchElement.querySelector('.event__participant--home')?.textContent || '';
        const awayTeam = matchElement.querySelector('.event__participant--away')?.textContent || '';
        const time = matchElement.querySelector('.event__time')?.textContent || '';
        return `${homeTeam}-${awayTeam}-${time}`.replace(/\s+/g, '-');
    }

    function extractMatchMid(url) {
        if (!url) return '';
        try {
            const parsed = new URL(url, window.location.origin);
            return parsed.searchParams.get('mid') || '';
        } catch {
            return '';
        }
    }

    // Extract potential team page hrefs for home/away from a match element
    function getTeamHref(matchElement, participantSelector) {
        try {
            const anchor = matchElement.querySelector(participantSelector + ' a[href], ' + participantSelector + ' [href]');
            if (anchor) {
                let h = anchor.getAttribute('href');
                if (h) {
                    let full;
                    // Handle absolute URLs
                    if (h.startsWith('http://') || h.startsWith('https://')) {
                        full = h;
                    }
                    // Handle URLs that already have the domain without protocol
                    else if (h.startsWith('www.flashscore.es')) {
                        full = 'https://' + h;
                    }
                    // Handle protocol-relative URLs
                    else if (h.startsWith('//')) {
                        full = 'https:' + h;
                    }
                    // Handle path-relative URLs
                    else {
                        full = 'https://www.flashscore.es' + (h.startsWith('/') ? h : '/' + h);
                    }
                    return normalizeTeamHref(full);
                }
            }
            const anchors = Array.from(matchElement.querySelectorAll('a[href]'));
            for (const a of anchors) {
                let h = a.getAttribute('href');
                if (/\/equipo\//i.test(h) || /\/team\//i.test(h)) {
                    let full;
                    // Handle absolute URLs
                    if (h.startsWith('http://') || h.startsWith('https://')) {
                        full = h;
                    }
                    // Handle URLs that already have the domain without protocol
                    else if (h.startsWith('www.flashscore.es')) {
                        full = 'https://' + h;
                    }
                    // Handle protocol-relative URLs
                    else if (h.startsWith('//')) {
                        full = 'https:' + h;
                    }
                    // Handle path-relative URLs
                    else {
                        full = 'https://www.flashscore.es' + (h.startsWith('/') ? h : '/' + h);
                    }
                    return normalizeTeamHref(full);
                }
            }
        } catch (e) {
            // ignore
        }
        return '';
    }

    // ========= LIVE UPDATE ENGINE (minutes/goals/cards by team) =========
    const liveState = new Map();       // matchId -> snapshot
    const liveObservers = new Map();   // matchId -> MutationObserver
    const liveScheduled = new Map();   // matchId -> timeoutId

    function toIntScore(x) {
        const n = parseInt((x || '').toString().replace(/[^\d]/g, ''), 10);
        return Number.isFinite(n) ? n : null;
    }

    function getTimeText(matchEl) {
        // Prefer event__time, fallback to stage (incluye 45+2', etc.)
        const t1 = (matchEl.querySelector('.event__time')?.textContent || '').trim();
        if (t1) return t1;
        const stage = (matchEl.querySelector('.event__stage')?.textContent || '').trim();
        return stage || '';
    }

    function countIcons(matchEl, selectors) {
        let total = 0;
        for (const sel of selectors) total += matchEl.querySelectorAll(sel).length;
        return total;
    }

    function snapshot(matchEl) {
        const homeScoreTxt = (matchEl.querySelector('.event__score--home')?.textContent || '').trim();
        const awayScoreTxt = (matchEl.querySelector('.event__score--away')?.textContent || '').trim();

        // Tarjetas: en lista suele haber rojas (icon--redCard). Amarillas a veces no aparecen; lo intentamos por si acaso.
        const homeRed = countIcons(matchEl, [
            '.event__participant--home .icon--redCard',
            '.event__participant--home [class*="redCard"]'
        ]);
        const awayRed = countIcons(matchEl, [
            '.event__participant--away .icon--redCard',
            '.event__participant--away [class*="redCard"]'
        ]);

        const homeYellow = countIcons(matchEl, [
            '.event__participant--home .icon--yellowCard',
            '.event__participant--home [class*="yellowCard"]',
            '.event__participant--home [class*="y-card"]'
        ]);
        const awayYellow = countIcons(matchEl, [
            '.event__participant--away .icon--yellowCard',
            '.event__participant--away [class*="yellowCard"]',
            '.event__participant--away [class*="y-card"]'
        ]);

        return {
            time: getTimeText(matchEl),
            homeScore: toIntScore(homeScoreTxt),
            awayScore: toIntScore(awayScoreTxt),
            homeRed,
            awayRed,
            homeYellow,
            awayYellow
        };
    }

    function computeIncidents(prev, curr) {
        const inc = [];

        // GOALS (por delta de marcador)
        if (prev && curr && prev.homeScore != null && curr.homeScore != null && curr.homeScore > prev.homeScore) {
            inc.push({ type: 'goal', team: 'home', delta: curr.homeScore - prev.homeScore, at: curr.time || '' });
        }
        if (prev && curr && prev.awayScore != null && curr.awayScore != null && curr.awayScore > prev.awayScore) {
            inc.push({ type: 'goal', team: 'away', delta: curr.awayScore - prev.awayScore, at: curr.time || '' });
        }

        // CARDS (por delta de contadores)
        if (prev && curr && curr.homeRed > prev.homeRed) {
            inc.push({ type: 'redCard', team: 'home', delta: curr.homeRed - prev.homeRed, at: curr.time || '' });
        }
        if (prev && curr && curr.awayRed > prev.awayRed) {
            inc.push({ type: 'redCard', team: 'away', delta: curr.awayRed - prev.awayRed, at: curr.time || '' });
        }

        if (prev && curr && curr.homeYellow > prev.homeYellow) {
            inc.push({ type: 'yellowCard', team: 'home', delta: curr.homeYellow - prev.homeYellow, at: curr.time || '' });
        }
        if (prev && curr && curr.awayYellow > prev.awayYellow) {
            inc.push({ type: 'yellowCard', team: 'away', delta: curr.awayYellow - prev.awayYellow, at: curr.time || '' });
        }

        return inc;
    }

    function sendLiveUpdate(matchEl, matchId, incidents = []) {
        const matchData = getMatchData(matchEl);

        // Importante: fuerza el tiempo desde snapshot (más consistente para “minutos”)
        const snap = snapshot(matchEl);
        matchData.time = snap.time;

        const header = findHeaderLeagueWrapper(matchEl);
        if (!header) return;
        const body = header.querySelector('.headerLeague__body');
        if (!body) return;

        const compData = getCompetitionData(body);

        // upsert en tu host (evita duplicados)
        sendToApp('updateMatch', { match: matchData, competition: compData, incidents });
    }

    function scheduleProcess(matchId) {
        if (liveScheduled.has(matchId)) return;
        const tid = setTimeout(() => {
            liveScheduled.delete(matchId);

            const el = document.getElementById(matchId);
            if (!el) return;

            const prev = liveState.get(matchId) || null;
            const curr = snapshot(el);

            // Si no hay cambios, no enviamos
            const changed =
                !prev ||
                prev.time !== curr.time ||
                prev.homeScore !== curr.homeScore ||
                prev.awayScore !== curr.awayScore ||
                prev.homeRed !== curr.homeRed ||
                prev.awayRed !== curr.awayRed ||
                prev.homeYellow !== curr.homeYellow ||
                prev.awayYellow !== curr.awayYellow;

            if (!changed) return;

            const incidents = prev ? computeIncidents(prev, curr) : [];
            liveState.set(matchId, curr);
            sendLiveUpdate(el, matchId, incidents);
        }, 250);

        liveScheduled.set(matchId, tid);
    }

    function startLiveTracking(matchElOrId) {
        const matchId = typeof matchElOrId === 'string' ? matchElOrId : (matchElOrId?.id || '');
        if (!matchId) return;

        // Estado inicial
        const el = typeof matchElOrId === 'string' ? document.getElementById(matchId) : matchElOrId;
        if (el) {
            liveState.set(matchId, snapshot(el));
            sendLiveUpdate(el, matchId, []); // snapshot inicial
        }

        if (liveObservers.has(matchId)) return;

        const obs = new MutationObserver(() => scheduleProcess(matchId));
        const target = document.getElementById(matchId);
        if (target) obs.observe(target, { subtree: true, childList: true, characterData: true, attributes: true });

        liveObservers.set(matchId, obs);
    }

    function stopLiveTracking(matchId) {
        const obs = liveObservers.get(matchId);
        if (obs) obs.disconnect();
        liveObservers.delete(matchId);

        const tid = liveScheduled.get(matchId);
        if (tid) clearTimeout(tid);
        liveScheduled.delete(matchId);

        liveState.delete(matchId);
    }

    function getTeamLogo(matchElement, side) {
        try {
            const idx = side === 'home' ? 0 : 1;

            // === Strategy 1: Football-style (wcl-participantLogo inside event__homeParticipant) ===
            const participantSel = side === 'home' ? '.event__homeParticipant' : '.event__awayParticipant';
            const participant = matchElement.querySelector(participantSel);
            if (participant) {
                const img = participant.querySelector(
                    'img[data-testid="wcl-participantLogo"], img[class*="wcl-logo"], img'
                );
                if (img?.src) return img.src;
            }

            // === Strategy 2: Basketball/other sports (event__logo--home / event__logo--away) ===
            const logoSel = side === 'home' ? 'img.event__logo--home' : 'img.event__logo--away';
            const logoImg = matchElement.querySelector(logoSel);
            if (logoImg?.src) return logoImg.src;

            // === Strategy 3: participant__image class (match detail pages) ===
            const participantImgs = matchElement.querySelectorAll('img.participant__image');
            if (participantImgs.length > idx && participantImgs[idx]?.src) {
                return participantImgs[idx].src;
            }

            // === Strategy 4: Any img inside event__participant--home/away ===
            const legacySel = side === 'home' ? '.event__participant--home' : '.event__participant--away';
            const legacy = matchElement.querySelector(legacySel);
            if (legacy) {
                const img = legacy.querySelector('img');
                if (img?.src) return img.src;
            }

            // === Strategy 5: Generic fallback (first/second img in match element) ===
            const allImgs = matchElement.querySelectorAll('img');
            if (allImgs.length > idx && allImgs[idx]?.src) {
                return allImgs[idx].src;
            }

            return '';
        } catch (e) {
            return '';
        }
    }

    // Extract quarter scores from the current page's smh__template (match detail pages)
    function extractQuartersFromPage() {
        const smh = document.querySelector('.smh__template');
        if (!smh) return { homeQuarters: [], awayQuarters: [] };

        const homeQuarters = [];
        const awayQuarters = [];
        for (let i = 1; i <= 8; i++) {
            const hPart = smh.querySelector(`.smh__part.smh__home.smh__part--${i}`);
            const aPart = smh.querySelector(`.smh__part.smh__away.smh__part--${i}`);
            const hText = hPart?.textContent?.trim() || '';
            const aText = aPart?.textContent?.trim() || '';
            if (hText || aText) {
                homeQuarters.push(hText);
                awayQuarters.push(aText);
            } else {
                break;
            }
        }
        return { homeQuarters, awayQuarters };
    }

    // Auto-detect quarter scores when on a match detail page and send to WPF
    function detectAndSendQuarterScores() {
        // Only run on match detail pages (URL contains /partido/)
        if (!location.pathname.includes('/partido/')) return;

        const matchMid = extractMatchMid(location.href);
        if (!matchMid) return;

        const isAutoFetch = location.hash.includes('fc_quarter_fetch');

        // Wait for smh__template to render (JS-rendered)
        let attempts = 0;
        const checker = setInterval(() => {
            attempts++;
            if (attempts > 30) {
                clearInterval(checker);
                // If auto-fetch tab and nothing found, close it
                if (isAutoFetch) { try { window.close(); } catch (e) { } }
                return;
            }

            const quarters = extractQuartersFromPage();
            if (quarters.homeQuarters.length === 0) return; // not rendered yet

            clearInterval(checker);

            // Send quarter scores as a match update
            const matchUrl = location.href.replace(/#.*$/, ''); // clean hash
            sendToApp('updateMatch', {
                match: {
                    matchMid: matchMid,
                    url: matchUrl,
                    homeQuarters: quarters.homeQuarters,
                    awayQuarters: quarters.awayQuarters
                }
            }).then(() => {
                // If this tab was opened automatically for quarter fetching, close it
                if (isAutoFetch) {
                    setTimeout(() => { try { window.close(); } catch (e) { } }, 500);
                }
            }).catch(e => {
                console.warn('[FC] Error sending quarter scores:', e);
                if (isAutoFetch) { try { window.close(); } catch (e2) { } }
            });
        }, 500);
    }

    function getMatchData(matchElement) {
        const linkElement = matchElement.querySelector('.eventRowLink, a[href*="/partido/"]');
        let matchUrl = '';
        if (linkElement) {
            const href = linkElement.getAttribute('href');
            if (href) {
                matchUrl = href.startsWith('http') ? href : `https://www.flashscore.es${href}`;
            }
        }

        // Blinking detection
        const stageEl = matchElement.querySelector('.event__stage');
        let stageText = stageEl?.textContent?.trim() || '';
        if (stageEl && stageEl.querySelector('.blink')) {
            if (!stageText.includes("'")) stageText += "'";
        }


        // Clonar el elemento para capturar el HTML
        const clonedElement = matchElement.cloneNode(true);
        // Remover el botón de overlay del HTML clonado
        const overlayBtn = clonedElement.querySelector('.fc-overlay-btn');
        if (overlayBtn) overlayBtn.remove();

        // Obtener HTML limpio
        const matchHtml = clonedElement.outerHTML;
        const matchId = getMatchIdentifier(matchElement);
        const matchMid = extractMatchMid(matchUrl);

        return {
            matchId,
            matchMid,
            overlayId: matchId,
            homeTeam: matchElement.querySelector('.wcl-name_jjfMf')?.textContent ||
                matchElement.querySelector('.event__participant--home')?.textContent || '',
            awayTeam: matchElement.querySelectorAll('.wcl-name_jjfMf')[1]?.textContent ||
                matchElement.querySelector('.event__participant--away')?.textContent || '',
            homeRedCards: matchElement.querySelectorAll('.event__participant--home .icon--redCard').length,
            awayRedCards: matchElement.querySelectorAll('.event__participant--away .icon--redCard').length,
            homeScore: matchElement.querySelector('.event__score--home')?.textContent || '',
            awayScore: matchElement.querySelector('.event__score--away')?.textContent || '',
            time: (matchElement.querySelector('.event__time')?.textContent || '') || (stageText.match(/^[\d\+']/) ? stageText : ''),
            stage: stageText,
            homeLogo: getTeamLogo(matchElement, 'home'),
            awayLogo: getTeamLogo(matchElement, 'away'),
            url: matchUrl,
            homeHref: getTeamHref(matchElement, '.event__participant--home'),
            awayHref: getTeamHref(matchElement, '.event__participant--away'),
            html: matchHtml,
            homeQuarters: [],
            awayQuarters: []
        };
    }

    function getCompetitionData(headerBody) {
        const titleText = headerBody.querySelector('.headerLeague__title-text')?.textContent.trim() || '';
        const categoryText = headerBody.querySelector('.headerLeague__category-text')?.textContent.trim() || '';
        const anchor = headerBody.querySelector('a[href]');
        let href = anchor ? anchor.getAttribute('href') : '';
        if (href && !href.startsWith('http')) href = `https://www.flashscore.es${href}`;
        // build a href that can signal the page to open the clasificación tab
        let hrefWithParam = href || '';
        try {
            if (hrefWithParam) {
                const u = new URL(hrefWithParam);
                u.searchParams.set('fc_open_tab', 'classification');
                hrefWithParam = u.toString();
            }
        } catch (e) {
            // ignore
        }

        return {
            competitionId: `${categoryText}:${titleText}`,
            title: titleText,
            category: categoryText,
            logo: headerBody.querySelector('.headerLeague__logo img')?.src || '',
            href: href,
            hrefWithParam: hrefWithParam
        };
    }

    function findHeaderLeagueWrapper(matchElement) {
        const matchRect = matchElement.getBoundingClientRect();
        const allWrappers = document.querySelectorAll('.headerLeague__wrapper');
        let closestWrapper = null;
        let minDistance = Infinity;

        allWrappers.forEach(wrapper => {
            const wrapperRect = wrapper.getBoundingClientRect();
            if (wrapperRect.bottom <= matchRect.top) {
                const distance = matchRect.top - wrapperRect.bottom;
                if (distance < minDistance) {
                    const body = wrapper.querySelector('.headerLeague__body');
                    if (body) {
                        minDistance = distance;
                        closestWrapper = wrapper;
                    }
                }
            }
        });

        return closestWrapper;
    }

    function createOverlayButton(matchElement) {
        if (matchElement.querySelector('.fc-overlay-btn')) return;

        const btn = document.createElement('button');
        btn.className = 'fc-overlay-btn';
        btn.textContent = '📌';
        btn.title = 'Añadir al overlay de Windows';

        btn.addEventListener('click', async function (e) {
            e.stopPropagation();
            e.preventDefault();

            const headerWrapper = findHeaderLeagueWrapper(matchElement);
            if (!headerWrapper) {
                alert('No se pudo identificar la competición');
                return;
            }

            const headerBody = headerWrapper.querySelector('.headerLeague__body');
            if (!headerBody) return;

            const matchData = getMatchData(matchElement);

            const competitionData = getCompetitionData(headerBody);

            const isActive = btn.classList.contains('active');

            if (isActive) {
                try {
                    await sendToApp('removeMatch', {
                        match: matchData,
                        competition: competitionData
                    });
                    btn.classList.remove('active');
                    untrackMatch(matchData.matchId); // UNTRACK IT
                    stopLiveTracking(matchData.matchId);
                } catch (error) {
                    console.error('No se pudo eliminar el overlay:', error);
                    alert('No se pudo cerrar el overlay. Asegúrate de que el host esté en ejecución.');
                }
            } else {
                try {
                    await sendToApp('addMatch', {
                        match: matchData,
                        competition: competitionData
                    });
                    btn.classList.add('active');
                    trackMatch(matchData.matchId); // TRACK IT
                    startLiveTracking(matchElement);
                    // Index team hrefs from this match
                    try { if (matchData.homeTeam && matchData.homeHref) indexTeam(matchData.homeTeam, matchData.homeHref); } catch (e) { }
                    try { if (matchData.awayTeam && matchData.awayHref) indexTeam(matchData.awayTeam, matchData.awayHref); } catch (e) { }

                    // Quarter scores are now fetched by the WPF app (hidden WebView2)
                    // No browser tab or popup needed
                } catch (error) {
                    console.error('No se pudo abrir el overlay:', error);
                    alert('No se pudo abrir el overlay. Inicia la aplicación Windows y vuelve a intentarlo.');
                }
            }
        });

        matchElement.style.position = 'relative';
        matchElement.appendChild(btn);

        // Check initial state
        const mid = getMatchIdentifier(matchElement);
        const tracked = loadTrackedMatches();
        if (tracked.includes(mid)) {
            btn.classList.add('active');
            startLiveTracking(matchElement);
        }
    }

    function addButtonsToAllMatches() {
        const matches = document.querySelectorAll('.event__match:not(.fc-processed)');
        matches.forEach(match => {
            match.classList.add('fc-processed');
            createOverlayButton(match);
        });
    }

    function handleExpandableSections() {
        const expandButtons = document.querySelectorAll('[data-testid="wcl-accordionButton"]');
        expandButtons.forEach(button => {
            if (!button.hasAttribute('data-fc-listener')) {
                button.setAttribute('data-fc-listener', 'true');
                button.addEventListener('click', function () {
                    setTimeout(addButtonsToAllMatches, 500);
                });
            }
        });
    }

    function tryOpenClassificationTabOnPage() {
        try {
            // Check sessionStorage flag OR URL query param
            const urlParams = new URLSearchParams(window.location.search);
            const param = urlParams.get('fc_open_tab');
            const desired = sessionStorage.getItem('fc_open_tab') || param;
            if (desired !== 'classification') return;

            const clickTab = () => {
                const textMatcher = /clasific/i;
                const candidates = Array.from(document.querySelectorAll('a,button,span,li')).filter(el => el.textContent && textMatcher.test(el.textContent));
                if (candidates.length) {
                    candidates[0].click();
                    sessionStorage.removeItem('fc_open_tab');
                    sessionStorage.removeItem('fc_open_href');
                    // remove fc_open_tab from URL if present
                    try {
                        if (param) {
                            urlParams.delete('fc_open_tab');
                            const newUrl = window.location.pathname + (urlParams.toString() ? '?' + urlParams.toString() : '') + window.location.hash;
                            history.replaceState({}, '', newUrl);
                        }
                    } catch (e) { }
                    return true;
                }

                const anchors = Array.from(document.querySelectorAll('a[href]')).filter(a => /clasif/i.test(a.getAttribute('href')));
                if (anchors.length) {
                    window.location.href = anchors[0].href;
                    sessionStorage.removeItem('fc_open_tab');
                    sessionStorage.removeItem('fc_open_href');
                    return true;
                }
                return false;
            };

            if (clickTab()) return;

            let attempts = 0;
            const maxAttempts = 6;
            const iv = setInterval(() => {
                attempts++;
                if (clickTab() || attempts >= maxAttempts) {
                    clearInterval(iv);
                    if (attempts >= maxAttempts) {
                        const savedHref = sessionStorage.getItem('fc_open_href') || '';
                        if (savedHref) {
                            let target = savedHref;
                            if (!/clasif/i.test(target)) {
                                if (!target.endsWith('/')) target += '/';
                                target += 'clasificacion/';
                            }
                            window.location.href = target;
                        }
                        sessionStorage.removeItem('fc_open_tab');
                        sessionStorage.removeItem('fc_open_href');
                    }
                }
            }, 700);
        } catch (err) {
            console.warn('Error al intentar abrir la pestaña clasificación:', err);
        }
    }

    function tryOpenTeamFromParam() {
        try {
            const urlParams = new URLSearchParams(window.location.search);
            const param = urlParams.get('fc_open_team');
            const desired = sessionStorage.getItem('fc_open_team') || param;
            if (!desired) return;

            const teamName = desired.trim();
            const normalize = (s) => (s || '').toString().normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[^a-z0-9]/gi, '').toLowerCase();

            const dispatchClick = (el) => {
                try {
                    const ev = new MouseEvent('click', { bubbles: true, cancelable: true, view: window });
                    el.dispatchEvent(ev);
                } catch (e) {
                    try { el.click(); } catch (e2) { }
                }
            };

            const clickTeamOnce = () => {
                const tn = normalize(teamName);

                // FIRST: Try team index for direct navigation
                const idx = loadTeamIndex();
                const k = normalizeKey(teamName);
                if (idx && idx[k]) {
                    try {
                        const normalized = normalizeTeamHref(idx[k]);
                        window.location.href = normalized;
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    } catch (e) { }
                }

                // SECOND: Try saved href if it's a team href
                const savedHref = sessionStorage.getItem('fc_open_href') || urlParams.get('fc_open_href') || '';
                if (savedHref && /\/equipo\//i.test(savedHref)) {
                    try {
                        window.location.href = normalizeTeamHref(savedHref);
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    } catch (e) { }
                }

                // THIRD: Find anchors with /equipo/ that match team name
                const anchors = Array.from(document.querySelectorAll('a[href]'))
                    .filter(a => /\/equipo\//i.test(a.getAttribute('href')) || /\/team\//i.test(a.getAttribute('href')));

                for (const a of anchors) {
                    const text = (a.textContent || '').trim();
                    const href = a.getAttribute('href') || '';
                    const hrefSlug = href.split('/').filter(Boolean).pop() || '';
                    if (text && normalize(text).includes(tn)) {
                        dispatchClick(a);
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    }
                    if (hrefSlug && normalize(hrefSlug).includes(tn)) {
                        dispatchClick(a);
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    }
                }

                return false;
            };

            if (clickTeamOnce()) return;

            let attempts = 0;
            const maxAttempts = 5;
            const iv = setInterval(() => {
                attempts++;
                if (clickTeamOnce()) {
                    clearInterval(iv);
                    return;
                }
                if (attempts >= maxAttempts) {
                    clearInterval(iv);
                    sessionStorage.removeItem('fc_open_team');
                    sessionStorage.removeItem('fc_open_href');
                }
            }, 300);
        } catch (err) {
            console.warn('Error al intentar abrir la ficha del equipo:', err);
        }
    }

    const observer = new MutationObserver(mutations => {
        let shouldCheckMatches = false;
        mutations.forEach(mutation => {
            if (mutation.addedNodes.length) {
                shouldCheckMatches = true;
                mutation.addedNodes.forEach(node => {
                    if (node.nodeType === 1 && node.classList && node.classList.contains('event__match')) {
                        node.classList.add('fc-processed');
                        createOverlayButton(node);
                    }
                });
            }
        });
        if (shouldCheckMatches) setTimeout(addButtonsToAllMatches, 100);
        // Update team index when DOM changes
        try { scanAndIndexTeams(); } catch (e) { }
        handleExpandableSections();
    });

    function init() {
        addButtonsToAllMatches();
        handleExpandableSections();
        observer.observe(document.body, { childList: true, subtree: true });

        // Ping al servidor para verificar conexión
        sendToApp('ping', { message: 'Script cargado' });
        // Intentar abrir una ficha de equipo si se solicitó desde la app (prioritario)
        tryOpenTeamFromParam();
        // Intentar abrir la pestaña clasificación si venimos de una navegación desde la lista
        tryOpenClassificationTabOnPage();
        // Start polling for commands from the native app (e.g., navigate in current tab)
        startPollingCommands();
        // Initial scan of team links and periodic rescans
        try { scanAndIndexTeams(); } catch (e) { }
        setInterval(() => { try { scanAndIndexTeams(); } catch (e) { } }, 5000);

        // Auto-detect quarter scores on match detail pages
        detectAndSendQuarterScores();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(addButtonsToAllMatches, 2000);

    // Re-hook observers periodically in case of DOM refresh
    setInterval(() => {
        const tracked = loadTrackedMatches();
        tracked.forEach(matchId => {
            if (!liveObservers.has(matchId)) startLiveTracking(matchId);
        });
    }, 5000);

})();