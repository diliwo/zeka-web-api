namespace ClientManagement.Core.Exceptions
{
    public class ClientAlreadyExists : Exception
    {
        public ClientAlreadyExists(string ClientSsn)
            :base($"The client with the given niss \"{ClientSsn}\" already exists !")
        {
        }
    }
}
