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
        try {
            await fetch(SERVER_URL, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({
                    action: action,
                    data: data
                })
            });
        } catch (error) {
            console.error('Error conectando con la aplicación:', error);
        }
    }

    function getMatchIdentifier(matchElement) {
        const id = matchElement.id;
        if (id) return id;
        const homeTeam = matchElement.querySelector('.event__participant--home')?.textContent || '';
        const awayTeam = matchElement.querySelector('.event__participant--away')?.textContent || '';
        const time = matchElement.querySelector('.event__time')?.textContent || '';
        return `${homeTeam}-${awayTeam}-${time}`.replace(/\s+/g, '-');
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
        
        return {
            matchId: getMatchIdentifier(matchElement),
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
        return {
            competitionId: `${categoryText}:${titleText}`,
            title: titleText,
            category: categoryText,
            logo: headerBody.querySelector('.headerLeague__logo img')?.src || ''
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
                btn.classList.remove('active');
                await sendToApp('removeMatch', {
                    match: matchData,
                    competition: competitionData
                });
            } else {
                btn.classList.add('active');
                await sendToApp('addMatch', {
                    match: matchData,
                    competition: competitionData
                });
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
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(addButtonsToAllMatches, 2000);
})();
