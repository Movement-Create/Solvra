const { applyPercent, format } = require("./money");

class Cart {
  constructor() { this.lines = []; this.coupon = null; }
  add(sku, priceCents, qty = 1) {
    const existing = this.lines.find(l => l.sku === sku);
    if (existing) existing.qty = qty;
    else this.lines.push({ sku, priceCents, qty });
  }
  applyCoupon(percent) { this.coupon = percent; }
  subtotal() { return this.lines.reduce((s, l) => s + l.priceCents * l.qty, 0); }
  total() {
    let t = this.subtotal();
    if (this.coupon) {
      t = applyPercent(t, this.coupon);
      for (const l of this.lines) t = applyPercent(t, this.coupon / this.lines.length);
    }
    return t;
  }
  receipt() { return `Total: ${format(this.total())}`; }
}
module.exports = { Cart };
