namespace Client.Core.Exceptions
{
   public class SeveralSociabiliClientsFoundedException : Exception
    {
        public SeveralSociabiliClientsFoundedException(string message) : base(message) { }
    }
}
