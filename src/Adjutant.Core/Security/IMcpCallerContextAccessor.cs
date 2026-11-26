namespace Adjutant.Core.Security;

/// <summary>Provides the credential scope for the current MCP call.</summary>
public interface IMcpCallerContextAccessor
{
    McpCallerContext Current { get; }
}

public sealed record McpCallerContext(string? UserId, string? CredentialId, IReadOnlyList<string> AllowedAccounts)
{
    public static McpCallerContext Unrestricted { get; } = new(null, null, []);

    public bool AllowsAccount(string accountId) =>
        AllowedAccounts.Count == 0 || AllowedAccounts.Contains(accountId, StringComparer.OrdinalIgnoreCase);
}

internal sealed class UnrestrictedMcpCallerContextAccessor : IMcpCallerContextAccessor
{
    public McpCallerContext Current => McpCallerContext.Unrestricted;
}
