namespace AdminAreaManagement.Core.Interfaces
{
    public interface IFileService
    {
        void SaveFile(Guid organisationId, int id, int partnerId, byte[] contentFile);
        byte[] GetContentFile(Guid organisationId, int partnerId, int docId);
        string GetFolderPath(Guid organisationId, int partnerId);
        void DeleteFile(Guid organisationId, int id, int partnerId);
    }
}