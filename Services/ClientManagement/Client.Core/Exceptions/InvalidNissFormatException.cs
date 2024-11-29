namespace ClientManagement.Core.Exceptions
{
    public class InvalidNissFormatException : Exception
    {
        public InvalidNissFormatException(string ClientNiss)
            :base($"Le format du niss \"{ClientNiss}\" n'est pas valide !")
        {
        }
    }
}
