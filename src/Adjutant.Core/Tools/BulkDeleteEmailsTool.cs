using System.ComponentModel;
using System.Text.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Adjutant.Core.Tools;

/// <summary>
/// MCP tool for deleting multiple emails in a single batch operation
/// </summary>
[McpServerToolType]
public sealed class BulkDeleteEmailsTool(
    [FromKeyedServices(McpAccountRegistry.ServiceKey)] IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<BulkDeleteEmailsTool> logger)
{
    private static readonly SemaphoreSlim Throttle = new(10);
    private const int MaxBatchSize = 50;

    [McpServerTool, Description("Delete multiple emails in a single batch operation. More efficient than calling delete_email repeatedly.")]
    public async Task<string> BulkDeleteEmails(
        [Description("Array of emails to delete, each with 'accountId' and 'emailId'. Maximum 50 items. Obtain values from get_emails or search_emails.")] BulkEmailItem[] items)
    {
        logger.LogInformation("Bulk deleting emails");

        if (items == null || items.Length == 0)
            throw new McpException("items array must not be empty");

        if (items.Length > MaxBatchSize)
            throw new McpException($"Batch size {items.Length} exceeds maximum of {MaxBatchSize}");

        // Validate all items have required fields
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.AccountId) || string.IsNullOrEmpty(item.EmailId))
                throw new McpException("Each item must have 'accountId' and 'emailId' fields");
        }

        try
        {
            // Resolve accounts once per unique accountId
            var accounts = await ToolGuard.ResolveBulkAccountsAsync(accountRegistry, items);

            var results = await Task.WhenAll(items.Select(async item =>
            {
                await Throttle.WaitAsync();
                try
                {
                    var (account, error) = accounts[item.AccountId];
                    if (account is null)
                        return new BulkResultItem(item.EmailId, item.AccountId, false, error);

                    var provider = providerFactory.GetProvider(account.Provider);
                    await provider.DeleteEmailAsync(item.AccountId, item.EmailId, CancellationToken.None);
                    return new BulkResultItem(item.EmailId, item.AccountId, true, null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error deleting email {EmailId} in account {AccountId}", item.EmailId, item.AccountId);
                    return new BulkResultItem(item.EmailId, item.AccountId, false, ToolGuard.DescribeItemFailure("delete email", ex));
                }
                finally
                {
                    Throttle.Release();
                }
            }));

            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);

            logger.LogInformation("Bulk delete complete: {Succeeded} succeeded, {Failed} failed out of {Total}",
                succeeded, failed, results.Length);

            return JsonSerializer.Serialize(new
            {
                totalRequested = results.Length,
                succeeded,
                failed,
                results = results.Select(r => r.Success
                    ? new { r.EmailId, r.AccountId, r.Success, error = (string?)null }
                    : new { r.EmailId, r.AccountId, r.Success, error = r.Error })
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in bulk_delete_emails tool");
            throw ToolGuard.Failure("bulk delete emails", ex);
        }
    }

    private sealed record BulkResultItem(string EmailId, string AccountId, bool Success, string? Error);
}
