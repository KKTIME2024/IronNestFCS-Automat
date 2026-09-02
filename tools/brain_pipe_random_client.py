"""Minimal standard-library client for the IronNest Brain named pipe.

The server intentionally handles one JSON request per connection. This client
therefore reconnects for every request and never imports PyTorch or a game
module. It expects an observation response payload shaped like:
{"valid_actions": {"hold": true, "continue": false, "abort": false,
"engageable_track_ids": ["Track-1"]}}
"""

from __future__ import annotations

import json
import random
import sys
from typing import Any


PROTOCOL_VERSION = "1"


def pipe_path(name: str) -> str:
    if sys.platform != "win32":
        raise RuntimeError("the named-pipe client currently targets Windows")
    return "\\\\.\\pipe\\" + name


def request(name: str, message: dict[str, Any]) -> dict[str, Any]:
    encoded = (json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8")
    with open(pipe_path(name), "r+b", buffering=0) as pipe:
        pipe.write(encoded)
        response = bytearray()
        while True:
            byte = pipe.read(1)
            if not byte:
                break
            if byte == b"\n":
                break
            response.extend(byte)
    if not response:
        raise RuntimeError("named pipe closed without a response")
    return json.loads(response.decode("utf-8"))


def choose_random_action(payload: dict[str, Any], rng: random.Random) -> dict[str, Any]:
    valid = payload.get("valid_actions", {})
    actions: list[dict[str, Any]] = []
    if valid.get("hold"):
        actions.append({"action_kind": "Hold"})
    if valid.get("continue"):
        actions.append({"action_kind": "ContinueCurrentMission"})
    if valid.get("abort"):
        actions.append({"action_kind": "AbortIfSafe"})
    for track_id in valid.get("engageable_track_ids", []):
        actions.append({"action_kind": "Engage", "track_id": track_id})
    return rng.choice(actions) if actions else {"action_kind": "Hold"}


def run(pipe_name: str, steps: int, seed: int) -> None:
    rng = random.Random(seed)
    hello = request(pipe_name, {"type": "hello", "protocol_version": PROTOCOL_VERSION})
    if hello.get("type") == "error":
        raise RuntimeError(hello.get("error", "hello failed"))

    episode_id = hello.get("episode_id")
    transitions = 0
    for _ in range(steps):
        observation_response = request(
            pipe_name,
            {
                "type": "observation",
                "protocol_version": PROTOCOL_VERSION,
                "episode_id": episode_id,
            },
        )
        if observation_response.get("type") == "terminal":
            print(json.dumps(observation_response, ensure_ascii=False))
            break
        payload = json.loads(observation_response.get("payload") or "{}")
        action = choose_random_action(payload, rng)
        action_response = request(
            pipe_name,
            {
                "type": "action",
                "protocol_version": PROTOCOL_VERSION,
                "episode_id": episode_id,
                "decision_id": observation_response.get("decision_id"),
                **action,
            },
        )
        transitions += 1
        print(json.dumps({"transition": transitions, "action": action, "response": action_response}, ensure_ascii=False))
        if action_response.get("type") == "terminal":
            break


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("usage: brain_pipe_random_client.py <pipe-name> [steps] [seed]")
    run(
        sys.argv[1],
        int(sys.argv[2]) if len(sys.argv) > 2 else 10,
        int(sys.argv[3]) if len(sys.argv) > 3 else 42,
    )
