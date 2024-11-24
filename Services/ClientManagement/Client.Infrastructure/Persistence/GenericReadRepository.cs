using Client.Core.Interfaces;

namespace Client.Infrastructure.Persistence
{
    public class GenericReadRepository<T> : List<T>, IGenericReadRepository<T>
    {
        public async Task<List<T>> GetItemsAsync() => this;
    }
}