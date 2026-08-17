namespace N4Sentinel.Domain;

/// <summary>
/// Seuils de corrélation du moteur de diagnostic (FR-065), administrables au
/// lieu d'être codés en dur — ligne unique, sur le même principe que
/// <see cref="RetentionPolicy"/>.
///
/// Ces seuils ne changent jamais CE QUI est observé, seulement à partir de
/// quand une observation est présentée comme établie plutôt que comme une
/// piste. Un site dont les signatures sont mal calibrées peut resserrer ces
/// seuils sans attendre une nouvelle version du produit.
/// </summary>
public class DiagnosticSettings : AuditableEntity
{
    /// <summary>
    /// Confiance minimale (0-100) pour qu'une hypothèse soit présentée comme
    /// établie plutôt que comme insuffisante (<see cref="DiagnosticHypothesis.EstEtablie"/>).
    /// </summary>
    public int HypothesisEstablishedThreshold { get; set; } = 70;

    /// <summary>
    /// Poids de confiance minimal d'une signature pour qu'un verdict
    /// « Cause confirmée » (plutôt que « Cause caractérisée ») soit retenu.
    /// </summary>
    public int ConclusiveSignatureConfidenceWeight { get; set; } = 90;

    /// <summary>
    /// Confiance minimale d'une hypothèse pour qu'un verdict « Piste sérieuse »
    /// (plutôt que « Anomalies sans cause ») soit retenu.
    /// </summary>
    public int SeriousLeadConfidenceThreshold { get; set; } = 50;
}
