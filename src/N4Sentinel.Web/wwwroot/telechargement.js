// Téléchargement d'un fichier produit côté serveur.
//
// Le contenu transite en base64 : un rapport d'exécution contient des accents,
// des guillemets typographiques et des retours à la ligne, et le passer en
// clair à travers l'interop JavaScript expose à des corruptions d'encodage qui
// ne se voient qu'à l'ouverture du fichier — c'est-à-dire chez le destinataire.
window.n4TelechargerFichier = function (nomFichier, contenuBase64, typeMime) {
    const binaire = atob(contenuBase64);
    const octets = new Uint8Array(binaire.length);
    for (let i = 0; i < binaire.length; i++) {
        octets[i] = binaire.charCodeAt(i);
    }

    const blob = new Blob([octets], { type: typeMime });
    const url = URL.createObjectURL(blob);

    const lien = document.createElement('a');
    lien.href = url;
    lien.download = nomFichier;
    document.body.appendChild(lien);
    lien.click();
    document.body.removeChild(lien);

    // Sans cette liberation, chaque telechargement laisse son contenu en
    // memoire jusqu'au rechargement de la page.
    URL.revokeObjectURL(url);
};

window.n4TelechargerTexte = function (nomFichier, contenuBase64) {
    window.n4TelechargerFichier(nomFichier, contenuBase64, 'text/markdown;charset=utf-8');
};
