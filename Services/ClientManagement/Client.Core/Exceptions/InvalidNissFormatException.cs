namespace ClientManagement.Core.Exceptions
{
    public class InvalidNissFormatException : Exception
    {
        public InvalidNissFormatException(string ClientSsn)
            :base($"Le format du niss \"{ClientSsn}\" n'est pas valide !")
        {
        }
    }
}
