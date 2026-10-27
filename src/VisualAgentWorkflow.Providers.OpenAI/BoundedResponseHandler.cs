using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Providers.OpenAI;

/// <summary>Bound the buffered non-streaming protocol before the SDK parses it, including error bodies.</summary>
internal sealed class BoundedResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaximumBytes = 1_048_576;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Model response exceeds the 1 MiB transport budget.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, cancellationToken);
                if (count == 0) break;
                if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Model response exceeds the 1 MiB transport budget.");
                buffer.Write(chunk, 0, count);
            }
            var body = buffer.ToArray();
            CaptureCacheWriteUsage(body);
            var content = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void CaptureCacheWriteUsage(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return;
            if (TryGetNonNegativeCount(usage, "cache_write_input_tokens", out var count) ||
                usage.TryGetProperty("prompt_tokens_details", out var details) &&
                TryGetNonNegativeCount(details, "cache_write_tokens", out count))
                ProviderUsageCapture.RecordCacheWriteInputTokens(count);
        }
        catch (JsonException)
        {
            // The SDK reports malformed model responses itself; this observer only reads optional usage metadata.
        }
    }

    private static bool TryGetNonNegativeCount(JsonElement element, string name, out long count)
    {
        count = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out count) && count >= 0;
    }
}
