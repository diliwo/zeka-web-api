namespace DiliBeneficiary.Core.Exceptions
{
    public class BeneficiaryAddressNotFoundException : Exception
    {
        public BeneficiaryAddressNotFoundException(string typeInfo, string data)
            :base($"Aucune adresse trouvée dans Sociabili pour le bénéficiaire \"{typeInfo}\" \"{data}\"!")
        {
        }
        //public BeneficiarySociabiliNotFoundException(string message) : base(message) { }
    }
}
