// Theme: "system" (default, follows Windows), "light" or "dark". Runs in <head> before first paint, so there is no flash.
(function () {
    var key = 'tm-theme';
    var query = window.matchMedia('(prefers-color-scheme: dark)');

    function stored() {
        try { return localStorage.getItem(key) || 'system'; } catch (e) { return 'system'; }
    }

    function apply(mode) {
        var resolved = mode === 'system' ? (query.matches ? 'dark' : 'light') : mode;
        var root = document.documentElement;
        root.setAttribute('data-theme', resolved);
        root.setAttribute('data-bs-theme', resolved);   // Bootstrap form controls and buttons follow too
    }

    apply(stored());
    query.addEventListener('change', function () { if (stored() === 'system') apply('system'); });

    window.tmTheme = {
        get: stored,
        set: function (mode) {
            try {
                if (mode === 'system') localStorage.removeItem(key);
                else localStorage.setItem(key, mode);
            } catch (e) { /* private mode: the choice just will not persist */ }
            apply(mode);
        }
    };
})();
