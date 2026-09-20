#!/usr/bin/env python3
"""
One-time Apple ID login/2FA handshake for Curato's iCloud sync (PLAN.md §4).

Deliberately a separate tiny script rather than driving icloudpd's own
interactive CLI: pyicloud's login/2FA API is a few clean calls
(PyiCloudService, .requires_2fa, .validate_2fa_code, .trust_session), so it
can speak a stable one-line-JSON-per-call protocol to Curato.Api instead of
Curato having to screen-scrape an interactive terminal prompt. Once a
session is trusted, icloudpd's own CLI (pointed at the same
--cookie-directory) picks it up for actual syncing without re-prompting for
2FA (Apple's own session lasts ~2 months before requiring re-auth).

The Apple password is read from stdin, never from argv (so it never shows
up in a process listing) and is never written to disk by this script —
only pyicloud's own session/cookie files are, in --cookie-directory.
"""
import argparse
import json
import sys

from pyicloud import PyiCloudService


def _print(payload: dict) -> None:
    print(json.dumps(payload), flush=True)


def login(username: str, cookie_directory: str) -> None:
    password = sys.stdin.readline().rstrip("\n")
    try:
        api = PyiCloudService(username, password, cookie_directory=cookie_directory)
    except Exception as exc:  # pyicloud raises several distinct exception types for bad creds/locked accounts/etc.
        _print({"status": "error", "message": str(exc)})
        return

    if getattr(api, "requires_2fa", False) or getattr(api, "requires_2sa", False):
        _print({"status": "2fa_required"})
        return

    _print({"status": "trusted"})


def verify_2fa(username: str, cookie_directory: str, code: str) -> None:
    # Re-authenticates against the same cookie directory rather than trying
    # to keep the `login` process alive — Curato.Api invokes this script
    # fresh per HTTP request, so the pending 2FA challenge has to be
    # resumable from just (username, cookie_directory, password). Whether
    # pyicloud actually needs the password again here (vs. resuming purely
    # from its cached session state) wasn't confirmed without a real Apple
    # ID to test against — Curato.Api sends it along either way as a hedge;
    # if pyicloud turns out not to need it, this still works unchanged.
    password = sys.stdin.readline().rstrip("\n")
    try:
        api = PyiCloudService(username, password, cookie_directory=cookie_directory)
        if not api.validate_2fa_code(code):
            _print({"status": "invalid_code"})
            return
        api.trust_session()
        _print({"status": "ok"})
    except Exception as exc:
        _print({"status": "error", "message": str(exc)})


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    login_parser = subparsers.add_parser("login")
    login_parser.add_argument("--username", required=True)
    login_parser.add_argument("--cookie-directory", required=True)

    verify_parser = subparsers.add_parser("verify-2fa")
    verify_parser.add_argument("--username", required=True)
    verify_parser.add_argument("--cookie-directory", required=True)
    verify_parser.add_argument("--code", required=True)

    args = parser.parse_args()

    if args.command == "login":
        login(args.username, args.cookie_directory)
    elif args.command == "verify-2fa":
        verify_2fa(args.username, args.cookie_directory, args.code)


if __name__ == "__main__":
    main()
