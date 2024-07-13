namespace DiliBeneficiary.Core.Exceptions
{
    public class BeneficiarySociabiliAlreadyExists : Exception
    {
        public BeneficiarySociabiliAlreadyExists(string beneficiaryNiss)
            :base($"Le bénéficiaire avec le niss \"{beneficiaryNiss}\" existe déjà !")
        {
        }
    }
}
