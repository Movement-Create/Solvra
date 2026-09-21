const test = require("node:test");
const assert = require("node:assert");
const { Cart } = require("../src/cart");

test("subtotal", () => {
  const c = new Cart();
  c.add("a", 250, 2);
  assert.strictEqual(c.subtotal(), 500);
});
