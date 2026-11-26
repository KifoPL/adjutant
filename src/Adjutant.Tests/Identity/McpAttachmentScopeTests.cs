using System.Net;
using System.Net.Http.Headers;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Adjutant.Tests.Identity;

[TestClass]
[DoNotParallelize]
public class McpAttachmentScopeTests
{
    [TestMethod]
    public async Task KeyScope_AppliesWhenDownloadingAndDeletingStashedAttachments()
    {
        await using var app = new AdjutantApp();
        var keys = app.Services.GetRequiredService<IMcpKeyStore>();
        var (_, scopedKey) = keys.Create("scoped", allowedAccounts: ["allowed"]);
        var (_, unrestrictedKey) = keys.Create("unrestricted");
        var store = app.Services.GetRequiredService<IAttachmentStore>();
        var bytes = "test attachment"u8.ToArray();
        var excluded = store.Put("excluded.txt", "text/plain", bytes, sourceAccountId: "excluded")!;
        var allowed = store.Put("allowed.txt", "text/plain", bytes, sourceAccountId: "allowed")!;
        var uploaded = store.Put("uploaded.txt", "text/plain", bytes)!;

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scopedKey);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/attachments/{excluded.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync($"/attachments/{excluded.Id}")).StatusCode);
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{allowed.Id}"));
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{uploaded.Id}"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unrestrictedKey);
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{excluded.Id}"));
    }
}
