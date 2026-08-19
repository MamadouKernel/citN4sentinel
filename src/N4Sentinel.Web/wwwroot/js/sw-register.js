// Enregistrement du service worker.
//
// Meme raison que password-toggle.js : ce code etait dans un bloc <script>
// en ligne, bloque par script-src 'self'. Le service worker n'etait donc plus
// enregistre du tout depuis l'ajout de la politique de securite de contenu.
(function () {
    'use strict';

    if (!('serviceWorker' in navigator)) return;

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/sw.js').catch(function (erreur) {
            // Un service worker qui ne s'enregistre pas ne doit pas casser
            // l'application : il n'apporte que le fonctionnement hors ligne.
            console.warn('Service worker non enregistré :', erreur);
        });
    });
})();
