"""Run a check with a disposable POSIX server and clean up its entire process group."""
import argparse
import os
import signal
import socket
import subprocess
import tempfile
import time


def listening(port):
    with socket.socket() as connection:
        connection.settimeout(0.2)
        return connection.connect_ex(('127.0.0.1', port)) == 0


def stop_group(process):
    # The shell may exit first; descendants still need a graceful shutdown window.
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        process.wait()
        return
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        process.poll()
        try:
            os.killpg(process.pid, 0)
        except ProcessLookupError:
            break
        time.sleep(0.05)
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait()


def run(server, port, timeout, command):
    if os.name != 'posix':
        raise RuntimeError('This test launcher requires POSIX process groups.')
    if listening(port):
        raise RuntimeError(f'Port {port} is already occupied; refusing to test an unrelated server.')
    with tempfile.TemporaryFile(mode='w+') as log:
        process = subprocess.Popen(server, shell=True, start_new_session=True, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + timeout
            while not listening(port):
                if process.poll() is not None:
                    raise RuntimeError(f'Server exited before listening (exit {process.returncode}).')
                if time.monotonic() >= deadline:
                    raise TimeoutError(f'Server did not listen on port {port} within {timeout} seconds.')
                time.sleep(0.05)
            return subprocess.run(command).returncode
        except Exception:
            log.seek(0)
            print(log.read()[-12000:], flush=True)
            raise
        finally:
            stop_group(process)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--timeout', type=float, default=30)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('Provide the check command after --.')
    raise SystemExit(run(args.server, args.port, args.timeout, command))
