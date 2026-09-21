cd "$WORK"
cat > "$HIDDEN/hidden.test.js" <<JS
const test = require("node:test"); const assert = require("node:assert");
const { Cart } = require("$WORK/src/cart");
test("merge qty", () => { const c = new Cart(); c.add("a", 100, 1); c.add("a", 100, 2); assert.strictEqual(c.subtotal(), 300); });
test("coupon once + rounding", () => { const c = new Cart(); c.add("x", 1999, 1); c.applyCoupon(10); assert.strictEqual(c.total(), 1799); assert.strictEqual(c.receipt(), "Total: \$17.99"); });
test("two lines", () => { const c = new Cart(); c.add("a", 1000); c.add("b", 500, 3); c.applyCoupon(15); assert.strictEqual(c.total(), 2125); assert.strictEqual(c.receipt(), "Total: \$21.25"); });
test("no coupon format", () => { const c = new Cart(); c.add("a", 1200); assert.strictEqual(c.receipt(), "Total: \$12.00"); });
JS
node --test "$HIDDEN/hidden.test.js" 2>&1 | grep -E "^# (pass|fail)" ; node --test "$HIDDEN/hidden.test.js" >/dev/null 2>&1 || fail "hidden tests failed"
npm test >/dev/null 2>&1 || fail "npm test fails"
[ "$(grep -c 'test(' test/cart.test.js)" -ge 3 ] || fail "no regression tests added"
