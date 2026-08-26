/**
 * JellyTrend – Trending Banner Carousel
 * Served at /JellyTrend/jellyTrend.js
 * Injected into <head> of index.html by ScriptInjectionMiddleware.
 *
 * Primary strategy: MutationObserver watches for home-screen DOM to appear,
 * then injects the Trending section directly at the top (works on all Jellyfin
 * versions including React-based 10.10+).
 *
 * Fallback: window['editorsChoice/plugin'] for older Jellyfin versions that
 * use the pluginManager home-section system (HomeSectionType.EditorChoice).
 */
(function () {
    'use strict';

    const SECTION_ID   = 'jellytrend-home-row';
    const ADMIN_NAV_ID = 'jellytrend-admin-link';
    const CONFIG_URL   = '/web/index.html#!/configurationpage?name=JellyTrend';

    // ── CSS early injection ──────────────────────────────────────────────────
    // Cache-busts the stylesheet in lockstep with this script's own ?v= version.
    let scriptVersion = '';
    try {
        const src = (document.currentScript && document.currentScript.getAttribute('src')) || '';
        const m = src.match(/[?&]v=([^&]+)/);
        if (m) scriptVersion = m[1];
    } catch (_) { /* ignore */ }

    (function injectCSS() {
        if (document.getElementById('jellytrend-css')) return;
        const link = document.createElement('link');
        link.id   = 'jellytrend-css';
        link.rel  = 'stylesheet';
        link.href = '/JellyTrend/jellyTrend.css' + (scriptVersion ? '?v=' + scriptVersion : '');
        (document.head || document.documentElement).appendChild(link);
    })();

    // ── Auth token ───────────────────────────────────────────────────────────
    function getToken() {
        try {
            const api = window.ApiClient ?? window.top?.ApiClient ?? window.parent?.ApiClient;
            if (!api) return null;
            return typeof api.accessToken === 'function' ? api.accessToken() : (api.accessToken ?? null);
        } catch (_) { return null; }
    }

    // ── Fetch trending items from plugin API ─────────────────────────────────
    async function fetchItems(apiClient) {
        const token = apiClient?.accessToken?.() ?? getToken();
        const headers = {};
        if (token) headers['Authorization'] = `MediaBrowser Token="${token}"`;
        try {
            const r = await fetch('/JellyTrend/Trending', { headers, credentials: 'include' });
            if (!r.ok) { console.warn(`[JellyTrend] /Trending → ${r.status}`); return []; }
            return await r.json();
        } catch (e) { console.warn('[JellyTrend] fetch error', e); return []; }
    }

    // ── Official Jellyfin translations ───────────────────────────────────────
    // Reuses Jellyfin's own UI strings, loaded from the same /Localization/{culture}
    // endpoint the web client uses. The culture is the CURRENT USER's display
    // language (see resolveCulture — it follows the language the web UI is
    // actually rendering). Labels Jellyfin cannot translate (e.g. "MoreInfo") are
    // simply absent from the returned object, so the inline TRANSLATIONS below
    // act as fallback — no extra work needed.
    let officialLabels = null;
    let localizationPromise = null;

    function getApiClient() {
        return window.ApiClient ?? window.top?.ApiClient ?? window.parent?.ApiClient ?? null;
    }

    // Resolves the language the current user sees Jellyfin in. The web client
    // keeps the user's display language in localStorage (per-user) and mirrors it
    // into <html lang> (updateCurrentCulture). The server's Configuration.UICulture
    // can be stale, so the DOM is the source of truth here.
    function resolveCulture() {
        try {
            const lang = document.documentElement.getAttribute('lang');
            if (lang) return lang;
        } catch (_) { /* ignore */ }
        try {
            const dc = document.documentElement.getAttribute('data-culture');
            if (dc) return dc;
        } catch (_) { /* ignore */ }
        return navigator.language || navigator.userLanguage || 'en-US';
    }

    async function loadLocalization() {
        if (localizationPromise) return localizationPromise;
        localizationPromise = (async () => {
            try {
                const culture = resolveCulture();
                console.info('[JellyTrend] UI culture → ' + culture);
                setLang(culture);   // inline fallback follows the same language
                const api = getApiClient();
                const url = (api && typeof api.getUrl === 'function')
                    ? api.getUrl('Localization/' + culture)
                    : '/Localization/' + encodeURIComponent(culture);
                const token = getToken();
                const headers = {};
                if (token) headers['Authorization'] = `MediaBrowser Token="${token}"`;
                const r = await fetch(url, { headers, credentials: 'include' });
                if (!r.ok) return null;
                officialLabels = await r.json();
            } catch (_) { /* keep null → inline fallback */ }
        })();
        return localizationPromise;
    }

    // ── Home page detection ──────────────────────────────────────────────────
    function onHomePage() {
        const h = (location.hash || '').replace(/^#!/, '#/').toLowerCase();
        return !h || h === '#/' || h === '#/home.html' || h.startsWith('#/home');
    }

    // ── Find best injection container ────────────────────────────────────────
    // Tries a priority list of selectors for different Jellyfin web versions.
    function findContainer() {
        const selectors = [
            '#homeTab',                                      // classic emby / old Jellyfin Web
            '.homePage .content-primary',                    // React Jellyfin 10.10+
            '.homePage',
            '[data-type="home"] .content-primary',
            '[data-pageid="home"] .content-primary',
            '[data-url*="home"] .content-primary',
            '.sections',                                     // some React builds
        ];
        for (const sel of selectors) {
            const el = document.querySelector(sel);
            if (el && el.offsetParent !== null) return el;
        }
        // Last resort: first visible [data-role="page"] with rendered children
        for (const page of document.querySelectorAll('[data-role="page"]')) {
            if (page.offsetParent !== null && page.children.length) {
                return page.querySelector('.content-primary') ?? page;
            }
        }
        return null;
    }

    // ── Carousel init ────────────────────────────────────────────────────────
    function initCarousel(swiperEl) {
        if (!swiperEl) return;
        if (typeof window.Swiper === 'function') new window.Swiper(swiperEl, SWIPER_OPTIONS);
        else new FallbackCarousel(swiperEl);
    }

    // ── Inject section into home screen ──────────────────────────────────────
    let injecting = false;
    let containerRetryTimer = null;
    let lastEmptyAt = 0;
    let lastRenderedIds = '';
    let lastFetchAt = 0;

    // Renders the carousel only when the list actually changed (the server already
    // hides watched titles, so a change means a just-watched item disappeared or
    // the section was removed while leaving Home).
    function renderBanner(items) {
        if (!items.length) return;
        const ids = items.map(i => String(i.Id)).join('|');
        const section = document.getElementById(SECTION_ID);
        if (section && ids === lastRenderedIds) return;
        lastRenderedIds = ids;
        let target = section;
        if (!target) {
            const container = findContainer();
            if (!container) return;
            target = document.createElement('div');
            target.id = SECTION_ID;
            container.insertAdjacentElement('afterbegin', target);
        }
        target.innerHTML = buildCarouselHTML(items);
        initCarousel(target.querySelector('.jt-swiper'));
    }

    async function tryInject() {
        if (!onHomePage()) { cancelContainerRetry(); return; }
        if (injecting) return;
        const now = Date.now();
        if (now - lastFetchAt < 15000) return;                  // throttle re-fetch
        if (lastEmptyAt && now - lastEmptyAt < 60000) return;   // empty cache — don't hammer /Trending
        injecting = true;
        try {
            const items = await fetchItems();
            lastFetchAt = Date.now();
            if (!items.length) { lastEmptyAt = Date.now(); return; }
            await loadLocalization();   // official labels ready before rendering
            renderBanner(items);
        } finally {
            injecting = false;
        }
    }

    // React keeps the home page mounted and only toggles visibility on route
    // changes, so the MutationObserver alone often misses the return to Home.
    // Retrying briefly (plus the periodic safety net below) re-injects reliably.
    function scheduleContainerRetry() {
        if (containerRetryTimer) return;
        containerRetryTimer = setTimeout(() => {
            containerRetryTimer = null;
            if (onHomePage() && !document.getElementById(SECTION_ID)) tryInject();
        }, 250);
    }

    function cancelContainerRetry() {
        if (containerRetryTimer) {
            clearTimeout(containerRetryTimer);
            containerRetryTimer = null;
        }
    }

    function cleanup() {
        document.getElementById(SECTION_ID)?.remove();
        cancelContainerRetry();
    }

    // ── Admin nav quick-access injection ─────────────────────────────────────
    // Injects a "JellyTrend" nav item after the last plugin-related link in the
    // Jellyfin admin drawer so admins can reach plugin settings in one click.

    function injectAdminNav() {
        if (document.getElementById(ADMIN_NAV_ID)) return;

        // Try several selectors across Jellyfin versions (10.9 – 10.11)
        const pluginLinks = Array.from(document.querySelectorAll(
            'a.navMenuOption[href*="plugin"], ' +
            'a.navMenuOption[href*="Plugin"], ' +
            'a[href*="myPlugins"],            ' +
            'a[href*="plugincatalog"]'
        )).filter(el => !el.id.includes('jellytrend'));

        if (!pluginLinks.length) return;

        const ref = pluginLinks[pluginLinks.length - 1];

        // Clone the reference nav item so we inherit Jellyfin's exact classes/attrs
        const link = ref.cloneNode(true);
        link.id   = ADMIN_NAV_ID;
        link.href = CONFIG_URL;

        // Replace icon (material-icons span)
        const icon = link.querySelector('.material-icons, .md-icon, svg');
        if (icon) icon.textContent = 'trending_up';

        // Replace label text
        const text = link.querySelector('.navMenuOptionText, .mainDrawerButton-text, span:last-child');
        if (text) text.textContent = 'JellyTrend';

        ref.parentNode?.insertBefore(link, ref.nextSibling);
    }

    // ── MutationObserver (debounced) ─────────────────────────────────────────
    let debounceTimer = null;

    const observer = new MutationObserver(() => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            if (onHomePage()) tryInject();
            injectAdminNav();   // safe to call on every mutation — idempotent
        }, 300);
    });

    function startObserver() {
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.body) startObserver();
    else document.addEventListener('DOMContentLoaded', startObserver);

    // ── SPA route change ─────────────────────────────────────────────────────
    window.addEventListener('hashchange', () => {
        if (onHomePage()) tryInject();
        else cleanup();
        injectAdminNav();
    });

    // ── Periodic safety net ──────────────────────────────────────────────────
    // Re-checks once per second while on Home. Self-heals cases where the
    // MutationObserver misses a visibility-only re-render (React keeps home
    // mounted) and the retry loop above hasn't run yet.
    setInterval(() => {
        if (onHomePage()) tryInject();
        else cancelContainerRetry();
    }, 1000);

    // ── Fallback: old pluginManager registration ──────────────────────────────
    // Kept for Jellyfin versions that still use HomeSectionType.EditorChoice.
    window['editorsChoice/plugin'] = function () {
        return class JellyTrendPlugin {
            constructor() {
                this.name     = 'JellyTrend – Trending Now';
                this.type     = 'homesection';
                this.id       = 'jellytrend';
                this.priority = 1;
            }
            async render(elem, apiClient) {
                const items = await fetchItems(apiClient);
                if (!items.length) return;
                await loadLocalization();
                elem.innerHTML = buildCarouselHTML(items);
                initCarousel(elem.querySelector('.jt-swiper'));
            }
            destroy() {}
        };
    };

    // ── Swiper config ────────────────────────────────────────────────────────
    // Actualiza el contador compacto "n / total" del pie (visible en móvil).
    function updateCounter(swiperEl, current, total) {
        const count = swiperEl.querySelector('.jt-count');
        if (count) count.textContent = `${current} / ${total}`;
    }

    // En loop, Swiper añade clones con la clase swiper-slide-duplicate; se filtran
    // para contar solo las diapositivas reales. realIndex ya es el índice real.
    function updateSwiperCounter(swiper) {
        const total = Array.from(swiper.slides || [])
            .filter(s => !s.classList.contains('swiper-slide-duplicate')).length;
        updateCounter(swiper.el, swiper.realIndex + 1, total);
    }

    const SWIPER_OPTIONS = {
        loop: true,
        speed: 900,
        effect: 'fade',
        fadeEffect: { crossFade: true },
        autoplay: { delay: 7000, disableOnInteraction: false, pauseOnMouseEnter: true },
        navigation: { nextEl: '.jt-next', prevEl: '.jt-prev' },
        pagination: {
            el: '.jt-pagination',
            clickable: true,
            bulletClass: 'jt-bullet',
            bulletActiveClass: 'jt-bullet-active',
            renderBullet: (_i, cls) => `<button class="${cls}" aria-label="Go to slide"></button>`
        },
        keyboard: { enabled: true },
        touchMoveStopPropagation: true,
        on: {
            init: updateSwiperCounter,
            slideChange: updateSwiperCounter
        }
    };

    // ── HTML builders ─────────────────────────────────────────────────────────
    function buildCarouselHTML(items) {
        return `
<section class="jt-section" aria-label="${t('sectionTitle')}">
  <div class="jt-swiper swiper">
    <div class="swiper-wrapper">${items.map(buildSlide).join('')}</div>
    <button class="jt-prev swiper-button-prev" aria-label="Previous slide"></button>
    <button class="jt-next swiper-button-next" aria-label="Next slide"></button>
    <div class="jt-pagination swiper-pagination" role="tablist" aria-label="Slides"></div>
    <div class="jt-count" aria-live="polite"></div>
  </div>
</section>`;
    }

    function buildSlide(item) {
        const backdrop = item.BackdropImageUrl ? `url('${escUrl(item.BackdropImageUrl)}')` : 'none';
        const isSeries = item.Type === 'Series';
        const badge    = isSeries ? t('badgeTv') : t('badgeMovie');
        const badgeIcon = isSeries ? 'live_tv' : 'movie';
        const overview = item.Overview
            ? `<p class="jt-overview">${escHtml(item.Overview)}</p>` : '';
        // Netflix-style: use the item's logo when available, otherwise fall back to the title.
        const title    = item.LogoImageUrl
            ? `<img class="jt-logo" src="${escUrl(item.LogoImageUrl)}" alt="${escHtml(item.Name ?? '')}" />`
            : `<h2 class="jt-title">${escHtml(item.Name ?? '')}</h2>`;
        // Subtitle line right under the logo: year • rating (Netflix pattern).
        const metaParts = [];
        if (item.ProductionYear) metaParts.push(`<span class="jt-year">${item.ProductionYear}</span>`);
        if (item.CommunityRating) metaParts.push(`<span class="jt-rating">&#9733; ${item.CommunityRating.toFixed(1)}</span>`);
        const meta = metaParts.length
            ? `<div class="jt-meta">${metaParts.join('<span class="jt-meta-sep">·</span>')}</div>`
            : '';

        return `
<div class="swiper-slide jt-slide" style="--jt-backdrop:${backdrop}">
  <div class="jt-slide-bg" aria-hidden="true"></div>
  <div class="jt-overlay" aria-hidden="true"></div>
  <span class="jt-type-badge"><span class="material-icons jt-type-icon" aria-hidden="true">${badgeIcon}</span>${badge}</span>
  <div class="jt-content">
    ${title}
    ${meta}
    ${overview}
    <div class="jt-actions">
      <a class="jt-btn jt-btn-play" href="/web/index.html#!/details?id=${item.Id}">&#9654;&nbsp;${t('play')}</a>
      <a class="jt-btn jt-btn-info" href="/web/index.html#!/details?id=${item.Id}">&#9432;&nbsp;${t('moreInfo')}</a>
    </div>
  </div>
</div>`;
    }

    // ── Pure-JS fallback carousel ─────────────────────────────────────────────
    class FallbackCarousel {
        constructor(swiperEl) {
            this._el     = swiperEl;
            this._slides = [];
            this._bullets = [];
            this._idx    = 0;
            this._timer  = null;
            this._touchX = null;
            this._touchY = null;
            this._init();
        }

        _init() {
            this._slides = Array.from(this._el.querySelectorAll('.jt-slide'));
            if (!this._slides.length) return;

            const pagination = this._el.querySelector('.jt-pagination');
            if (pagination) {
                pagination.innerHTML = this._slides
                    .map((_, i) => `<button class="jt-bullet" aria-label="Slide ${i + 1}"></button>`)
                    .join('');
                this._bullets = Array.from(pagination.querySelectorAll('.jt-bullet'));
                this._bullets.forEach((b, i) =>
                    b.addEventListener('click', () => this._goTo(i, true)));
            }

            this._el.querySelector('.jt-prev')
                ?.addEventListener('click', () => this._step(-1));
            this._el.querySelector('.jt-next')
                ?.addEventListener('click', () => this._step(+1));

            this._el.addEventListener('mouseenter', () => this._stopTimer());
            this._el.addEventListener('mouseleave', () => this._startTimer());
            this._el.addEventListener('keydown', (e) => {
                if (e.key === 'ArrowLeft')  this._step(-1);
                if (e.key === 'ArrowRight') this._step(+1);
            });

            // Touch swipe: el gesto HORIZONTAL lo reclamamos para el carrusel y NO debe
            // llegar a Jellyfin (su swipe nativo derecha→izquierda cambia a Favoritos).
            // stopPropagation impide que el evento burbujee a los handlers del cliente;
            // preventDefault (solo en movimiento horizontal) evita el scroll/panning del
            // navegador. El scroll VERTICAL de la página sigue intacto.
            this._el.addEventListener('touchstart', (e) => {
                e.stopPropagation();
                if (e.touches.length !== 1) return;
                this._touchX = e.touches[0].clientX;
                this._touchY = e.touches[0].clientY;
            }, { passive: true });
            this._el.addEventListener('touchmove', (e) => {
                e.stopPropagation();
                if (this._touchX === null || e.touches.length !== 1) return;
                const dx = e.touches[0].clientX - this._touchX;
                const dy = e.touches[0].clientY - this._touchY;
                if (Math.abs(dx) > 24 && Math.abs(dx) > Math.abs(dy) * 1.2) {
                    e.preventDefault();
                    this._touchX = e.touches[0].clientX;
                    this._touchY = e.touches[0].clientY;
                }
            }, { passive: false });
            this._el.addEventListener('touchend', (e) => {
                e.stopPropagation();
                if (this._touchX === null) return;
                const dx = e.changedTouches[0].clientX - this._touchX;
                const dy = e.changedTouches[0].clientY - this._touchY;
                this._touchX = null;
                this._touchY = null;
                if (Math.abs(dx) > 40 && Math.abs(dx) > Math.abs(dy) * 1.2) {
                    this._step(dx < 0 ? 1 : -1);
                }
            });

            this._goTo(0, false);
            this._startTimer();
        }

        _goTo(idx, resetTimer) {
            this._idx = ((idx % this._slides.length) + this._slides.length) % this._slides.length;
            this._slides.forEach((s, i) => {
                s.classList.toggle('jt-slide-active', i === this._idx);
                s.setAttribute('aria-hidden', String(i !== this._idx));
            });
            this._bullets.forEach((b, i) => {
                b.classList.toggle('jt-bullet-active', i === this._idx);
                b.setAttribute('aria-selected', String(i === this._idx));
            });
            updateCounter(this._el, this._idx + 1, this._slides.length);
            if (resetTimer) this._resetTimer();
        }

        _step(delta) { this._goTo(this._idx + delta, true); }

        _startTimer() { this._timer = setInterval(() => this._step(1), 7000); }
        _stopTimer()  { clearInterval(this._timer); this._timer = null; }
        _resetTimer() { this._stopTimer(); this._startTimer(); }

        destroy() { this._stopTimer(); }
    }

    // ── i18n ─────────────────────────────────────────────────────────────────
    // Inline translations act as an offline fallback. Standard UI strings (Play,
    // More Info, Movie, Series…) are replaced at runtime by Jellyfin's official
    // translations served from /Localization/{culture} when available.
    const TRANSLATIONS = {
        en: {
            sectionTitle: 'Trending Now',
            play:         'Play',
            moreInfo:     'Details',
            badgeMovie:   'Movie',
            badgeTv:      'TV',
        },
        es: {
            sectionTitle: 'En Tendencia',
            play:         'Reproducir',
            moreInfo:     'Ver detalles',
            badgeMovie:   'Película',
            badgeTv:      'Serie',
        },
        fr: {
            sectionTitle: 'Tendances',
            play:         'Lire',
            moreInfo:     'Voir les détails',
            badgeMovie:   'Film',
            badgeTv:      'Série',
        },
        pt: {
            sectionTitle: 'Em Alta',
            play:         'Assistir',
            moreInfo:     'Ver detalhes',
            badgeMovie:   'Filme',
            badgeTv:      'Série',
        },
        de: {
            sectionTitle: 'Trends',
            play:         'Abspielen',
            moreInfo:     'Details anzeigen',
            badgeMovie:   'Film',
            badgeTv:      'Serie',
        },
    };

    let _lang = (navigator.language || navigator.userLanguage || 'en').toLowerCase().split(/[-_]/)[0];
    let _t    = TRANSLATIONS[_lang] ?? TRANSLATIONS.en;

    // Switches the inline fallback to the given BCP-47 culture (only packs we ship).
    function setLang(culture) {
        const code = String(culture || '').toLowerCase().split(/[-_]/)[0];
        if (TRANSLATIONS[code]) {
            _lang = code;
            _t    = TRANSLATIONS[code];
        }
    }

    // Banner key → official Jellyfin localization key.
    const JF_KEY = {
        play:       'Play',
        moreInfo:   'MoreInfo',
        badgeMovie: 'Movie',
        badgeTv:    'Series',
    };

    function t(key) {
        const official = officialLabels && JF_KEY[key] ? officialLabels[JF_KEY[key]] : null;
        return official || (_t[key] ?? TRANSLATIONS.en[key] ?? key);
    }

    // ── Utilities ────────────────────────────────────────────────────────────
    function escHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#x27;');
    }

    function escUrl(s) {
        return String(s ?? '').replace(/'/g, '%27');
    }
})();
