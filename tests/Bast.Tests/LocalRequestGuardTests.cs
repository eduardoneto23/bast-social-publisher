using Bast.Application;

namespace Bast.Tests;

public class LocalRequestGuardTests
{
    private const string Host = "127.0.0.1:5080";

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Local_script_without_browser_headers_is_allowed(string method) =>
        Assert.True(LocalRequestGuard.IsAllowed(method, Host, null, null));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void The_panel_itself_is_allowed(string method) =>
        Assert.True(LocalRequestGuard.IsAllowed(method, Host, "http://127.0.0.1:5080", "same-origin"));

    [Fact]
    public void Panel_opened_as_localhost_is_allowed() =>
        Assert.True(LocalRequestGuard.IsAllowed("POST", "localhost:5080", "http://localhost:5080", "same-origin"));

    [Theory]
    [InlineData("https://site-de-terceiros.example")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("http://localhost:5080")]
    [InlineData("null")]
    public void Another_origin_cannot_change_anything(string origin)
    {
        Assert.False(LocalRequestGuard.IsAllowed("POST", Host, origin, null));
        Assert.False(LocalRequestGuard.IsAllowed("DELETE", Host, origin, "cross-site"));
    }

    [Fact]
    public void Cross_site_request_is_refused_even_without_origin() =>
        Assert.False(LocalRequestGuard.IsAllowed("POST", Host, null, "cross-site"));

    [Fact]
    public void Same_site_but_other_port_is_refused() =>
        Assert.False(LocalRequestGuard.IsAllowed("POST", Host, null, "same-site"));

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public void Foreign_host_name_pointing_to_this_machine_is_refused(string method)
    {
        Assert.False(LocalRequestGuard.IsAllowed(method, "atacante.example:5080", null, null));
        Assert.False(LocalRequestGuard.IsAllowed(method, "127.0.0.1.atacante.example", null, null));
        Assert.False(LocalRequestGuard.IsAllowed(method, null, null, null));
    }

    [Fact]
    public void Reads_from_the_local_address_are_allowed() =>
        Assert.True(LocalRequestGuard.IsAllowed("GET", Host, "https://site-de-terceiros.example", "cross-site"));
}
