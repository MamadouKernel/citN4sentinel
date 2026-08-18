/**
 * Sentinel Voice Copilot - Web Speech API Integration
 * Reconnaissance vocale (Speech-to-Text) & Synthèse vocale (Text-to-Speech)
 */
window.n4VoiceCopilot = {
    recognition: null,
    watchdogTimer: null,
    shortcutHandler: null,

    isSupported: function () {
        return 'webkitSpeechRecognition' in window || 'SpeechRecognition' in window;
    },

    // Raccourci global Alt+Maj+M : permet à un utilisateur non-voyant de
    // déclencher l'écoute depuis n'importe où sur la page, sans avoir à
    // localiser le bouton flottant au clavier ou à la souris. Remplace tout
    // gestionnaire précédent pour éviter d'appeler une référence .NET disposée
    // si le composant qui l'héberge est recréé.
    registerGlobalShortcut: function (dotNetHelper) {
        if (this.shortcutHandler) {
            window.removeEventListener('keydown', this.shortcutHandler);
        }
        this.shortcutHandler = function (e) {
            if (e.altKey && e.shiftKey && (e.key === 'M' || e.key === 'm')) {
                e.preventDefault();
                dotNetHelper.invokeMethodAsync('ToggleListenFromShortcut');
            }
        };
        window.addEventListener('keydown', this.shortcutHandler);
    },

    errorMessages: {
        'no-speech': 'Aucune parole détectée. Réessayez.',
        'not-allowed': 'Accès au microphone refusé. Autorisez-le dans les paramètres du navigateur.',
        'permission-denied': 'Accès au microphone refusé. Autorisez-le dans les paramètres du navigateur.',
        'audio-capture': 'Aucun microphone détecté.',
        'network': 'Problème réseau pendant la reconnaissance vocale.',
        'aborted': 'Écoute interrompue.',
        'service-not-allowed': 'Service de reconnaissance vocale indisponible.'
    },

    friendlyError: function (code) {
        return this.errorMessages[code] || ('Erreur de saisie vocale : ' + code);
    },

    startListening: function (dotNetHelper) {
        if (!this.isSupported()) {
            dotNetHelper.invokeMethodAsync('OnVoiceError', 'La reconnaissance vocale n\'est pas supportée par ce navigateur. Utilisez Chrome ou Edge.');
            return;
        }

        // Une instance précédente peut encore être en train de se terminer : on l'arrête proprement avant d'en créer une nouvelle.
        if (this.recognition) {
            try { this.recognition.onend = null; this.recognition.onerror = null; this.recognition.abort(); } catch (e) { /* déjà arrêtée */ }
        }

        var SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        this.recognition = new SpeechRecognition();
        this.recognition.lang = 'fr-FR';
        this.recognition.interimResults = false;
        this.recognition.maxAlternatives = 1;

        var self = this;

        this.recognition.onresult = function (event) {
            self.clearWatchdog();
            if (event.results && event.results.length > 0) {
                var transcript = event.results[0][0].transcript;
                dotNetHelper.invokeMethodAsync('OnVoiceTranscriptReceived', transcript);
            }
        };

        this.recognition.onerror = function (event) {
            self.clearWatchdog();
            dotNetHelper.invokeMethodAsync('OnVoiceError', self.friendlyError(event.error));
        };

        this.recognition.onend = function () {
            self.clearWatchdog();
            dotNetHelper.invokeMethodAsync('OnVoiceListeningEnded');
        };

        // Filet de sécurité : si le service de reconnaissance (distant, via le
        // navigateur) ne répond ni par un résultat ni par une erreur — service
        // injoignable, micro bloqué silencieusement par le navigateur —, le
        // bouton restait bloqué en écoute indéfiniment sans aucun retour.
        this.watchdogTimer = setTimeout(function () {
            console.warn('[Voice Copilot] Aucune réponse du service de reconnaissance vocale après 10s.');
            try { self.recognition && self.recognition.abort(); } catch (e) { /* déjà arrêtée */ }
            dotNetHelper.invokeMethodAsync('OnVoiceError', 'Aucune réponse du service de reconnaissance vocale. Vérifiez la connexion réseau et l\'autorisation du microphone.');
        }, 10000);

        try {
            this.recognition.start();
        } catch (e) {
            console.warn('[Voice Copilot] Échec du démarrage de la reconnaissance vocale.', e);
            this.clearWatchdog();
            dotNetHelper.invokeMethodAsync('OnVoiceError', 'Impossible de démarrer le micro. Réessayez dans un instant.');
        }
    },

    clearWatchdog: function () {
        if (this.watchdogTimer) {
            clearTimeout(this.watchdogTimer);
            this.watchdogTimer = null;
        }
    },

    stopListening: function () {
        this.clearWatchdog();
        if (this.recognition) {
            this.recognition.stop();
        }
    },

    speak: function (textToSpeak) {
        if ('speechSynthesis' in window) {
            window.speechSynthesis.cancel(); // Annule toute synthèse en cours
            var utterance = new SpeechSynthesisUtterance(textToSpeak);
            utterance.lang = 'fr-FR';
            utterance.rate = 1.0;
            utterance.pitch = 1.0;
            window.speechSynthesis.speak(utterance);
        }
    }
};
