using Client.Application.Models;

namespace Client.Application.Common.Interfaces
{
    public interface IDocumentGeneratorService
    {
        public Task<byte[]> GenerateBilanReportAsync(BilanReportDocumentModel bilanModel);
    }
}
