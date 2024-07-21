namespace DiliBeneficiary.Core.Exceptions
{
    public class InvalidAcronymFormatException : Exception
    {
        public InvalidAcronymFormatException(string beneficiaryNiss)
            :base($"Le format de l'acronym \"{beneficiaryNiss}\" n'est pas valide !")
        {
        }
    }
}
