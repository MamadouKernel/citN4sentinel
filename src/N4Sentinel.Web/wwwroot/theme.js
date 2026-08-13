// N4 Sentinel — Dynamic Theme Palette Switcher
(function () {
    const savedTheme = localStorage.getItem('n4_sentinel_theme') || 'ocean';
    document.documentElement.setAttribute('data-theme', savedTheme);
})();

window.setSentinelTheme = function (themeName) {
    if (!themeName) return;
    document.documentElement.setAttribute('data-theme', themeName);
    localStorage.setItem('n4_sentinel_theme', themeName);
};

window.getSentinelTheme = function () {
    return localStorage.getItem('n4_sentinel_theme') || 'ocean';
};
