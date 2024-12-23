namespace AdminAreaManagement.Core.Interfaces;

public interface ISortHelper<T>
{ 
    IQueryable<T> ApplySort(IQueryable<T> entities, string orderByQueryString);
}