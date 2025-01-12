namespace ClientManagement.Core.Exceptions
{
    public class InvalidLanguageNameFormatException : Exception
    {
        public InvalidLanguageNameFormatException(string nativeLanguage)
            : base($"The language \"{nativeLanguage}\" must contain at least 3 characters ")
        {
        }
    }
}
