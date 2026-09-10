using System.Linq.Expressions;
using ClientManagement.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence;

public class RepositoryBase<T> : IRepositoryBase<T> where T : class
{
    protected ApplicationDbContext ApplicationDbContext;

    public RepositoryBase(ApplicationDbContext context) => ApplicationDbContext = context;

    public IQueryable<T> FindAll(bool trackChanges) =>
        !trackChanges
            ? ApplicationDbContext.Visible<T>()
                .AsNoTracking()
            : ApplicationDbContext.Visible<T>();

        public IQueryable<T> FindByCondition(Expression<Func<T, bool>> expression, bool trackChanges) =>
            !trackChanges
                ? ApplicationDbContext.Visible<T>()
                    .Where(expression)
                    .AsNoTracking()
                : ApplicationDbContext.Visible<T>()
                    .Where(expression);

    public void Create(T entity) => ApplicationDbContext.Set<T>().Add(entity);

    public void Update(T entity) => ApplicationDbContext.Set<T>().Update(entity);

    public void Delete(T entity) => ApplicationDbContext.Set<T>().Remove(entity);
}
