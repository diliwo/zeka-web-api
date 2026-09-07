namespace ClientManagement.Core.Exceptions
{
    public class ClientAlreadyExists : Exception
    {
        public ClientAlreadyExists()
            :base("A client with the supplied NISS already exists.")
        {
        }
    }
}
