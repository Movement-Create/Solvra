cd "$WORK"
mkdir -p "$HIDDEN/ht" && cp -r Ledger "$HIDDEN/ht/" && mkdir -p "$HIDDEN/ht/Ledger.Tests" && cp Ledger.Tests/Ledger.Tests.csproj "$HIDDEN/ht/Ledger.Tests/"
cat > "$HIDDEN/ht/Ledger.Tests/Hidden.cs" <<'CS'
using Ledger; using Xunit;
public class Hidden
{
    [Fact] public void Overdraft() { var a = new Account { OverdraftLimit = 100m }; Assert.True(a.TryWithdraw(50m)); Assert.Equal(-50m, a.Balance); Assert.True(a.TryWithdraw(50m)); Assert.False(a.TryWithdraw(0.01m)); }
    [Fact] public void NoOverdraft() { var a = new Account(); a.Deposit(10m); Assert.False(a.TryWithdraw(10.01m)); Assert.True(a.TryWithdraw(10m)); }
    [Fact] public void Interest() { var a = new Account(); a.Deposit(100m); Assert.Equal(1.00m, a.MonthlyInterest(12m)); var b = new Account(); b.Deposit(12.5m); Assert.Equal(0.13m, b.MonthlyInterest(12m)); }
    [Fact] public void NegativeBalanceNoInterest() { var a = new Account { OverdraftLimit = 50m }; a.TryWithdraw(20m); Assert.Equal(0m, a.MonthlyInterest(12m)); }
    [Fact] public void NegativeWithdraw() { var a = new Account(); Assert.Throws<ArgumentException>(() => a.TryWithdraw(-5m)); }
}
CS
(cd "$HIDDEN/ht" && timeout 300 dotnet test Ledger.Tests 2>&1 | grep -E "Passed!|Failed!|error" | head -3); (cd "$HIDDEN/ht" && timeout 300 dotnet test Ledger.Tests >/dev/null 2>&1) || fail "hidden xunit tests failed"
timeout 300 dotnet test Ledger.Tests >/dev/null 2>&1 || fail "own tests fail"
[ "$(grep -c '\[Fact\]\|\[Theory\]' Ledger.Tests/AccountTests.cs)" -ge 4 ] || fail "regression tests not added"
