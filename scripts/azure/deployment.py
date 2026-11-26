#!/usr/bin/env python3
"""Verify an isolated Flex target, then run one-shot pipeline announcements."""

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
CHAT_INPUT_PATH = Path(__file__).resolve().parents[2] / "config" / "chat-input-budget.json"


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


def request(url):
    headers = {"Accept": "application/json", "Cache-Control": "no-cache"}
    req = urllib.request.Request(url, headers=headers)
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


def configure_chat_input_budget(app, group):
    desired = json.loads(CHAT_INPUT_PATH.read_text(encoding="utf-8"))
    names = {"AZURE_OPENAI_CHAT_CONTEXT_WINDOW_TOKENS", "AZURE_OPENAI_CHAT_INPUT_TOKENS"}
    if (not isinstance(desired, dict) or set(desired) != names
            or any(type(value) is not int or not 0 < value <= 2147483647 for value in desired.values())):
        raise ValueError("Managed chat budget must contain only positive input/context integer settings")

    def settings():
        return {entry["name"]: entry["value"] for entry in json.loads(azure(
            "functionapp", "config", "appsettings", "list",
            "--name", app, "--resource-group", group, "--output", "json",
        ))}

    hosted = settings()
    try:
        output = int(hosted.get("AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS", "2048"))
        margin = int(hosted.get("AZURE_OPENAI_CHAT_SAFETY_MARGIN_TOKENS", "1024"))
    except (ValueError, TypeError):
        raise ValueError("Hosted chat output and safety budgets must be numeric") from None
    if (output <= 0 or margin < 0
            or desired["AZURE_OPENAI_CHAT_INPUT_TOKENS"] + output + margin
            > desired["AZURE_OPENAI_CHAT_CONTEXT_WINDOW_TOKENS"]):
        raise ValueError("Chat input plus unchanged output/safety budgets must fit the context window")
    protected = {name: value for name, value in hosted.items()
                 if name.startswith("AZURE_OPENAI_") and name not in names}
    if any(hosted.get(name) != str(value) for name, value in desired.items()):
        azure(
            "functionapp", "config", "appsettings", "set", "--name", app, "--resource-group", group,
            "--settings", *(f"{name}={value}" for name, value in sorted(desired.items())),
            "--output", "none",
        )
    confirmed = settings()
    if (any(confirmed.get(name) != str(value) for name, value in desired.items())
            or protected != {name: value for name, value in confirmed.items()
                             if name.startswith("AZURE_OPENAI_") and name not in names}):
        raise RuntimeError("Chat budget verification failed or other AI settings changed; deployment must NOT proceed")
    print(f"Verified chat input {desired['AZURE_OPENAI_CHAT_INPUT_TOKENS']} and context "
          f"{desired['AZURE_OPENAI_CHAT_CONTEXT_WINDOW_TOKENS']}; output and other AI settings are unchanged.")


def wait_for_health(url, sha, *, attempts=30, delay=10):
    for attempt in range(attempts):
        try:
            status, body = request(f"{url}/api/health?deployment={sha}&attempt={attempt}")
            data = json.loads(body)
            if isinstance(data, dict) and status == 200 and data.get("status") == "ready" and data.get("version") == sha:
                return
        except (urllib.error.URLError, OSError, ValueError, TypeError):
            pass
        if attempt + 1 < attempts:
            time.sleep(delay)
    raise RuntimeError("Health check did not confirm the expected artifact/configuration; release was NOT activated")


def announcement_environment(app, group):
    hosted = {entry["name"]: entry["value"] for entry in json.loads(azure(
        "functionapp", "config", "appsettings", "list",
        "--name", app, "--resource-group", group, "--output", "json",
    ))}
    required = ("AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_CHAT_DEPLOYMENT",
                "CosmosDbConnectionString", "CosmosDatabase", "CosmosTimedTasksContainer", "TELEGRAM_BOT_TOKEN")
    if any(not isinstance(hosted.get(name), str) or not hosted[name].strip() for name in required):
        raise RuntimeError("Hosted announcement configuration is incomplete; no delivery was attempted")
    return {name: value for name, value in hosted.items()
            if name.startswith("AZURE_OPENAI_") or name in required or name == "TestUsers"}


def announce(app, group, sha, url, runner):
    wait_for_health(url, sha)
    private = announcement_environment(app, group)
    if not os.environ.get("GITHUB_TOKEN"):
        raise RuntimeError("The pipeline's read-only GitHub token is required")
    runner = Path(runner).resolve()
    if not runner.is_file() or runner.name != "release-announcer.dll":
        raise ValueError("A published release-announcer.dll artifact is required")
    result = subprocess.run(
        ["dotnet", str(runner)], env={**os.environ, **private},
        capture_output=True, text=True, check=False, timeout=540,
    )
    # The runner emits only sanitized progress/errors, never provider bodies or credentials.
    print(result.stdout, end="")
    print(result.stderr, end="", file=sys.stderr)
    if result.returncode:
        raise RuntimeError("Announcement delivery failed; deployment is live but the release job must NOT report success")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("package", "validate", "configure", "announce"))
    parser.add_argument("--source")
    parser.add_argument("--destination")
    parser.add_argument("--runner")
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
        announcement_environment(app, group)
        print(f"Verified isolated Flex target: {app}")
        print("Verified required hosted announcement settings; credentials were not changed.")
    elif args.phase == "configure":
        configure_chat_input_budget(app, group)
        configure_conversation_policy(app, group)
    else:
        if not args.runner:
            parser.error("announce requires --runner")
        announce(app, group, sha, url, args.runner)


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
