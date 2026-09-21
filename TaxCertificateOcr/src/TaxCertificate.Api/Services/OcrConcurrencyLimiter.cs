using Microsoft.Extensions.Options;
using TaxCertificate.Api.Configuration;

namespace TaxCertificate.Api.Services;

/// <summary>
/// Bounds how many analyses are in flight. Without this, ten simultaneous uploads would each
/// hold a buffered request body while queuing behind a single-threaded OCR engine, which is
/// exactly how an 8 GB machine runs out of memory.
/// </summary>
public sealed class OcrConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _queueTimeout;

    public OcrConcurrencyLimiter(IOptions<UploadOptions> options)
    {
        var value = options.Value;
        var permits = Math.Max(1, value.MaxConcurrentAnalyses);
        _semaphore = new SemaphoreSlim(permits, permits);
        _queueTimeout = TimeSpan.FromSeconds(Math.Max(1, value.ConcurrencyQueueTimeoutSeconds));
    }

    /// <summary>Returns a disposable lease, or null when the queue timeout elapsed first.</summary>
    public async Task<IDisposable?> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            // Null out first so a double-dispose cannot release the semaphore twice.
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
