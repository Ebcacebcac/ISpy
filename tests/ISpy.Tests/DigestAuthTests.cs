using ISpy.Core.Isapi;
using Xunit;

namespace ISpy.Tests;

public class DigestAuthTests
{
    [Fact]
    public void Matches_the_rfc2617_worked_example()
    {
        // Straight from RFC 2617 §3.5 - if our arithmetic matches this, it matches every device.
        var challenge = DigestChallenge.Parse(
            "Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", " +
            "nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", " +
            "opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");

        Assert.NotNull(challenge);
        Assert.Equal("auth", challenge.Qop);

        var header = challenge.CreateAuthorization(
            "Mufasa", "Circle Of Life", "GET", "/dir/index.html",
            nonceCount: 1, clientNonce: "0a4f113b");

        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header);
        Assert.Contains("nc=00000001", header);
        Assert.Contains("uri=\"/dir/index.html\"", header);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header);
    }

    [Fact]
    public void A_challenge_without_qop_uses_the_legacy_response_form()
    {
        // Older Hikvision firmware omits qop; the response is MD5(HA1:nonce:HA2) with no nc/cnonce.
        var challenge = DigestChallenge.Parse("Digest realm=\"iVMS\", nonce=\"abc123\"");

        Assert.NotNull(challenge);
        Assert.Null(challenge.Qop);

        var header = challenge.CreateAuthorization("admin", "pw", "GET", "/ISAPI/System/deviceInfo", 1, "cn");

        Assert.DoesNotContain("nc=", header);
        Assert.DoesNotContain("cnonce", header);
        Assert.Contains("response=\"", header);
    }

    [Fact]
    public void Parsing_ignores_a_basic_only_challenge() =>
        Assert.Null(DigestChallenge.Parse("Basic realm=\"iVMS\""));

    [Fact]
    public void Parsing_survives_junk()
    {
        Assert.Null(DigestChallenge.Parse(null));
        Assert.Null(DigestChallenge.Parse(""));
        Assert.Null(DigestChallenge.Parse("Digest realm=\"only-realm\""));  // no nonce
    }

    [Fact]
    public void Algorithm_defaults_to_md5_when_unstated() =>
        Assert.Equal("MD5", DigestChallenge.Parse("Digest realm=\"r\", nonce=\"n\"")!.Algorithm);

    [Fact]
    public void Fields_are_parsed_regardless_of_spacing_and_order()
    {
        var challenge = DigestChallenge.Parse(
            "Digest  nonce=\"n1\",realm=\"r1\", algorithm=MD5 ,qop=\"auth\"");

        Assert.NotNull(challenge);
        Assert.Equal("r1", challenge.Realm);
        Assert.Equal("n1", challenge.Nonce);
        Assert.Equal("auth", challenge.Qop);
    }

    [Fact]
    public void Client_nonces_are_random_and_hex()
    {
        var a = DigestChallenge.NewClientNonce();
        var b = DigestChallenge.NewClientNonce();

        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]+$", a);
    }
}
