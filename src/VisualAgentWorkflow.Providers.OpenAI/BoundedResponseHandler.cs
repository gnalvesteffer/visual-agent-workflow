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
            var content = new ByteArrayContent(buffer.ToArray());
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
}
