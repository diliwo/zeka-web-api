namespace DiliBeneficiary.Core.Exceptions
{
    public class InvalidNissFormatException : Exception
    {
        public InvalidNissFormatException(string beneficiaryNiss)
            :base($"Le format du niss \"{beneficiaryNiss}\" n'est pas valide !")
        {
        }
    }
}
