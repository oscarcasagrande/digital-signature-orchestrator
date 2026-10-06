using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Artifacts;

namespace Orchestrator.Infrastructure.Storage;

public sealed class S3Options
{
    public string Endpoint { get; set; } = "http://localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string Bucket { get; set; } = "signature-artifacts";
    public string Region { get; set; } = "us-east-1";
}

/// <summary>S3-compatible store (MinIO locally). Uses only the S3 API so the backend stays swappable.</summary>
public sealed class S3ArtifactStore : IArtifactStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private volatile bool _bucketReady;

    public S3ArtifactStore(IOptions<S3Options> options)
    {
        var o = options.Value;
        _bucket = o.Bucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
        {
            ServiceURL = o.Endpoint, ForcePathStyle = true, AuthenticationRegion = o.Region
        });
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        using var ms = new MemoryStream(content);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, InputStream = ms, ContentType = contentType, AutoCloseStream = false,
            UseChunkEncoding = false
        }, ct);
    }

    public async Task<StoredObject> GetAsync(string key, CancellationToken ct)
    {
        var resp = await _client.GetObjectAsync(_bucket, key, ct);
        return new StoredObject(resp.ResponseStream, resp.Headers.ContentType, resp.ContentLength);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _client.DeleteObjectAsync(_bucket, key, ct); // S3 delete is idempotent

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;
            try { await _client.PutBucketAsync(_bucket, ct); }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
            _bucketReady = true;
        }
        finally { _bucketLock.Release(); }
    }

    public void Dispose() => _client.Dispose();
}
