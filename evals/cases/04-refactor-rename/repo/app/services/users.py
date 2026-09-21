USERS = {1: {"first": "Ada", "last": "Lovelace"}, 2: {"first": "Alan", "last": "Turing"}}


def get_user_name(user_id):
    u = USERS[user_id]
    return f"{u['first']} {u['last']}"


def get_user_name_or_default(user_id, default="unknown"):
    return get_user_name(user_id) if user_id in USERS else default
