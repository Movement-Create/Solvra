import sys
from invoicer.config import load
from invoicer.render import render

ITEMS = [("widget", 2, 10.0), ("gadget", 1, 5.5)]


def main(argv):
    cfg = load(argv[1] if len(argv) > 1 else "invoicer.json")
    print(render(ITEMS, cfg))


if __name__ == "__main__":
    main(sys.argv)
