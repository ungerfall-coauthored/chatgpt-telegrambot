import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location(
    "deployment", Path(__file__).resolve().parents[2] / "scripts" / "azure" / "deployment.py"
)
deployment = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(deployment)

SHA = "a" * 40
APP = deployment.DEPLOYMENT_APP
GROUP = deployment.DEPLOYMENT_GROUP
URL = f"https://{APP}.azurewebsites.net"


def health(version=SHA, **values):
    return 200, json.dumps({
        "status": "ready", "version": version,
        "announcementsEnabled": True, "requiresApproval": True, **values,
    }).encode()


class DeploymentTests(unittest.TestCase):
    def test_package_keeps_binding_files_at_zip_root(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "publish"
            (source / ".azurefunctions").mkdir(parents=True)
            (source / "host.json").write_text("{}")
            (source / ".azurefunctions" / "binding.dll").write_bytes(b"test")
            digest = deployment.package(source, root / "function.zip")
            self.assertRegex(digest, r"^[a-f0-9]{64}$")
            with zipfile.ZipFile(root / "function.zip") as archive:
                self.assertEqual([".azurefunctions/binding.dll", "host.json"], archive.namelist())
            (source / "local.settings.json").write_text('{"Values":{"SECRET":"test-only"}}')
            with self.assertRaises(ValueError):
                deployment.package(source, root / "unsafe.zip")
            self.assertFalse((root / "unsafe.zip").exists())

    def test_package_rejects_symbolic_links(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "publish"
            (root / ".azurefunctions").mkdir(parents=True)
            (root / "host.json").write_text("{}")
            (root / "link").symlink_to(root / "host.json")
            with self.assertRaises(ValueError):
                deployment.package(root, root.parent / "unsafe.zip")

    def test_target_context_rejects_upstream_wrong_branch_and_existing_resources(self):
        valid = {
            "GITHUB_REPOSITORY": deployment.REPOSITORY, "GITHUB_REF": "refs/heads/master",
            "GITHUB_SHA": SHA, "AZURE_FUNCTIONAPP_NAME": APP, "AZURE_RESOURCE_GROUP": GROUP,
        }
        self.assertEqual((APP, GROUP, SHA), deployment.context(valid))
        for field, value in (
            ("GITHUB_REPOSITORY", "Ungerfall/chatgpt-telegrambot"),
            ("GITHUB_REF", "refs/pull/5/merge"),
            ("GITHUB_SHA", "master"),
            ("AZURE_FUNCTIONAPP_NAME", "fapp-ungerfall-chatgpt-telegram-webhook"),
            ("AZURE_RESOURCE_GROUP", "rg-upstream"),
            ("AZURE_RESOURCE_GROUP", "rg-coauthored-telegram-test"),
        ):
            with self.subTest(field=field), self.assertRaises(ValueError):
                deployment.context({**valid, field: value})

    def test_target_verifies_runtime_and_azure_hostname_before_deploy(self):
        metadata = {
            "name": APP, "resourceGroup": GROUP, "defaultHostName": APP + "-unique.westeurope-01.azurewebsites.net",
            "kind": "functionapp,linux", "functionAppConfig": {"runtime": {"name": "dotnet-isolated", "version": "10.0"}},
        }
        for properties in (None, {}, {"unrelated": "value"}):
            with self.subTest(properties=properties), patch.object(
                deployment, "azure", return_value=json.dumps({**metadata, "properties": properties})
            ) as azure:
                self.assertEqual("https://" + metadata["defaultHostName"], deployment.target(APP, GROUP))
                self.assertNotIn("--query", azure.call_args.args)
        nested = {
            key: value for key, value in metadata.items()
            if key not in ("defaultHostName", "functionAppConfig")
        }
        nested["properties"] = {
            key: metadata[key] for key in ("defaultHostName", "functionAppConfig")
        }
        with patch.object(deployment, "azure", return_value=json.dumps(nested)):
            self.assertEqual("https://" + metadata["defaultHostName"], deployment.target(APP, GROUP))
        for update in (
            {"functionAppConfig": None},
            {"functionAppConfig": {"runtime": {"name": "dotnet-isolated", "version": "8.0"}}},
            {"functionAppConfig": {"runtime": {"name": "dotnet", "version": "10.0"}}},
            {"defaultHostName": None}, {"defaultHostName": "https://evil.example"},
            {"defaultHostName": "other.azurewebsites.net"},
            {"defaultHostName": APP + ".azurewebsites.net.evil.example"},
        ):
            for response in ({**metadata, **update}, {**nested, "properties": {**nested["properties"], **update}}):
                with self.subTest(update=update, nested="properties" in response), patch.object(
                    deployment, "azure", return_value=json.dumps(response)
                ), self.assertRaises(ValueError):
                    deployment.target(APP, GROUP)
            with self.subTest(update=update, nested_overrides_flat=True), patch.object(
                deployment, "azure", return_value=json.dumps({**metadata, "properties": update})
            ), self.assertRaises(ValueError):
                deployment.target(APP, GROUP)
        for update in (
            {"name": "other-app"}, {"resourceGroup": "other-group"},
            {"kind": "functionapp,windows"}, {"kind": "app,linux"},
        ):
            for response in ({**metadata, **update}, {**nested, **update}):
                with self.subTest(update=update, nested="properties" in response), patch.object(
                    deployment, "azure", return_value=json.dumps(response)
                ), self.assertRaises(ValueError):
                    deployment.target(APP, GROUP)

    def test_health_requires_exact_artifact(self):
        with patch.object(deployment, "request", side_effect=[
            health("b" * 40), (200, b"[]"), health()
        ]) as request:
            deployment.wait_for_health(URL, SHA, attempts=3, delay=0)
        self.assertEqual(3, request.call_count)

    def test_managed_policy_merges_only_reviewed_flags_and_verifies_without_touching_keys(self):
        desired = {"-123": {"DecisionsEnabled": True, "NaturalRequestsEnabled": True, "AutonomousEnabled": False}}
        current = {"-123": {"DecisionDailyLimit": 17, "AutonomousEnabled": True}, "-456": {"DecisionsEnabled": False}}
        updated = {"-123": {**current["-123"], **desired["-123"]}, "-456": current["-456"]}
        secrets = {
            "AZURE_DECISIONS_ENDPOINT": "https://example.services.ai.azure.com/",
            "AZURE_DECISIONS_API_KEY": "test-key-not-for-cli-or-logs",
            "AZURE_DECISIONS_DEPLOYMENT": "test-deployment",
        }

        def listing(policy):
            return json.dumps([{"name": name, "value": value} for name, value in
                               {**secrets, "ConversationPolicyChats": json.dumps(policy)}.items()])

        for existing, responses, writes in (
            (current, [listing(current), "", listing(updated)], 1),
            (updated, [listing(updated), listing(updated)], 0),
        ):
            with self.subTest(existing=existing), patch.object(deployment.Path, "read_text", return_value=json.dumps(desired)), \
                    patch.object(deployment, "azure", side_effect=responses) as azure, contextlib.redirect_stdout(io.StringIO()) as output:
                deployment.configure_conversation_policy(APP, GROUP)
                setters = [call.args for call in azure.call_args_list if "set" in call.args]
                self.assertEqual(writes, len(setters))
                if setters:
                    self.assertIn("ConversationPolicyChats=" + json.dumps(updated, separators=(",", ":")), setters[0])
                self.assertNotIn(secrets["AZURE_DECISIONS_API_KEY"], output.getvalue() + repr(azure.call_args_list))

    def test_chat_input_budget_changes_only_input_and_context_and_is_idempotent(self):
        desired = json.loads(deployment.CHAT_INPUT_PATH.read_text())
        original = {
            "AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS": "8192",
            "AZURE_OPENAI_SUMMARY_MAX_COMPLETION_TOKENS": "4096",
            "AZURE_OPENAI_AUTONOMOUS_MAX_COMPLETION_TOKENS": "120",
            "AZURE_OPENAI_API_KEY": "test-key-not-for-cli-or-logs",
        }
        updated = {**original, **{name: str(value) for name, value in desired.items()}}

        def listing(values):
            return json.dumps([{"name": name, "value": value} for name, value in values.items()])

        for responses, writes in (([listing(original), "", listing(updated)], 1),
                                  ([listing(updated), listing(updated)], 0)):
            with self.subTest(writes=writes), patch.object(deployment, "azure", side_effect=responses) as azure, \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                deployment.configure_chat_input_budget(APP, GROUP)
                setters = [call.args for call in azure.call_args_list if "set" in call.args]
                self.assertEqual(writes, len(setters))
                if setters:
                    start = setters[0].index("--settings") + 1
                    end = setters[0].index("--output")
                    self.assertEqual({f"{name}={value}" for name, value in desired.items()}, set(setters[0][start:end]))
                self.assertNotIn(original["AZURE_OPENAI_API_KEY"], output.getvalue() + repr(azure.call_args_list))

    def test_chat_input_budget_blocks_output_changes_and_failed_verification(self):
        desired = json.loads(deployment.CHAT_INPUT_PATH.read_text())
        original = {"AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS": "8192"}
        for confirmed in ({**original, **{name: str(value) for name, value in desired.items()},
                           "AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS": "16384"}, original):
            listing = lambda values: json.dumps([{"name": name, "value": value} for name, value in values.items()])
            with self.subTest(confirmed=confirmed), patch.object(deployment, "azure",
                    side_effect=[listing(original), "", listing(confirmed)]), self.assertRaises(RuntimeError):
                deployment.configure_chat_input_budget(APP, GROUP)
        unsafe = {**desired, "AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS": 16384}
        with patch.object(deployment.Path, "read_text", return_value=json.dumps(unsafe)), \
                patch.object(deployment, "azure") as azure, self.assertRaises(ValueError):
            deployment.configure_chat_input_budget(APP, GROUP)
        azure.assert_not_called()

    def test_chat_output_budget_updates_only_reviewed_chat_settings(self):
        desired = json.loads(deployment.CHAT_OUTPUT_PATH.read_text())
        original = {
            "AZURE_OPENAI_CHAT_INPUT_TOKENS": "131072",
            "AZURE_OPENAI_CHAT_CONTEXT_WINDOW_TOKENS": "400000",
            "AZURE_OPENAI_CHAT_MAX_COMPLETION_TOKENS": "8192",
            "AZURE_OPENAI_SUMMARY_MAX_COMPLETION_TOKENS": "4096",
            "AZURE_OPENAI_AUTONOMOUS_MAX_COMPLETION_TOKENS": "120",
            "AZURE_OPENAI_API_KEY": "test-private-key",
        }
        updated = {**original, **{name: str(value) for name, value in desired.items()}}
        listing = lambda values: json.dumps([{"name": name, "value": value} for name, value in values.items()])
        for responses, writes in (([listing(original), "", listing(updated)], 1),
                                  ([listing(updated), listing(updated)], 0)):
            with patch.object(deployment, "azure", side_effect=responses) as azure, \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                deployment.configure_chat_output_budget(APP, GROUP)
                setters = [call.args for call in azure.call_args_list if "set" in call.args]
                self.assertEqual(writes, len(setters))
                if setters:
                    start, end = setters[0].index("--settings") + 1, setters[0].index("--output")
                    self.assertEqual({f"{name}={value}" for name, value in desired.items()}, set(setters[0][start:end]))
                self.assertNotIn("test-private-key", output.getvalue() + repr(azure.call_args_list))

    def test_chat_output_budget_blocks_unreviewed_keys_overflow_and_changed_protected_settings(self):
        desired = json.loads(deployment.CHAT_OUTPUT_PATH.read_text())
        with patch.object(deployment.Path, "read_text", return_value=json.dumps({**desired, "AZURE_OPENAI_API_KEY": 1})), \
                patch.object(deployment, "azure") as azure, self.assertRaises(ValueError):
            deployment.configure_chat_output_budget(APP, GROUP)
        azure.assert_not_called()
        with patch.object(deployment, "azure", return_value='[{"name":"AZURE_OPENAI_CHAT_INPUT_TOKENS","value":"127000"}]'), \
                self.assertRaises(ValueError):
            deployment.configure_chat_output_budget(APP, GROUP)
        original = {"AZURE_OPENAI_CHAT_INPUT_TOKENS": "131072", "AZURE_OPENAI_CHAT_CONTEXT_WINDOW_TOKENS": "400000"}
        updated = {**original, **{name: str(value) for name, value in desired.items()}}
        for confirmed in (original, {**updated, "AZURE_OPENAI_CHAT_INPUT_TOKENS": "131000"}):
            listing = lambda values: json.dumps([{"name": name, "value": value} for name, value in values.items()])
            with patch.object(deployment, "azure", side_effect=[listing(original), "", listing(confirmed)]), \
                    self.assertRaises(RuntimeError):
                deployment.configure_chat_output_budget(APP, GROUP)

    def test_managed_policy_rejects_bad_source_missing_credentials_and_invalid_hosted_state(self):
        for desired in ([], {"123": {}}, {"-123": {"Unknown": True}}, {"-123": {"NaturalRequestsEnabled": "true"}}):
            with self.subTest(desired=desired), patch.object(deployment.Path, "read_text", return_value=json.dumps(desired)), \
                    patch.object(deployment, "azure") as azure, self.assertRaises(ValueError):
                deployment.configure_conversation_policy(APP, GROUP)
            azure.assert_not_called()
        desired = {"-123": {"NaturalRequestsEnabled": True}}
        with patch.object(deployment.Path, "read_text", return_value=json.dumps(desired)), \
                patch.object(deployment, "azure", return_value="[]") as azure, self.assertRaises(RuntimeError):
            deployment.configure_conversation_policy(APP, GROUP)
        self.assertEqual(1, azure.call_count)
        with patch.object(deployment.Path, "read_text", return_value='{"-123":{"NaturalRequestsEnabled":false}}'), \
                patch.object(deployment, "azure", return_value='[{"name":"ConversationPolicyChats","value":"[]"}]') as azure, \
                self.assertRaises(ValueError):
            deployment.configure_conversation_policy(APP, GROUP)
        self.assertEqual(1, azure.call_count)

    def test_managed_policy_requires_readback_confirmation(self):
        desired = '{"-123":{"NaturalRequestsEnabled":false}}'
        with patch.object(deployment.Path, "read_text", return_value=desired), \
                patch.object(deployment, "azure", side_effect=["[]", "", "[]"]), self.assertRaises(RuntimeError):
            deployment.configure_conversation_policy(APP, GROUP)

    def test_unhealthy_artifact_never_reads_keys_or_runs_announcer(self):
        with patch.object(deployment, "wait_for_health", side_effect=RuntimeError("unhealthy")), \
                patch.object(deployment, "azure") as azure, patch.object(deployment.subprocess, "run") as run:
            with self.assertRaises(RuntimeError):
                deployment.announce(APP, GROUP, SHA, URL, "release-announcer.dll")
        azure.assert_not_called()
        run.assert_not_called()

    def test_one_shot_runner_uses_existing_hosted_keys_only_in_environment_and_propagates_failure(self):
        events = []
        hosted = {
            "AZURE_OPENAI_ENDPOINT": "https://example.openai.azure.com/",
            "AZURE_OPENAI_API_KEY": "test-existing-azure-key",
            "AZURE_OPENAI_CHAT_DEPLOYMENT": "chat",
            "AZURE_OPENAI_SUMMARY_DEPLOYMENT": "summary",
            "CosmosDbConnectionString": "private-cosmos",
            "CosmosDatabase": "db", "CosmosTimedTasksContainer": "tasks",
            "TELEGRAM_BOT_TOKEN": "private-telegram", "ServiceBusConnection": "not-needed",
            "GITHUB_SHA": "malicious-hosted-override",
        }

        def settings(*args):
            events.append("settings")
            return json.dumps([{"name": name, "value": value} for name, value in hosted.items()])

        for returncode in (0, 1):
            events.clear()
            with tempfile.TemporaryDirectory() as directory:
                runner = Path(directory) / "release-announcer.dll"
                runner.touch()
                with patch.dict(deployment.os.environ, {"GITHUB_TOKEN": "pipeline-token", "GITHUB_SHA": SHA}), \
                        patch.object(deployment, "wait_for_health", side_effect=lambda *a: events.append("health")), \
                        patch.object(deployment, "azure", side_effect=settings) as cli, \
                        patch.object(deployment.subprocess, "run") as run, contextlib.redirect_stdout(io.StringIO()):
                    run.return_value.returncode = returncode
                    run.return_value.stdout = ""
                    run.return_value.stderr = ""
                    if returncode:
                        with self.assertRaises(RuntimeError):
                            deployment.announce(APP, GROUP, SHA, URL, runner)
                    else:
                        deployment.announce(APP, GROUP, SHA, URL, runner)
                    self.assertEqual(["health", "settings"], events)
                    run.assert_called_once()
                    self.assertEqual(["dotnet", str(runner)], run.call_args.args[0])
                    self.assertEqual(hosted["AZURE_OPENAI_API_KEY"], run.call_args.kwargs["env"]["AZURE_OPENAI_API_KEY"])
                    self.assertEqual("pipeline-token", run.call_args.kwargs["env"]["GITHUB_TOKEN"])
                    self.assertEqual(SHA, run.call_args.kwargs["env"]["GITHUB_SHA"])
                    self.assertNotIn("ServiceBusConnection", run.call_args.kwargs["env"])
                    self.assertNotIn(hosted["AZURE_OPENAI_API_KEY"], repr(cli.call_args_list) + repr(run.call_args.args))

    def test_missing_hosted_configuration_never_launches_announcer(self):
        with patch.object(deployment, "wait_for_health"), patch.object(deployment, "azure", return_value="[]"), \
                patch.object(deployment.subprocess, "run") as run, self.assertRaises(RuntimeError):
            deployment.announce(APP, GROUP, SHA, URL, "release-announcer.dll")
        run.assert_not_called()

    def test_health_timeout_and_redirects_fail_closed(self):
        with patch.object(deployment, "request", return_value=health("b" * 40)), self.assertRaises(RuntimeError):
            deployment.wait_for_health(URL, SHA, attempts=2, delay=0)
        with self.assertRaises(ValueError):
            deployment.NoRedirect().redirect_request(None, None, 302, "", {}, "https://elsewhere.example/")


if __name__ == "__main__":
    unittest.main()
