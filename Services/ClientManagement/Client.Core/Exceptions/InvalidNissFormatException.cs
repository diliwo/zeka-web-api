namespace ClientManagement.Core.Exceptions
{
    public class InvalidNissFormatException : Exception
    {
        public InvalidNissFormatException()
            :base("NISS must contain exactly eleven ASCII digits and a valid modulo-97 check.")
        {
        }
    }
}
