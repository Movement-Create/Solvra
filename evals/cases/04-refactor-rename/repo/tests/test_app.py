import unittest
from app.api.handlers import profile, safe_profile
from app.api.report import all_names
from app.services.users import get_user_name


class T(unittest.TestCase):
    def test_profile(self):
        self.assertEqual(profile(1)["name"], "Ada Lovelace")
    def test_safe(self):
        self.assertEqual(safe_profile(9)["name"], "unknown")
    def test_names(self):
        self.assertEqual(all_names(), ["Ada Lovelace", "Alan Turing"])
    def test_direct(self):
        self.assertEqual(get_user_name(2), "Alan Turing")
