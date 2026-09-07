using ClientManagement.Core.Entities;
using ClientManagement.Core.Exceptions;
using ClientManagement.Core.ValueObjects;
using ClientManagement.Tests.Common;

namespace Domain.UnitTests;

public sealed class NissTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("letter")]
    [InlineData("unicode-digits")]
    [InlineData("outer-space")]
    [InlineData("punctuation")]
    [InlineData("bad-check")]
    [InlineData("zero-check")]
    public void Invalid_input_is_rejected_on_parse_creation_and_update_without_disclosure(string scenario)
    {
        var valid = SyntheticClient.Niss();
        string? input = scenario switch
        {
            "null" => null,
            "empty" => "",
            "whitespace" => new string(' ', 11),
            "short" => valid[..10],
            "long" => valid + "0",
            "letter" => "x" + valid[1..],
            "unicode-digits" => new string(valid.Select(c => (char)('\u0660' + c - '0')).ToArray()),
            "outer-space" => " " + valid + " ",
            "punctuation" => valid.Insert(6, "-"),
            "bad-check" => valid[..9] + "01",
            "zero-check" => valid[..9] + "00",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var parsedException = Assert.Throws<InvalidNissFormatException>(() => Niss.Parse(input));
        Assert.Throws<InvalidNissFormatException>(() => SyntheticClient.Create(input));
        var client = SyntheticClient.Create(valid);
        Assert.Throws<InvalidNissFormatException>(() => client.Ssn = input!);
        Assert.True(client.Ssn == valid, "Rejected reassignment must preserve the previous NISS.");
        if (!string.IsNullOrEmpty(input))
            Assert.False(parsedException.ToString().Contains(input), "Exception must not disclose submitted input.");
    }

    [Theory]
    [InlineData(80, 3, 15, 1, false)]
    [InlineData(0, 3, 15, 1, true)]
    [InlineData(80, 43, 15, 1, false)]
    [InlineData(0, 43, 15, 1, true)]
    [InlineData(80, 23, 15, 2, false)]
    [InlineData(0, 23, 15, 2, true)]
    [InlineData(80, 0, 0, 1, false)]
    [InlineData(80, 0, 32, 1, false)]
    [InlineData(0, 0, 0, 97, false)]
    public void RN_BIS_unknown_dates_and_check_97_preserve_canonical_digits(
        int year, int month, int day, int sequence, bool post2000)
    {
        var input = SyntheticClient.Niss(year, month, day, sequence, post2000);
        var value = Niss.Parse(input);
        var client = SyntheticClient.Create(input);
        Assert.True(value.Value == input && client.Ssn == input);
        Assert.False(value.ToString().Contains(input));
        Assert.True(value.Equals(Niss.Parse(input)));
    }

    [Fact]
    public void Independent_pre_and_post_2000_checksum_vectors_are_accepted()
    {
        var pre = string.Concat("80", "03", "15", "001", "86");
        var post = string.Concat("00", "03", "15", "001", "84");
        Assert.True(Niss.Parse(pre).Value == pre);
        Assert.True(Niss.Parse(post).Value == post);
    }

    [Fact]
    public void Valid_reassignment_updates_the_identifier()
    {
        var client = SyntheticClient.Create(SyntheticClient.Niss());
        var replacement = SyntheticClient.Niss(sequence: 2);
        client.Ssn = replacement;
        Assert.True(client.Ssn == replacement);
    }

    [Fact]
    public void Empty_client_creation_is_not_public()
    {
        Assert.Null(typeof(Client).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void Duplicate_exception_does_not_accept_or_store_an_identifier()
    {
        Assert.Empty(typeof(ClientAlreadyExists).GetConstructors().Single().GetParameters());
        Assert.Empty(new ClientAlreadyExists().Data);
    }
}
