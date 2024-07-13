namespace DiliBeneficiary.Core.Exceptions
{
    public class InvalidLanguageNameFormatException : Exception
    {
        public InvalidLanguageNameFormatException(string nativeLanguage)
            : base($"La langue \"{nativeLanguage}\" doit contenir au minimum 3 caractères ")
        {
        }
    }
}
