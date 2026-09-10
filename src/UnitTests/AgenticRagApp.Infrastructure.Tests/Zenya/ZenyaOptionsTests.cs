using AgenticRagApp.Infrastructure.Clients.Zenya;
using Microsoft.Extensions.Configuration;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// The three settings arrive from a variable group exactly as pasted; the first live run failed
// on a leading space and the base URL has been questioned twice (with vs without /api). These
// pin the normalisation so both questions stay answered.
[TestClass]
public class ZenyaOptionsTests
{
    private static IConfiguration Config(string? baseUrl, string? clientId = "cid", string? secret = "sec", string? scope = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ZenyaOptions.BaseUrlKey]      = baseUrl,
            [ZenyaOptions.ClientIdKey]     = clientId,
            [ZenyaOptions.ClientSecretKey] = secret,
            [ZenyaOptions.EntraScopeKey]   = scope,
        }).Build();

    [TestMethod]
    public void FromConfiguration_ScopeOnly_IsClientAssertionMode()
    {
        var options = ZenyaOptions.FromConfiguration(Config("https://t.zenya.work", secret: null, scope: " api://zenya/.default "));

        Assert.IsTrue(options.UsesClientAssertion);
        Assert.AreEqual("api://zenya/.default", options.EntraScope);
        Assert.IsNull(options.ClientSecret);
    }

    [TestMethod]
    public void FromConfiguration_SecretOnly_IsClientSecretMode()
    {
        var options = ZenyaOptions.FromConfiguration(Config("https://t.zenya.work", secret: "s", scope: null));

        Assert.IsFalse(options.UsesClientAssertion);
        Assert.AreEqual("s", options.ClientSecret);
    }

    [TestMethod]
    public void FromConfiguration_NeitherSecretNorScope_ThrowsNamingBothKeys()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ZenyaOptions.FromConfiguration(Config("https://t.zenya.work", secret: null, scope: null)));

        StringAssert.Contains(ex.Message, ZenyaOptions.EntraScopeKey);
        StringAssert.Contains(ex.Message, ZenyaOptions.ClientSecretKey);
    }

    [TestMethod]
    public void FromConfiguration_BothSecretAndScope_Throws()
    {
        // The two modes send different form fields to the same endpoint; a host must pick one so
        // a refusal can be read unambiguously.
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ZenyaOptions.FromConfiguration(Config("https://t.zenya.work", secret: "s", scope: "api://x/.default")));
    }

    [DataTestMethod]
    [DataRow("https://contoso.zenya.work")]
    [DataRow("https://contoso.zenya.work/")]
    [DataRow("https://contoso.zenya.work/api")]
    [DataRow("https://contoso.zenya.work/api/")]
    [DataRow("  https://contoso.zenya.work/api  ")]
    public void FromConfiguration_NormalisesBaseUrl_ToApiRootWithTrailingSlash(string given)
    {
        var options = ZenyaOptions.FromConfiguration(Config(given));

        Assert.AreEqual("https://contoso.zenya.work/api/", options.BaseUrl.ToString());
        // Relative endpoint paths must resolve beneath /api, not replace it.
        Assert.AreEqual("https://contoso.zenya.work/api/oauth/token", new Uri(options.BaseUrl, "oauth/token").ToString());
    }

    [TestMethod]
    public void FromConfiguration_TrimsClientIdAndSecret()
    {
        var options = ZenyaOptions.FromConfiguration(Config("https://t.zenya.work/api", " id-1 ", " s3cret\n"));

        Assert.AreEqual("id-1", options.ClientId);
        Assert.AreEqual("s3cret", options.ClientSecret);
    }

    [TestMethod]
    public void FromConfiguration_MissingSettings_NamesEveryMissingKey()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ZenyaOptions.FromConfiguration(Config(baseUrl: null, clientId: "  ", secret: "s")));

        StringAssert.Contains(ex.Message, ZenyaOptions.BaseUrlKey);
        StringAssert.Contains(ex.Message, ZenyaOptions.ClientIdKey);
        Assert.IsFalse(ex.Message.Contains(ZenyaOptions.ClientSecretKey), "a present key must not be reported missing");
    }

    [TestMethod]
    public void FromConfiguration_RelativeBaseUrl_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ZenyaOptions.FromConfiguration(Config("contoso.zenya.work/api")));
    }
}
