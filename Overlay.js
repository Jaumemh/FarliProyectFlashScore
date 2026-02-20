<<<<<<< HEAD
// ==UserScript==
// @name         Flashcore Overlay
// @namespace    http://tampermonkey.net/
// @version      11.2
// @description  Overlay agrupado por competiciones con enlaces a equipos y partidos separados por doble clic.
// @author       TuNombre
// @match        https://www.flashscore.es/*
// @grant        GM_addStyle
// @run-at       document-end
// ==/UserScript==

(function() {
    'use strict';

    // --- ESTILOS CSS ---
    GM_addStyle(`
        /* Estilo del botón circular */
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

        /* Contenedor del overlay */
        .fc-overlay {
            position: fixed;
            top: 50px;
            left: 50px;
            z-index: 2147483647;
            border-radius: 8px;
            overflow: hidden;
            box-shadow: 0 4px 20px rgba(0,0,0,0.8);
            background-color: #000000 !important;
            min-width: 400px;
            border: 1px solid #333;
            cursor: default !important;
        }

        .fc-overlay .fc-overlay-btn {
            display: none !important;
        }

        .fc-overlay.always-on-top {
            z-index: 2147483647 !important;
        }

        .fc-overlay-inner {
            width: 100%;
            height: 100%;
            overflow: auto;
            background-color: #000000;
            cursor: default !important;
        }

        /* Contenedor de competición */
        .fc-competition-section {
            margin-bottom: 5px;
            border-bottom: 2px solid #333;
        }
        .fc-competition-section:last-child {
            border-bottom: none;
        }

        /* Header de competición (una sola vez por competición) */
        .fc-competition-header {
            padding: 8px 12px;
            background: #1a1a1a;
            border-bottom: 1px solid #333;
            color: white;
            font-weight: bold;
            font-size: 12px;
            cursor: default !important;
            position: sticky;
            top: 0;
            z-index: 100;
        }

        /* Contenedor de partidos de la competición */
        .fc-competition-matches {
            padding: 10px;
            background-color: #000000;
            cursor: default !important;
        }

        /* Cada partido dentro del contenedor */
        .fc-match-item {
            padding: 0;
            margin-bottom: 5px;
            border-bottom: 1px solid #333;
            background-color: #000000;
        }
        .fc-match-item:last-child {
            border-bottom: none;
            margin-bottom: 0;
        }

        /* Ocultar elementos no deseados en el overlay */
        .fc-overlay .headerLeague__star,
        .fc-overlay .wizard__relativeWrapper,
        .fc-overlay .headerLeague__actions,
        .fc-overlay .anclar-partido-btn,
        .fc-overlay .wcl-favorite_ggUc2,
        .fc-overlay .wcl-pin_J5btx,
        .fc-overlay .wcl-accordion_7Fi80 {
            display: none !important;
        }

        .fc-overlay a {
            pointer-events: none !important;
        }

        .fc-overlay .eventRowLink {
            display: none !important;
        }

        /* Partido dentro del overlay */
        .fc-overlay .event__match {
            display: grid !important;
            grid-template-columns: 30px auto 1fr auto 30px 40px;
            grid-template-rows: auto auto;
            align-items: center;
            padding: 8px 10px;
            background-color: #000000;
            min-height: 60px;
            gap: 4px;
            width: 100%;
            box-sizing: border-box;
            cursor: default !important;
            border-bottom: none !important;
        }
        .fc-overlay .event__match:last-child {
            border-bottom: none;
        }

        .fc-overlay .event__stage {
            grid-column: 1;
            grid-row: 1 / span 2;
            color: #ff4444 !important;
            font-size: 11px;
            text-align: center;
            white-space: nowrap;
        }
        .fc-overlay .event__logo--home {
            grid-column: 2;
            grid-row: 1;
            width: 20px;
            height: 20px;
        }
        .fc-overlay .event__participant--home {
            grid-column: 3;
            grid-row: 1;
            color: #ffffff !important;
            font-size: 12px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .fc-overlay .event__score--home {
            grid-column: 4;
            grid-row: 1;
            color: #ffffff !important;
            font-size: 14px;
            font-weight: bold;
            text-align: center;
        }
        .fc-overlay .event__part--home {
            grid-column: 5;
            grid-row: 1;
            color: #cccccc !important;
            font-size: 11px;
            text-align: center;
        }
        .fc-overlay .event__logo--away {
            grid-column: 2;
            grid-row: 2;
            width: 20px;
            height: 20px;
        }
        .fc-overlay .event__participant--away {
            grid-column: 3;
            grid-row: 2;
            color: #ffffff !important;
            font-size: 12px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .fc-overlay .event__score--away {
            grid-column: 4;
            grid-row: 2;
            color: #ffffff !important;
            font-size: 14px;
            font-weight: bold;
            text-align: center;
        }
        .fc-overlay .event__part--away {
            grid-column: 5;
            grid-row: 2;
            color: #cccccc !important;
            font-size: 11px;
            text-align: center;
        }
        .fc-overlay .event__icon--tv {
            grid-column: 6;
            grid-row: 1 / span 2;
            align-self: center;
            justify-self: center;
        }

        .fc-close-btn {
            cursor: default !important;
        }
    `);

    // --- VARIABLES GLOBALES ---
    let isDragging = false;
    let dragOffset = { x: 0, y: 0 };
    let dragTarget = null;
    let clickStartTime = 0;
    let clickStartX = 0;
    let clickStartY = 0;
    let mainOverlay = null;
    const competitionsInOverlay = new Map(); // competitionId -> { section, matchesContainer, matchIds }
    const matchToCompetitionMap = new Map(); // matchId -> competitionId

    // --- CLONADO CON ESTILOS ---
    function getComputedStyleText(element) {
        const computed = getComputedStyle(element);
        let styleText = '';
        for (let i = 0; i < computed.length; i++) {
            const prop = computed[i];
            if (!['width', 'height', 'position', 'top', 'left', 'right', 'bottom', 'z-index'].includes(prop)) {
                const value = computed.getPropertyValue(prop);
                if (value) styleText += `${prop}:${value};`;
            }
        }
        return styleText;
    }

    function cloneWithStyles(element) {
        const clone = element.cloneNode(false);
        clone.setAttribute('style', getComputedStyleText(element));
        for (let i = 0; i < element.childNodes.length; i++) {
            const child = element.childNodes[i];
            if (child.nodeType === Node.ELEMENT_NODE) {
                clone.appendChild(cloneWithStyles(child));
            } else if (child.nodeType === Node.TEXT_NODE) {
                clone.appendChild(document.createTextNode(child.textContent));
            }
        }
        return clone;
    }

    function getMatchIdentifier(matchElement) {
        const id = matchElement.id;
        if (id) return id;
        const homeTeam = matchElement.querySelector('.event__participant--home')?.textContent || '';
        const awayTeam = matchElement.querySelector('.event__participant--away')?.textContent || '';
        const time = matchElement.querySelector('.event__time')?.textContent || '';
        return `${homeTeam}-${awayTeam}-${time}`.replace(/\s+/g, '-');
    }

    function getCompetitionIdentifier(headerBody) {
        const titleText = headerBody.querySelector('.headerLeague__title-text')?.textContent.trim();
        const categoryText = headerBody.querySelector('.headerLeague__category-text')?.textContent.trim();
        if (titleText && categoryText) return `${categoryText}:${titleText}`;
        return titleText || 'unknown';
    }

    // --- LOCALIZAR HEADER DE COMPETICIÓN ---
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
                    const titleWrapper = body?.querySelector('.headerLeague__titleWrapper');
                    const categoryText = body?.querySelector('.headerLeague__category-text');
                    if (body && titleWrapper && categoryText) {
                        minDistance = distance;
                        closestWrapper = wrapper;
                    }
                }
            }
        });

        return closestWrapper;
    }

    // --- BOTÓN EN CADA PARTIDO ---
    function createOverlayButton(matchElement) {
        if (matchElement.querySelector('.fc-overlay-btn')) return;

        const btn = document.createElement('button');
        btn.className = 'fc-overlay-btn';
        btn.title = 'Abrir/Cerrar overlay del partido';

        btn.addEventListener('click', function(e) {
            e.stopPropagation();
            e.preventDefault();
            toggleMatchInOverlay(matchElement, btn);
        });

        matchElement.style.position = 'relative';
        matchElement.appendChild(btn);
    }

    // --- TOGGLE PARTIDO EN OVERLAY ---
    function toggleMatchInOverlay(matchElement, btn) {
        const matchId = getMatchIdentifier(matchElement);

        // Si ya está, quitarlo
        if (matchToCompetitionMap.has(matchId)) {
            const competitionId = matchToCompetitionMap.get(matchId);
            removeMatchFromOverlay(competitionId, matchId);
            btn.classList.remove('active');
            return;
        }

        // Buscar header de competición
        const headerWrapper = findHeaderLeagueWrapper(matchElement);
        if (!headerWrapper) {
            alert('No se pudo identificar la competición de este partido');
            return;
        }

        const headerBody = headerWrapper.querySelector('.headerLeague__body');
        if (!headerBody) {
            alert('Header de competición incompleto');
            return;
        }

        const competitionId = getCompetitionIdentifier(headerBody);

        if (!mainOverlay) {
            createMainOverlay();
        }

        if (!competitionsInOverlay.has(competitionId)) {
            createCompetitionSection(competitionId, headerBody);
        }

        addMatchToCompetition(competitionId, matchElement, matchId);
        btn.classList.add('active');
    }

    // --- OVERLAY PRINCIPAL ---
    function createMainOverlay() {
        const overlay = document.createElement('div');
        overlay.className = 'fc-overlay always-on-top';

        const overlayInner = document.createElement('div');
        overlayInner.className = 'fc-overlay-inner';

        overlay.appendChild(overlayInner);
        document.body.appendChild(overlay);
        mainOverlay = overlay;

        // Drag & drop
        overlay.addEventListener('mousedown', function(e) {
            if (e.button === 0) {
                clickStartTime = Date.now();
                clickStartX = e.clientX;
                clickStartY = e.clientY;
                isDragging = false;
                setTimeout(() => {
                    if (isDragging) {
                        dragTarget = overlay;
                        const rect = overlay.getBoundingClientRect();
                        dragOffset.x = e.clientX - rect.left;
                        dragOffset.y = e.clientY - rect.top;
                        bringToFront(overlay);
                    }
                }, 100);
                e.preventDefault();
            }
        });

        overlay.addEventListener('mousemove', function(e) {
            if (clickStartTime > 0) {
                const moveX = Math.abs(e.clientX - clickStartX);
                const moveY = Math.abs(e.clientY - clickStartY);
                if (moveX > 5 || moveY > 5) {
                    isDragging = true;
                }
            }
        });

        // Doble clic en overlay solo trae al frente
        overlay.addEventListener('dblclick', function(e) {
            bringToFront(overlay);
            e.preventDefault();
        });

        // Cerrar con botón derecho
        overlay.addEventListener('contextmenu', function(e) {
            document.querySelectorAll('.fc-overlay-btn.active').forEach(btn => btn.classList.remove('active'));
            competitionsInOverlay.clear();
            matchToCompetitionMap.clear();
            document.body.removeChild(overlay);
            mainOverlay = null;
            e.preventDefault();
        });

        overlay.addEventListener('click', function(e) {
            const currentTime = Date.now();
            const moveX = Math.abs(e.clientX - clickStartX);
            const moveY = Math.abs(e.clientY - clickStartY);
            if (currentTime - clickStartTime < 300 && moveX < 5 && moveY < 5) {
                bringToFront(overlay);
                e.preventDefault();
                e.stopPropagation();
            }
        });

        overlay.addEventListener('mouseup', function() {
            clickStartTime = 0;
            isDragging = false;
        });
    }

    // --- SECCIÓN DE COMPETICIÓN ---
    function createCompetitionSection(competitionId, headerBody) {
        const overlayInner = mainOverlay.querySelector('.fc-overlay-inner');

        const section = document.createElement('div');
        section.className = 'fc-competition-section';
        section.dataset.competitionId = competitionId;

        const headerClone = cloneWithStyles(headerBody);
        const unwanted = headerClone.querySelectorAll('.headerLeague__star, .wizard__relativeWrapper, .headerLeague__actions');
        unwanted.forEach(el => el.remove());

        const competitionHeader = document.createElement('div');
        competitionHeader.className = 'fc-competition-header';
        competitionHeader.appendChild(headerClone);

        const leagueLink = headerBody.querySelector('.headerLeague__title');
        if (leagueLink && leagueLink.href) {
            competitionHeader.addEventListener('dblclick', function(e) {
                window.open(leagueLink.href, '_blank');
                e.stopPropagation();
            });
        }

        const matchesContainer = document.createElement('div');
        matchesContainer.className = 'fc-competition-matches';

        section.appendChild(competitionHeader);
        section.appendChild(matchesContainer);
        overlayInner.appendChild(section);

        competitionsInOverlay.set(competitionId, {
            section,
            matchesContainer,
            matchIds: new Set()
        });
    }

    // --- AÑADIR PARTIDO A COMPETICIÓN ---
    function addMatchToCompetition(competitionId, matchElement, matchId) {
        const competition = competitionsInOverlay.get(competitionId);
        if (!competition) return;

        const matchItem = document.createElement('div');
        matchItem.className = 'fc-match-item';
        matchItem.dataset.matchId = matchId;

        const matchClone = cloneWithStyles(matchElement);

        const btnClone = matchClone.querySelector('.fc-overlay-btn');
        if (btnClone) btnClone.remove();

        const rowLink = matchElement.querySelector('a.eventRowLink');
        const matchUrl = rowLink ? rowLink.href : null;

        const rowLinkClone = matchClone.querySelector('.eventRowLink');
        if (rowLinkClone) rowLinkClone.remove();

        const unwanted = matchClone.querySelectorAll('.wcl-favorite_ggUc2, .anclar-partido-btn, .liveBetWrapper');
        unwanted.forEach(el => el.remove());

        matchClone.style.width = '100%';
        matchClone.style.boxSizing = 'border-box';

        // Doble clic en el bloque de partido => página del PARTIDO
        matchClone.addEventListener('dblclick', function(e) {
            if (e.target.closest('.event__participant')) return;
            if (matchUrl) window.open(matchUrl, '_blank');
            e.stopPropagation();
        });

        // Doble clic en nombres / logos => página del EQUIPO
        const participants = matchClone.querySelectorAll('.event__participant');
        participants.forEach(participant => {
            const isHome = participant.classList.contains('event__participant--home');
            const originalParticipant = matchElement.querySelector(
                isHome ? '.event__participant--home' : '.event__participant--away'
            );
            const teamLink = originalParticipant?.querySelector('a');
            if (teamLink && teamLink.href) {
                participant.style.cursor = 'pointer';
                participant.addEventListener('dblclick', function(e) {
                    window.open(teamLink.href, '_blank'); // p.ej. /equipo/bielefeld/...
                    e.stopPropagation();
                });
            }
        });

        matchItem.appendChild(matchClone);
        competition.matchesContainer.appendChild(matchItem);
        competition.matchIds.add(matchId);
        matchToCompetitionMap.set(matchId, competitionId);
    }

    // --- QUITAR PARTIDO DEL OVERLAY ---
    function removeMatchFromOverlay(competitionId, matchId) {
        const competition = competitionsInOverlay.get(competitionId);
        if (!competition) return;

        const matchItem = competition.matchesContainer.querySelector(`[data-match-id="${matchId}"]`);
        if (matchItem) matchItem.remove();

        competition.matchIds.delete(matchId);
        matchToCompetitionMap.delete(matchId);

        if (competition.matchIds.size === 0) {
            competition.section.remove();
            competitionsInOverlay.delete(competitionId);
        }

        if (competitionsInOverlay.size === 0 && mainOverlay) {
            document.body.removeChild(mainOverlay);
            mainOverlay = null;
        }
    }

    // --- Z-INDEX ---
    function bringToFront(overlay) {
        let maxZIndex = 2147483640;
        document.querySelectorAll('.fc-overlay').forEach(ov => {
            const zIndex = parseInt(window.getComputedStyle(ov).zIndex);
            if (zIndex > maxZIndex) maxZIndex = zIndex;
        });
        overlay.style.zIndex = (maxZIndex + 1).toString();
    }

    document.addEventListener('mousemove', function(e) {
        if (isDragging && dragTarget) {
            dragTarget.style.left = (e.clientX - dragOffset.x) + 'px';
            dragTarget.style.top = (e.clientY - dragOffset.y) + 'px';
        }
    });

    document.addEventListener('mouseup', function() {
        isDragging = false;
        dragTarget = null;
    });

    // --- INYECCIÓN DE BOTONES ---
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

    document.addEventListener('visibilitychange', function() {
        if (!document.hidden && mainOverlay) bringToFront(mainOverlay);
    });

    window.addEventListener('focus', function() {
        if (mainOverlay) bringToFront(mainOverlay);
    });

    function init() {
        addButtonsToAllMatches();
        handleExpandableSections();
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    setInterval(() => {
        if (mainOverlay) {
            const currentZIndex = parseInt(window.getComputedStyle(mainOverlay).zIndex);
            if (currentZIndex < 2147483640) {
                mainOverlay.style.zIndex = '2147483640';
            }
        }
    }, 1000);

    setInterval(addButtonsToAllMatches, 2000);
})();
