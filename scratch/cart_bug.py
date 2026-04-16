"""
A realistic buggy shopping cart calculator.
It crashes on a specific item; the cause isn't obvious at the call site.
"""

TAX_RATES = {
    'food': 0.05,
    'electronics': 0.10,
    'clothing': 0.08,
}


def get_tax_rate(category):
    return TAX_RATES[category]


def price_with_tax(item):
    base = item['price']
    tax = base * get_tax_rate(item['category'])
    return base + tax


def process_cart(cart):
    total = 0.0
    for item in cart:
        total += price_with_tax(item)
    return total


if __name__ == '__main__':
    cart = [
        {'name': 'apple',  'category': 'food',        'price': 1.0},
        {'name': 'shirt',  'category': 'clothing',    'price': 20.0},
        {'name': 'laptop', 'category': 'electronics', 'price': 1000.0},
        {'name': 'book',   'category': 'books',       'price': 15.0},
    ]
    print(f"Total: {process_cart(cart):.2f}")
