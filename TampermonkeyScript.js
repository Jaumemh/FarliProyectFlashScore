// ==UserScript==
// @name         Flashcore Overlay - Windows App Integration
// @namespace    http://tampermonkey.net/
// @version      13.1
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
            if (json) {
                if (json.action === 'navigate' && json.href) {
                    try { sessionStorage.removeItem('fc_open_tab'); sessionStorage.removeItem('fc_open_href'); } catch { }
                    window.location.href = json.href;
                } else if (json.action === 'uncheck' && json.matchId) {
                    let targetBtn = document.querySelector(`.fc-overlay-btn[data-match-id="${json.matchId}"]`);

                    if (!targetBtn) {
                        const matchEl = document.getElementById(json.matchId) || document.querySelector(`[id='${json.matchId}']`);
                        if (matchEl) targetBtn = matchEl.querySelector('.fc-overlay-btn');
                    }

                    if (targetBtn) {
                        targetBtn.classList.remove('active');
                    }
                }
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
        const eventId = matchElement.getAttribute('data-event-id') || matchElement.id;
        if (eventId && eventId.length > 5) return eventId;

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
                const h = anchor.getAttribute('href');
                if (h) return h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
            }
            const anchors = Array.from(matchElement.querySelectorAll('a[href]'));
            for (const a of anchors) {
                const h = a.getAttribute('href');
                if (/\/equipo\//i.test(h) || /\/team\//i.test(h)) {
                    return h.startsWith('http') ? h : `https://www.flashscore.es${h}`;
                }
            }
        } catch (e) {
            // ignore
        }
        return '';
    }

    // Helper to get participant data (name, logo, href)
    function getParticipantData(matchNode, participantSelector) {
        const participantEl = matchNode.querySelector(participantSelector);
        if (!participantEl) return { name: '', logo: '', href: '' };

        // Try multiple selectors for name
        let name = '';
        const nameNode = participantEl.querySelector('.wcl-name_jjfMf') ||
            participantEl.querySelector('.event__participant') ||
            participantEl.querySelector('.participant__participantName'); // Possible new generic class

        if (nameNode) {
            name = nameNode.textContent.trim();
        } else {
            // Fallback: Get own text but exclude image text if any
            name = participantEl.innerText.trim();
        }

        // Try src and data-src for logo
        const img = participantEl.querySelector('img');
        let logo = '';
        if (img) {
            logo = img.getAttribute('src') || img.getAttribute('data-src') || '';
        }

        let href = getTeamHref(matchNode, participantSelector);

        return { name, logo, href };
    }

    function makeLinksAbsolute(node) {
        node.querySelectorAll('a').forEach(a => {
            a.href = a.href;
        });
        node.querySelectorAll('img').forEach(img => {
            img.src = img.src;
        });
    }

    function getCompetitionData(headerNode) {
        if (!headerNode) return null;

        const titleNode = headerNode.querySelector('.headerLeague__title');
        const categoryNode = headerNode.querySelector('.headerLeague__category-text');
        const logoImg = headerNode.querySelector('.headerLeague__flag');
        const classificationLink = headerNode.querySelector('a[href*="/clasificacion/"], a[href*="/cuadro/"]');
        const titleLink = headerNode.querySelector('a.headerLeague__title');

        // Extract Title
        let title = "Competición";
        if (titleNode) title = titleNode.innerText.trim();

        // Extract Category
        let category = "";
        if (categoryNode) category = categoryNode.innerText.trim();

        // Extract Logo
        let logo = "";
        if (logoImg && logoImg.src) logo = logoImg.src;

        // Generate ID
        let id = (category + "-" + title).replace(/\s+/g, '-').toLowerCase();

        // Extract Href - Prioritize Classification/Standings/Draw
        let href = "";
        if (classificationLink) {
            href = classificationLink.href;
        } else if (titleLink) {
            href = titleLink.href;
        }

        // RAW HTML EXTRACTION
        let htmlSource = "";
        try {
            const clone = headerNode.cloneNode(true);
            makeLinksAbsolute(clone);
            const accordion = clone.querySelector('.wcl-accordion_7Fi80');
            if (accordion) accordion.remove();

            htmlSource = clone.outerHTML;
        } catch (e) { console.error("Error cloning header", e); }

        return {
            competitionId: id,
            title: title,
            category: category,
            logo: logo,
            href: href,
            htmlSource: htmlSource
        };
    }

    function getMatchData(matchNode) {
        if (!matchNode) return null;

        const id = matchNode.getAttribute('id');
        const mid = matchNode.getAttribute('data-mid'); // Internal match ID

        const homeParams = getParticipantData(matchNode, '.event__participant--home');
        const awayParams = getParticipantData(matchNode, '.event__participant--away');

        const timeNode = matchNode.querySelector('.event__time');
        const stageNode = matchNode.querySelector('.event__stage');
        const homeScoreNode = matchNode.querySelector('.event__score--home');
        const awayScoreNode = matchNode.querySelector('.event__score--away');
        const linkNode = matchNode.querySelector('a.eventRowLink, a[href*="/partido/"]');

        let url = "";
        if (linkNode) {
            const href = linkNode.getAttribute('href');
            if (href) {
                url = href.startsWith('http') ? href : `https://www.flashscore.es${href}`;
            }
        }

        // RAW HTML EXTRACTION
        let htmlSource = "";
        try {
            const clone = matchNode.cloneNode(true);
            makeLinksAbsolute(clone);
            // Remove our own overlay button to prevent recursion
            const overlayBtn = clone.querySelector('.fc-overlay-btn');
            if (overlayBtn) overlayBtn.remove();

            htmlSource = clone.outerHTML;
        } catch (e) { console.error("Error cloning match", e); }

        return {
            matchId: id,
            overlayId: id,
            url: url,
            matchMid: mid,
            homeTeam: homeParams.name,
            awayTeam: awayParams.name,
            homeLogo: homeParams.logo,
            awayLogo: awayParams.logo,
            homeHref: homeParams.href,
            awayHref: awayParams.href,
            time: timeNode ? timeNode.innerText.trim() : "",
            stage: stageNode ? stageNode.innerText.trim() : "",
            homeScore: homeScoreNode ? homeScoreNode.innerText.trim() : "",
            awayScore: awayScoreNode ? awayScoreNode.innerText.trim() : "",
            htmlSource: htmlSource
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

        // Store the matchId on the button for easy lookup from uncheck commands
        const matchId = getMatchIdentifier(matchElement);
        btn.setAttribute('data-match-id', matchId);

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
            const competitionData = getCompetitionData(headerWrapper);

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
                button.addEventListener('click', function () {
                    setTimeout(addButtonsToAllMatches, 500);
                });
            }
        });
    }

    function tryOpenClassificationTabOnPage() {
        try {
            const urlParams = new URLSearchParams(window.location.search);
            const param = urlParams.get('fc_open_tab');
            const desired = sessionStorage.getItem('fc_open_tab') || param;
            if (desired !== 'classification') return;

            const cleanup = () => {
                sessionStorage.removeItem('fc_open_tab');
                sessionStorage.removeItem('fc_open_href');
                try {
                    if (param) {
                        urlParams.delete('fc_open_tab');
                        const newUrl = window.location.pathname + (urlParams.toString() ? '?' + urlParams.toString() : '') + window.location.hash;
                        history.replaceState({}, '', newUrl);
                    }
                } catch (e) { }
            };

            const clickTab = (regex) => {
                const candidates = Array.from(document.querySelectorAll('a,button,span,li'))
                    .filter(el => el.textContent && regex.test(el.textContent));

                if (candidates.length) {
                    candidates[0].click();
                    return true;
                }

                const anchors = Array.from(document.querySelectorAll('a[href]'))
                    .filter(a => regex.test(a.getAttribute('href')));

                if (anchors.length) {
                    window.location.href = anchors[0].href;
                    return true;
                }
                return false;
            };

            if (clickTab(/clasific/i)) {
                cleanup();
                return;
            }

            let attempts = 0;
            const maxAttempts = 6;
            const iv = setInterval(() => {
                attempts++;
                if (clickTab(/clasific/i)) {
                    clearInterval(iv);
                    cleanup();
                    return;
                }

                if (attempts >= maxAttempts) {
                    clearInterval(iv);
                    console.log('Clasificación not found, trying Cuadro...');
                    if (clickTab(/cuadro/i)) {
                        cleanup();
                        return;
                    }

                    let target = window.location.pathname;
                    if (/clasificacion\/?$/i.test(target)) {
                        target = target.replace(/clasificacion\/?$/i, 'cuadro/');
                    }
                    else if (!/cuadro\/?$/i.test(target) && !/resultados\/?$/i.test(target)) {
                        if (!target.endsWith('/')) target += '/';
                        target += 'cuadro/';
                    }
                    else {
                        if (!target.endsWith('/')) target += '/';
                        if (!/cuadro\/?$/i.test(target)) target += 'cuadro/';
                    }

                    console.log('Navigating to fallback URL:', target);
                    window.location.href = target;
                    cleanup();
                }
            }, 700);
        } catch (err) {
            console.warn('Error al intentar abrir la pestaña clasificación:', err);
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
        handleExpandableSections();
    });

    function init() {
        addButtonsToAllMatches();
        handleExpandableSections();
        observer.observe(document.body, { childList: true, subtree: true });

        sendToApp('ping', { message: 'Script cargado' });
        tryOpenClassificationTabOnPage();
        startPollingCommands();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(addButtonsToAllMatches, 2000);
})();
