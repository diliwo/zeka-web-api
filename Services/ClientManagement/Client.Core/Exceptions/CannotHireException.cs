namespace ClientManagement.Core.Exceptions
{
    public class CannotHireException : Exception
    {
        public CannotHireException(string ClientName)
            :base($"Le bénéficaire \"{ClientName}\" ne peut être embauché, car il n'a pas postulé à ce poste !")
        {
        }
    }
}
