namespace Client.Core.Exceptions
{
    public class InvalidEmailException : Exception
    {
        public InvalidEmailException(string ClientNiss)
            :base($"Le format de l'adresse email \"{ClientNiss}\" n'est pas correct !")
        {
        }
    }
}
