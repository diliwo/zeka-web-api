namespace DiliBeneficiary.Core.Interfaces
{
    public interface IDocumentRepository
    {
        Task<byte[]> GeneratePaperBilanAsync();

    }
}
