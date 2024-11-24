namespace Client.Core.Exceptions
{
    public class ClientAddressNotFoundException : Exception
    {
        public ClientAddressNotFoundException(string typeInfo, string data)
            :base($"Aucune adresse trouvée dans Sociabili pour le bénéficiaire \"{typeInfo}\" \"{data}\"!")
        {
        }
        //public ClientSociabiliNotFoundException(string message) : base(message) { }
    }
}
