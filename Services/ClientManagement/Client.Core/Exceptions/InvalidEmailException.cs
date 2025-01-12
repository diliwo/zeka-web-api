namespace ClientManagement.Core.Exceptions
{
    public class InvalidEmailException : Exception
    {
        public InvalidEmailException(string ClientNiss)
            :base($"The format of the email address \"{ClientNiss}\" is not correct !")
        {
        }
    }
}
