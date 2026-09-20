#!/usr/bin/env python3
"""
One-time Apple ID login/2FA handshake for Curato's iCloud sync (PLAN.md §4).

Runs as a single long-lived process for the *whole* login+2FA handshake —
confirmed live that pyicloud's 2FA API needs the same PyiCloudService
object for both steps: reconstructing it from scratch for the verify step
(even pointed at the same --cookie-directory) re-authenticates with Apple
and issues a brand new pending challenge, so a code from the first prompt
just fails against a stale one — visible as an endless loop back to
another 2FA prompt instead of ever completing. Reads one JSON command per
line from stdin, replies with one JSON line per command on stdout, and
stays alive between "login" and "verify-2fa" so both operate on the same
`api` object.

The Apple password travels inside the JSON command over stdin, never as a
process argument (so it never shows up in a process listing) and is never
written to disk by this script — only pyicloud's own session/cookie files
are, in --cookie-directory.
"""
import json
import sys

from pyicloud import PyiCloudService


def _print(payload: dict) -> None:
    print(json.dumps(payload), flush=True)


def main() -> None:
    api = None

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        try:
            command = json.loads(line)
        except json.JSONDecodeError:
            _print({"status": "error", "message": "Malformed command."})
            return

        action = command.get("command")

        if action == "login":
            try:
                api = PyiCloudService(
                    command["username"], command["password"], cookie_directory=command["cookieDirectory"]
                )
            except Exception as exc:  # bad creds, locked account, network error, etc.
                _print({"status": "error", "message": str(exc)})
                return

            if getattr(api, "requires_2fa", False) or getattr(api, "requires_2sa", False):
                _print({"status": "2fa_required"})
                continue  # stay alive — verify-2fa below reuses this same `api`

            _print({"status": "trusted"})
            return

        elif action == "verify-2fa":
            if api is None:
                _print({"status": "error", "message": "No sign-in in progress."})
                return

            try:
                if not api.validate_2fa_code(command["code"]):
                    _print({"status": "invalid_code"})
                    continue  # let the caller retry against the same pending challenge
                api.trust_session()
                _print({"status": "ok"})
            except Exception as exc:
                _print({"status": "error", "message": str(exc)})
            return

        else:
            _print({"status": "error", "message": f"Unknown command: {action}"})
            return


if __name__ == "__main__":
    main()
