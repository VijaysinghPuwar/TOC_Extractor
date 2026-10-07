"""Which cookies mean a reader is signed in."""

from __future__ import annotations

import pytest

from toc_extractor.browser import is_account_cookie


@pytest.mark.parametrize(
    "name",
    [
        # NextAuth sets these for every visitor; novellunar.com showed
        # "Signed in." to a reader who never signed in.
        "__Host-next-auth.csrf-token",
        "__Secure-next-auth.callback-url",
        "next-auth.pkce.code_verifier",
        "_ga",
        "cf_clearance",
    ],
)
def test_cookies_every_visitor_gets_are_not_a_sign_in(name: str) -> None:
    assert not is_account_cookie(name)


@pytest.mark.parametrize(
    "name",
    ["__Secure-next-auth.session-token", "wordpress_logged_in_abc", "remember_web_59ba"],
)
def test_sign_in_cookies_still_count(name: str) -> None:
    assert is_account_cookie(name)
