"""Sample target for testing the wake tracer."""

def greet(name):
    message = f"Hello, {name}!"
    return message


def compute(x, y):
    result = x + y
    if result > 10:
        raise ValueError(f"too big: {result}")
    return result


def main():
    msg = greet("wake")
    print(msg)

    total = 0
    for i in range(3):
        total = compute(total, i + 1)
    print(f"total = {total}")

    try:
        compute(5, 10)
    except ValueError as e:
        print(f"caught: {e}")


if __name__ == "__main__":
    main()
