// Money helpers. Amounts are integer cents.
function applyPercent(cents, percent) {
  return cents - cents * percent / 100;
}
function format(cents) {
  return "$" + (cents / 100).toString();
}
module.exports = { applyPercent, format };
