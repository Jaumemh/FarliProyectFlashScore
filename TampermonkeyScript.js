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
        
        // Ping al servidor para verificar conexión
        sendToApp('ping', { message: 'Script cargado' });
        // Intentar abrir la pestaña clasificación si venimos de una navegación desde la lista
        tryOpenClassificationTabOnPage();
        // Start polling for commands from the native app (e.g., navigate in current tab)
        startPollingCommands();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(addButtonsToAllMatches, 2000);
})();
