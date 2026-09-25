using System.Diagnostics;
using GGdown.Settings;

namespace GGdown.Tests.Settings;

public class ProxyConfigTests
{
    [Fact]
    public void Empty_host_or_invalid_port_is_disabled()
    {
        Assert.False(new ProxyConfig().IsEnabled);
        Assert.Null(new ProxyConfig().ToUrl());
        Assert.False(new ProxyConfig("http", "127.0.0.1", 0).IsEnabled);
        Assert.False(new ProxyConfig("http", "127.0.0.1", 70000).IsEnabled);
        Assert.False(new ProxyConfig("http", "  ", 7890).IsEnabled);
    }

    [Fact]
    public void ToUrl_builds_scheme_host_port()
    {
        Assert.Equal("http://127.0.0.1:7890",
            new ProxyConfig("http", "127.0.0.1", 7890).ToUrl());
        Assert.Equal("socks5h://127.0.0.1:1080",
            new ProxyConfig("socks5h", "127.0.0.1", 1080).ToUrl());
        Assert.Equal("https://proxy.example:443",
            new ProxyConfig("HTTPS", "proxy.example", 443).ToUrl());
    }

    [Fact]
    public void ToUrl_rewrites_local_https_and_v2rayn_socks_port()
    {
        // 本机 https:// 会 TLS 握手 HTTP/SOCKS 代理 → SSLEOF / ProxyError
        Assert.Equal("http://127.0.0.1:7890",
            new ProxyConfig("https", "127.0.0.1", 7890).ToUrl());
        Assert.Equal("http://localhost:10809",
            new ProxyConfig("https", "localhost", 10809).ToUrl());
        // v2rayN 默认 SOCKS 口 10808
        Assert.Equal("socks5h://127.0.0.1:10808",
            new ProxyConfig("https", "127.0.0.1", 10808).ToUrl());
        Assert.Equal("socks5h://127.0.0.1:10808",
            new ProxyConfig("http", "127.0.0.1", 10808).ToUrl());
    }

    [Fact]
    public void ToUrl_encodes_userinfo()
    {
        Assert.Equal("socks5://user:p%40ss@127.0.0.1:1080",
            new ProxyConfig("socks5", "127.0.0.1", 1080, "user", "p@ss").ToUrl());
    }

    [Fact]
    public void Unknown_scheme_falls_back_to_http()
    {
        Assert.Equal("http://127.0.0.1:1",
            new ProxyConfig("ftp", "127.0.0.1", 1).ToUrl());
    }

    [Fact]
    public void ApplyTo_sets_proxy_env_when_url_present()
    {
        var psi = new ProcessStartInfo();
        ProxyConfig.ApplyTo(psi, "socks5h://127.0.0.1:1080");
        Assert.Equal("socks5h://127.0.0.1:1080", psi.Environment["HTTP_PROXY"]);
        Assert.Equal("socks5h://127.0.0.1:1080", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("socks5h://127.0.0.1:1080", psi.Environment["ALL_PROXY"]);
    }

    [Fact]
    public void MergeIntoOptions_sets_extractor_and_downloader_proxy()
    {
        var opts = new Dictionary<string, object?>
        {
            ["extractor"] = new Dictionary<string, object?> { ["twitter"] = new Dictionary<string, object?>() },
            ["base-directory"] = @"D:\x",
        };
        var merged = ProxyConfig.MergeIntoOptions(opts, "http://127.0.0.1:7890");
        var extractor = Assert.IsType<Dictionary<string, object?>>(merged["extractor"]);
        Assert.Equal("http://127.0.0.1:7890", extractor["proxy"]);
        Assert.True(extractor.ContainsKey("twitter"));
        var downloader = Assert.IsType<Dictionary<string, object?>>(merged["downloader"]);
        Assert.Equal("http://127.0.0.1:7890", downloader["proxy"]);
        Assert.Same(opts, ProxyConfig.MergeIntoOptions(opts, null));
    }

    [Fact]
    public void ApplyTo_strips_inherited_proxy_when_url_blank()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["HTTP_PROXY"] = "socks5://127.0.0.1:1";
        psi.Environment["HTTPS_PROXY"] = "socks5://127.0.0.1:1";
        psi.Environment["ALL_PROXY"] = "socks5://127.0.0.1:1";
        ProxyConfig.ApplyTo(psi, null);
        Assert.False(psi.Environment.ContainsKey("HTTP_PROXY"));
        Assert.False(psi.Environment.ContainsKey("HTTPS_PROXY"));
        Assert.False(psi.Environment.ContainsKey("ALL_PROXY"));
    }
}
