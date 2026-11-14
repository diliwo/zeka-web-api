using Xunit;

namespace Domain.UnitTests;

public sealed class Slice1aLiveNegativeTests
{
    [Fact]
    public void Rejects_new_failure_signature() => Assert.True(false);
}
