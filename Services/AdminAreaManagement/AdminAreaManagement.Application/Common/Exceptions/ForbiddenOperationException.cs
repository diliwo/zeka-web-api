namespace AdminAreaManagement.Application.Common.Exceptions
{
    public class ForbiddenOperationException : ApplicationExceptionBase
    {
        public string NumCandidacy { get; set; }
        public ForbiddenOperationException(string numCandidacy) : base($"Une erreur s'est produite lors de la suppression de la candidature n° {numCandidacy}")
        {
            this.NumCandidacy = numCandidacy;
        }

        public override int ErrorRestCode => 405;

        public override string ErrorTypeUrl => "https://tools.ietf.org/html/rfc7231#section-6.5.5";

        public override string ErrorTitle => $"Une erreur s'est produite lors de la suppression de la candidature n° {NumCandidacy}";
    }
}
