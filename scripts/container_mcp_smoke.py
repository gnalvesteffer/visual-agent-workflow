"""Build and exercise a digest-pinned MCP worker in a disposable restricted container."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "samples" / "IsolatedMcpServer" / "IsolatedMcpServer.csproj"
DOCKERFILE = ROOT / "samples" / "IsolatedMcpServer" / "Dockerfile"
TEST_PROJECT = ROOT / "tests" / "VisualAgentWorkflow.Mcp.Tests" / "VisualAgentWorkflow.Mcp.Tests.csproj"


def run(arguments, **kwargs):
    subprocess.run(arguments, cwd=ROOT, check=True, **kwargs)


def main():
    image_tag = "vaw-isolated-mcp-smoke:" + uuid.uuid4().hex[:12]
    image_id = None
    try:
        with tempfile.TemporaryDirectory(prefix="vaw-container-mcp-") as temporary:
            temp = Path(temporary)
            published = temp / "published"
            context = temp / "context"
            application = context / "application"
            docker_config = temp / "docker-config"
            application.mkdir(parents=True)
            docker_config.mkdir()
            run(["dotnet", "publish", str(PROJECT), "--configuration", "Release", "--no-restore", "--output", str(published)])
            shutil.copytree(published, application, dirs_exist_ok=True)
            shutil.copy2(DOCKERFILE, context / "Dockerfile")
            build_environment = os.environ.copy()
            build_environment["DOCKER_CONFIG"] = str(docker_config)
            run(["docker", "build", "--pull", "--tag", image_tag, "--file", str(context / "Dockerfile"), str(context)],
                env=build_environment)
            image_id = subprocess.check_output(
                ["docker", "image", "inspect", image_tag, "--format", "{{.Id}}"], text=True, cwd=ROOT
            ).strip()

            environment = os.environ.copy()
            environment["VAW_CONTAINER_MCP_IMAGE"] = image_id
            environment["VAW_CONTAINER_MCP_HOST_SECRET"] = "host-secret-must-not-cross-boundary"
            run([
                "dotnet", "test", str(TEST_PROJECT), "--no-build", "--no-restore", "--configuration", "Release",
                "--filter", "Category=ContainerSandbox", "--logger", "console;verbosity=normal"
            ], env=environment)
    finally:
        if image_id:
            subprocess.run(["docker", "image", "rm", "--force", image_id], cwd=ROOT, check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            subprocess.run(["docker", "image", "rm", "--force", image_tag], cwd=ROOT, check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    print("Container MCP discovery, invocation, node grants, and network/filesystem/environment isolation passed.")


if __name__ == "__main__":
    main()
