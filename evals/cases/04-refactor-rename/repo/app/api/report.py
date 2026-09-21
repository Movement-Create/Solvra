from app.services.users import USERS, get_user_name


def all_names():
    # uses get_user_name for every user
    return sorted(get_user_name(uid) for uid in USERS)
