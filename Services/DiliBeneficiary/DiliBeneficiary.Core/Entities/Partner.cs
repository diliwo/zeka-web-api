using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;
using DiliBeneficiary.Core.ValueObjects;

namespace DiliBeneficiary.Core.Entities
{
    public class Partner : AggregateRoot
    {
        public int PartnerNumber { get; set; }
        public String Name { get; set; }
        public Address Address { get; set; }
        public int JobCoachId { get; set; }
        public Referent JobCoach { get; set; }
        public CategoryOfPartner CategoryOfPartner { get; set; }
        public string CategoryOfPartnerName { get; set; }
        public StatusOfPartner StatusOfPartner { get; set; } = StatusOfPartner.Active;
        public DateTime DateOfAgreementSignature { get; set; }
        public DateTime? DateOfConclusion { get; set; }
        public Boolean? IsEconomieSociale { get; set; } = false;
        public String? Note { get; set; }
        public IList<Job> Jobs { get; set; } = new List<Job>();
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
            //List<Email> emails,
            //List<Phone> phones,
            Referent jobCoach,
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
            //Phones = phones;
            //Emails = emails;
            JobCoach = jobCoach;
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
