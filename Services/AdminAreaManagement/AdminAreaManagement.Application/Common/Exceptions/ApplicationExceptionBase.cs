namespace AdminAreaManagement.Application.Common.Exceptions
{
    public abstract class ApplicationExceptionBase : Exception
    {
        public abstract int ErrorRestCode { get; }
        public abstract string ErrorTypeUrl { get; }
        public abstract string ErrorTitle { get; }
        public ApplicationExceptionBase()
        {

        }
        public ApplicationExceptionBase(string message)
      : base(message)
        {
        }

        public ApplicationExceptionBase(string message, Exception innerException)
            : base(message, innerException)
        {
        }

    }
}
