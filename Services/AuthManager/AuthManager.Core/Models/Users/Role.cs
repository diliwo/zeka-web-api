using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuthManager.Core.Models.Users
{
    public sealed class Role : IdentityRole<Guid>
    {
        private Role() { }

        public Role(string name)
        {
            Name = name;
            NormalizedName = name.ToUpperInvariant();
        }
    }
}
