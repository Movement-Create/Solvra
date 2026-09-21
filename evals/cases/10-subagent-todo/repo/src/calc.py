def average(xs):
    return sum(xs) / len(xs) if xs else 0


def percent_change(old, new):
    return (new - old) / new * 100
