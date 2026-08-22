namespace PathEcho.Core.Update;

public sealed record UpdateNetworkOptions
{
    public IReadOnlyList<UpdateUrlRoute> UrlRoutes { get; init; } = new[] { UpdateUrlRoute.Direct };

    public string? HttpProxy { get; init; }
}

public sealed record UpdateUrlRoute
{
    public static UpdateUrlRoute Direct { get; } = new()
    {
        IsDirect = true,
        Priority = 5,
    };

    public string? BaseUrl { get; init; }

    public int Priority { get; init; } = 5;

    public bool IsDirect { get; init; }
}
