"""Language model runtime for correction.py.

LlamaServer runs Subtitler's own llama.cpp server (runtime/llama, installed by
Setup.cmd) on the correction model for one correction job and stops it afterwards,
which returns its memory before diarization loads. The model downloads into the
recognition cache's models folder on first use, like the recognition models.
"""
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.error
import urllib.request

from correction import RuntimeUnavailable
from progress_reporting import loads_model

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
START_SECONDS, ANSWER_SECONDS = 600, 1800
# The correction model (CorrectionModel in WorkerClient.cs). Its Corrector key is the
# Hugging Face repository and file name; a missing file downloads from the reviewed revision.
CATALOG = {
    "lmstudio-community/Qwen3.5-9B-GGUF/Qwen3.5-9B-Q6_K.gguf": ("lmstudio-community/Qwen3.5-9B-GGUF", "1379f25c6b505a3fc737bd7818cb09389cf807c1"),
}


def model_path(root, repo, revision, name):
    """Where huggingface_hub keeps the file in the cache's models folder (no symlinks on Windows)."""
    return Path(root) / "models" / ("models--" + repo.replace("/", "--")) / "snapshots" / revision / name


def find_server(settings):
    """llama-server from the bundled engine, as Setup.cmd recorded it."""
    path = settings.get("LlamaServerPath")
    if not path or not Path(path).is_file():
        raise RuntimeUnavailable("The correction engine is not installed. Close VEGAS and run Setup.cmd again.")
    return Path(path)


def download(repo, revision, path):
    """Fetches the correction model into the cache's models folder. Inside a model scope the
    worker's download hooks report its bytes, as for the recognition models."""
    from huggingface_hub import hf_hub_download
    hf_hub_download(repo, path.name, revision=revision, cache_dir=str(path.parents[3]))


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def end_with_worker(process):
    """A Windows job that ends the server when this worker exits. Cancelling generation
    kills the worker, and Windows does not end a killed process's children, which would
    leave the model holding GPU memory. Returns the job handle to keep open."""
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes

    class Basic(ctypes.Structure):
        _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64), ("LimitFlags", wintypes.DWORD),
                    ("MinimumWorkingSetSize", ctypes.c_size_t), ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]

    class Extended(ctypes.Structure):
        _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", ctypes.c_uint64 * 6), ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t), ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    job = kernel32.CreateJobObjectW(None, None)
    limits = Extended()
    limits.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not job or not kernel32.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or \
            not kernel32.AssignProcessToJobObject(job, int(process._handle)):
        raise RuntimeUnavailable("The correction server could not be tied to the worker (error %d)." % ctypes.get_last_error())
    return job


def close_job(job):
    if job:
        import ctypes
        ctypes.WinDLL("kernel32").CloseHandle(ctypes.c_void_p(job))


class LlamaServer:
    def __init__(self, settings):
        key = settings["Corrector"]
        if key not in CATALOG:
            raise RuntimeUnavailable("The correction model " + key + " is not one Subtitler offers.")
        repo, revision = CATALOG[key]
        root = Path(settings["CacheDirectory"])
        self.source = (repo, revision)
        self.model = model_path(root, repo, revision, key.rsplit("/", 1)[1])
        self.label = settings.get("CorrectorLabel") or self.model.stem
        self.server = find_server(settings)
        self.log = root / "correction-server.log"
        self.process = self.port = self.job = None
        self.device = "gpu"
        self.timings = []

    @loads_model("Corrector", lambda self: self.label)
    def load(self, context_length):
        if not self.model.is_file():
            try:
                download(*self.source, self.model)
            except Exception as error:
                raise RuntimeUnavailable("The correction model could not be downloaded: " + str(error))
        self.port = free_port()
        args = [str(self.server), "-m", str(self.model), "-c", str(context_length), "--host", "127.0.0.1", "--port", str(self.port),
                "-np", "1", "--no-mmproj", "--reasoning-budget", "0", "-fa", "auto"]
        environment = dict(os.environ)
        # The engine's CUDA runtime DLLs are beside it.
        environment["PATH"] = os.pathsep.join([str(self.server.parent), environment.get("PATH", "")])
        self.log.parent.mkdir(parents=True, exist_ok=True)
        with open(self.log, "w", encoding="utf-8", errors="replace") as log:
            self.process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=environment,
                                            creationflags=NO_WINDOW)
        self.job = end_with_worker(self.process)
        deadline = time.monotonic() + START_SECONDS
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeUnavailable("The correction model stopped while loading. " + self.tail())
            try:
                with urllib.request.urlopen("http://127.0.0.1:%d/health" % self.port, timeout=2) as response:
                    if response.status == 200:
                        return
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(0.25)
        raise RuntimeUnavailable("The correction model did not finish loading in %d seconds." % START_SECONDS)

    def complete(self, request, schema, max_tokens):
        body = {"messages": request, "temperature": 0, "max_tokens": max_tokens, "stream": False,
                "response_format": {"type": "json_schema", "json_schema": {"name": "corrections", "strict": True, "schema": schema}},
                # The server's reasoning budget of 0 ends thinking; the template switch is the model's own.
                "chat_template_kwargs": {"enable_thinking": False}}
        call = urllib.request.Request("http://127.0.0.1:%d/v1/chat/completions" % self.port, data=json.dumps(body).encode("utf-8"),
                                      headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(call, timeout=ANSWER_SECONDS) as response:
                data = json.load(response)
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise RuntimeUnavailable("The correction model did not answer: " + str(error) + " " + self.tail())
        self.timings.append(data.get("timings") or {})
        try:
            return data["choices"][0]["message"].get("content") or ""
        except (KeyError, IndexError, TypeError):
            raise RuntimeUnavailable("The correction model returned an unexpected response.")

    def tail(self, lines=4):
        try:
            text = self.log.read_text(encoding="utf-8", errors="replace").strip().splitlines()
        except OSError:
            return ""
        return " ".join(line.strip() for line in text[-lines:])

    def release(self):
        if self.process is None:
            return
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(10)
        close_job(self.job)
        self.process = self.job = None


def create_runtime(settings):
    return LlamaServer(settings)
