/**
 * Recopie la police d'icônes Bootstrap Icons depuis node_modules vers wwwroot.
 *
 * Les icônes venaient d'un CDN public. Sur un réseau d'exploitation isolé,
 * l'appel échoue et l'interface perd toutes ses icônes — sans message, sans
 * trace, et sans que personne comprenne pourquoi les boutons sont vides.
 *
 * Ce qui est recopié est versionné dans le dépôt : l'application n'a donc
 * besoin ni de Node ni du réseau pour s'afficher correctement.
 */

const fs = require('fs');
const path = require('path');

const racine = path.resolve(__dirname, '..');
const source = path.join(racine, 'node_modules', 'bootstrap-icons', 'font');
const destination = path.join(racine, 'wwwroot', 'lib', 'bootstrap-icons');

if (!fs.existsSync(source)) {
  console.error('bootstrap-icons introuvable dans node_modules. Lancez d\'abord : npm install');
  process.exit(1);
}

fs.mkdirSync(path.join(destination, 'fonts'), { recursive: true });

// La feuille de styles référence les polices en ../fonts/ : la structure de
// dossiers doit donc être conservée telle quelle.
fs.copyFileSync(
  path.join(source, 'bootstrap-icons.min.css'),
  path.join(destination, 'bootstrap-icons.min.css'));

let copiees = 0;
for (const fichier of fs.readdirSync(path.join(source, 'fonts'))) {
  fs.copyFileSync(
    path.join(source, 'fonts', fichier),
    path.join(destination, 'fonts', fichier));
  copiees++;
}

console.log(`Icônes copiées : feuille de styles + ${copiees} fichier(s) de police -> wwwroot/lib/bootstrap-icons`);
