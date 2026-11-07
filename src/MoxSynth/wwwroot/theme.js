// Colour scheme handling. Loaded synchronously in <head> so the theme is applied before first paint (no flash).
// Themes: system | light | dark | neon. Neon is built on top of Bootstrap's dark mode.
(function () {
    const KEY = 'moxsynth-theme';
    const THEMES = ['system', 'light', 'dark', 'neon'];
    const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

    function stored() {
        try {
            const t = localStorage.getItem(KEY);
            return THEMES.includes(t) ? t : 'system';
        } catch {
            return 'system';
        }
    }

    function apply(theme) {
        const root = document.documentElement;
        const base = theme === 'system' ? (darkQuery.matches ? 'dark' : 'light')
            : theme === 'light' ? 'light'
            : 'dark';
        root.setAttribute('data-bs-theme', base);
        root.classList.toggle('theme-neon', theme === 'neon');
        root.dataset.theme = theme;
    }

    window.moxTheme = {
        get: stored,
        set: function (theme) {
            if (!THEMES.includes(theme)) theme = 'system';
            try { localStorage.setItem(KEY, theme); } catch { /* storage unavailable: still apply for this page */ }
            apply(theme);
        },
    };

    apply(stored());
    darkQuery.addEventListener('change', function () {
        if (stored() === 'system') apply('system');
    });
})();
