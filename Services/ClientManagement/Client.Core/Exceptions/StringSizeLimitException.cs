namespace Client.Core.Exceptions
{
    public class StringSizeLimitException : Exception
    {
        public StringSizeLimitException(string item)
            :base($"La chaine de caractère \"{item}\" a dépassé la limite authorisée!")
        {
        }
    }
}
