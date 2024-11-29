namespace ClientManagement.Core.Exceptions
{
    public class ClientSociabiliNotFoundException : Exception
    {
        public ClientSociabiliNotFoundException(string typeInfo, string data)
            :base($"Aucun bénéficiaire trouvé dans Sociabili avec le \"{typeInfo}\" \"{data}\"!")
        {
        }
        //public ClientSociabiliNotFoundException(string message) : base(message) { }
    }
}
