"""Regression checks for test-launcher ownership and child cleanup."""
import importlib.util
import os
from pathlib import Path
import shlex
import socket
import sys
import tempfile
import time
import unittest

SPEC = importlib.util.spec_from_file_location('owned_server', Path(__file__).parents[1] / 'with_owned_server.py')
LAUNCHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LAUNCHER)


@unittest.skipUnless(os.name == 'posix', 'POSIX test launcher')
class OwnedServerTests(unittest.TestCase):
    def free_port(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            return listener.getsockname()[1]

    def test_cleans_server_child_when_check_succeeds_or_fails(self):
        for exit_code in (0, 7):
            with self.subTest(exit_code=exit_code):
                port = self.free_port()
                # A waiting shell deliberately remains the parent of the HTTP server.
                server = f'{shlex.quote(sys.executable)} -m http.server {port} --bind 127.0.0.1 & wait'
                self.assertEqual(LAUNCHER.run(server, port, 10, [sys.executable, '-c', f'raise SystemExit({exit_code})']), exit_code)
                deadline = time.monotonic() + 3
                while LAUNCHER.listening(port) and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertFalse(LAUNCHER.listening(port), 'Child server survived launcher cleanup')

    def test_occupied_port_does_not_start_or_kill_a_server(self):
        with socket.socket() as listener, tempfile.TemporaryDirectory() as temporary:
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            marker = Path(temporary) / 'unexpected'
            with self.assertRaisesRegex(RuntimeError, 'already occupied'):
                LAUNCHER.run(f'touch {shlex.quote(str(marker))}', listener.getsockname()[1], 1, ['true'])
            self.assertFalse(marker.exists())
            self.assertTrue(LAUNCHER.listening(listener.getsockname()[1]))

    def test_startup_timeout_cleans_a_child_listening_on_the_wrong_port(self):
        actual_port = self.free_port()
        expected_port = self.free_port()
        server = f'{shlex.quote(sys.executable)} -m http.server {actual_port} --bind 127.0.0.1 & wait'
        with self.assertRaises(TimeoutError):
            LAUNCHER.run(server, expected_port, 0.3, ['true'])
        self.assertFalse(LAUNCHER.listening(actual_port))

    def test_startup_failure_is_reported(self):
        with self.assertRaisesRegex(RuntimeError, 'exited before listening'):
            LAUNCHER.run('exit 9', self.free_port(), 2, ['true'])


if __name__ == '__main__':
    unittest.main()
