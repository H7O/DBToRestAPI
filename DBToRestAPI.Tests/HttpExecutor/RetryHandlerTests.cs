using DBToRestAPI.Services.HttpExecutor.Internal;
using DBToRestAPI.Services.HttpExecutor.Models;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Unit tests for retry handling functionality.
/// </summary>
public class RetryHandlerTests
{
    [Fact]
    public void Constructor_DefaultConfig_EnsuresAtLeastOneAttempt()
    {
        // Arrange & Act
        var handler = new RetryHandler(null, 0);

        // Assert - Always need at least 1 attempt for the initial request
        Assert.Equal(1, handler.MaxAttempts);
    }

    [Fact]
    public void Constructor_WithDefaultAttempts_UsesDefaultAttempts()
    {
        // Arrange & Act
        var handler = new RetryHandler(null, 3);

        // Assert
        Assert.Equal(3, handler.MaxAttempts);
    }


    [Fact]
    public void Constructor_WithConfig_UsesConfigValues()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 5,
            DelayMs = 2000
        };

        // Act
        var handler = new RetryHandler(config, 0);

        // Assert
        Assert.Equal(5, handler.MaxAttempts);
    }

    [Fact]
    public void ShouldRetry_FirstAttempt_ReturnsTrueForRetryableStatusCode()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetry(503, 1);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldRetry_LastAttempt_ReturnsFalse()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetry(503, 3);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldRetry_NonRetryableStatusCode_ReturnsFalse()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetry(404, 1);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void ShouldRetry_DefaultRetryStatusCodes_ReturnsTrue(int statusCode)
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetry(statusCode, 1);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldRetry_CustomRetryStatusCodes_RespectsConfig()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 3,
            RetryStatusCodes = [429, 503]
        };
        var handler = new RetryHandler(config, 0);

        // Act & Assert
        Assert.True(handler.ShouldRetry(429, 1));
        Assert.True(handler.ShouldRetry(503, 1));
        Assert.False(handler.ShouldRetry(500, 1)); // Not in custom list
        Assert.False(handler.ShouldRetry(502, 1)); // Not in custom list
    }

    [Fact]
    public void ShouldRetryOnException_HttpRequestException_ReturnsTrue()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetryOnException(new HttpRequestException(), 1);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldRetryOnException_LastAttempt_ReturnsFalse()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetryOnException(new HttpRequestException(), 3);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldRetryOnException_TimeoutException_ReturnsTrue()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);
        var exception = new TaskCanceledException("timeout", new TimeoutException());

        // Act
        var result = handler.ShouldRetryOnException(exception, 1);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldRetryOnException_OtherException_ReturnsFalse()
    {
        // Arrange
        var config = new HttpExecutorRetry { MaxAttempts = 3 };
        var handler = new RetryHandler(config, 0);

        // Act
        var result = handler.ShouldRetryOnException(new InvalidOperationException(), 1);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetDelay_FirstAttempt_ReturnsBaseDelay()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 3,
            DelayMs = 1000,
            ExponentialBackoff = true
        };
        var handler = new RetryHandler(config, 0);

        // Act
        var delay = handler.GetDelay(1);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(1000), delay);
    }

    [Fact]
    public void GetDelay_WithExponentialBackoff_DoublesDelay()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 5,
            DelayMs = 1000,
            ExponentialBackoff = true
        };
        var handler = new RetryHandler(config, 0);

        // Act & Assert
        Assert.Equal(TimeSpan.FromMilliseconds(1000), handler.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), handler.GetDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), handler.GetDelay(3));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), handler.GetDelay(4));
    }

    [Fact]
    public void GetDelay_WithoutExponentialBackoff_ReturnsConstantDelay()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 5,
            DelayMs = 1000,
            ExponentialBackoff = false
        };
        var handler = new RetryHandler(config, 0);

        // Act & Assert
        Assert.Equal(TimeSpan.FromMilliseconds(1000), handler.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), handler.GetDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), handler.GetDelay(3));
    }

    [Fact]
    public void GetDelay_ExponentialBackoff_CapsAt60Seconds()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 10,
            DelayMs = 10000, // 10 seconds base
            ExponentialBackoff = true
        };
        var handler = new RetryHandler(config, 0);

        // Act - attempt 4 would be 10 * 8 = 80 seconds, should cap at 60
        var delay = handler.GetDelay(4);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(60000), delay);
    }

    [Fact]
    public async Task WaitAsync_WaitsForDelay()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 3,
            DelayMs = 100 // Short delay for testing
        };
        var handler = new RetryHandler(config, 0);

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await handler.WaitAsync(1, CancellationToken.None);
        sw.Stop();

        // Assert - should have waited at least 90ms (allowing some tolerance)
        Assert.True(sw.ElapsedMilliseconds >= 90);
    }

    [Fact]
    public async Task WaitAsync_CancellationToken_Cancels()
    {
        // Arrange
        var config = new HttpExecutorRetry
        {
            MaxAttempts = 3,
            DelayMs = 10000 // Long delay
        };
        var handler = new RetryHandler(config, 0);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => handler.WaitAsync(1, cts.Token));
    }
}
