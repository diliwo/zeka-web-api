namespace Client.Core.Exceptions
{
    public class ClientSociabiliAlreadyExists : Exception
    {
        public ClientSociabiliAlreadyExists(string ClientNiss)
            :base($"Le bénéficiaire avec le niss \"{ClientNiss}\" existe déjà !")
        {
        }
    }
}
