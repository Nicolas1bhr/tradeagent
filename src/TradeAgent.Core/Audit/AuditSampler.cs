using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TradeAgent.Core.Audit;

/// <summary>THE AUDIT LINE'S POLICY, as named constants the owner's rulings set (<c>U-org-audit-a</c>, seat A's ruling R2).</summary>
public static class AuditPolicy
{
    /// <summary>
    /// The share of accepted deliveries the line draws: one in four. It spends nothing in part a — every check on a sample is code — so
    /// the rate is bounded by nothing but the line's own minute.
    /// </summary>
    public const double SampleRate = 0.25;

    /// <summary>
    /// THE RATE AT WHICH THE LINE PLANTS IMPOSSIBLE ASSIGNMENTS IN THE OWNER'S REAL HOME: zero until M-org1 (the owner's Q3 of 2026-10-22,
    /// "traps in his real home at rate 0 until M-org1"). A constant, not a setting: no key, op, verb or board act can raise it. Until a later
    /// owner press does, the line plants traps only in its own scratch proving home (<see cref="AuditCanaries"/>).
    /// </summary>
    public const double RealTrapRate = 0;
}

/// <summary>
/// THE DRAW (<c>U-org-audit-a</c>; ORGANISATION § 2's audit row — it "wakes on sampled deliveries"): an accepted delivery is drawn when
/// HMAC-SHA256 under the line's salt of the delivery's id, its first eight bytes read as an unsigned integer, falls below the rate times
/// 2^64. The salt is in the line's own file and nowhere else, so nobody in the organisation can tell which delivery will be drawn — and the
/// same salt draws the same deliveries for ever, so a restart or a second sweep draws nothing twice and nothing new.
/// </summary>
public static class AuditSampler
{
    /// <summary>Whether <paramref name="deliveryId"/> is drawn under <paramref name="salt"/> at <paramref name="rate"/> (0 draws none, 1 every one).</summary>
    public static bool Draws(ReadOnlySpan<byte> salt, string deliveryId, double rate)
    {
        if (rate >= 1) return true;
        if (!(rate > 0)) return false;
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(deliveryId), mac);
        return BinaryPrimitives.ReadUInt64BigEndian(mac) < (ulong)(rate * 18446744073709551616.0);
    }
}
