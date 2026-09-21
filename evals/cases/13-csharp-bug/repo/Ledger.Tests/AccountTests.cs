using Ledger;
using Xunit;

public class AccountTests
{
    [Fact]
    public void DepositIncreasesBalance()
    {
        var a = new Account();
        a.Deposit(10m);
        Assert.Equal(10m, a.Balance);
    }
}
