namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// FR-093 : export PDF/Word des rapports déjà produits en Markdown par
/// <c>ExecutionReportService</c> et <c>HistoryService</c>. Rend le MÊME
/// contenu, pas un contenu recomposé pour l'occasion — le Markdown existant
/// reste la source de vérité, le PDF/DOCX n'en sont que des présentations.
/// </summary>
public sealed class ReportDocumentService
{
    public byte[] RenderDocx(string title, string markdown) => DocxRenderer.Render(title, markdown);
    public byte[] RenderPdf(string title, string markdown) => PdfRenderer.Render(title, markdown);
}
