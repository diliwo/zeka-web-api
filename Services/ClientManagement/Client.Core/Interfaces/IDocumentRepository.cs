namespace ClientManagement.Core.Interfaces
{
    public interface IDocumentRepository
    {
        Task<byte[]> GeneratePaperBilanAsync();

    }
}
