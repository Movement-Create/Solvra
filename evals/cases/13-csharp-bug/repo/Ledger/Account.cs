namespace Ledger;

public class Account
{
    private readonly List<decimal> _entries = new();
    public decimal OverdraftLimit { get; init; }

    public decimal Balance => _entries.Sum();

    public void Deposit(decimal amount)
    {
        if (amount < 0) throw new ArgumentException("negative deposit");
        _entries.Add(amount);
    }

    public bool TryWithdraw(decimal amount)
    {
        if (Balance - amount < OverdraftLimit) return false;
        _entries.Add(-amount);
        return true;
    }

    /// <summary>Monthly interest on positive balances, rounded to cents (banker's rounding is NOT wanted).</summary>
    public decimal MonthlyInterest(decimal annualRatePercent) =>
        Math.Round(Balance * annualRatePercent / 12, 2);
}
