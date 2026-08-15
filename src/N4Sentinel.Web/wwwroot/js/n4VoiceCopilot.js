/**
 * Sentinel Voice Copilot - Web Speech API Integration
 * Reconnaissance vocale (Speech-to-Text) & Synthèse vocale (Text-to-Speech)
 */
window.n4VoiceCopilot = {
    recognition: null,

    isSupported: function () {
        return 'webkitSpeechRecognition' in window || 'SpeechRecognition' in window;
    },

    startListening: function (dotNetHelper) {
        if (!this.isSupported()) {
            dotNetHelper.invokeMethodAsync('OnVoiceError', 'La reconnaissance vocale n\'est pas supportée par ce navigateur.');
            return;
        }

        var SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        this.recognition = new SpeechRecognition();
        this.recognition.lang = 'fr-FR';
        this.recognition.interimResults = false;
        this.recognition.maxAlternatives = 1;

        this.recognition.onresult = function (event) {
            if (event.results && event.results.length > 0) {
                var transcript = event.results[0][0].transcript;
                dotNetHelper.invokeMethodAsync('OnVoiceTranscriptReceived', transcript);
            }
        };

        this.recognition.onerror = function (event) {
            dotNetHelper.invokeMethodAsync('OnVoiceError', 'Erreur de saisie vocale : ' + event.error);
        };

        this.recognition.onend = function () {
            dotNetHelper.invokeMethodAsync('OnVoiceListeningEnded');
        };

        try {
            this.recognition.start();
        } catch (e) {
            console.warn('[Voice Copilot] Re-démarrage reconnaissance vocale.', e);
        }
    },

    stopListening: function () {
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
