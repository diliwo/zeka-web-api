namespace AdminAreaManagement.Core.Interfaces
{
    public interface IDocumentRepository
    {
        Task<byte[]> GeneratePaperBilanAsync();

    }
}
