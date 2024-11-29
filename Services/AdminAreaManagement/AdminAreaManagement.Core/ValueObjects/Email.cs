using AdminAreaManagement.Core.Commun;

namespace AdminAreaManagement.Core.ValueObjects
{
    public class Email : ValueObject
    {
        public string EmailAddress { get; set; }

        public Email() { }

        public Email(string emailAddress)
        {
            //if (!String.IsNullOrEmpty(emailAddress))
            //{
            //    string emailPattern = @"^([a-zA-Z0-9_\-\.]+)@((\[[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.)|(([a-zA-Z0-9\-]+\.)+))([azA-Z]{2,4}|[0-9]{1,3})(\]?)$";
            //    if (!Regex.Match(emailAddress, emailPattern, RegexOptions.IgnoreCase).Success)
            //    {
            //        throw new InvalidEmailException(emailAddress);
            //    }
            //}

            EmailAddress = emailAddress;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return EmailAddress;
        }
    }
}
