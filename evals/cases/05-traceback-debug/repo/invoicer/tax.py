def tax_for(amount, cfg):
    rate = cfg["tax"]["rate"]
    if cfg["tax"]["inclusive"]:
        return round(amount - amount / (1 + rate), 2)
    return round(amount * rate, 2)
