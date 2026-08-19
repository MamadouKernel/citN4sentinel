// Bascule « afficher / masquer » des champs de mot de passe.
//
// Ce code vivait dans un attribut onclick="..." en ligne. La politique de
// securite de contenu declare script-src 'self' sans 'unsafe-inline' : les
// gestionnaires en ligne sont donc bloques par le navigateur, et le bouton ne
// faisait plus rien. Le sortir dans un fichier servi par l'application le
// remet en marche SANS affaiblir la politique.
//
// Ecoute deleguee sur document : le formulaire peut etre rendu en SSR statique
// comme par un circuit Blazor, et Blazor remplace des morceaux de DOM a la
// volee. Un ecouteur pose sur chaque bouton au chargement disparaitrait au
// premier re-rendu ; la delegation survit a tout.
(function () {
    'use strict';

    document.addEventListener('click', function (evenement) {
        var bouton = evenement.target.closest('[data-bascule-mot-de-passe]');
        if (!bouton) return;

        var conteneur = bouton.closest('[data-champ-mot-de-passe]');
        if (!conteneur) return;

        var champ = conteneur.querySelector('input');
        if (!champ) return;

        var masque = champ.type === 'password';
        champ.type = masque ? 'text' : 'password';

        var icone = bouton.querySelector('i');
        if (icone) icone.className = 'bi ' + (masque ? 'bi-eye-slash' : 'bi-eye');

        bouton.setAttribute('aria-label',
            masque ? 'Masquer le mot de passe' : 'Afficher le mot de passe');
    });
})();
