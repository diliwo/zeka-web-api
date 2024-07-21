using DiliBeneficiary.Application.Models;

namespace DiliBeneficiary.Application.Common.Interfaces
{
    public interface IDocumentGeneratorService
    {
        public Task<byte[]> GenerateBilanReportAsync(BilanReportDocumentModel bilanModel);
    }
}
