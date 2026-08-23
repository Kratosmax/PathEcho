using System.Net;

namespace PathEcho.Core.Update;

public static class UpdateRoutePlanner
{
    private static readonly HashSet<string> AllowedOriginalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "raw.githubusercontent.com",
    };

    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "github-releases.githubusercontent.com",
        "objects.githubusercontent.com",
        "raw.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public static UpdateNetworkOptions Normalize(UpdateNetworkOptions? options)
    {
        options ??= new UpdateNetworkOptions();
        var routes = new List<UpdateUrlRoute>();
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directAdded = false;

        foreach (var route in options.UrlRoutes ?? Array.Empty<UpdateUrlRoute>())
        {
            if (route.Priority is < 0 or > 10)
            {
                throw new InvalidDataException("更新线路优先级必须在 0 到 10 之间。");
            }

            if (route.IsDirect)
            {
                if (!directAdded)
                {
                    routes.Add(UpdateUrlRoute.Direct with { Priority = route.Priority });
                    directAdded = true;
                }

                continue;
            }

            var prefix = NormalizePrefix(route.BaseUrl);
            if (prefixes.Add(prefix))
            {
                routes.Add(route with { BaseUrl = prefix, IsDirect = false });
            }
        }

        if (!directAdded)
        {
            routes.Add(UpdateUrlRoute.Direct);
        }

        return options with
        {
            UrlRoutes = routes,
            HttpProxy = NormalizeHttpProxy(options.HttpProxy),
        };
    }

    public static IReadOnlyList<UpdateRequestRoute> CreateRoutes(Uri originalUri, UpdateNetworkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(originalUri);
        if (!originalUri.IsAbsoluteUri || !AllowedOriginalHosts.Contains(originalUri.Host))
        {
            throw new InvalidDataException("请求 URL 必须来自允许的 GitHub 域名。");
        }

        var normalized = Normalize(options);
        var routes = normalized.UrlRoutes
            .Select((route, index) => (route, index))
            .Where(item => item.route.Priority > 0)
            .OrderByDescending(item => item.route.Priority)
            .ThenBy(item => item.index)
            .Select(item => item.route.IsDirect
                ? new UpdateRequestRoute("直连", originalUri)
                : new UpdateRequestRoute(item.route.BaseUrl!, new Uri($"{item.route.BaseUrl}/{originalUri.AbsoluteUri}")))
            .ToArray();
        return routes.Length > 0
            ? routes
            : throw new InvalidOperationException("没有可用的更新线路。");
    }

    public static bool IsAllowedResponseUri(Uri requestUri, Uri responseUri) =>
        string.Equals(requestUri.Host, responseUri.Host, StringComparison.OrdinalIgnoreCase) ||
        AllowedRedirectHosts.Contains(responseUri.Host);

    public static HttpClient CreateHttpClient(UpdateNetworkOptions? options)
    {
        var normalized = Normalize(options);
        var handler = new HttpClientHandler();
        if (normalized.HttpProxy is not null)
        {
            handler.Proxy = new WebProxy(normalized.HttpProxy);
            handler.UseProxy = true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static string NormalizePrefix(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("URL 前缀线路必须是不含凭据、查询或片段的 HTTP/HTTPS 绝对地址。");
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string? NormalizeHttpProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("HTTP 出网代理必须是不含凭据和路径的 http://host:port 地址。");
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}

public sealed record UpdateRequestRoute(string Name, Uri RequestUri);
