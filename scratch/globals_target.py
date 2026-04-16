"""Sample target with globals for testing -IncludeGlobals and -Watch."""

# Module globals
GLOBAL_COUNTER = 0
CONFIG = {"debug": True, "retries": 3}
shared_state = []


def increment():
    global GLOBAL_COUNTER
    GLOBAL_COUNTER += 1
    shared_state.append(GLOBAL_COUNTER)


def process_item(item):
    if item < 0:
        raise ValueError(f"invalid item: {item}")
    increment()
    return item * 2


def main():
    for item in [1, 3, 5, -1]:
        try:
            result = process_item(item)
            print(f"processed {item} -> {result}")
        except ValueError as e:
            print(f"error: {e}")

    print(f"final counter: {GLOBAL_COUNTER}, state: {shared_state}")


if __name__ == "__main__":
    main()
