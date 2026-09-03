using System.IO.Hashing;
using System.Text;
using DBToRestAPI.Cache;

namespace DBToRestAPI.Tests;

/// <summary>
/// Tests for the invalidator-value hash used to build cache keys (StringExtensions.ToXxHash3).
///
/// Every invalidator value is hashed before it enters a cache key. That is what lets a value of any
/// length (a bearer token, say) be nominated safely, and what stops a caller forging a key segment
/// with "|" or "=". The hash has a stack fast path for short strings and a pooled-buffer path for
/// long ones; both must agree with a plain reference computation.
/// </summary>
public class CacheKeyHashingTests
{
    private static ulong Reference(string text) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("")]
    [InlineData("tenant=a")]
    [InlineData("784-1990-1234567-1")]
    [InlineData("ünïcödé — 😀 عربى")] // multi-byte: bytes written < max byte count
    public void ShortValue_MatchesReferenceHash(string text)
    {
        Assert.Equal(Reference(text), text.ToXxHash3());
    }

    [Fact]
    public void ValuesAroundTheStackBudget_MatchReferenceHash()
    {
        // GetMaxByteCount(n) = 3n + 3: n = 340 is 1023 bytes (stack path), n = 341 is 1026 (rented).
        foreach (var length in new[] { 339, 340, 341, 342, 1024, 4096 })
        {
            var text = new string('x', length);
            Assert.Equal(Reference(text), text.ToXxHash3());
        }
    }

    [Fact]
    public void VeryLongValue_HashesWithoutExhaustingTheStack()
    {
        // The previous implementation stack-allocated 3n+3 bytes for ANY input; a value this
        // size would have overflowed the thread's stack and taken the whole process down.
        var text = new string('t', 2_000_000);

        var hash = text.ToXxHash3();

        Assert.Equal(Reference(text), hash);
        Assert.Equal(hash, text.ToXxHash3()); // deterministic across calls
    }

    [Fact]
    public void HashedValue_IsDelimiterFreeAndDistinct()
    {
        // "|" and "=" are legal inside header and query-string values, so raw embedding let
        // ?tenant=a|user=victim assemble the same key text as ?tenant=a&user=victim.
        var forged = "a|user=victim".ToXxHash3().ToString();

        Assert.Matches("^[0-9]+$", forged); // a ulong: no "|" or "=" can survive into the key
        Assert.NotEqual("a".ToXxHash3(), "a|user=victim".ToXxHash3());
    }

    [Fact]
    public void DifferentLongValues_ProduceDifferentHashes()
    {
        // Dropping over-long values (the old behaviour) collapsed two callers onto one entry.
        var tokenA = "Bearer " + new string('a', 1500);
        var tokenB = "Bearer " + new string('a', 1499) + "b";

        Assert.NotEqual(tokenA.ToXxHash3(), tokenB.ToXxHash3());
    }
}
