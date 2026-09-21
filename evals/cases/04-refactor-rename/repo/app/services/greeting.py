from app.services.users import get_user_name


def greet(user_id):
    return f"Hello, {get_user_name(user_id)}!"
