namespace DiliBeneficiary.Core.Exceptions
{
    public class BeneficiarySociabiliNotFoundException : Exception
    {
        public BeneficiarySociabiliNotFoundException(string typeInfo, string data)
            :base($"Aucun bénéficiaire trouvé dans Sociabili avec le \"{typeInfo}\" \"{data}\"!")
        {
        }
        //public BeneficiarySociabiliNotFoundException(string message) : base(message) { }
    }
}
