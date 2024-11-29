namespace ClientManagement.Core.Interfaces
{
    public interface IFileService
    {
        void SaveFile(int id, int partnerId, int jobId, string fileName, byte[] contentFile, string contentType);
        byte[] GetContentFile(int partnerId, int jobId, int docId, string contentType);
        public string GetFolderPath(int partnerId);
        void DeleteFile(int id, int partnerId, int jobId, string contentType);
    }
}