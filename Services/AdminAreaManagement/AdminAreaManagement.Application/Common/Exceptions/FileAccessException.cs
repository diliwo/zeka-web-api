namespace AdminAreaManagement.Application.Common.Exceptions
{
    public class FileAccessException : ApplicationExceptionBase
    {
        public string FileName { get; set; }
        public FileAccessException(string filename) : base($"Une erreur s'est produite lors de l'ouverture du fichier {filename}")
        {
            this.FileName = filename;
        }

        public override int ErrorRestCode => 405;

        public override string ErrorTypeUrl => "https://tools.ietf.org/html/rfc7231#section-6.5.5";

        public override string ErrorTitle => $"Une erreur s'est produite lors de l'ouverture du fichier {FileName}";
    }
}
