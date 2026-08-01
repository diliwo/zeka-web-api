using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuthManager.Core.Exceptions
{
    public sealed class RefreshTokenBadRequestException()
        : BadRequestException("Invalid or expired refresh token.")
    {
    }
}
