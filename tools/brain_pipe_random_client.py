"""Minimal standard-library scripted client for the IronNest Brain named pipe.

The server intentionally handles one JSON request per connection. This client
therefore reconnects for every request and never imports PyTorch or a game
module. It expects an observation response payload shaped like:
{"valid_actions": {"hold": true, "continue": false, "abort": false,
"engageable_track_ids": ["Track-1"]}}

The policy is deliberately deterministic for the first runtime bring-up:
Engage the first legal track, otherwise Hold. It reconnects for each request
because the C# server intentionally handles one request per connection.
"""

from __future__ import annotations

import json
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


def choose_scripted_action(payload: dict[str, Any]) -> dict[str, Any]:
    valid = payload.get("valid_actions", {})
    engageable = valid.get("engageable_track_ids", [])
    if engageable:
        return {"action_kind": "Engage", "track_id": engageable[0]}
    return {"action_kind": "Hold"}


def run(pipe_name: str, steps: int, seed: int) -> None:
    # Keep the seed argument for command-line compatibility with the old
    # random client; the first bring-up policy is intentionally deterministic.
    hello = request(pipe_name, {"type": "hello", "protocol_version": PROTOCOL_VERSION})
    if hello.get("type") == "error":
        raise RuntimeError(hello.get("error", "hello failed"))

    episode_id = hello.get("episode_id")
    runtime_generation = hello.get("runtime_generation", 0)
    print(json.dumps({"event": "hello", "episode": episode_id, "response": hello}, ensure_ascii=False))
    transitions = 0
    for _ in range(steps):
        observation_response = request(
            pipe_name,
            {
                "type": "observation",
                "protocol_version": PROTOCOL_VERSION,
                "episode_id": episode_id,
                "runtime_generation": runtime_generation,
            },
        )
        print(json.dumps({
            "event": "observation_received",
            "episode": observation_response.get("episode_id"),
            "decision_id": observation_response.get("decision_id"),
            "type": observation_response.get("type"),
        }, ensure_ascii=False))
        if observation_response.get("episode_id"):
            episode_id = observation_response["episode_id"]
        if observation_response.get("runtime_generation") is not None:
            runtime_generation = observation_response["runtime_generation"]
        if observation_response.get("type") == "terminal":
            print(json.dumps(observation_response, ensure_ascii=False))
            # The game may still be between OLD_MISSION_GONE and
            # RESET_SUCCESS. Re-handshake so the next request carries the
            # current episode generation instead of a stale id.
            hello = request(pipe_name, {"type": "hello", "protocol_version": PROTOCOL_VERSION})
            episode_id = hello.get("episode_id", episode_id)
            runtime_generation = hello.get("runtime_generation", runtime_generation)
            continue
        if not observation_response.get("accepted", True):
            # A mission transition can invalidate the request between two
            # reconnects. The response carries the current generation; retry
            # the next loop with that generation instead of inventing an
            # action for a stale observation.
            continue
        if observation_response.get("type") in {"waiting", "resetting"}:
            continue
        payload = json.loads(observation_response.get("payload") or "{}")
        valid = payload.get("valid_actions", {})
        action = choose_scripted_action(payload)
        print(json.dumps({
            "event": "action_selected",
            "episode": episode_id,
            "decision_id": observation_response.get("decision_id"),
            "valid_actions": valid,
            "action": action,
        }, ensure_ascii=False))
        action_response = request(
            pipe_name,
            {
                "type": "action",
                "protocol_version": PROTOCOL_VERSION,
                "episode_id": episode_id,
                "runtime_generation": runtime_generation,
                "decision_id": observation_response.get("decision_id"),
                **action,
            },
        )
        transitions += 1
        print(json.dumps({
            "event": "transition",
            "transition": transitions,
            "episode": action_response.get("episode_id"),
            "decision_id": action_response.get("decision_id"),
            "action": action,
            "accepted": action_response.get("accepted"),
            "error": action_response.get("error"),
            "reward": json.loads(action_response.get("payload") or "{}").get("reward"),
            "response": action_response,
        }, ensure_ascii=False))
        if action_response.get("runtime_generation") is not None:
            runtime_generation = action_response["runtime_generation"]
        if action_response.get("type") == "terminal":
            hello = request(pipe_name, {"type": "hello", "protocol_version": PROTOCOL_VERSION})
            episode_id = hello.get("episode_id", episode_id)
            runtime_generation = hello.get("runtime_generation", runtime_generation)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("usage: brain_pipe_random_client.py <pipe-name> [steps] [seed]")
    run(
        sys.argv[1],
        int(sys.argv[2]) if len(sys.argv) > 2 else 10,
        int(sys.argv[3]) if len(sys.argv) > 3 else 42,
    )
