/**
 * Configuration Tailwind de N4 Sentinel.
 *
 * Reprend à l'identique la configuration qui était déclarée en ligne dans
 * App.razor du temps du CDN, afin que le rendu ne change pas.
 *
 * Le CDN Tailwind compilait le CSS dans le navigateur à chaque chargement de
 * page. C'est un outil de développement, pas un mode de livraison : il impose
 * un accès Internet au moment où l'utilisateur ouvre l'application, ce qu'un
 * réseau d'exploitation isolé ne fournit pas.
 */
module.exports = {
  darkMode: 'class',

  // Tailwind ne conserve que les classes réellement rencontrées dans ces
  // fichiers. Toute classe construite par concaténation en C# lui échapperait :
  // les valeurs de retour doivent rester des chaînes complètes.
  content: [
    './**/*.{razor,html,cshtml}',
    '!./bin/**',
    '!./obj/**',
    '!./node_modules/**'
  ],

  theme: {
    extend: {
      colors: {
        n4navy: {
          950: '#070e17',
          900: '#0b192c',
          800: '#0f172a',
          700: '#1e293b',
          600: '#334155'
        },
        n4blue: {
          50: '#f0f9ff',
          100: '#e0f2fe',
          400: '#38bdf8',
          500: '#0ea5e9',
          600: '#0284c7',
          700: '#0369a1'
        },
        n4emerald: {
          50: '#ecfdf5',
          100: '#d1fae5',
          500: '#10b981',
          600: '#059669',
          700: '#047857'
        },
        n4amber: {
          50: '#fffbeb',
          100: '#fef3c7',
          500: '#f59e0b',
          600: '#d97706'
        },
        n4rose: {
          50: '#fff1f2',
          100: '#ffe4e6',
          500: '#f43f5e',
          600: '#e11d48'
        }
      },

      // Polices systèmes : aucune requête vers un fournisseur externe, et un
      // rendu identique sur un poste hors ligne.
      fontFamily: {
        sans: ['"Segoe UI Variable Text"', '"Segoe UI"', 'ui-sans-serif',
               'system-ui', '-apple-system', '"Helvetica Neue"', 'Arial', 'sans-serif'],
        mono: ['ui-monospace', '"Cascadia Mono"', 'Consolas',
               '"SFMono-Regular"', 'Menlo', 'monospace']
      }
    }
  },

  plugins: []
};
