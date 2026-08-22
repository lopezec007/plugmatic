#!/usr/bin/env python3
"""Fetch RepeaterBook data through the approved `repeaterbook` client.

Why this exists as a separate Python step rather than C# in Plugmatic.Providers: since
2026-03-03 RepeaterBook's export API is closed to unapproved clients, and the per-user
`rbuapp_` token is bound to *one approved application*. Plugmatic is not that application,
so a request from our own HTTP client is rejected with `auth_missing` no matter which
header carries the token — verified against the live endpoint. The `repeaterbook` PyPI
package *is* an approved distributed client, so the sanctioned path is to let it make the
call with the user's own token and hand the result over.

Its application identity (`app_name`/`app_version`/`app_contact`) is left at the package's
defaults on purpose: the token is bound to that app, and misrepresenting the client would
be exactly the circumvention the approval process exists to prevent.

Output is RepeaterBook's own export JSON field names, so
`Plugmatic.Providers.RepeaterBook.Parse` consumes it unchanged.

    tools/repeaterbook_fetch.py --state 08 [--gmrs] [--out FILE]

The token is read from $REPEATERBOOK, else $TOKEN, else the same keys in ./.env.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import pathlib
import sys

TOKEN_KEYS = ("REPEATERBOOK", "TOKEN")

# RepeaterBook keys states by FIPS id; Plugmatic resolves locations to state names.
STATE_FIPS = {
    "alabama": "01", "alaska": "02", "arizona": "04", "arkansas": "05", "california": "06",
    "colorado": "08", "connecticut": "09", "delaware": "10", "district of columbia": "11",
    "florida": "12", "georgia": "13", "hawaii": "15", "idaho": "16", "illinois": "17",
    "indiana": "18", "iowa": "19", "kansas": "20", "kentucky": "21", "louisiana": "22",
    "maine": "23", "maryland": "24", "massachusetts": "25", "michigan": "26",
    "minnesota": "27", "mississippi": "28", "missouri": "29", "montana": "30",
    "nebraska": "31", "nevada": "32", "new hampshire": "33", "new jersey": "34",
    "new mexico": "35", "new york": "36", "north carolina": "37", "north dakota": "38",
    "ohio": "39", "oklahoma": "40", "oregon": "41", "pennsylvania": "42",
    "rhode island": "44", "south carolina": "45", "south dakota": "46", "tennessee": "47",
    "texas": "48", "utah": "49", "vermont": "50", "virginia": "51", "washington": "53",
    "west virginia": "54", "wisconsin": "55", "wyoming": "56", "puerto rico": "72",
}


def state_id(value: str) -> str:
    """Accept either a FIPS id ("08") or a state name ("Colorado")."""
    value = value.strip()
    if value.isdigit():
        return value.zfill(2)
    if (fips := STATE_FIPS.get(value.lower())) is not None:
        return fips
    sys.exit(f"Unknown US state {value!r}; pass a name like Colorado or a FIPS id like 08.")


def load_token(env_path: pathlib.Path) -> str:
    for key in TOKEN_KEYS:
        if (value := os.environ.get(key)):
            return value.strip()
    if env_path.is_file():
        for line in env_path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            if key.strip() in TOKEN_KEYS:
                return value.strip().strip('"').strip("'")
    sys.exit(
        "No RepeaterBook token. Set $REPEATERBOOK or put TOKEN=rbuapp_… in .env.\n"
        "Tokens are minted per user, per approved application, at\n"
        "  https://www.repeaterbook.com/user/api_apps.php"
    )


async def fetch(token: str, state: str, gmrs: bool) -> tuple[list[dict], int]:
    """Return RepeaterBook's export rows verbatim.

    The raw export JSON already uses the field names Plugmatic.Providers.RepeaterBook.Parse
    reads ("Frequency", "Input Freq", "PL", "Operational Status", …), so it is passed
    straight through. Going via export_multi_json rather than the package's download()
    deliberately skips its Pydantic models: RepeaterBook's own GMRS feed carries rows with
    `Frequency: 0.00000`, which fail model validation and abort the whole download. The C#
    parser drops those rows on its own.
    """
    import pycountry
    from repeaterbook.models import ExportQuery, ServiceType
    from repeaterbook.services import RepeaterBookAPI

    api = RepeaterBookAPI(app_token=token)          # keep the package's own app identity
    query = ExportQuery(
        state_ids=frozenset({state}),
        countries=frozenset({pycountry.countries.get(alpha_2="US")}),
        service_types=frozenset({ServiceType.GMRS}) if gmrs else frozenset(),
    )
    rows: list[dict] = []
    for payload in await api.export_multi_json(api.urls_export(query)):
        rows.extend(payload.get("results") or [])
    return rows, len(rows)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--state", required=True, help="state name (Colorado) or FIPS id (08)")
    ap.add_argument("--gmrs", action="store_true", help="GMRS service instead of amateur")
    ap.add_argument("--out", help="write here instead of stdout")
    ap.add_argument("--env", default=".env", help="dotenv file to read the token from")
    args = ap.parse_args()

    token = load_token(pathlib.Path(args.env))
    try:
        rows, _ = asyncio.run(fetch(token, state_id(args.state), args.gmrs))
    except Exception as exc:                         # noqa: BLE001 - surfaced to the CLI
        print(f"repeaterbook: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2

    payload = json.dumps({"count": len(rows), "results": rows}, indent=None)
    if args.out:
        pathlib.Path(args.out).write_text(payload)
        print(f"{len(rows)} repeaters -> {args.out}", file=sys.stderr)
    else:
        sys.stdout.write(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
