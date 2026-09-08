namespace NettoMarkdowns.Services;

/// <summary>
/// Counts calls to the Products EAN API, which is capped at 100 requests a day.
/// In-memory and per-process, so it resets when the app restarts — it exists to stop
/// a stray loop burning the day's budget, not to be an exact ledger. Salling's own
/// 429 remains the real authority.
/// </summary>
public sealed class ApiQuota(ILogger<ApiQuota> logger)
{
    private readonly Lock _gate = new();
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private int _used;

    public int Used
    {
        get { lock (_gate) { RollOver(); return _used; } }
    }

    /// <summary>Takes one unit if any remain. False means the cap is reached for today.</summary>
    public bool TryConsume(int limit)
    {
        lock (_gate)
        {
            RollOver();
            if (_used >= limit)
            {
                logger.LogWarning("Products API daily limit of {Limit} reached", limit);
                return false;
            }

            _used++;
            return true;
        }
    }

    /// <summary>Hands a unit back when the call never actually happened.</summary>
    public void Refund()
    {
        lock (_gate)
        {
            if (_used > 0) _used--;
        }
    }

    private void RollOver()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today == _day) return;

        _day = today;
        _used = 0;
    }
}
