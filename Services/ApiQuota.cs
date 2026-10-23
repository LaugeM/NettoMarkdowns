namespace NettoMarkdowns.Services;

/// <summary>
/// Counts calls to the Products EAN API, which is capped at 100 requests a day.
/// The count is saved in <see cref="ProductStore"/>, so a restart or a redeploy
/// doesn't hand back a fresh budget. Salling's own 429 remains the real authority.
/// </summary>
public sealed class ApiQuota(ProductStore store, ILogger<ApiQuota> logger)
{
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _used;

    public int Used
    {
        get { lock (_gate) { RollOver(); return _used; } }
    }

    /// <summary>Takes one unit if fewer than <paramref name="limit"/> are used today. False means no.</summary>
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
            Save();
            return true;
        }
    }

    /// <summary>Hands a unit back when the call never actually happened.</summary>
    public void Refund()
    {
        lock (_gate)
        {
            if (_used <= 0) return;
            _used--;
            Save();
        }
    }

    private void RollOver()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today == _day) return;

        _day = today;
        _used = store.GetUsage(today);
    }

    private void Save() => store.SetUsage(_day, _used);
}
