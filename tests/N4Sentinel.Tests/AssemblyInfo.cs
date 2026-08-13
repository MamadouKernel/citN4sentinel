using Xunit;

// Désactivation du parallélisme d'exécution des classes de test d'intégration SQL Server
[assembly: CollectionBehavior(DisableTestParallelization = true)]
