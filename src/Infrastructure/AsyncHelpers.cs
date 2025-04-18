using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Admiral.Policies
{
    public static class AsyncHelpers
    {
        public static async Task<IList<TEntity>> GetAllAsync<TEntity>(this Azure.AsyncPageable<TEntity> query)
        {
            var list = new List<TEntity>();
            var enumerator = query.GetAsyncEnumerator();
            try
            {
                while (await enumerator.MoveNextAsync())
                    list.Add(enumerator.Current);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
            return list;
        }
    }
}