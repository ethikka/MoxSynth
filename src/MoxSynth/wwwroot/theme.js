// Colour scheme handling. Loaded synchronously in <head> so the theme is applied before first paint (no flash).
//
// Every theme sits on one of Bootstrap's two colour modes (data-bs-theme = light | dark) and may add a class:
//   theme-neon                 – neon (own CSS block)
//   mx-theme theme-{name}      – palette themes (seasons, halloween, steampunk, cyberpunk): the shared .mx-theme
//                                rules in themes.css apply the --mx-* palette that each theme-{name} class defines.
// "system" follows the OS light/dark setting; "seasonal" picks the season theme from the current month.
(function () {
    const KEY = 'moxsynth-theme';
    const THEMES = {
        system: null, light: 'light', dark: 'dark',
        neon: 'dark',
        seasonal: null,
        spring: 'light', summer: 'light', autumn: 'light', winter: 'light',
        halloween: 'dark', steampunk: 'dark', cyberpunk: 'dark',
    };
    const PALETTE_THEMES = ['spring', 'summer', 'autumn', 'winter', 'halloween', 'steampunk', 'cyberpunk'];
    const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

    function stored() {
        try {
            const t = localStorage.getItem(KEY);
            return t in THEMES ? t : 'system';
        } catch {
            return 'system';
        }
    }

    // Meteorological seasons (northern hemisphere): Mar–May spring, Jun–Aug summer, Sep–Nov autumn, Dec–Feb winter.
    function currentSeason() {
        const m = new Date().getMonth(); // 0 = January
        return m >= 2 && m <= 4 ? 'spring' : m >= 5 && m <= 7 ? 'summer' : m >= 8 && m <= 10 ? 'autumn' : 'winter';
    }

    function apply(theme) {
        const root = document.documentElement;
        const effective = theme === 'seasonal' ? currentSeason() : theme;
        const base = effective === 'system' ? (darkQuery.matches ? 'dark' : 'light') : THEMES[effective];

        root.setAttribute('data-bs-theme', base);
        [...root.classList].filter(c => c.startsWith('theme-') || c === 'mx-theme').forEach(c => root.classList.remove(c));
        if (effective === 'neon') root.classList.add('theme-neon');
        if (PALETTE_THEMES.includes(effective)) root.classList.add('mx-theme', 'theme-' + effective);
        root.dataset.theme = theme;
    }

    window.moxTheme = {
        get: stored,
        set: function (theme) {
            if (!(theme in THEMES)) theme = 'system';
            try { localStorage.setItem(KEY, theme); } catch { /* storage unavailable: still apply for this page */ }
            apply(theme);
        },
    };

    // Small per-browser preferences (e.g. the price source); storage may be unavailable (private mode).
    window.moxPrefs = {
        get: function (key) { try { return localStorage.getItem(key); } catch { return null; } },
        set: function (key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } },
    };

    apply(stored());
    darkQuery.addEventListener('change', function () {
        if (stored() === 'system') apply('system');
    });
})();
