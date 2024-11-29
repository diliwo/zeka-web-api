using AdminAreaManagement.Core.Commun;
using AdminAreaManagement.Core.Enums;

namespace AdminAreaManagement.Core.ValueObjects
{
    public class Phone : ValueObject
    {
        public String PhoneNumber { get; set; }
        public String ContactName { get; set; }
        public Gender Gender { get; set; }
        public bool ToDelete { get; set; } = false;


        public Phone() { }

        public Phone(String phoneNumber, String contactName, Gender gender)
        {
            //if (String.IsNullOrEmpty(phoneNumber))
            //{
            //    throw new ArgumentNullException(nameof(phoneNumber));
            //}

            PhoneNumber = phoneNumber;
            ContactName = contactName;
            Gender = gender;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return this.PhoneNumber;
        }
    }
}
