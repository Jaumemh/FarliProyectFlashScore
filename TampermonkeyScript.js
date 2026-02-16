// ==UserScript==
// @name         Flashcore Overlay - Windows App Integration
// @namespace    http://tampermonkey.net/
// @version      12.0
// @description  Overlay que envía datos a la aplicación WPF local
// @author       TuNombre
// @match        https://www.flashscore.es/*
// @grant        GM_addStyle
// @run-at       document-end
// ==/UserScript==

(function() {
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

    // Team index stored in sessionStorage to quickly resolve team hrefs
    const TEAM_INDEX_KEY = 'fc_team_index';

    function normalizeKey(s) {
        return (s||'').toString().normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[^a-z0-9]/gi, '').toLowerCase();
    }

    function normalizeTeamHref(href) {
        if (!href) return href;
        // Convert team href with ID separated by dash to use slash instead
        // e.g., /equipo/ca-osasuna-ETdxjU8a/ -> /equipo/ca-osasuna/ETdxjU8a/
        const match = href.match(/^(.*)\/equipo\/([^\/]+)-([A-Za-z0-9]{4,})\/?(.*)$/i);
        if (match) {
            const [, prefix, slug, id, suffix] = match;
            return `${prefix}/equipo/${slug}/${id}/${suffix}`.replace(/\/+/g, '/');
        }
        return href;
    }

    function loadTeamIndex() {
        try {
            const json = sessionStorage.getItem(TEAM_INDEX_KEY) || '{}';
            return JSON.parse(json);
        } catch (e) { return {}; }
    }

    function saveTeamIndex(idx) {
        try { sessionStorage.setItem(TEAM_INDEX_KEY, JSON.stringify(idx)); } catch (e) {}
    }

    function indexTeam(name, href) {
        try {
            if (!name || !href) return;
            const k = normalizeKey(name);
            if (!k) return;
            const idx = loadTeamIndex();
            // prefer full host href
            let full = href.startsWith('http') ? href : `https://www.flashscore.es${href}`;
            full = normalizeTeamHref(full);
            idx[k] = full;
            saveTeamIndex(idx);
        } catch (e) {}
    }

    function scanAndIndexTeams() {
        try {
            const anchors = Array.from(document.querySelectorAll('a[href]'));
            const idx = loadTeamIndex();
            for (const a of anchors) {
                try {
                    const h = a.getAttribute('href') || '';
                    if (!/\/(equipo|team)\//i.test(h)) continue;
                    const t = (a.textContent||'').trim();
                    if (t) {
                        let fullHref = h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
                        fullHref = normalizeTeamHref(fullHref);
                        idx[normalizeKey(t)] = fullHref;
                    } else {
                        // try slug
                        const slug = h.split('/').filter(Boolean).pop();
                        if (slug) {
                            let fullHref = h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
                            fullHref = normalizeTeamHref(fullHref);
                            idx[normalizeKey(slug)] = fullHref;
                        }
                    }
                } catch (e) {}
            }
            saveTeamIndex(idx);
        } catch (e) {}
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
                try { sessionStorage.removeItem('fc_open_tab'); sessionStorage.removeItem('fc_open_href'); } catch {}
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
                h = h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
                return normalizeTeamHref(h);
            }
        }
        const anchors = Array.from(matchElement.querySelectorAll('a[href]'));
        for (const a of anchors) {
            let h = a.getAttribute('href');
            if (/\/equipo\//i.test(h) || /\/team\//i.test(h)) {
                h = h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
                return normalizeTeamHref(h);
            }
        }
    } catch (e) {
        // ignore
    }
    return '';
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
            homeScore: matchElement.querySelector('.event__score--home')?.textContent || '',
            awayScore: matchElement.querySelector('.event__score--away')?.textContent || '',
            time: matchElement.querySelector('.event__time')?.textContent || '',
            stage: matchElement.querySelector('.event__stage')?.textContent || '',
            homeLogo: matchElement.querySelector('img[alt]')?.src || '',
            awayLogo: matchElement.querySelectorAll('img[alt]')[1]?.src || '',
            url: matchUrl,
            homeHref: getTeamHref(matchElement, '.event__participant--home'),
            awayHref: getTeamHref(matchElement, '.event__participant--away'),
            html: matchHtml
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

        btn.addEventListener('click', async function(e) {
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
                    // Después de añadir al overlay: el app recibirá la info de la competición
                    // No navegamos desde la web al pulsar el botón: la app recibirá el enlace
                    // Index team hrefs from this match so we can navigate to team pages directly
                    try { if (matchData.homeTeam && matchData.homeHref) indexTeam(matchData.homeTeam, matchData.homeHref); } catch(e) {}
                    try { if (matchData.awayTeam && matchData.awayHref) indexTeam(matchData.awayTeam, matchData.awayHref); } catch(e) {}
                } catch (error) {
                    console.error('No se pudo abrir el overlay:', error);
                    alert('No se pudo abrir el overlay. Inicia la aplicación Windows y vuelve a intentarlo.');
                }
            }
        });

        matchElement.style.position = 'relative';
        matchElement.appendChild(btn);
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
                button.addEventListener('click', function() {
                    setTimeout(addButtonsToAllMatches, 500);
                });
            }
        });
    }

    function tryOpenClassificationTabOnPage() {
        try {
            // If a team open request is present, skip classification to avoid conflicts
            const urlParams = new URLSearchParams(window.location.search);
            const teamParam = sessionStorage.getItem('fc_open_team') || urlParams.get('fc_open_team');
            if (teamParam) return;

            // Check sessionStorage flag OR URL query param
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
                    } catch (e) {}
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
            const normalize = (s) => (s||'').toString().normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[^a-z0-9]/gi, '').toLowerCase();

            const dispatchClick = (el) => {
                try {
                    const ev = new MouseEvent('click', { bubbles: true, cancelable: true, view: window });
                    el.dispatchEvent(ev);
                } catch (e) {
                    try { el.click(); } catch (e) {}
                }
            };

            // Do NOT navigate directly to team href from the index — prefer to stay on the match page
            // and let this script find and click the team anchor there. Keep index only as fallback.

            // If we're already on a match page, try to extract team slugs from the URL path
            try {
                const pathname = window.location.pathname || '';
                if (pathname.includes('/partido/')) {
                    const parts = pathname.split('/').filter(Boolean);
                    const pi = parts.indexOf('partido');
                    if (pi >= 0) {
                        // team slugs usually follow: /partido/<sport>/<team1slug>/<team2slug>/
                        const slugs = parts.slice(pi + 2, pi + 6); // grab a few in case of variants
                        const tn = normalize(teamName);
                        for (let idx = 0; idx < slugs.length; idx++) {
                            let s = slugs[idx];
                            if (!s) continue;
                            s = s.replace(/^\/+|\/+$/g, '');
                            
                            // Check if slug contains ID at the end (separated by dash)
                            let slugClean = s;
                            let idFromSlug = '';
                            const lastDashIdx = s.lastIndexOf('-');
                            if (lastDashIdx > 0) {
                                const potentialId = s.substring(lastDashIdx + 1);
                                if (/^[A-Za-z0-9]{4,}$/.test(potentialId)) {
                                    idFromSlug = potentialId;
                                    slugClean = s.substring(0, lastDashIdx);
                                }
                            }
                            
                            const slugNorm = slugClean.toString().normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/[^a-z0-9]/gi, '').toLowerCase();
                            
                            // If we extracted an ID from the slug, use it
                            if (idFromSlug && (slugNorm && (slugNorm.includes(tn) || tn.includes(slugNorm)))) {
                                const teamUrl = `https://www.flashscore.es/equipo/${encodeURIComponent(slugClean)}/${encodeURIComponent(idFromSlug)}/`;
                                try { window.location.href = teamUrl; } catch (e) { window.open(teamUrl, '_self'); }
                                sessionStorage.removeItem('fc_open_team');
                                sessionStorage.removeItem('fc_open_href');
                                return;
                            }
                            
                            // try to pair with the next segment as id: parts global index is pi+2+idx
                            const globalIdx = pi + 2 + idx;
                            const possibleId = parts[globalIdx + 1] || '';
                            const possibleIdClean = (possibleId || '').replace(/\/$/, '');
                            const looksLikeId = /^[A-Za-z0-9]{4,}$/.test(possibleIdClean);
                            if (looksLikeId && (slugNorm && (slugNorm.includes(tn) || tn.includes(slugNorm)))) {
                                const idClean = possibleIdClean.replace(/^\/+|\/+$/g, '');
                                const teamUrl = `https://www.flashscore.es/equipo/${encodeURIComponent(slugClean)}/${encodeURIComponent(idClean)}/`;
                                try { window.location.href = teamUrl; } catch (e) { window.open(teamUrl, '_self'); }
                                sessionStorage.removeItem('fc_open_team');
                                sessionStorage.removeItem('fc_open_href');
                                return;
                            }
                            // fallback: match only slug
                            if (slugNorm && (slugNorm.includes(tn) || tn.includes(slugNorm))) {
                                const teamUrl = `https://www.flashscore.es/equipo/${encodeURIComponent(slugClean)}/`;
                                try { window.location.href = teamUrl; } catch (e) { window.open(teamUrl, '_self'); }
                                sessionStorage.removeItem('fc_open_team');
                                sessionStorage.removeItem('fc_open_href');
                                return;
                            }
                        }
                    }
                }
            } catch (e) { }

            const clickTeamOnce = () => {
                const tn = normalize(teamName);

                // FIRST: Try team index for direct navigation (fastest path)
                const idx = loadTeamIndex();
                const k = normalizeKey(teamName);
                if (idx && idx[k]) {
                    try {
                        const normalized = normalizeTeamHref(idx[k]);
                        try { window.location.href = normalized; } catch (e) { window.open(normalized, '_self'); }
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    } catch (e) { }
                }

                // SECOND: Try to navigate directly from savedHref only if it's a team href, not a match href
                const savedHref = sessionStorage.getItem('fc_open_href') || urlParams.get('fc_open_href') || '';
                if (savedHref && /\/equipo\//i.test(savedHref)) {
                    try {
                        const normalized = normalizeTeamHref(savedHref);
                        try { window.location.href = normalized; } catch (e) { window.open(normalized, '_self'); }
                        sessionStorage.removeItem('fc_open_team');
                        sessionStorage.removeItem('fc_open_href');
                        return true;
                    } catch (e) { }
                }

                // Priority: look inside participant name blocks for exact anchors
                try {
                    const special = Array.from(document.querySelectorAll('.participant__participantName.participant__overflow a[href]'));
                    for (const a of special) {
                        const text = (a.textContent||'').trim();
                        const href = a.getAttribute('href') || '';
                        const hrefSlug = href.split('/').filter(Boolean).pop() || '';
                        if ((text && normalize(text).includes(tn)) || (hrefSlug && normalize(hrefSlug).includes(tn))) {
                            dispatchClick(a);
                            sessionStorage.removeItem('fc_open_team');
                            sessionStorage.removeItem('fc_open_href');
                            return true;
                        }
                    }
                } catch (e) { }

                // First try to find anchors with /equipo/ where either text or href slug matches the team
                const anchors = Array.from(document.querySelectorAll('a[href]'))
                    .filter(a => /\/equipo\//i.test(a.getAttribute('href')) || /\/team\//i.test(a.getAttribute('href')));

                for (const a of anchors) {
                    const text = (a.textContent||'').trim();
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

                // Second: search within common match containers for elements containing the team name
                const scopeCandidates = Array.from(document.querySelectorAll('.event__match, .duelParticipant, .matchHeader, body'));
                for (const scope of scopeCandidates) {
                    const els = Array.from(scope.querySelectorAll('a,button,span,li,div'));
                    for (const el of els) {
                        const txt = (el.textContent||'').trim();
                        if (txt && normalize(txt).includes(tn)) {
                            dispatchClick(el);
                            sessionStorage.removeItem('fc_open_team');
                            sessionStorage.removeItem('fc_open_href');
                            return true;
                        }
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
                    // This should not happen if team index works
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
        try { scanAndIndexTeams(); } catch (e) {}
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
        try { scanAndIndexTeams(); } catch (e) {}
        setInterval(() => { try { scanAndIndexTeams(); } catch (e) {} }, 5000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(addButtonsToAllMatches, 2000);
})();
