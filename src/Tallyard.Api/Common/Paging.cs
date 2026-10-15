using Microsoft.EntityFrameworkCore;

namespace Tallyard.Api.Common;

public record PageQuery(int Page = 1, int PageSize = 25, string? Q = null)
{
    public int SafePage => Math.Max(1, Page);
    public int SafeSize => Math.Clamp(PageSize, 1, 200);
}

public record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

public static class PagingExtensions
{
    public static async Task<Page<T>> ToPageAsync<T>(this IQueryable<T> query, PageQuery p, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip((p.SafePage - 1) * p.SafeSize).Take(p.SafeSize).ToListAsync(ct);
        return new Page<T>(items, total, p.SafePage, p.SafeSize);
    }
}
