﻿using Client.Core.Common;
using Client.Core.Exceptions;

namespace Client.Core.Entities
{
    public class Service : Entity
    {
        public string Name { get; set; }
        public string Acronym { get; set; }

        public Service() {}

        public Service(string name, string acronym)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrEmpty(acronym))
            {
                throw new ArgumentNullException(nameof(acronym));
            }

            if (acronym.Length > 7 && acronym.Length < 2)
            {
                throw new InvalidAcronymFormatException(acronym);
            }

            Name = name;
            Acronym = acronym.ToUpper();
        }
        public ICollection<Staff> Staffs { get; private set; } = new HashSet<Staff>();
    }
}