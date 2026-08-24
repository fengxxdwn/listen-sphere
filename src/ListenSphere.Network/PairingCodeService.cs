using System.Security.Cryptography;

namespace ListenSphere.Network;

public sealed record PairingCode(string Value, DateTimeOffset ExpiresAt);

public enum PairingCodeValidation
{
    Accepted,
    Invalid,
    Expired,
    RateLimited
}

public sealed class PairingCodeService
{
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private PairingCode? activeCode;
    private int failedAttempts;
    private DateTimeOffset blockedUntil;

    public PairingCodeService(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PairingCode Generate(TimeSpan? lifetime = null)
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            activeCode = new PairingCode(
                RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
                now.Add(lifetime ?? TimeSpan.FromMinutes(2)));
            failedAttempts = 0;
            blockedUntil = DateTimeOffset.MinValue;
            return activeCode;
        }
    }

    public PairingCodeValidation ValidateAndConsume(string value)
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (now < blockedUntil)
            {
                return PairingCodeValidation.RateLimited;
            }

            if (activeCode is null || now > activeCode.ExpiresAt)
            {
                activeCode = null;
                return PairingCodeValidation.Expired;
            }

            bool matches = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(activeCode.Value),
                System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty));
            if (matches)
            {
                activeCode = null;
                failedAttempts = 0;
                return PairingCodeValidation.Accepted;
            }

            failedAttempts++;
            if (failedAttempts >= 5)
            {
                activeCode = null;
                blockedUntil = now.AddMinutes(10);
                return PairingCodeValidation.RateLimited;
            }

            return PairingCodeValidation.Invalid;
        }
    }
}
