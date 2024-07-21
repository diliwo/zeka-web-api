using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Common.Models
{
    public class PaginatedList<T>
    {
        public List<T> Items { get; set; }
        public string SortOrder { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }

        //public PaginatedList(){}

        public PaginatedList(List<T> items, int pageNumber, string sortOrder, int pageSize, int count = 0)
        {
            Items = items;
            PageNumber = pageNumber;
            SortOrder = sortOrder;
            PageSize = pageSize;
            TotalCount = count;
        }

        public static async Task<PaginatedList<T>> CreateAsync(IQueryable<T> source, int pageNumber, int pageSize, string sortOrder = "")
        {

            var count = await source.CountAsync();
            var items = await source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

            return new PaginatedList<T>(items, pageNumber, sortOrder, pageSize, count);
        }
    }
}
