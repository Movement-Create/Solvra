from invoicer.tax import tax_for


def render(items, cfg):
    net = sum(q * p for _, q, p in items)
    tax = tax_for(net, cfg)
    lines = [f"{name:10} {q:3} x {p:8.2f}" for name, q, p in items]
    lines.append(f"TAX {tax:.2f} {cfg['currency']}")
    lines.append(f"TOTAL {net + tax:.2f} {cfg['currency']}")
    return "\n".join(lines)
