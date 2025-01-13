namespace ClientManagement.Core.Exceptions
{
    public class InvalidAcronymFormatException : Exception
    {
        public InvalidAcronymFormatException(string ClientNiss)
            :base($"The acronym format \"{ClientNiss}\" is not !")
        {
        }
    }
}
