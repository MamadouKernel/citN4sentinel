// Dictée vocale dans un champ de TEXTE LIBRE.
//
// POURQUOI SEULEMENT DU TEXTE LIBRE. La reconnaissance vocale se trompe, et
// elle se trompe SILENCIEUSEMENT : elle rend toujours quelque chose de
// plausible. Sur un motif d'opération, une erreur se voit et se corrige. Sur
// un nom d'hôte, un nom de service Windows, un chemin de journal ou une
// expression régulière de marqueur, elle produirait une valeur crédible et
// fausse — et un chemin de journal erroné fait lire la preuve de démarrage
// dans le mauvais fichier. Le bouton n'est donc posé que sur des champs de
// prose, jamais sur un identifiant.
//
// Écoute déléguée : le formulaire peut être rendu en SSR statique comme par un
// circuit Blazor, et Blazor remplace des morceaux de DOM à la volée.
(function () {
    'use strict';

    var enCours = null;
    var boutonActif = null;

    function support() {
        return 'webkitSpeechRecognition' in window || 'SpeechRecognition' in window;
    }

    function marquer(bouton, actif) {
        if (!bouton) return;
        bouton.setAttribute('aria-pressed', actif ? 'true' : 'false');
        var icone = bouton.querySelector('i');
        if (icone) icone.className = 'bi ' + (actif ? 'bi-mic-fill' : 'bi-mic');
        bouton.setAttribute('aria-label', actif ? 'Arrêter la dictée' : 'Dicter le texte');
    }

    function arreter() {
        if (enCours) {
            try { enCours.onend = null; enCours.onerror = null; enCours.abort(); } catch (e) { /* déjà arrêtée */ }
            enCours = null;
        }
        marquer(boutonActif, false);
        boutonActif = null;
    }

    function ecrire(champ, texte) {
        if (!champ || !texte) return;

        var separateur = champ.value && !/\s$/.test(champ.value) ? ' ' : '';
        champ.value = champ.value + separateur + texte;

        // INDISPENSABLE : Blazor lie la valeur sur l'évènement input. Écrire
        // dans le DOM sans émettre l'évènement remplirait le champ à l'écran
        // sans jamais renseigner le modèle — l'opérateur croirait avoir saisi
        // un motif, et l'enregistrement partirait vide.
        champ.dispatchEvent(new Event('input', { bubbles: true }));
        champ.dispatchEvent(new Event('change', { bubbles: true }));
    }

    document.addEventListener('click', function (evenement) {
        var bouton = evenement.target.closest('[data-dictee]');
        if (!bouton) return;

        evenement.preventDefault();

        var champ = document.getElementById(bouton.getAttribute('data-dictee'));
        if (!champ) return;

        if (!support()) {
            // Dire que ce n'est pas disponible plutôt que de ne rien faire :
            // un bouton qui ne réagit pas passe pour une panne.
            champ.setAttribute('placeholder', 'Dictée non disponible sur ce navigateur. Saisissez le texte.');
            return;
        }

        // Deuxième clic : on arrête.
        if (enCours && boutonActif === bouton) { arreter(); return; }
        arreter();

        var Reconnaissance = window.SpeechRecognition || window.webkitSpeechRecognition;
        var r = new Reconnaissance();
        r.lang = 'fr-FR';
        r.interimResults = false;
        r.maxAlternatives = 1;
        r.continuous = true;

        r.onresult = function (e) {
            for (var i = e.resultIndex; i < e.results.length; i++) {
                if (e.results[i].isFinal) ecrire(champ, e.results[i][0].transcript.trim());
            }
        };

        r.onerror = function () { arreter(); };
        r.onend = function () { arreter(); };

        enCours = r;
        boutonActif = bouton;
        marquer(bouton, true);

        try { r.start(); } catch (e) { arreter(); }
    });

    // Quitter la page en dictée laisserait le micro ouvert.
    window.addEventListener('pagehide', arreter);
})();
