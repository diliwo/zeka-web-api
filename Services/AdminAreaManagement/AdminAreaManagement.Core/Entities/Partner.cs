using AdminAreaManagement.Core.Commun;
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
        public List<Phone> Phones { get; set; } = new List<Phone>();
        public List<Email> Emails { get; set; } = new List<Email>();
        public IEnumerable<Phone> PhoneNumbers
        {
            get
            {
                return this.Phones;
            }
        }

        public Partner()
        {
        }

        public Partner(
            int partnerNumber,
            String name,
            Address address,
            StaffMember StaffMember,
            CategoryOfPartner categoryOfPartner,
            StatusOfPartner statusOfPartner,
            DateTime dateOfAgreementSignature,
            Boolean? isEconomieSociale,
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
            CategoryOfPartner  = categoryOfPartner;
            StatusOfPartner = statusOfPartner;
            DateOfAgreementSignature = dateOfAgreementSignature;
            IsEconomieSociale = isEconomieSociale;
            Note = note;
        }

        public void AssignPhone(Phone phone)
        {
            var exists = this.Phones.Contains(phone);

            if (!exists)
            {
                this.Phones.Add(phone);
            }

        }

        public void removePhone(List<Phone> phones)
        {
            foreach (var phone in this.Phones.ToList())
            {
                var exists = phones.Contains(phone);

                if (!exists)
                {
                    this.Phones.Remove(phone);
                }
            }
            
        }
    }
}
