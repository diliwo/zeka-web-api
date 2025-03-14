﻿using ClientManagement.Core.Common;

namespace ClientManagement.Core.ValueObjects
{
    public class Address : ValueObject
    {
        public string Number { get; set; }
        public string Street { get; set; }
        public string PostalCode { get; set; }
        public string City { get; set; }
        public string Country { get; set; }

        public Address(string number, string street, string postalCode, string city, string country)
        {
            Number = number;
            Street = street;
            PostalCode = postalCode;
            City = city;
            Country = country;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Street;
        }
    }
}
