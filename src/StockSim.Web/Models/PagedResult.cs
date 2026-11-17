namespace StockSim.Web.Models;

/// <summary>通用分页结果（内存切片或服务端查询均可）。</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public int TotalCount { get; init; }
    public string PageParam { get; init; } = "page";

    public int TotalPages => PageSize <= 0
        ? 0
        : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool HasPrev => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> From(
        IEnumerable<T> source,
        int page,
        int pageSize = 20,
        string pageParam = "page")
    {
        pageSize = Math.Clamp(pageSize, 5, 100);
        page = Math.Max(1, page);
        var list = source as IReadOnlyList<T> ?? source.ToList();
        var total = list.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        if (page > totalPages) page = totalPages;
        IReadOnlyList<T> items = total == 0
            ? Array.Empty<T>()
            : list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            PageParam = pageParam
        };
    }

    public static PagedResult<T> Create(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        int totalCount,
        string pageParam = "page")
    {
        pageSize = Math.Clamp(pageSize, 5, 100);
        page = Math.Max(1, page);
        return new PagedResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            PageParam = pageParam
        };
    }
}

/// <summary>供 Partial 渲染的分页信息（不依赖具体 Items 类型）。</summary>
public sealed class PagerModel
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public int TotalCount { get; init; }
    public string PageParam { get; init; } = "page";
    public string? Anchor { get; init; }

    public int TotalPages => PageSize <= 0
        ? 0
        : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool HasPrev => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PagerModel From<T>(PagedResult<T> p, string? anchor = null) => new()
    {
        Page = p.Page,
        PageSize = p.PageSize,
        TotalCount = p.TotalCount,
        PageParam = p.PageParam,
        Anchor = anchor
    };
}
