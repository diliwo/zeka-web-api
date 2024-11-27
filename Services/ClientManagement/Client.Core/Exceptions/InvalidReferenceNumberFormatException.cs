namespace Client.Core.Exceptions
{
    public class InvalidReferenceNumberFormatException : Exception
    {
        public InvalidReferenceNumberFormatException(string referenceNumber) 
            : base($"Le format du numéro de dossier \"{referenceNumber}\" n'est pas valide !")
        {
        }
    }
}
