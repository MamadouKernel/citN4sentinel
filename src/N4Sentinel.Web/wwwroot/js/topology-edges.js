// FR-050 — tracé des dépendances déclarées sur la carte de topologie.
//
// La carte est une grille responsive, pas un canevas à coordonnées : les
// positions ne sont connues qu'une fois la page rendue, et elles changent à
// chaque redimensionnement ou changement de point de rupture. Le tracé est
// donc MESURÉ dans le navigateur puis recalculé à chaque changement.
//
// C'est aussi pourquoi il ne remplace pas le bouton « dépend de N » posé sur
// chaque nœud : si une mesure échoue ou si un nœud n'est pas rendu, l'arête
// disparaît en silence, alors que le bouton, lui, reste. Les deux disent la
// même chose par deux moyens différents.
//
// Écoute déléguée et observateurs sur document : Blazor remplace des morceaux
// de DOM à la volée, un écouteur posé au chargement ne survivrait pas.
(function () {
    'use strict';

    var NS = 'http://www.w3.org/2000/svg';
    var imagePlanifiee = 0;
    var minuterie = 0;

    function centre(element, origine) {
        var r = element.getBoundingClientRect();
        return {
            x: r.left - origine.left + r.width / 2,
            y: r.top - origine.top + r.height / 2
        };
    }

    function tracerUneCarte(conteneur) {
        var calque = conteneur.querySelector('[data-calque-dependances]');
        if (!calque) return;

        // Repartir d'un calque vide : un tracé périmé serait pire qu'aucun.
        while (calque.firstChild) calque.removeChild(calque.firstChild);

        var couples;
        try {
            couples = JSON.parse(conteneur.getAttribute('data-dependances') || '[]');
        } catch (e) {
            return;
        }
        if (!couples.length) return;

        var origine = conteneur.getBoundingClientRect();
        if (origine.width === 0 || origine.height === 0) return;

        calque.setAttribute('viewBox', '0 0 ' + origine.width + ' ' + origine.height);

        // Une pointe de flèche, définie une fois : le sens compte. « A dépend
        // de B » et « B dépend de A » n'ont pas du tout les mêmes conséquences
        // au démarrage comme à l'arrêt.
        var defs = document.createElementNS(NS, 'defs');
        var marker = document.createElementNS(NS, 'marker');
        marker.setAttribute('id', 'n4-fleche-dependance');
        marker.setAttribute('viewBox', '0 0 10 10');
        marker.setAttribute('refX', '9');
        marker.setAttribute('refY', '5');
        marker.setAttribute('markerWidth', '5');
        marker.setAttribute('markerHeight', '5');
        marker.setAttribute('orient', 'auto-start-reverse');
        var pointe = document.createElementNS(NS, 'path');
        pointe.setAttribute('d', 'M 0 0 L 10 5 L 0 10 z');
        pointe.setAttribute('fill', 'rgb(251 191 36)');
        marker.appendChild(pointe);
        defs.appendChild(marker);
        calque.appendChild(defs);

        couples.forEach(function (couple) {
            var depuis = conteneur.querySelector('[data-composant-id="' + couple.de + '"]');
            var vers = conteneur.querySelector('[data-composant-id="' + couple.vers + '"]');
            if (!depuis || !vers) return;

            var a = centre(depuis, origine);
            var b = centre(vers, origine);

            // Courbe et non segment droit : sur une grille, des traits droits
            // se superposent et deviennent illisibles dès qu'il y a plusieurs
            // dépendances entre deux mêmes colonnes.
            var mx = (a.x + b.x) / 2;
            var my = (a.y + b.y) / 2 - Math.min(60, Math.abs(b.x - a.x) / 4);

            var trait = document.createElementNS(NS, 'path');
            trait.setAttribute('d', 'M ' + a.x + ' ' + a.y + ' Q ' + mx + ' ' + my + ' ' + b.x + ' ' + b.y);
            trait.setAttribute('fill', 'none');
            trait.setAttribute('stroke', 'rgb(251 191 36)');
            trait.setAttribute('stroke-width', '2');
            trait.setAttribute('stroke-opacity', '0.45');
            trait.setAttribute('stroke-dasharray', '6 4');
            trait.setAttribute('marker-end', 'url(#n4-fleche-dependance)');
            calque.appendChild(trait);
        });
    }

    function tracer() {
        if (imagePlanifiee) { cancelAnimationFrame(imagePlanifiee); imagePlanifiee = 0; }
        if (minuterie) { clearTimeout(minuterie); minuterie = 0; }

        var cartes = document.querySelectorAll('[data-carte-topologie]');
        for (var i = 0; i < cartes.length; i++) tracerUneCarte(cartes[i]);
    }

    // Un seul tracé par image : redimensionner déclenche des dizaines
    // d'événements, et mesurer coûte un reflow à chaque fois.
    //
    // On ANNULE puis REPLANIFIE plutôt que d'ignorer les demandes tant qu'une
    // est en attente. La différence compte : dans un onglet d'arrière-plan,
    // requestAnimationFrame ne se déclenche pas du tout. Un simple drapeau
    // « déjà planifié » resterait alors levé pour toujours, et plus aucune
    // demande ne passerait — même après retour au premier plan. Constaté à la
    // vérification : la carte restait vide et le redimensionnement n'y
    // changeait rien.
    // Deux déclencheurs, le premier qui arrive gagne et annule l'autre :
    //
    //   — requestAnimationFrame, chemin normal, cale le tracé sur le rendu ;
    //   — un délai de 250 ms, FILET DE SÉCURITÉ, parce que rAF ne se déclenche
    //     pas du tout dans un onglet d'arrière-plan. Sans ce filet, la carte
    //     reste vide tant que l'onglet n'a pas été regardé, et le tracé n'est
    //     jamais rattrapé. Constaté à la vérification.
    function planifier() {
        if (imagePlanifiee) cancelAnimationFrame(imagePlanifiee);
        if (minuterie) clearTimeout(minuterie);

        imagePlanifiee = requestAnimationFrame(tracer);
        minuterie = setTimeout(tracer, 250);
    }

    window.addEventListener('resize', planifier);
    window.addEventListener('load', planifier);
    document.addEventListener('DOMContentLoaded', planifier);

    // Retour au premier plan : les mesures faites en arrière-plan sont
    // souvent nulles, et le tracé a pu ne jamais s'exécuter.
    document.addEventListener('visibilitychange', function () {
        if (!document.hidden) planifier();
    });

    // Blazor remplace le contenu sans recharger la page : il faut redessiner
    // quand les nœuds changent, apparaissent ou disparaissent.
    if (window.MutationObserver) {
        new MutationObserver(planifier).observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['data-dependances']
        });
    }

    planifier();
})();
