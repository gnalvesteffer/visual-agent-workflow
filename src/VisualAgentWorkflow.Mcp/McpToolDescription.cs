using System.Security.Cryptography;
using System.Text;

namespace VisualAgentWorkflow.Mcp;

public static class McpToolDescription
{
    public const int MaximumLength = 2048;

    public static string Normalize(string? description)
    {
        var normalized = description?.Trim() ?? "";
        if (normalized.Length > MaximumLength)
            throw new McpToolDescriptionException($"MCP tool descriptions cannot exceed {MaximumLength} characters.");
        return normalized;
    }

    public static string ComputeHash(string? description) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(description))));

    public static bool IsSupported(string? description)
    {
        try { Normalize(description); return true; }
        catch (McpToolDescriptionException) { return false; }
    }
}

public sealed class McpToolDescriptionException(string message) : InvalidOperationException(message);
