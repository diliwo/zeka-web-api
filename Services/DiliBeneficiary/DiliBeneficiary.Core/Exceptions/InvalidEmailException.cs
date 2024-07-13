namespace DiliBeneficiary.Core.Exceptions
{
    public class InvalidEmailException : Exception
    {
        public InvalidEmailException(string beneficiaryNiss)
            :base($"Le format de l'adresse email \"{beneficiaryNiss}\" n'est pas correct !")
        {
        }
    }
}
