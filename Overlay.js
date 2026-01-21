// ==UserScript==
// @name         Flashcore Overlay
// @namespace    http://tampermonkey.net/
// @version      10.2
// @description  Overlay con búsqueda específica de headerLeague__wrapper
// @author       TuNombre
// @match        https://www.flashscore.es/*
// @grant        GM_addStyle
// @run-at       document-end
// ==/UserScript==

(function() {
    'use strict';

    // --- ESTILOS CSS ---
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

        .fc-overlay {
            position: fixed;
            top: 50px;
            left: 50px;
            z-index: 2147483647;
            border-radius: 8px;
            overflow: hidden;
            box-shadow: 0 4px 20px rgba(0,0,0,0.8);
            background-color: #000000 !important;
            min-width: 450px;
            max-width: 600px;
            max-height: 80vh;
            border: 1px solid #333;
            cursor: move;
        }

        .fc-overlay .fc-overlay-btn {
            display: none !important;
        }

        .fc-overlay-inner {
            width: 100%;
            height: 100%;
            overflow-y: auto;
            overflow-x: hidden;
            background-color: #000000;
            padding: 5px;
        }

        .fc-match-block {
            margin-bottom: 10px;
            border: 1px solid #333;
            border-radius: 4px;
            overflow: hidden;
            background-color: #0a0a0a;
        }

        .fc-match-competition-header {
            padding: 6px 10px;
            background: #1a1a1a;
            border-bottom: 1px solid #444;
            cursor: pointer;
            font-size: 11px;
        }
        .fc-match-competition-header:hover {
            background: #2a2a2a;
        }

        .fc-match-content {
            padding: 5px;
            background-color: #000000;
        }

        .fc-overlay .wcl-accordion_7Fi80 {
            display: none !important;
        }

        .fc-overlay .headerLeague__title {
            pointer-events: auto !important;
            cursor: pointer !important;
        }

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
            cursor: pointer;
            transition: background-color 0.2s;
            border-bottom: none !important;
        }

        .fc-overlay .event__match:hover {
            background-color: #1a1a1a;
        }

        .fc-overlay .event__participant {
            pointer-events: auto !important;
            cursor: pointer !important;
        }
        .fc-overlay .event__participant:hover {
            text-decoration: underline;
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
    `);

    // --- VARIABLES GLOBALES ---
    let isDragging = false;
    let dragOffset = { x: 0, y: 0 };
    let dragTarget = null;
    let clickStartTime = 0;
    let clickStartX = 0;
    let clickStartY = 0;
    let mainOverlay = null;
    const matchesInOverlay = new Map();

    // --- FUNCIONES DE CLONADO ---
    function getComputedStyleText(element) {
        const computed = getComputedStyle(element);
        let styleText = '';

        for (let i = 0; i < computed.length; i++) {
            const prop = computed[i];
            if (!['width', 'height', 'position', 'top', 'left', 'right', 'bottom', 'z-index'].includes(prop)) {
                const value = computed.getPropertyValue(prop);
                if (value) {
                    styleText += `${prop}:${value};`;
                }
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

    // --- BÚSQUEDA ESPECÍFICA DEL HEADER ---
    function findHeaderLeagueWrapper(matchElement) {
        console.log('🔍 Buscando headerLeague__wrapper para partido:', getMatchIdentifier(matchElement));
        
        // Obtener la posición del partido
        const matchRect = matchElement.getBoundingClientRect();
        
        // Buscar TODOS los headerLeague__wrapper en la página
        const allWrappers = document.querySelectorAll('.headerLeague__wrapper');
        console.log('📋 Encontrados', allWrappers.length, 'headerLeague__wrapper en la página');
        
        let closestWrapper = null;
        let minDistance = Infinity;
        
        allWrappers.forEach((wrapper, index) => {
            const wrapperRect = wrapper.getBoundingClientRect();
            
            // Solo considerar wrappers que estén ARRIBA del partido
            if (wrapperRect.bottom <= matchRect.top) {
                const distance = matchRect.top - wrapperRect.bottom;
                
                console.log(`  Wrapper ${index}: distancia = ${distance.toFixed(2)}px`);
                
                if (distance < minDistance) {
                    // Verificar que tiene headerLeague__body válido
                    const body = wrapper.querySelector('.headerLeague__body');
                    const titleWrapper = body?.querySelector('.headerLeague__titleWrapper');
                    const categoryText = body?.querySelector('.headerLeague__category-text');
                    
                    if (body && titleWrapper && categoryText) {
                        minDistance = distance;
                        closestWrapper = wrapper;
                        console.log(`  ✓ Wrapper ${index} es candidato válido (distancia: ${distance.toFixed(2)}px)`);
                    } else {
                        console.log(`  ✗ Wrapper ${index} no tiene estructura válida`);
                    }
                }
            }
        });
        
        if (closestWrapper) {
            const body = closestWrapper.querySelector('.headerLeague__body');
            const titleText = body.querySelector('.headerLeague__title-text')?.textContent;
            const categoryText = body.querySelector('.headerLeague__category-text')?.textContent;
            console.log(`✅ Header encontrado: ${categoryText} - ${titleText} (distancia: ${minDistance.toFixed(2)}px)`);
            return closestWrapper;
        }
        
        console.error('❌ No se encontró headerLeague__wrapper válido para este partido');
        return null;
    }

    // --- FUNCIONES PRINCIPALES ---
    function createOverlayButton(matchElement) {
        if (matchElement.querySelector('.fc-overlay-btn')) {
            return;
        }

        const btn = document.createElement('button');
        btn.className = 'fc-overlay-btn';
        btn.textContent = '⬚';
        btn.title = 'Abrir/Cerrar overlay del partido';

        btn.addEventListener('click', function(e) {
            e.stopPropagation();
            e.preventDefault();
            toggleMatchInOverlay(matchElement, btn);
        });

        matchElement.style.position = 'relative';
        matchElement.appendChild(btn);
    }

    function toggleMatchInOverlay(matchElement, btn) {
        const matchId = getMatchIdentifier(matchElement);

        // Si el partido ya está añadido, quitarlo
        if (matchesInOverlay.has(matchId)) {
            removeMatchFromOverlay(matchId);
            btn.classList.remove('active');
            return;
        }

        // Buscar el headerLeague__wrapper más cercano
        const headerWrapper = findHeaderLeagueWrapper(matchElement);

        if (!headerWrapper) {
            console.error('No se encontró headerLeague__wrapper para este partido');
            alert('No se pudo identificar la competición de este partido');
            return;
        }

        // Obtener el headerLeague__body
        const headerBody = headerWrapper.querySelector('.headerLeague__body');
        if (!headerBody) {
            console.error('No se encontró headerLeague__body');
            alert('Header de competición incompleto');
            return;
        }

        // Verificar estructura completa
        const titleWrapper = headerBody.querySelector('.headerLeague__titleWrapper');
        const categoryText = headerBody.querySelector('.headerLeague__category-text');
        
        if (!titleWrapper || !categoryText) {
            console.error('Estructura del header incompleta');
            alert('No se pudo extraer información de la competición');
            return;
        }

        // Crear el overlay si no existe
        if (!mainOverlay) {
            createMainOverlay();
        }

        // Añadir el partido con su header
        addMatchToOverlay(matchElement, headerBody, matchId);
        btn.classList.add('active');
    }

    function createMainOverlay() {
        const overlay = document.createElement('div');
        overlay.className = 'fc-overlay always-on-top';

        const overlayInner = document.createElement('div');
        overlayInner.className = 'fc-overlay-inner';

        overlay.appendChild(overlayInner);
        document.body.appendChild(overlay);

        mainOverlay = overlay;

        // --- DRAG AND DROP ---
        overlay.addEventListener('mousedown', function(e) {
            if (e.button === 0) {
                clickStartTime = Date.now();
                clickStartX = e.clientX;
                clickStartY = e.clientY;
                isDragging = false;

                setTimeout(() => {
                    if (isDragging) {
                        dragTarget = overlay;
                        dragOffset.x = e.clientX - overlay.getBoundingClientRect().left;
                        dragOffset.y = e.clientY - overlay.getBoundingClientRect().top;
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

        // --- CERRAR CON BOTÓN DERECHO ---
        overlay.addEventListener('contextmenu', function(e) {
            document.querySelectorAll('.fc-overlay-btn.active').forEach(btn => {
                btn.classList.remove('active');
            });
            
            matchesInOverlay.clear();
            document.body.removeChild(overlay);
            mainOverlay = null;
            e.preventDefault();
        });

        overlay.addEventListener('mouseup', function() {
            clickStartTime = 0;
            isDragging = false;
        });
    }

    function addMatchToOverlay(matchElement, headerBody, matchId) {
        const overlayInner = mainOverlay.querySelector('.fc-overlay-inner');

        // Crear el bloque del partido
        const matchBlock = document.createElement('div');
        matchBlock.className = 'fc-match-block';
        matchBlock.dataset.matchId = matchId;

        // Clonar el headerLeague__body completo
        const headerClone = cloneWithStyles(headerBody);

        const competitionHeader = document.createElement('div');
        competitionHeader.className = 'fc-match-competition-header';
        competitionHeader.appendChild(headerClone);

        // Doble clic en header abre la competición
        const leagueLink = headerBody.querySelector('.headerLeague__title');
        if (leagueLink && leagueLink.href) {
            competitionHeader.addEventListener('dblclick', function(e) {
                window.open(leagueLink.href, '_blank');
                e.stopPropagation();
            });
        }

        // Clonar el partido
        const matchClone = cloneWithStyles(matchElement);

        // Limpiar elementos
        const btnClone = matchClone.querySelector('.fc-overlay-btn');
        if (btnClone) btnClone.remove();

        const rowLink = matchElement.querySelector('a.eventRowLink');
        const matchUrl = rowLink ? rowLink.href : null;

        const rowLinkClone = matchClone.querySelector('.eventRowLink');
        if (rowLinkClone) rowLinkClone.remove();

        const unwantedElements = matchClone.querySelectorAll('.wcl-favorite_ggUc2, .anclar-partido-btn, .liveBetWrapper');
        unwantedElements.forEach(el => el.remove());

        matchClone.style.width = '100%';
        matchClone.style.boxSizing = 'border-box';

        // Doble clic en el partido
        matchClone.addEventListener('dblclick', function(e) {
            if (matchUrl && !e.target.closest('.event__participant')) {
                window.open(matchUrl, '_blank');
                e.stopPropagation();
            }
        });

        // Doble clic en equipos
        const participants = matchClone.querySelectorAll('.event__participant');
        participants.forEach(participant => {
            const isHome = participant.classList.contains('event__participant--home');
            const originalParticipant = matchElement.querySelector(isHome ? '.event__participant--home' : '.event__participant--away');
            const teamLink = originalParticipant?.querySelector('a');
            
            if (teamLink && teamLink.href) {
                participant.style.cursor = 'pointer';
                participant.addEventListener('dblclick', function(e) {
                    window.open(teamLink.href, '_blank');
                    e.stopPropagation();
                });
            }
        });

        // Contenedor del partido
        const matchContent = document.createElement('div');
        matchContent.className = 'fc-match-content';
        matchContent.appendChild(matchClone);

        // Ensamblar el bloque
        matchBlock.appendChild(competitionHeader);
        matchBlock.appendChild(matchContent);

        // Añadir al overlay
        overlayInner.appendChild(matchBlock);

        // Guardar referencia
        matchesInOverlay.set(matchId, matchBlock);

        const titleText = headerBody.querySelector('.headerLeague__title-text')?.textContent;
        const categoryText = headerBody.querySelector('.headerLeague__category-text')?.textContent;
        console.log('✅ Partido añadido:', categoryText, '-', titleText);
    }

    function removeMatchFromOverlay(matchId) {
        const matchBlock = matchesInOverlay.get(matchId);
        if (!matchBlock) return;

        matchBlock.remove();
        matchesInOverlay.delete(matchId);

        console.log('✓ Partido eliminado:', matchId);

        // Si no quedan partidos, cerrar el overlay
        if (matchesInOverlay.size === 0 && mainOverlay) {
            document.body.removeChild(mainOverlay);
            mainOverlay = null;
        }
    }

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

    function addButtonsToAllMatches() {
        const matches = document.querySelectorAll('.event__match:not(.fc-processed)');
        matches.forEach(function(match) {
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

    const observer = new MutationObserver(function(mutations) {
        let shouldCheckMatches = false;

        mutations.forEach(function(mutation) {
            if (mutation.addedNodes.length) {
                shouldCheckMatches = true;
                mutation.addedNodes.forEach(function(node) {
                    if (node.nodeType === 1 && node.classList && node.classList.contains('event__match')) {
                        node.classList.add('fc-processed');
                        createOverlayButton(node);
                    }
                });
            }
        });

        if (shouldCheckMatches) {
            setTimeout(addButtonsToAllMatches, 100);
        }

        handleExpandableSections();
    });

    document.addEventListener('visibilitychange', function() {
        if (!document.hidden && mainOverlay) {
            bringToFront(mainOverlay);
        }
    });

    window.addEventListener('focus', function() {
        if (mainOverlay) {
            bringToFront(mainOverlay);
        }
    });

    function init() {
        addButtonsToAllMatches();
        handleExpandableSections();

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });
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
