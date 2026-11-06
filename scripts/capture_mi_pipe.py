from __future__ import annotations

import argparse
import json
import pathlib
import threading
import time

import frida


AGENT = r"""
const target = "\\\\.\\pipe\\E09DF953-5B84-4CB7-A23B-969218E24781".toLowerCase();
const pipeHandles = new Set();
const pendingReads = new Map();

function handleKey(value) {
  return value.toString();
}

const createFileW = Module.getGlobalExportByName("CreateFileW");
Interceptor.attach(createFileW, {
  onEnter(args) {
    this.path = args[0].isNull() ? "" : args[0].readUtf16String();
  },
  onLeave(result) {
    if (this.path && this.path.toLowerCase() === target && !result.equals(ptr("-1"))) {
      pipeHandles.add(handleKey(result));
      send({ kind: "pipe-open", handle: handleKey(result), path: this.path });
    }
  }
});

const createFileA = Module.getGlobalExportByName("CreateFileA");
Interceptor.attach(createFileA, {
  onEnter(args) {
    this.path = args[0].isNull() ? "" : args[0].readUtf8String();
  },
  onLeave(result) {
    if (this.path && this.path.toLowerCase() === target && !result.equals(ptr("-1"))) {
      pipeHandles.add(handleKey(result));
      send({ kind: "pipe-open", api: "CreateFileA", handle: handleKey(result), path: this.path });
    }
  }
});

const closeHandle = Module.getGlobalExportByName("CloseHandle");
Interceptor.attach(closeHandle, {
  onEnter(args) {
    pipeHandles.delete(handleKey(args[0]));
  }
});

const writeFile = Module.getGlobalExportByName("WriteFile");
Interceptor.attach(writeFile, {
  onEnter(args) {
    const requested = args[2].toUInt32();
    if (requested < 4 || requested > 65536)
      return;

    const captured = Math.min(requested, 65536);
    let bytes = "";
    try {
      const data = args[1].readByteArray(captured);
      const view = new Uint8Array(data);
      const idLength = view[2] | (view[3] << 8);
      const looksLikeEnvelope = (view[0] === 1 || view[0] === 2) &&
        view[1] === 0 && idLength <= requested - 4;
      if (!pipeHandles.has(handleKey(args[0])) && !looksLikeEnvelope)
        return;
      bytes = Array.from(view, b => b.toString(16).padStart(2, "0")).join("");
    } catch (error) {
      send({ kind: "capture-error", error: String(error), requested });
      return;
    }
    send({
      kind: "pipe-write",
      api: "WriteFile",
      handle: handleKey(args[0]),
      requested,
      captured,
      hex: bytes
    });
  }
});

const ntWriteFile = Module.getGlobalExportByName("NtWriteFile");
Interceptor.attach(ntWriteFile, {
  onEnter(args) {
    const requested = args[6].toUInt32();
    if (requested < 4 || requested > 65536)
      return;
    try {
      const data = args[5].readByteArray(requested);
      const view = new Uint8Array(data);
      const idLength = view[2] | (view[3] << 8);
      const looksLikeEnvelope = (view[0] === 1 || view[0] === 2) &&
        view[1] === 0 && idLength <= requested - 4;
      if (!pipeHandles.has(handleKey(args[0])) && !looksLikeEnvelope)
        return;
      send({
        kind: "pipe-write",
        api: "NtWriteFile",
        handle: handleKey(args[0]),
        requested,
        captured: requested,
        hex: Array.from(view, b => b.toString(16).padStart(2, "0")).join("")
      });
    } catch (error) {
      send({ kind: "capture-error", api: "NtWriteFile", error: String(error), requested });
    }
  }
});

const ntFsControlFile = Module.getGlobalExportByName("NtFsControlFile");
Interceptor.attach(ntFsControlFile, {
  onEnter(args) {
    const requested = args[7].toUInt32();
    if (args[6].isNull() || requested < 4 || requested > 65536)
      return;
    try {
      const data = args[6].readByteArray(requested);
      const view = new Uint8Array(data);
      const idLength = view[2] | (view[3] << 8);
      const looksLikeEnvelope = (view[0] === 1 || view[0] === 2) &&
        view[1] === 0 && idLength <= requested - 4;
      if (!pipeHandles.has(handleKey(args[0])) && !looksLikeEnvelope)
        return;
      send({
        kind: "pipe-write",
        api: "NtFsControlFile",
        handle: handleKey(args[0]),
        requested,
        captured: requested,
        hex: Array.from(view, b => b.toString(16).padStart(2, "0")).join("")
      });
    } catch (error) {
      send({ kind: "capture-error", api: "NtFsControlFile", error: String(error), requested });
    }
  }
});

function emitRead(api, handle, buffer, length) {
  if (length <= 0 || length > 65536 || buffer.isNull())
    return;
  try {
    const data = buffer.readByteArray(length);
    const view = new Uint8Array(data);
    if (view.length < 4)
      return;
    const idLength = view[2] | (view[3] << 8);
    const looksLikeEnvelope = ((view[0] === 1 || view[0] === 2) &&
      view[1] === 0 && idLength <= length - 4) ||
      (view[0] === 0 && view[1] === 0 && view[2] === 0 && view[3] === 0);
    if (!pipeHandles.has(handleKey(handle)) && !looksLikeEnvelope)
      return;
    send({
      kind: "pipe-read",
      api,
      handle: handleKey(handle),
      length,
      hex: Array.from(view, b => b.toString(16).padStart(2, "0")).join("")
    });
  } catch (error) {
    send({ kind: "capture-error", api, error: String(error), length });
  }
}

const readFile = Module.getGlobalExportByName("ReadFile");
Interceptor.attach(readFile, {
  onEnter(args) {
    const requested = args[2].toUInt32();
    if (requested < 4 || requested > 65536)
      return;
    this.handle = args[0];
    this.buffer = args[1];
    this.bytesRead = args[3];
    this.overlapped = args[4];
    this.tracked = true;
    if (!this.overlapped.isNull()) {
      pendingReads.set(handleKey(this.overlapped), {
        handle: this.handle,
        buffer: this.buffer
      });
    }
  },
  onLeave(result) {
    if (!this.tracked || result.toInt32() === 0 || this.bytesRead.isNull())
      return;
    emitRead("ReadFile", this.handle, this.buffer, this.bytesRead.readU32());
    if (!this.overlapped.isNull())
      pendingReads.delete(handleKey(this.overlapped));
  }
});

const getOverlappedResult = Module.getGlobalExportByName("GetOverlappedResult");
Interceptor.attach(getOverlappedResult, {
  onEnter(args) {
    this.handle = args[0];
    this.overlapped = args[1];
    this.bytesTransferred = args[2];
  },
  onLeave(result) {
    if (result.toInt32() === 0 || this.bytesTransferred.isNull())
      return;
    const pending = pendingReads.get(handleKey(this.overlapped));
    if (!pending)
      return;
    emitRead("GetOverlappedResult", pending.handle, pending.buffer, this.bytesTransferred.readU32());
    pendingReads.delete(handleKey(this.overlapped));
  }
});

send({ kind: "ready" });
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="Passively capture Xiaomi G Command Center pipe writes.")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--exe", help="Absolute path to XiaomiGCommandCenter.exe")
    source.add_argument("--pid", type=int, action="append", help="Attach to an existing process; repeat for CEF children")
    parser.add_argument("--output", required=True, help="JSONL output path")
    parser.add_argument("--seconds", type=float, default=20.0)
    args = parser.parse_args()

    output = pathlib.Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)

    device = frida.get_local_device()
    spawned = not args.pid
    if spawned:
        executable = pathlib.Path(args.exe).resolve(strict=True)
        pids = [device.spawn([str(executable)], cwd=str(executable.parent))]
    else:
        pids = args.pid
    sessions = []
    scripts = []
    stream_lock = threading.Lock()

    with output.open("w", encoding="utf-8") as stream:
        def make_handler(attached_pid):
            def on_message(message, data):
                record = {
                    "timestamp": time.time(),
                    "pid": attached_pid,
                    "message": message,
                }
                if data is not None:
                    record["data_hex"] = bytes(data).hex()
                with stream_lock:
                    stream.write(json.dumps(record, ensure_ascii=False) + "\n")
                    stream.flush()
            return on_message

        for pid in pids:
            session = device.attach(pid)
            script = session.create_script(AGENT)
            script.on("message", make_handler(pid))
            script.load()
            sessions.append(session)
            scripts.append(script)
        if spawned:
            device.resume(pids[0])
        print(f"CAPTURE_PIDS={','.join(str(pid) for pid in pids)}")
        print(f"CAPTURE_OUTPUT={output}")
        time.sleep(max(1.0, args.seconds))

    for session in sessions:
        session.detach()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
