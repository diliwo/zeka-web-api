namespace ClientManagement.Core.Exceptions
{
    public class InvalidAcronymFormatException : Exception
    {
        public InvalidAcronymFormatException(string ClientNiss)
            :base($"Le format de l'acronym \"{ClientNiss}\" n'est pas valide !")
        {
        }
    }
}
