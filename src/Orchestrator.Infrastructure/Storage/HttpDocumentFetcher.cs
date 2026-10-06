using System.Net;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Providers;

namespace Orchestrator.Infrastructure.Storage;

/// <summary>Fetches the source document with time and size limits (http/https only).</summary>
public sealed class HttpDocumentFetcher(HttpClient http) : IDocumentFetcher
{
    public async Task<FetchedDocument> FetchAsync(Uri source, long maxBytes, CancellationToken ct)
    {
        if (source.Scheme is not ("http" or "https"))
            throw new OperationException("Only http(s) document sources are supported", transient: false);
        try
        {
            using var resp = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var transient = Orchestrator.Application.Resilience.ErrorClassifier.FromHttpStatus((int)resp.StatusCode) == Orchestrator.Domain.StateMachines.ErrorClass.TRANSIENT;
                throw new OperationException($"Document source returned HTTP {(int)resp.StatusCode}", transient);
            }
            if (resp.Content.Headers.ContentLength > maxBytes)
                throw new OperationException($"Document exceeds the {maxBytes} byte limit", transient: false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > maxBytes)
                    throw new OperationException($"Document exceeds the {maxBytes} byte limit", transient: false);
            }
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new FetchedDocument(ms.ToArray(), contentType);
        }
        catch (HttpRequestException ex) when (ex.InnerException is Orchestrator.Application.Callbacks.SsrfBlockedException blocked)
        {
            throw new OperationException(blocked.Message, transient: false);
        }
        catch (HttpRequestException ex)
        {
            throw new OperationException($"Document source unreachable: {ex.Message}", transient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OperationException("Document source timed out", transient: true);
        }
    }
}
