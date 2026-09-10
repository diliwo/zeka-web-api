using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.Application.Clients.Commands.UpdateNativeLanguage;
using ClientManagement.Application.Clients.Commands.UpSertIbisNumber;
using ClientManagement.Application.Clients.Model;
using ClientManagement.Core.Exceptions;
using ClientManagement.Tests.Common;

namespace Application.UnitTests;

public sealed class NissCommandTests
{
    [Fact]
    public async Task Creation_rejects_noncanonical_input_before_repository_access()
    {
        var fixture = SyntheticClient.Create(SyntheticClient.Niss());
        var request = new AddClientCommand
        {
            FirstName = fixture.FirstName, LastName = fixture.LastName,
            Email = fixture.Email!.EmailAddress, Phone = fixture.Phone!.PhoneNumber,
            MobilePhone = fixture.MobilePhone!.PhoneNumber, Ssn = " " + fixture.Ssn,
            Address = fixture.Address!
        };
        var handler = new AddClientCommand.AddClientCommandHandler(null!);
        await Assert.ThrowsAsync<InvalidNissFormatException>(() => handler.Handle(request, default));
    }

    [Fact]
    public async Task Update_lookup_rejects_noncanonical_input_before_repository_access()
    {
        var handler = new UpdateNativeLanguageCommand.UpdateNativeLanguageCommandHandler(null!);
        await Assert.ThrowsAsync<InvalidNissFormatException>(() => handler.Handle(
            new UpdateNativeLanguageCommand { Niss = " " + SyntheticClient.Niss(), Language = "Français" }, default));
    }

    [Fact]
    public async Task Patch_lookup_rejects_noncanonical_input_before_repository_access()
    {
        var handler = new UpSertIbisNumberCommand.UpSertIbisNumberCommandHandler(null!, null!);
        await Assert.ThrowsAsync<InvalidNissFormatException>(() => handler.Handle(
            new UpSertIbisNumberCommand
            {
                Niss = " " + SyntheticClient.Niss(), PatchDoc = new EmptyPatch()
            }, default));
    }

    private sealed class EmptyPatch : IClientPatch
    {
        public IEnumerable<string> ReplacementValues => Array.Empty<string>();
        public void ApplyTo(UpdateClientDto client) { }
    }
}
