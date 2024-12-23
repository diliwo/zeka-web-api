using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Enums;

namespace AdminAreaManagement.Core.ValueObjects
{
    public class ContactPerson : ValueObject
    {
        public String ContactDetails { get; set; }
        public String ContactName { get; set; }
        public Gender Gender { get; set; }
        public bool ToDelete { get; set; } = false;


        public ContactPerson() { }

        public ContactPerson(String contactDetails, String contactName, Gender gender)
        {
            ContactDetails = contactDetails;
            ContactName = contactName;
            Gender = gender;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return this.ContactDetails;
        }
    }
}
