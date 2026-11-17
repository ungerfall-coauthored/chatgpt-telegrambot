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
APP = "fapp-coauthored-telegram-test"
GROUP = "rg-coauthored-telegram-test"
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
            ("AZURE_FUNCTIONAPP_NAME", "fapp-upstream-telegram-webhook"),
            ("AZURE_RESOURCE_GROUP", "rg-upstream"),
        ):
            with self.subTest(field=field), self.assertRaises(ValueError):
                deployment.context({**valid, field: value})

    def test_target_verifies_runtime_and_azure_hostname_before_deploy(self):
        metadata = {
            "name": APP, "resourceGroup": GROUP, "host": APP + "-unique.westeurope-01.azurewebsites.net",
            "kind": "functionapp,linux", "runtime": {"name": "dotnet-isolated", "version": "10.0"},
        }
        with patch.object(deployment, "azure", return_value=json.dumps(metadata)):
            self.assertEqual("https://" + metadata["host"], deployment.target(APP, GROUP))
        for update in (
            {"runtime": None}, {"runtime": {"name": "dotnet-isolated", "version": "8.0"}},
            {"host": "https://evil.example"}, {"host": "other.azurewebsites.net"},
            {"host": APP + ".azurewebsites.net.evil.example"},
        ):
            with self.subTest(update=update), patch.object(
                deployment, "azure", return_value=json.dumps({**metadata, **update})
            ), self.assertRaises(ValueError):
                deployment.target(APP, GROUP)

    def test_health_requires_exact_artifact_and_gate_configuration(self):
        with patch.object(deployment, "request", side_effect=[
            health("b" * 40), health(requiresApproval=False), (200, b"[]"), health()
        ]) as request:
            deployment.wait_for_health(URL, SHA, announcements=True, attempts=4, delay=0)
        self.assertEqual(4, request.call_count)

    def test_unhealthy_artifact_never_changes_settings_or_activates(self):
        with patch.object(deployment, "wait_for_health", side_effect=RuntimeError("unhealthy")), \
                patch.object(deployment, "azure") as azure, patch.object(deployment, "request") as request:
            with self.assertRaises(RuntimeError):
                deployment.activate(APP, GROUP, SHA, URL)
        azure.assert_not_called()
        request.assert_not_called()

    def test_unhealthy_restarted_configuration_never_fetches_key_or_activates(self):
        with patch.object(deployment, "wait_for_health", side_effect=[None, RuntimeError("unhealthy")]), \
                patch.object(deployment, "azure") as azure, patch.object(deployment, "request") as request:
            with self.assertRaises(RuntimeError):
                deployment.activate(APP, GROUP, SHA, URL)
        azure.assert_called_once()
        self.assertIn("appsettings", azure.call_args.args)
        request.assert_not_called()

    def test_activation_occurs_only_after_health_and_keeps_key_out_of_urls_and_cli(self):
        events = []
        key = "test-function-key-that-must-not-leak"

        def azure(*args):
            events.append("key" if "keys" in args else "settings")
            return key if "keys" in args else ""

        def ready(*args, **kwargs):
            events.append("health-with-gate" if kwargs.get("announcements") else "health")

        def activate(url, supplied_key=None):
            events.append("activate")
            self.assertEqual(f"{URL}/api/releases/{SHA}/activate", url)
            self.assertEqual(key, supplied_key)
            self.assertNotIn(key, url)
            return 204, b""

        output = io.StringIO()
        with patch.object(deployment, "azure", side_effect=azure) as cli, \
                patch.object(deployment, "wait_for_health", side_effect=ready), \
                patch.object(deployment, "request", side_effect=activate), contextlib.redirect_stdout(output):
            deployment.activate(APP, GROUP, SHA, URL)
        self.assertEqual(["health", "settings", "health-with-gate", "key", "activate"], events)
        self.assertTrue(output.getvalue().startswith("::add-mask::" + key))
        self.assertNotIn(key, repr(cli.call_args_list))

    def test_health_timeout_and_redirects_fail_closed(self):
        with patch.object(deployment, "request", return_value=health("b" * 40)), self.assertRaises(RuntimeError):
            deployment.wait_for_health(URL, SHA, attempts=2, delay=0)
        with self.assertRaises(ValueError):
            deployment.NoRedirect().redirect_request(None, None, 302, "", {}, "https://elsewhere.example/")


if __name__ == "__main__":
    unittest.main()
