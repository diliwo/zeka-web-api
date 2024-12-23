namespace AdminAreaManagement.Core.Interfaces
{
    public interface IGenericReadRepository<T>
    {
        Task<List<T>> GetItemsAsync();
    }
}