from app.services import users
from app.services.greeting import greet


def profile(user_id):
    return {"name": users.get_user_name(user_id), "greeting": greet(user_id)}


def safe_profile(user_id):
    return {"name": users.get_user_name_or_default(user_id)}
