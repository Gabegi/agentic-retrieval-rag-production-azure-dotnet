using System.Net;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// 429 backoff for the Zenya HttpClient pipeline. Limits and retry headers are undocumented
// (D155 §5) and the sync is an N+1 call shape - one listing page, then one metadata GET per
// document - so this backs off defensively and counts, and A8 measures.
//
// Policy: on 429, wait Retry-After if Zenya sends one (delta-seconds or HTTP-date), otherwise
// RetryBaseDelay * 2^attempt; give up after MaxRetries and return the 429 to the caller, who
// surfaces it as ZenyaApiException. Only 429 is retried - a 5xx from Zenya carries an
// error_code to quote to their support (D155 §5) and is not something a retry loop should hide.
public sealed class ZenyaRetryHandler : DelegatingHandler
{
    private readonly ZenyaOptions _options;
    private readonly ILogger<ZenyaRetryHandler> _logger;
    private readonly TimeProvider _time;
    private int _throttledResponses;

    public ZenyaRetryHandler(ZenyaOptions options, ILogger<ZenyaRetryHandler> logger, TimeProvider? time = null)
    {
        _options = options;
        _logger  = logger;
        _time    = time ?? TimeProvider.System;
    }

    // Total 429s seen over the handler's lifetime - the number A8 records.
    public int ThrottledResponses => Volatile.Read(ref _throttledResponses);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            Interlocked.Increment(ref _throttledResponses);
            if (attempt >= _options.MaxRetries)
            {
                _logger.LogWarning("Zenya 429 on {Method} {Path}: giving up after {Attempts} retries",
                    request.Method, request.RequestUri?.AbsolutePath, attempt);
                return response;
            }

            var delay = RetryAfter(response) ?? _options.RetryBaseDelay * Math.Pow(2, attempt);
            _logger.LogWarning("Zenya 429 on {Method} {Path}: retry {Attempt}/{Max} after {Delay}",
                request.Method, request.RequestUri?.AbsolutePath, attempt + 1, _options.MaxRetries, delay);
            response.Dispose();
            await Task.Delay(delay, _time, ct);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}
