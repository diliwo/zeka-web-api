using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.ValueObjects
{
    public class Email : ValueObject
    {
        public string EmailAddress { get; set; }

        public Email() { }

        public Email(string emailAddress)
        {
            EmailAddress = emailAddress;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return EmailAddress;
        }
    }
}
