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
    private const int MaximumFailedAttempts = 5;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitDuration = TimeSpan.FromMinutes(10);
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private readonly Dictionary<string, PairingAttemptState> attemptsBySource =
        new(StringComparer.Ordinal);
    private PairingCode? activeCode;

    public PairingCodeService(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PairingCode Generate(TimeSpan? lifetime = null)
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveInactiveAttemptStates(now);
            activeCode = new PairingCode(
                RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
                now.Add(lifetime ?? DefaultLifetime));
            return activeCode;
        }
    }

    public bool HasActiveCode
    {
        get
        {
            lock (sync)
            {
                ExpireCodeIfNeeded(timeProvider.GetUtcNow());
                return activeCode is not null;
            }
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            activeCode = null;
        }
    }

    public PairingCodeValidation ValidateAndConsume(
        string value,
        string rateLimitKey = "default")
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            string source = string.IsNullOrWhiteSpace(rateLimitKey)
                ? "default"
                : rateLimitKey.Trim();
            if (attemptsBySource.TryGetValue(source, out PairingAttemptState? attempts) &&
                now < attempts.BlockedUntil)
            {
                return PairingCodeValidation.RateLimited;
            }

            ExpireCodeIfNeeded(now);
            if (activeCode is null)
            {
                return PairingCodeValidation.Expired;
            }

            string submitted = value ?? string.Empty;
            bool matches = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(activeCode.Value),
                System.Text.Encoding.ASCII.GetBytes(submitted));
            if (matches)
            {
                activeCode = null;
                attemptsBySource.Remove(source);
                return PairingCodeValidation.Accepted;
            }

            attempts ??= new PairingAttemptState();
            attempts.FailedAttempts++;
            if (attempts.FailedAttempts >= MaximumFailedAttempts)
            {
                attempts.BlockedUntil = now.Add(RateLimitDuration);
                attemptsBySource[source] = attempts;
                return PairingCodeValidation.RateLimited;
            }

            attemptsBySource[source] = attempts;
            return PairingCodeValidation.Invalid;
        }
    }

    private void ExpireCodeIfNeeded(DateTimeOffset now)
    {
        if (activeCode is not null && now >= activeCode.ExpiresAt)
        {
            activeCode = null;
        }
    }

    private void RemoveInactiveAttemptStates(DateTimeOffset now)
    {
        foreach (string source in attemptsBySource
                     .Where(item => item.Value.BlockedUntil <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            attemptsBySource.Remove(source);
        }
    }

    private sealed class PairingAttemptState
    {
        public int FailedAttempts { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
    }
}
