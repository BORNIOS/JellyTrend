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
    (function injectCSS() {
        if (document.getElementById('jellytrend-css')) return;
        const link = document.createElement('link');
        link.id   = 'jellytrend-css';
        link.rel  = 'stylesheet';
        link.href = '/JellyTrend/jellyTrend.css';
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

    async function tryInject() {
        if (document.getElementById(SECTION_ID)) return;
        if (!onHomePage()) return;
        const container = findContainer();
        if (!container) return;
        if (injecting) return;
        injecting = true;
        try {
            const items = await fetchItems();
            if (!items.length) return;
            if (document.getElementById(SECTION_ID)) return; // re-check after await
            const wrapper = document.createElement('div');
            wrapper.id = SECTION_ID;
            wrapper.innerHTML = buildCarouselHTML(items);
            container.insertAdjacentElement('afterbegin', wrapper);
            initCarousel(wrapper.querySelector('.jt-swiper'));
        } finally {
            injecting = false;
        }
    }

    function cleanup() {
        document.getElementById(SECTION_ID)?.remove();
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
                elem.innerHTML = buildCarouselHTML(items);
                initCarousel(elem.querySelector('.jt-swiper'));
            }
            destroy() {}
        };
    };

    // ── Swiper config ────────────────────────────────────────────────────────
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
        keyboard: { enabled: true }
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
  </div>
</section>`;
    }

    function buildSlide(item) {
        const backdrop = item.BackdropImageUrl ? `url('${escUrl(item.BackdropImageUrl)}')` : 'none';
        const badge    = item.Type === 'Series' ? t('badgeTv') : t('badgeMovie');
        const rating   = item.CommunityRating
            ? `<span class="jt-rating">&#9733; ${item.CommunityRating.toFixed(1)}</span>` : '';
        const year     = item.ProductionYear
            ? `<span class="jt-year">${item.ProductionYear}</span>` : '';
        const overview = item.Overview
            ? `<p class="jt-overview">${escHtml(item.Overview)}</p>` : '';

        return `
<div class="swiper-slide jt-slide" style="--jt-backdrop:${backdrop}">
  <div class="jt-slide-bg" aria-hidden="true"></div>
  <div class="jt-overlay" aria-hidden="true"></div>
  <div class="jt-content">
    <h2 class="jt-title">${escHtml(item.Name ?? '')}</h2>
    ${overview}
    <div class="jt-meta">${year}${rating}<span class="jt-badge">${badge}</span></div>
    <div class="jt-actions">
      <a class="jt-btn jt-btn-play" href="/web/index.html#!/details?id=${item.Id}">${t('play')}</a>
      <a class="jt-btn jt-btn-info" href="/web/index.html#!/details?id=${item.Id}">${t('moreInfo')}</a>
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
            if (resetTimer) this._resetTimer();
        }

        _step(delta) { this._goTo(this._idx + delta, true); }

        _startTimer() { this._timer = setInterval(() => this._step(1), 7000); }
        _stopTimer()  { clearInterval(this._timer); this._timer = null; }
        _resetTimer() { this._stopTimer(); this._startTimer(); }

        destroy() { this._stopTimer(); }
    }

    // ── i18n ─────────────────────────────────────────────────────────────────
    const TRANSLATIONS = {
        en: {
            sectionTitle: 'Trending Now',
            play:         '▶ Play',
            moreInfo:     'ℹ More Info',
            badgeMovie:   'Movie',
            badgeTv:      'TV',
        },
        es: {
            sectionTitle: 'En Tendencia',
            play:         '▶ Reproducir',
            moreInfo:     'ℹ Más Info',
            badgeMovie:   'Película',
            badgeTv:      'Serie',
        },
        fr: {
            sectionTitle: 'Tendances',
            play:         '▶ Lire',
            moreInfo:     'ℹ Plus d\'infos',
            badgeMovie:   'Film',
            badgeTv:      'Série',
        },
        pt: {
            sectionTitle: 'Em Alta',
            play:         '▶ Assistir',
            moreInfo:     'ℹ Mais Info',
            badgeMovie:   'Filme',
            badgeTv:      'Série',
        },
        de: {
            sectionTitle: 'Trends',
            play:         '▶ Abspielen',
            moreInfo:     'ℹ Mehr Infos',
            badgeMovie:   'Film',
            badgeTv:      'Serie',
        },
    };

    const _lang = (navigator.language || navigator.userLanguage || 'en').toLowerCase().split(/[-_]/)[0];
    const _t    = TRANSLATIONS[_lang] ?? TRANSLATIONS.en;

    function t(key) { return _t[key] ?? TRANSLATIONS.en[key] ?? key; }

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
