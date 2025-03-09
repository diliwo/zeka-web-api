using System.Text.RegularExpressions;
using ClientManagement.Core.Common;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Exceptions;
using ClientManagement.Core.ValueObjects;

namespace ClientManagement.Core.Entities
{
    public class Client : AggregateRoot
    {
        public string? ReferenceNumber { get; set; }
        public CivilStatus CivilStatus { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName => $"{LastName} {FirstName}";
        public Gender Gender { get; set; }
        public DateTime BirthDate { get; set; }
        public string PlaceOfBirth { get; set; }
        public string? Nationality { get; set; }
        public string Ssn { get; set; }
        public Email Email { get; set; }
        public Phone Phone { get; set; }
        public Phone MobilePhone { get; set; }
        public ValueObjects.Language NativeLanguage { get; set; }
        public ValueObjects.Language ContactLanguage { get; set; }
        public Address Address { get; set; }
        public string SocialWorkerName { get; set; }
        public virtual IList<Support> Supports { get; set; } = new List<Support>();
        public virtual IList<SchoolRegistration> SchoolRegistrations { get; set; } = new List<SchoolRegistration>();
        public virtual IList<ProfessionnalExperience> ProfessionnalExpectations { get; set; } = new List<ProfessionnalExperience>();
        public virtual IList<Assessment> Assessments { get; set; } = new List<Assessment>();
        public virtual IList<MonitoringReport> MonitoringReports { get; set; } = new List<MonitoringReport>();
        public IEnumerable<SchoolRegistration> Registrations
        {
            get => SchoolRegistrations;
        }

        public Client()
        {
        }

        public Client(
            string referenceNumber, 
            CivilStatus civilStatus, 
            string firstFirstName,
            string lastName,
            Gender gender,
            DateTime birthDate,
            string placeOfBirth,
            string nationality,
            string ssn,
            Email email,
            Phone phone,
            Phone mobilePhone,
            ValueObjects.Language nativeLanguage,
            ValueObjects.Language contactLanguage,
            Address address,
            string socialWorkerName) {

            if (string.IsNullOrEmpty(firstFirstName))
            {
                throw new ArgumentNullException(nameof(firstFirstName));
            }

            if (string.IsNullOrEmpty(lastName))
            {
                throw new ArgumentNullException(nameof(lastName));
            }



            ReferenceNumber = referenceNumber;
            CivilStatus = civilStatus;
            FirstName = firstFirstName;
            LastName = lastName;
            Gender = gender;
            BirthDate = birthDate;
            PlaceOfBirth = placeOfBirth;
            Nationality = nationality;
            Ssn = ssn;
            Email = email;
            Phone = phone;
            MobilePhone = mobilePhone;
            NativeLanguage = nativeLanguage;
            ContactLanguage = contactLanguage;
            Address = address;
            SocialWorkerName = socialWorkerName;
        }

        public void UpdateNativeLanguage(string nativeLanguage)
        {
            string nativeLanguagePattern = @"^[a-zA-Z àâäèéêëîïôœùûüÿçÀÂÄÈÉÊËÎÏÔŒÙÛÜŸÇ]{4,}$";
            if (!string.IsNullOrWhiteSpace(nativeLanguage) && !Regex.Match(nativeLanguage, nativeLanguagePattern, RegexOptions.IgnoreCase).Success)
            {
                throw new InvalidLanguageNameFormatException(nativeLanguage);
            }

            NativeLanguage.SpokenLanguage = nativeLanguage;
        }
    }
}
