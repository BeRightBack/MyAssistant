using System.Net.Http;

namespace MyAssistant.Abstractions.Services;

public static class RetryHelper
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxRetries = 3,
        TimeSpan? initialDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        var delay = initialDelay ?? TimeSpan.FromSeconds(1);
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt == maxRetries)
                    break;

                if (shouldRetry != null && !shouldRetry(ex))
                    break;

                if (ex is HttpRequestException httpEx &&
                    httpEx.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden or
                    System.Net.HttpStatusCode.NotFound)
                    break;

                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }

        throw lastException!;
    }

    public static async Task ExecuteAsync(
        Func<Task> action,
        int maxRetries = 3,
        TimeSpan? initialDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<object?>(async () =>
        {
            await action();
            return null;
        }, maxRetries, initialDelay, shouldRetry, cancellationToken);
    }
}
