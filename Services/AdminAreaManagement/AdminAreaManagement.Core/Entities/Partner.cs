using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.ValueObjects;

namespace AdminAreaManagement.Core.Entities
{
    public class Partner : AggregateRoot
    {
        public int PartnerNumber { get; set; }
        public String Name { get; set; }
        public Address Address { get; set; }
        public int StaffMemberId { get; set; }
        public StaffMember StaffMember { get; set; }
        public CategoryOfPartner CategoryOfPartner { get; set; }
        public string CategoryOfPartnerName { get; set; }
        public StatusOfPartner StatusOfPartner { get; set; } = StatusOfPartner.Active;
        public DateTime DateOfAgreementSignature { get; set; }
        public DateTime? DateOfConclusion { get; set; }
        public Boolean? IsEconomieSociale { get; set; } = false;
        public String? Note { get; set; }
        public IList<DocumentPartner> Documents { get; set; } = new List<DocumentPartner>();
        public List<ContactPerson> ContactPersons { get; set; } = new List<ContactPerson>();
        public List<Email> Emails { get; set; } = new List<Email>();
        public IEnumerable<ContactPerson> Contacts
        {
            get
            {
                return this.ContactPersons;
            }
        }

        public Partner()
        {
        }

        public Partner(
            int partnerNumber,
            String name,
            Address address,
            StaffMember staffMember,
            CategoryOfPartner categoryOfPartner,
            StatusOfPartner statusOfPartner,
            DateTime dateOfAgreementSignature,
            String? note
            )
        {
            if (String.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            PartnerNumber = partnerNumber;
            Name = name;
            Address = address;
            StaffMember = staffMember;
            CategoryOfPartner  = categoryOfPartner;
            StatusOfPartner = statusOfPartner;
            DateOfAgreementSignature = dateOfAgreementSignature;
            Note = note;
        }

        public void AssignContactPerson(ContactPerson contactPerson)
        {
            var exists = this.ContactPersons.Contains(contactPerson);

            if (!exists)
            {
                this.ContactPersons.Add(contactPerson);
            }

        }

        public void removeContactPerson(List<ContactPerson> contactPerson)
        {
            foreach (var contact in this.ContactPersons.ToList())
            {
                var exists = contactPerson.Contains(contact);

                if (!exists)
                {
                    this.ContactPersons.Remove(contact);
                }
            }
            
        }
    }
}
