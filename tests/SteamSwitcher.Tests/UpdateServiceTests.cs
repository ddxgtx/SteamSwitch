using System.Net;
using System.Text;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class UpdateServiceTests
{
    [TestMethod]
    public async Task CheckForUpdate_ReturnsNewRelease()
    {
        using var temp = new TemporaryDirectory();
        var expectedDigest = new string('A', 64);
        var json = $$"""
        {
          "tag_name": "v99.0.0",
          "body": "notes",
          "html_url": "https://github.com/ddxgtx/SteamSwitch/releases/tag/v99.0.0",
          "assets": [{
            "name": "SteamSwitch-v99.0.0-win-x64-setup.exe",
            "browser_download_url": "https://github.com/ddxgtx/SteamSwitch/releases/download/v99.0.0/setup.exe",
            "size": 1024,
            "digest": "sha256:{{expectedDigest}}"
          }]
        }
        """;
        using var service = new UpdateService(
            new StubHandler(_ => JsonResponse(HttpStatusCode.OK, json)),
            temp.Combine("rate-limit.json"));

        var update = await service.CheckForUpdateAsync();

        Assert.IsNotNull(update);
        Assert.AreEqual("99.0.0", update.Version);
        Assert.AreEqual(expectedDigest, update.ExpectedSha256);
    }

    [TestMethod]
    public async Task BetaChannel_SelectsHighestNonDraftRelease()
    {
        using var temp = new TemporaryDirectory();
        var requestedPath = "";
        var json = """
        [
          {
            "tag_name": "v100.0.0",
            "body": "draft",
            "html_url": "https://example.invalid/draft",
            "draft": true,
            "prerelease": true,
            "assets": []
          },
          {
            "tag_name": "v99.1.0",
            "body": "beta notes",
            "html_url": "https://github.com/ddxgtx/SteamSwitch/releases/tag/v99.1.0",
            "draft": false,
            "prerelease": true,
            "assets": []
          },
          {
            "tag_name": "v99.0.0",
            "body": "stable notes",
            "html_url": "https://github.com/ddxgtx/SteamSwitch/releases/tag/v99.0.0",
            "draft": false,
            "prerelease": false,
            "assets": []
          }
        ]
        """;
        using var service = new UpdateService(
            new StubHandler(request =>
            {
                requestedPath = request.RequestUri?.PathAndQuery ?? "";
                return JsonResponse(HttpStatusCode.OK, json);
            }),
            temp.Combine("rate-limit.json"));

        var update = await service.CheckForUpdateAsync(includePrereleases: true);

        Assert.IsNotNull(update);
        Assert.AreEqual("99.1.0", update.Version);
        Assert.IsTrue(update.IsPrerelease);
        StringAssert.Contains(requestedPath, "/releases?per_page=20");
    }

    [TestMethod]
    public async Task RateLimit_IsReportedAndPersistedWithoutRetrying()
    {
        using var temp = new TemporaryDirectory();
        var statePath = temp.Combine("rate-limit.json");
        var calls = 0;
        var reset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString();
        using (var service = new UpdateService(new StubHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset);
            return response;
        }), statePath))
        {
            var error = await Assert.ThrowsExactlyAsync<UpdateCheckException>(() => service.CheckForUpdateAsync());
            Assert.IsNotNull(error.RetryAfter);
        }

        using (var service = new UpdateService(new StubHandler(_ =>
        {
            calls++;
            return JsonResponse(HttpStatusCode.OK, "{}");
        }), statePath))
        {
            await Assert.ThrowsExactlyAsync<UpdateCheckException>(() => service.CheckForUpdateAsync());
        }
        Assert.AreEqual(1, calls, "Persisted backoff should prevent another GitHub request.");
    }

    [TestMethod]
    public async Task NetworkFailure_IsNotReportedAsLatestVersion()
    {
        using var temp = new TemporaryDirectory();
        using var service = new UpdateService(
            new StubHandler(_ => throw new HttpRequestException("offline")),
            temp.Combine("rate-limit.json"));

        var error = await Assert.ThrowsExactlyAsync<UpdateCheckException>(() => service.CheckForUpdateAsync());
        StringAssert.Contains(error.Message, "无法连接 GitHub");
    }

    [TestMethod]
    public async Task Timeout_IsReportedAsUpdateCheckFailure()
    {
        using var temp = new TemporaryDirectory();
        using var service = new UpdateService(
            new StubHandler(_ => throw new TaskCanceledException("timeout")),
            temp.Combine("rate-limit.json"));

        var error = await Assert.ThrowsExactlyAsync<UpdateCheckException>(() => service.CheckForUpdateAsync());
        StringAssert.Contains(error.Message, "超时");
    }
    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
