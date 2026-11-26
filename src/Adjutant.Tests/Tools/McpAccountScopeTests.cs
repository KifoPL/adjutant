using System.Reflection;
using System.Text.Json;
using Adjutant.Core.Configuration;
using Adjutant.Core.Models;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Rocks;

namespace Adjutant.Tests.Tools;

[TestClass]
public class McpAccountScopeTests
{
    [TestMethod]
    public void Tools_InjectTheScopedAccountRegistry()
    {
        var tools = typeof(ListAccountsTool).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false));

        foreach (var tool in tools)
        {
            var registryParameters = tool.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Where(parameter => parameter.ParameterType == typeof(IAccountRegistry));

            foreach (var parameter in registryParameters)
                Assert.AreEqual(McpAccountRegistry.ServiceKey, parameter.GetCustomAttribute<FromKeyedServicesAttribute>()?.Key,
                    $"{tool.Name} must use the credential-scoped account registry.");
        }
    }

    [TestMethod]
    [DataRow("microsoft365")]
    [DataRow("outlook.com")]
    [DataRow("google")]
    [DataRow("imap")]
    [DataRow("exchange")]
    [DataRow("dav")]
    [DataRow("ics")]
    [DataRow("json")]
    public async Task Tools_DenyAnExplicitExcludedAccountBeforeProviderSelection(string provider)
    {
        var factory = new IProviderServiceFactoryCreateExpectations();
        using var services = CreateServices(factory, TestData.CreateAccount(id: "excluded", provider: provider));
        const string error = "This credential is not authorized for account 'excluded'.";

        var read = await Assert.ThrowsExactlyAsync<McpAccountAccessException>(() =>
            services.GetRequiredService<GetEmailDetailsTool>().GetEmailDetails("excluded", "message"));
        var search = await Assert.ThrowsExactlyAsync<McpAccountAccessException>(() =>
            services.GetRequiredService<SearchEmailsTool>().SearchEmails("query", "excluded"));
        var send = await Assert.ThrowsExactlyAsync<McpAccountAccessException>(() =>
            services.GetRequiredService<SendEmailTool>().SendEmail(["recipient@example.com"], "Subject", accountId: "excluded"));
        var calendar = await Assert.ThrowsExactlyAsync<McpAccountAccessException>(() =>
            services.GetRequiredService<DeleteEventTool>().DeleteEvent("event", "excluded"));
        var contacts = await Assert.ThrowsExactlyAsync<McpAccountAccessException>(() =>
            services.GetRequiredService<DeleteContactTool>().DeleteContact("excluded", "contact"));

        Assert.AreEqual(error, read.Message);
        Assert.AreEqual(error, search.Message);
        Assert.AreEqual(error, send.Message);
        Assert.AreEqual(error, calendar.Message);
        Assert.AreEqual(error, contacts.Message);
        factory.Verify();
    }

    [TestMethod]
    public async Task Tools_UseOnlyAllowedAccountsForListingSearchAndSendRouting()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.SearchEmailsAsync(
                "allowed", "test", Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>([TestData.CreateEmail(accountId: "allowed")]));
        provider.Setups.SendEmailAsync(
                "allowed", "recipient@example.com", "Subject", "", "html",
                Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("message"));
        var factory = new IProviderServiceFactoryCreateExpectations();
        factory.Setups.GetProvider("microsoft365").ReturnValue(provider.Instance()).ExpectedCallCount(2);

        using var services = CreateServices(factory,
            TestData.CreateAccount(id: "excluded", provider: "google"),
            TestData.CreateAccount(id: "disabled", provider: "google", enabled: false),
            TestData.CreateAccount(id: "allowed", domains: ["other.example"]));

        using var listing = JsonDocument.Parse(await services.GetRequiredService<ListAccountsTool>().ListAccounts(includeDisabled: true));
        var accounts = listing.RootElement.GetProperty("accounts");
        Assert.AreEqual(1, accounts.GetArrayLength());
        Assert.AreEqual("allowed", accounts[0].GetProperty("accountId").GetString());

        using var search = JsonDocument.Parse(await services.GetRequiredService<SearchEmailsTool>().SearchEmails("test"));
        var emails = search.RootElement.GetProperty("emails");
        Assert.AreEqual(1, emails.GetArrayLength());
        Assert.AreEqual("allowed", emails[0].GetProperty("accountId").GetString());
        Assert.IsFalse(search.RootElement.TryGetProperty("warnings", out var warnings) && warnings.ValueKind != JsonValueKind.Null);

        using var send = JsonDocument.Parse(await services.GetRequiredService<SendEmailTool>().SendEmail(["recipient@example.com"], "Subject"));
        Assert.AreEqual("allowed", send.RootElement.GetProperty("accountUsed").GetString());

        var adminAccounts = await services.GetRequiredService<IAccountRegistry>().GetAllAccountsAsync();
        Assert.AreEqual(3, adminAccounts.Count());
        factory.Verify();
        provider.Verify();
    }

    [TestMethod]
    public async Task BulkDeleteEmails_ReportsTheExcludedAccountAndKeepsAllowedItems()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.DeleteEmailAsync("allowed", "allowed-message", Arg.Any<CancellationToken>()).ReturnValue(Task.CompletedTask);
        var factory = new IProviderServiceFactoryCreateExpectations();
        factory.Setups.GetProvider("microsoft365").ReturnValue(provider.Instance());

        using var services = CreateServices(factory,
            TestData.CreateAccount(id: "excluded", provider: "google"),
            TestData.CreateAccount(id: "allowed"));
        using var result = JsonDocument.Parse(await services.GetRequiredService<BulkDeleteEmailsTool>().BulkDeleteEmails(
        [
            new() { AccountId = "excluded", EmailId = "excluded-message" },
            new() { AccountId = "allowed", EmailId = "allowed-message" }
        ]));

        Assert.AreEqual(1, result.RootElement.GetProperty("succeeded").GetInt32());
        Assert.AreEqual(1, result.RootElement.GetProperty("failed").GetInt32());
        var items = result.RootElement.GetProperty("results");
        Assert.AreEqual("This credential is not authorized for account 'excluded'.", items[0].GetProperty("error").GetString());
        Assert.IsTrue(items[1].GetProperty("Success").GetBoolean());
        factory.Verify();
        provider.Verify();
    }

    private static ServiceProvider CreateServices(IProviderServiceFactoryCreateExpectations factory, params AccountInfo[] accounts) =>
        new ServiceCollection()
            .AddLogging()
            .AddAdjutantCore()
            .Configure<AdjutantConfiguration>(options => options.Accounts = accounts.ToList())
            .AddSingleton<IMcpCallerContextAccessor, ScopedCaller>()
            .AddSingleton(factory.Instance())
            .BuildServiceProvider();

    private sealed class ScopedCaller : IMcpCallerContextAccessor
    {
        public McpCallerContext Current { get; } = new("owner", "credential", ["ALLOWED"]);
    }
}
