"""Harbor adapter for a self-contained Solvra build.

Set ``time_limit_seconds`` to the Harbor agent timeout minus a small cleanup
allowance. Harbor does not expose its outer timeout through ``AgentContext``, so
the run configuration is the authoritative bridge for task-specific budgets.
"""

from __future__ import annotations

import json
import shlex
from pathlib import Path, PurePosixPath

from pydantic import Field

from harbor.agents.installed.base import BaseInstalledAgent, with_prompt_template
from harbor.agents.model_connection import ModelConnectionSpec
from harbor.agents.options import InstalledAgentOptions
from harbor.environments.base import BaseEnvironment
from harbor.models.agent.context import AgentContext
from harbor.models.trial.paths import EnvironmentPaths


class SolvraOptions(InstalledAgentOptions):
    max_turns: int = Field(default=500, description="Solvra --max-turns.")
    effort: str = Field(default="high", description="Solvra reasoning effort.")
    time_limit_seconds: int = Field(
        default=0,
        ge=0,
        description="Internal deadline; set to Harbor's agent timeout minus cleanup allowance.",
    )


class Solvra(BaseInstalledAgent):
    MODEL_CONNECTION = ModelConnectionSpec(passthrough=True)
    options_model = SolvraOptions
    options: SolvraOptions

    _REMOTE_BIN_DIR = PurePosixPath("/installed-agent/solvra")
    _REMOTE_SECRETS_DIR = PurePosixPath("/tmp/solvra-secrets")
    _REMOTE_SOLVRA_HOME = PurePosixPath("/tmp/solvra-home")
    _OUTPUT_FILENAME = "solvra.txt"
    _INSTRUCTION_FILENAME = "instruction.md"

    @staticmethod
    def name() -> str:
        return "solvra"

    def get_version_command(self) -> str | None:
        return f"{self._REMOTE_BIN_DIR}/Solvra --version"

    def parse_version(self, stdout: str) -> str:
        return stdout.strip().splitlines()[-1].strip()

    def _publish_dir(self) -> Path:
        raw = self._get_env("SOLVRA_PUBLISH_DIR")
        if not raw:
            raise ValueError("SOLVRA_PUBLISH_DIR must point at a self-contained Solvra publish")
        path = Path(raw)
        if not (path / "Solvra").is_file():
            raise ValueError(f"{path}/Solvra not found")
        return path

    async def install(self, environment: BaseEnvironment) -> None:
        try:
            await self.ensure_system_dependencies(environment, ())
        except Exception as exc:  # noqa: BLE001
            self.logger.warning("system package install failed; continuing: %s", str(exc)[:200])
        await environment.upload_dir(self._publish_dir(), self._REMOTE_BIN_DIR.as_posix())
        await self.exec_as_root(
            environment,
            command=(
                f"chmod -R a+rX {self._REMOTE_BIN_DIR} && chmod a+x {self._REMOTE_BIN_DIR}/Solvra"
                f" && {self._REMOTE_BIN_DIR}/Solvra --version"
            ),
            env={"DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1"},
        )

    def _auth_json_path(self) -> Path:
        explicit = self._get_env("CODEX_AUTH_JSON_PATH")
        path = Path(explicit) if explicit else Path.home() / ".codex" / "auth.json"
        if not path.is_file():
            raise ValueError(f"Codex auth.json not found at {path}")
        return path

    def _run_command(
        self,
        solvra_model: str,
        remote_instruction: str,
        output_path: str,
        time_limit_seconds: int,
    ) -> str:
        deadline_arg = ""
        if time_limit_seconds > 0:
            deadline_arg = f"--time-limit-seconds {time_limit_seconds} "
        return (
            f"{self._REMOTE_BIN_DIR}/Solvra run - --auto --no-session --json "
            f"-m {shlex.quote(solvra_model)} --max-turns {int(self.options.max_turns)} "
            f"--effort {shlex.quote(self.options.effort)} {deadline_arg}"
            f'--cwd "$PWD" < {shlex.quote(remote_instruction)} 2>&1 | tee {shlex.quote(output_path)}'
        )

    @with_prompt_template
    async def run(self, instruction: str, environment: BaseEnvironment, context: AgentContext) -> None:
        if not self.model_name:
            raise ValueError("Model name is required, e.g. chatgpt/gpt-5.6-sol")
        provider, _, model = self.model_name.partition("/")
        solvra_model = f"{provider}:{model}" if model else provider

        agent_dir = EnvironmentPaths.agent_dir.as_posix()
        secrets_dir = self._REMOTE_SECRETS_DIR.as_posix()
        remote_auth = (self._REMOTE_SECRETS_DIR / "auth.json").as_posix()
        remote_instruction = f"{agent_dir}/{self._INSTRUCTION_FILENAME}"
        output_path = f"{agent_dir}/{self._OUTPUT_FILENAME}"
        env = {
            "CODEX_HOME": secrets_dir,
            "SOLVRA_HOME": self._REMOTE_SOLVRA_HOME.as_posix(),
            "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        }

        await self.exec_as_agent(
            environment,
            command=f"mkdir -p {shlex.quote(secrets_dir)} {shlex.quote(env['SOLVRA_HOME'])} {shlex.quote(agent_dir)}"
            f" && chmod 700 {shlex.quote(secrets_dir)}",
            env=env,
        )
        await environment.upload_file(self._auth_json_path(), remote_auth)
        if environment.default_user is not None:
            await self.exec_as_root(
                environment,
                command=f"chown {environment.default_user} {shlex.quote(remote_auth)} {shlex.quote(secrets_dir)}",
            )
        await self.exec_as_agent(environment, command=f"chmod 600 {shlex.quote(remote_auth)}", env=env)
        await self._upload_config_text(
            environment,
            content=instruction if instruction.endswith("\n") else instruction + "\n",
            remote_path=remote_instruction,
            filename=self._INSTRUCTION_FILENAME,
        )

        configured_limit = int(self.options.time_limit_seconds)
        cmd = self._run_command(solvra_model, remote_instruction, output_path, configured_limit)
        try:
            await self.exec_as_agent(environment, command=cmd, env=env)
        finally:
            try:
                await self.exec_as_agent(
                    environment,
                    command=(
                        f"rm -rf {agent_dir}/solvra-home && "
                        f"cp -R {shlex.quote(env['SOLVRA_HOME'])} {agent_dir}/solvra-home 2>/dev/null; "
                        f"rm -rf {shlex.quote(secrets_dir)}"
                    ),
                    env=env,
                )
            except Exception:
                pass

    @staticmethod
    def _extract_result_json(text: str) -> dict | None:
        lines = text.splitlines()
        starts = [i for i, line in enumerate(lines) if line.strip() == "{"]
        for start in reversed(starts):
            for end in range(len(lines) - 1, start, -1):
                if lines[end].strip() != "}":
                    continue
                try:
                    return json.loads("\n".join(lines[start : end + 1]))
                except json.JSONDecodeError:
                    break
        return None

    def _partial_audit(self) -> dict:
        totals = {"input": 0, "output": 0}
        completed = failed = interrupted = 0
        audit_dir = self.logs_dir / "solvra-home" / "logs"
        for path in sorted(audit_dir.glob("audit-*.jsonl")) if audit_dir.exists() else []:
            for line in path.read_text(errors="replace").splitlines():
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                kind = event.get("event_type")
                data = event.get("data") or {}
                if kind == "model_call_completed":
                    usage = data.get("usage") or {}
                    totals["input"] += int(usage.get("input") or 0)
                    totals["output"] += int(usage.get("output") or 0)
                    completed += 1
                elif kind == "model_call_failed":
                    failed += 1
                elif kind == "operation_interrupted":
                    interrupted += 1
        return {
            "usage": totals,
            "completed_model_calls": completed,
            "failed_model_calls": failed,
            "interrupted_operations": interrupted,
        }

    def populate_context_post_run(self, context: AgentContext) -> None:
        output_file = self.logs_dir / self._OUTPUT_FILENAME
        result = self._extract_result_json(output_file.read_text(errors="replace")) if output_file.exists() else None
        partial = self._partial_audit()
        usage = (result or {}).get("usage") or partial["usage"]
        context.n_input_tokens = usage.get("input") or None
        context.n_output_tokens = usage.get("output") or None
        cost = (result or {}).get("cost_usd")
        context.cost_usd = cost if cost else None
        context.metadata = {
            "solvra_result": "complete" if result else "partial",
            "solvra_stop_reason": (result or {}).get("stop_reason"),
            "solvra_turns": (result or {}).get("turns"),
            "solvra_error": (result or {}).get("error"),
            "solvra_model": (result or {}).get("model"),
            "solvra_completed_model_calls": partial["completed_model_calls"],
            "solvra_failed_model_calls": partial["failed_model_calls"],
            "solvra_interrupted_operations": partial["interrupted_operations"],
            "solvra_cache_usage": "unavailable",
        }
