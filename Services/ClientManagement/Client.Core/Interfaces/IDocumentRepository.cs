namespace Client.Core.Interfaces
{
    public interface IDocumentRepository
    {
        Task<byte[]> GeneratePaperBilanAsync();

    }
}
