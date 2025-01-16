using AdminAreaManagement.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Isp.Domain.UnitTests
{
    public class TeamTest
    {
        [Fact]
        public void Constructor_NameNull_ThrowException()
        {
            FluentActions.Invoking(() => new Team(null,"SAL")).Should()
                .Throw<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_AcronymNull_ThrowException()
        {
            FluentActions.Invoking(() => new Team("Socio-Professional Integration Service", string.Empty)).Should()
                .Throw<ArgumentNullException>();
        }
    }
}
