namespace N4Sentinel.Domain;

/// <summary>
/// Règle de la matrice de criticité (FR-013, FR-027) : ce qui, selon
/// l'environnement, le scénario et le niveau de risque des composants
/// concernés, exige une approbation simple ou double avant lancement — ou
/// avant le contournement d'un contrôle en Production.
///
/// UN PARAMÈTRE DE WORKFLOW NE PEUT JAMAIS ABAISSER CE QUE LA MATRICE EXIGE.
/// <see cref="Workflow.RequiresApproval"/> et <see cref="Workflow.RequiresDoubleApproval"/>
/// restent un plancher que le concepteur du workflow peut relever, jamais un
/// moyen de contourner la matrice — voir <c>ExecutionService.PrepareAsync</c>.
/// </summary>
public class ApprovalMatrixRule : AuditableEntity
{
    /// <summary>Null = s'applique à tous les environnements.</summary>
    public EnvironmentKind? EnvironmentKind { get; set; }

    /// <summary>Null = s'applique à tous les scénarios.</summary>
    public WorkflowKind? WorkflowKind { get; set; }

    /// <summary>
    /// Seuil : la règle s'applique si au moins un composant touché par
    /// l'exécution a une criticité supérieure ou égale à celle-ci.
    /// </summary>
    public CriticalityLevel MinCriticality { get; set; } = CriticalityLevel.Faible;

    public bool RequiresApproval { get; set; }
    public bool RequiresDoubleApproval { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Notes { get; set; }

    /// <summary>
    /// Spécificité de la règle : plus le nombre de dimensions renseignées est
    /// élevé, plus la règle prime sur une règle plus générale qui matcherait
    /// aussi. Départagée ensuite par le seuil de criticité le plus élevé.
    /// </summary>
    public int Specificity => (EnvironmentKind is null ? 0 : 1) + (WorkflowKind is null ? 0 : 1);
}
