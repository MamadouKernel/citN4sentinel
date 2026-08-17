// Deconnexion automatique apres inactivite (souris, clavier, defilement,
// tactile) : sur une session Blazor Server, le circuit reste ouvert sans
// nouvelle requete HTTP tant que rien n'est clique, donc le cookie
// d'authentification ne "voit" jamais l'inactivite tout seul. Ce minuteur
// cote client comble cet angle mort en soumettant le formulaire de
// deconnexion (avec jeton anti-CSRF) des que le delai est atteint.
window.N4SentinelIdle = (function () {
    let timer = null;
    let lastReset = 0;
    let minutes = 30;
    let formEl = null;
    let started = false;

    function logout() {
        if (!formEl) return;
        if (formEl.requestSubmit) formEl.requestSubmit();
        else formEl.submit();
    }

    function reset() {
        const now = Date.now();
        if (now - lastReset < 1000) return; // evite de reprogrammer a chaque pixel de mousemove
        lastReset = now;
        if (timer) clearTimeout(timer);
        timer = setTimeout(logout, minutes * 60 * 1000);
    }

    function start(minutesParam, formSelector) {
        if (started) return;
        formEl = document.querySelector(formSelector);
        if (!formEl) return;

        started = true;
        minutes = minutesParam;

        ['mousemove', 'mousedown', 'keydown', 'wheel', 'scroll', 'touchstart'].forEach(evt =>
            document.addEventListener(evt, reset, { passive: true }));

        reset();
    }

    return { start };
})();
