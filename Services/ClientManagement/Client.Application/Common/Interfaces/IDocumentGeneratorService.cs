using ClientManagement.Application.Models;

namespace ClientManagement.Application.Common.Interfaces
{
    public interface IDocumentGeneratorService
    {
        public Task<byte[]> GenerateAssessmentReportAsync(AssessmentReportDocumentModel assessmentModel);
    }
}
