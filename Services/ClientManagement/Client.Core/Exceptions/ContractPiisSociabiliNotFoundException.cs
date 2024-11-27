namespace Client.Core.Exceptions
{
    public class ContractPiisSociabiliNotFoundException : Exception
    {
        public ContractPiisSociabiliNotFoundException(string typeInfo, string data)
            :base($"Aucun contrat trouvé dans Sociabili avec le \"{typeInfo}\" \"{data}\"!")
        {
        }
    }
}
