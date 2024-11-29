namespace AdminAreaManagement.Infrastructure.Persistence.Helpers
{
    public static class FileRepositoryHelper
    {
        public static string GetFolderNameForPartnerId(int partnerId)
        {
            //const int nbDocumentByFolder = 1000;
            //int inferiorLimit = (int)Math.Floor((decimal) partnerId / (nbDocumentByFolder+1)) * nbDocumentByFolder + 1;
            //int superiorLimit = (int)inferiorLimit + (nbDocumentByFolder - 1);
            //return $"{inferiorLimit:D6}-{superiorLimit:D6}";

            return partnerId.ToString();
        }
    }
}
