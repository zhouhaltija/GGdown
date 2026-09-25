using System.Diagnostics;

namespace GGdown.Settings;

/// <summary>
/// 全局代理配置。Host 为空或端口非法视为关闭（直连）。
/// ToUrl 供 runner 环境变量与 gallery-dl config 使用。
/// </summary>
public sealed record ProxyConfig(
    string Scheme = "http",
    string Host = "",
    int Port = 0,
    string? Username = null,
    string? Password = null)
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "socks5", "socks5h",
    };

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    public string? ToUrl()
    {
        if (!IsEnabled) return null;
        var scheme = AllowedSchemes.Contains(Scheme) ? Scheme.ToLowerInvariant() : "http";
        var host = Host.Trim();
        scheme = NormalizeLocalScheme(scheme, host, Port);
        if (string.IsNullOrEmpty(Username))
            return $"{scheme}://{host}:{Port}";
        var user = Uri.EscapeDataString(Username);
        var pass = Uri.EscapeDataString(Password ?? "");
        return $"{scheme}://{user}:{pass}@{host}:{Port}";
    }

    /// <summary>
    /// 本机代理几乎从不是 TLS 服务端：https://127.0.0.1 会 SSL 握手失败（SSLEOF）。
    /// v2rayN 默认 SOCKS 口是 10808，误选 http/https 时改成 socks5h。
    /// </summary>
    private static string NormalizeLocalScheme(string scheme, string host, int port)
    {
        if (!IsLoopback(host)) return scheme;
        if (port == 10808 && scheme is "http" or "https") return "socks5h";
        if (scheme == "https") return "http";
        return scheme;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.Ordinal);

    public static void ApplyTo(ProcessStartInfo psi, string? proxyUrl)
    {
        // 子进程默认继承父进程环境。空配置必须直连，故先剥掉 Clash 等注入的代理变量。
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
        {
            psi.Environment.Remove(key);
            psi.EnvironmentVariables.Remove(key);
        }
        if (string.IsNullOrWhiteSpace(proxyUrl)) return;
        psi.Environment["HTTP_PROXY"] = proxyUrl;
        psi.Environment["HTTPS_PROXY"] = proxyUrl;
        psi.Environment["ALL_PROXY"] = proxyUrl;
    }

    public static IReadOnlyDictionary<string, object?> MergeIntoOptions(
        IReadOnlyDictionary<string, object?> options, string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return options;
        var d = new Dictionary<string, object?>(options);
        d["extractor"] = MergeMap(d.TryGetValue("extractor", out var ex) ? ex : null, proxyUrl);
        d["downloader"] = MergeMap(d.TryGetValue("downloader", out var dl) ? dl : null, proxyUrl);
        return d;
    }

    private static Dictionary<string, object?> MergeMap(object? existing, string proxyUrl)
    {
        var map = existing is IReadOnlyDictionary<string, object?> src
            ? new Dictionary<string, object?>(src)
            : [];
        map["proxy"] = proxyUrl;
        return map;
    }
}
