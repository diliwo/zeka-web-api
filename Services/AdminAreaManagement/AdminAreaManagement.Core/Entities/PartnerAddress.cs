using AdminAreaManagement.Core.ValueObjects;

namespace AdminAreaManagement.Core.Entities;

// Scalar accessors preserve the existing optional address columns without creating an EF entity.
public partial class Partner
{
    private string? AddressNumber
    {
        get => Address?.Number;
        set
        {
            if (value is null && Address is null) return;
            Address ??= new Address(null!, null!, null!, null!);
            Address.Number = value!;
        }
    }
    private string? AddressStreet
    {
        get => Address?.Street;
        set
        {
            if (value is null && Address is null) return;
            Address ??= new Address(null!, null!, null!, null!);
            Address.Street = value!;
        }
    }
    private string? AddressPostalCode
    {
        get => Address?.PostalCode;
        set
        {
            if (value is null && Address is null) return;
            Address ??= new Address(null!, null!, null!, null!);
            Address.PostalCode = value!;
        }
    }
    private string? AddressCity
    {
        get => Address?.City;
        set
        {
            if (value is null && Address is null) return;
            Address ??= new Address(null!, null!, null!, null!);
            Address.City = value!;
        }
    }
}
