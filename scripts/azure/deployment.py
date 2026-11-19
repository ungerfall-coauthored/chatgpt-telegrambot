#!/usr/bin/env python3
"""Verify an isolated Flex target, then health-gate release announcements."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

REPOSITORY = "ungerfall-coauthored/chatgpt-telegrambot"
DEPLOYMENT_APP = "fapp-ungerfall-chatgpt-telegram-flex"
DEPLOYMENT_GROUP = "rg-chatgpt"
POLICY_PATH = Path(__file__).resolve().parents[2] / "config" / "conversation-policy.json"


def package(source, destination):
    source, destination = Path(source).resolve(), Path(destination).resolve()
    if not (source / "host.json").is_file() or not (source / ".azurefunctions").is_dir():
        raise ValueError("Publish output is missing host.json or hidden binding dependencies")
    if destination.is_relative_to(source):
        raise ValueError("Package destination must be outside the publish directory")
    entries = sorted(source.rglob("*"))
    if any(entry.is_symlink() or entry.name.lower() == "local.settings.json" for entry in entries):
        raise ValueError("Refusing to package local settings or symbolic links")
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for entry in entries:
            if entry.is_file():
                archive.write(entry, entry.relative_to(source).as_posix())
    with destination.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Deployment endpoints must not redirect")


def azure(*arguments):
    result = subprocess.run(
        ["az", *arguments, "--only-show-errors"],
        capture_output=True, text=True, check=False, timeout=120,
    )
    if result.returncode:
        # Azure errors can contain credentials or request details.
        raise RuntimeError("Azure operation failed; check app-scoped OIDC permissions and portal configuration")
    return result.stdout.strip()


def context(environ=os.environ):
    if environ.get("GITHUB_REPOSITORY") != REPOSITORY or environ.get("GITHUB_REF") != "refs/heads/master":
        raise ValueError("Deployment is restricted to the fork's master branch")
    sha = environ.get("GITHUB_SHA", "")
    app = environ.get("AZURE_FUNCTIONAPP_NAME", "")
    group = environ.get("AZURE_RESOURCE_GROUP", "")
    if not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise ValueError("GITHUB_SHA must be a full deployed commit SHA")
    if app != DEPLOYMENT_APP or group != DEPLOYMENT_GROUP:
        raise ValueError("Deployment is restricted to the approved replacement app in rg-chatgpt")
    return app, group, sha


def target(app, group):
    data = json.loads(azure(
        "functionapp", "show", "--name", app, "--resource-group", group,
        "--output", "json",
    ))
    properties = data.get("properties") or {}
    runtime = (properties.get("functionAppConfig", data.get("functionAppConfig")) or {}).get("runtime") or {}
    host = properties.get("defaultHostName", data.get("defaultHostName")) or ""
    if (data.get("name") != DEPLOYMENT_APP or data.get("resourceGroup", "").lower() != DEPLOYMENT_GROUP
            or "functionapp" not in data.get("kind", "").lower()
            or "linux" not in data.get("kind", "").lower()
            or runtime.get("name") != "dotnet-isolated" or runtime.get("version") != "10.0"):
        raise ValueError("Target must be the configured isolated Linux Flex Consumption app running .NET 10")
    if (not re.fullmatch(r"[a-z0-9][a-z0-9.-]*\.azurewebsites\.net", host)
            or not (host == app + ".azurewebsites.net" or host.startswith(app + "-"))):
        raise ValueError("Azure returned an unexpected app hostname")
    return "https://" + host


def request(url, key=None):
    headers = {"Accept": "application/json", "Cache-Control": "no-cache"}
    if key:
        headers["x-functions-key"] = key
    req = urllib.request.Request(url, data=b"" if key else None, headers=headers)
    with urllib.request.build_opener(NoRedirect()).open(req, timeout=15) as response:
        body = response.read(65537)
        if len(body) > 65536:
            raise ValueError("Unexpectedly large deployment response")
        return response.status, body


def configure_conversation_policy(app, group):
    desired = json.loads(POLICY_PATH.read_text())
    flags = {"DecisionsEnabled", "NaturalRequestsEnabled", "ContinuationEnabled", "AutonomousEnabled"}
    if not isinstance(desired, dict) or any(
        not re.fullmatch(r"-[1-9][0-9]*", chat)
        or not isinstance(options, dict)
        or any(name not in flags or type(value) is not bool for name, value in options.items())
        for chat, options in desired.items()
    ):
        raise ValueError("Managed conversation policy must contain negative group IDs and boolean feature flags only")

    def settings():
        return {entry["name"]: entry["value"] for entry in json.loads(azure(
            "functionapp", "config", "appsettings", "list",
            "--name", app, "--resource-group", group, "--output", "json",
        ))}

    hosted = settings()
    if any(options.get(flag) for options in desired.values() for flag in flags - {"DecisionsEnabled"}):
        if any(not isinstance(hosted.get(name), str) or not hosted[name].strip() for name in (
            "AZURE_DECISIONS_ENDPOINT", "AZURE_DECISIONS_DEPLOYMENT", "AZURE_DECISIONS_API_KEY",
        )):
            raise RuntimeError("Decision settings are incomplete; group policy was NOT changed")
    current = json.loads(hosted.get("ConversationPolicyChats") or "{}")
    if not isinstance(current, dict) or any(not isinstance(options, dict) for options in current.values()):
        raise ValueError("Hosted conversation policy is invalid; refusing to overwrite it")
    updated = {chat: {**options} for chat, options in current.items()}
    for chat, options in desired.items():
        updated.setdefault(chat, {}).update(options)
    if updated != current:
        azure(
            "functionapp", "config", "appsettings", "set", "--name", app, "--resource-group", group,
            "--settings", "ConversationPolicyChats=" + json.dumps(updated, separators=(",", ":")),
            "--output", "none",
        )
    confirmed = json.loads(settings().get("ConversationPolicyChats") or "{}")
    if confirmed != updated:
        raise RuntimeError("Hosted conversation policy verification failed; deployment must NOT proceed")
    print(f"Verified managed conversation policy for {len(desired)} group(s); credentials were not changed.")


def wait_for_health(url, sha, *, announcements=False, attempts=30, delay=10):
    for attempt in range(attempts):
        try:
            status, body = request(f"{url}/api/health?deployment={sha}&attempt={attempt}")
            data = json.loads(body)
            if (isinstance(data, dict) and status == 200 and data.get("status") == "ready" and data.get("version") == sha
                    and (not announcements or (
                        data.get("announcementsEnabled") is True and data.get("requiresApproval") is True))):
                return
        except (urllib.error.URLError, OSError, ValueError, TypeError):
            pass
        if attempt + 1 < attempts:
            time.sleep(delay)
    raise RuntimeError("Health check did not confirm the expected artifact/configuration; release was NOT activated")


def activate(app, group, sha, url):
    wait_for_health(url, sha)
    azure(
        "functionapp", "config", "appsettings", "set", "--name", app, "--resource-group", group,
        "--settings", "ReleaseAnnouncementsRequireApproval=true", "ReleaseAnnouncementsEnabled=true",
        "--output", "none",
    )
    wait_for_health(url, sha, announcements=True)
    key = azure(
        "functionapp", "keys", "list", "--name", app, "--resource-group", group,
        "--query", "functionKeys.default", "--output", "tsv",
    )
    if not key or key in ("None", "null") or "\n" in key or "\r" in key:
        raise RuntimeError("No default Function key available; release was NOT activated")
    print("::add-mask::" + key.replace("%", "%25"), flush=True)
    status, _ = request(f"{url}/api/releases/{sha}/activate", key)
    if status != 204:
        raise RuntimeError("Release activation was not confirmed; inspect the deployment before retrying")
    print(f"Healthy artifact {sha} activated; the bot timer will announce it without duplicate sends.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("package", "validate", "configure", "activate"))
    parser.add_argument("--source")
    parser.add_argument("--destination")
    args = parser.parse_args()
    if args.phase == "package":
        if not args.source or not args.destination:
            parser.error("package requires --source and --destination")
        digest = package(args.source, args.destination)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write(f"sha256={digest}\n")
        print(f"Packaged Function SHA-256: {digest}")
        return
    app, group, sha = context()
    url = target(app, group)
    if args.phase == "validate":
        print(f"Verified isolated Flex target: {app}")
    elif args.phase == "configure":
        configure_conversation_policy(app, group)
    else:
        activate(app, group, sha, url)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Never echo HTTP bodies, subprocess stderr, keys, or exception URLs.
        if isinstance(error, (ValueError, RuntimeError)):
            print(f"Deployment failed: {error}", file=sys.stderr)
        else:
            print(f"Deployment failed ({type(error).__name__}); inspect Azure health and permissions", file=sys.stderr)
        sys.exit(1)
