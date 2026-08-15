namespace N4Sentinel.Domain;

/// <summary>
/// Niveau d'automatisation des opérations et des workflows (Section 2.2.3 du cahier des charges).
/// 
/// Palier 1 — Semi-automatique : l'application pilote les services, mais chaque action mutative
/// exige une confirmation explicite de l'opérateur selon le niveau de risque.
/// 
/// Palier 2 — Automatique de bout en bout : exécution contrôlée sans confirmation humaine intermédiaire
/// à chaque étape sur les workflows validés et fiabilisés.
/// </summary>
public enum AutomationLevel
{
    /// <summary>Palier 1 — Semi-automatique avec validation humaine étape par étape (Défaut V1).</summary>
    SemiAutomatique = 1,

    /// <summary>Palier 2 — Orchestration automatique de bout en bout (Évolution V2 / Palier 2).</summary>
    AutomatiqueBoutEnBout = 2
}
