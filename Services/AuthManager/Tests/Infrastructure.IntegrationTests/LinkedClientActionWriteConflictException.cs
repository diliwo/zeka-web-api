namespace ClientManagement.Application.ActionWrites;

// The linked Client authority scope only needs this exception from ActionWriteContracts.cs.
// Keep its contract without pulling unrelated MediatR handlers into this test assembly.
internal sealed class ActionWriteConflictException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
