using Client.Core.Common;

namespace Client.Core.ValueObjects
{
    public class Phone : ValueObject
    {
        public String PhoneNumber { get; set; }

        public Phone() { }

        public Phone(String phoneNumber)
        {

            PhoneNumber = phoneNumber;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return PhoneNumber;
        }
    }
}
