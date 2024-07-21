using DiliBeneficiary.Core.Interfaces;
using DiliBeneficiary.Infrastructure.Persistence.Helpers;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class FileService : IFileService
    {
        private FileRepositorySettings _settings;
        private readonly string _fileServerPath;

        public FileService(FileRepositorySettings settings)
        {
            _settings = settings;
            //var filePathSettings =  System.Configuration.ConfigurationManager.AppSettings["FileServerPath"];
            var filePathSettings = _settings.FileServerPath;
            if (string.IsNullOrEmpty(filePathSettings))
            {
                throw new Exception("FileServerPath configuration setting missing");
            }
            _fileServerPath = filePathSettings;
        }

        public FileService(string fileServerPath)
        {
            this._fileServerPath = fileServerPath;
            if (!Directory.Exists(_fileServerPath))
            {
                Directory.CreateDirectory(_fileServerPath);
            }
        }

        public string GetFolderPath(int partnerId)
        {
            return $@"{_fileServerPath}{FileRepositoryHelper.GetFolderNameForPartnerId(partnerId)}";
        }

        public void SaveFile(int id, int partnerId, int jobId, string fileName, byte[] contentFile, string contentType)
        {
            var folderPath = GetFolderPath(partnerId);
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
            }
            catch (Exception e)
            {
                // can't create folder: do nothing and return
                throw new ApplicationException($"Erreur lors de la tentative d'importation du document : le dossier d'enregistrement n'a pas pu être créé ou n'existe pas ! ({folderPath})");
            }

            var fileFullPath = $@"{folderPath}\Document-{partnerId}-{jobId}-{id}.{contentType}";

            //if (File.Exists(fileFullPath))
            //{

            //}

            try
            {
                File.WriteAllBytes(fileFullPath,contentFile);
            }
            catch (Exception e)
            {
                throw new ApplicationException($"Erreur lors de la tentative d'importation  du document: une erreur est survenue lors de l'enregistrement du fichier ! ({fileFullPath}");
            }
        }

        public byte[] GetContentFile(int partnerId, int jobId, int docId, string contentType)
        {
            var fileName = $"Document-{partnerId}-{jobId}-{docId}.{contentType}";
            var filePath = $"{GetFolderPath(partnerId)}\\{fileName}";

            using System.IO.FileStream fs = System.IO.File.OpenRead(filePath);
            byte[] data = new byte[fs.Length];
            int br = fs.Read(data, 0, data.Length);
            if (br != fs.Length)
                throw new System.IO.IOException(filePath);
            return data;
        }


        public void DeleteFile(int id, int partnerId, int jobId, string contentType)
        {
            var folderPath = GetFolderPath(partnerId);
            var fileFullPath = $@"{folderPath}\Document-{partnerId}-{jobId}-{id}.{contentType}";
            if (!File.Exists(fileFullPath))
            {
                throw new ApplicationException($"Erreur lors de la tentative de suppression du document : le fichier n'existe pas sur le serveur! ({folderPath})");
            }
            try
            {
                File.Delete(fileFullPath);
            }
            catch (Exception e)
            {
                throw new ApplicationException($"Erreur lors de la tentative  de suppression du document: une erreur est survenue lors de la suppression du fichier ! ({fileFullPath}");
            }
        }
    }

    public class FileRepositorySettings
    {
        public string FileServerPath { get; private set; }

        public FileRepositorySettings(string fileServerPath)
        {
            FileServerPath = fileServerPath;
        }
    }
}