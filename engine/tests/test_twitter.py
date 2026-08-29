import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites.twitter import map_transformed


def test_map_transformed_fields():
    u = map_transformed({
        "id": 44196397, "name": "elonmusk", "nick": "Elon Musk",
        "profile_image": "https://pbs.twimg.com/profile/a.jpg",
    })
    assert u == {
        "rest_id": "44196397",
        "screen_name": "elonmusk",
        "display_name": "Elon Musk",
        "avatar_url": "https://pbs.twimg.com/profile/a.jpg",
    }
