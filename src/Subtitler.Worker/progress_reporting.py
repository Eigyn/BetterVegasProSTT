"""Structured, non-console progress for the isolated recognition worker.

The installed HF versions expose different download APIs. Adapt their byte bars
inside this process only; do not change package files or model-loading behavior.
"""
from contextlib import contextmanager
from functools import wraps
import fnmatch
import importlib
import inspect
from pathlib import Path
import sys
import threading
import time

_emit = None
_scope = None
_local = threading.local()
_installed = False


def configure(emit):
    global _emit
    _emit = emit


def report(stage, *, model_type=None, model_name=None, completed=None, total=None, percent=None, detail=None):
    parts = [stage + (" " + model_type if model_type else "")]
    if model_name:
        parts.append(model_name)
    if stage == "Downloading" and percent is not None:
        parts.append(str(max(0, min(100, int(percent)))) + "/100")
    elif completed is not None and total:
        parts.append(str(completed) + "/" + str(total))
        percent = min(100, int(100 * completed / total))
    _emit("Progress", Stage=stage, Message=" · ".join(parts), ModelType=model_type, ModelName=model_name,
          Completed=completed, Total=total, Percent=percent, Detail=detail)


class Download:
    """Aggregate verified/cache bytes and live byte positions, not file counts."""
    def __init__(self, scope, sizes=None, cached=None, detail=""):
        self.scope, self.sizes = scope, dict(sizes or {})
        self.positions = dict(cached or {})
        self.done = set(self.positions)
        self.detail = detail
        self.started = False
        self.lock = threading.RLock()
        self.last_percent, self.last_time = -1, 0

    def update(self, key, position, size=None, complete=False):
        with self.lock:
            self.started = True
            if size is not None and size > 0:
                self.sizes[key] = size
            self.positions[key] = max(0, position)
            if complete:
                self.done.add(key)
            known = bool(self.sizes) and all(n is not None for n in self.sizes.values())
            total = sum(self.sizes.values()) if known else 0
            current = sum(min(self.positions.get(k, 0), n) for k, n in self.sizes.items()) if known else 0
            finished = known and self.done.issuperset(self.sizes)
            percent = (100 if finished else min(99, int(current * 100 / total))) if total else None
            now = time.monotonic()
            if percent == self.last_percent or (percent not in (0, 100, None) and now - self.last_time < .15):
                return
            self.last_percent, self.last_time = percent, now
            report("Downloading", model_type=self.scope.kind, model_name=self.scope.name, percent=percent,
                   detail=self.detail + ("\n" if self.detail else "") + key)

    def complete(self, key, path):
        size = Path(path).stat().st_size
        expected = self.sizes.get(key)
        if expected is not None and size != expected:
            raise OSError("Downloaded model file has an unexpected size: " + key)
        self.update(key, size, size, complete=True)


class ByteBar:
    """The byte-bar subset used by HF HTTP/Xet and torch.hub downloads."""
    def __init__(self, download, key, total=None, initial=0):
        self.download, self.key, self.total, self.n = download, key, total, initial
        self.format_dict = {"rate": None}
        download.update(key, self.n, total)

    def update(self, amount=1):
        self.n += amount
        self.download.update(self.key, self.n, self.total)

    def refresh(self):
        self.download.update(self.key, self.n, self.total)

    def reset(self, total=None):
        self.total, self.n = total, 0
        self.refresh()

    def set_postfix_str(self, *args, **kwargs):
        pass

    def close(self):
        pass  # A closed/failed transfer is not evidence of a complete file.

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class ModelScope:
    def __init__(self, kind, name, device=None):
        self.kind, self.name, self.device, self.group = kind, name, device, None

    def loading(self):
        report("Loading", model_type=self.kind, model_name=self.name, detail="Device: " + str(self.device or "cpu"))


def _call_arguments(function, args, kwargs):
    signature = inspect.signature(function)
    # Hub 1.x's validation wrapper accepts/removes deprecated options (e.g.
    # proxies) absent from the wrapped signature. Introspection must not reject
    # those before the library can handle them. Forward original kwargs unchanged.
    if not any(p.kind == inspect.Parameter.VAR_KEYWORD for p in signature.parameters.values()):
        kwargs = {k: v for k, v in kwargs.items() if k in signature.parameters}
    return signature.bind_partial(*args, **kwargs).arguments


def _snapshot_group(scope, options):
    """Exact requested snapshot manifest. Failed/offline metadata uses file bars."""
    from huggingface_hub import HfApi, try_to_load_from_cache
    repo = options["repo_id"]
    if options.get("local_files_only"):
        return None
    try:
        info = HfApi().model_info(repo, revision=options.get("revision"), files_metadata=True,
                                  token=options.get("token"), timeout=3)
        allow, ignore = options.get("allow_patterns"), options.get("ignore_patterns")
        allow = [allow] if isinstance(allow, str) else allow
        ignore = [ignore] if isinstance(ignore, str) else ignore
        files = [f for f in info.siblings if (not allow or any(fnmatch.fnmatch(f.rfilename, p) for p in allow))
                 and not (ignore and any(fnmatch.fnmatch(f.rfilename, p) for p in ignore))]
        sizes, cached = {}, {}
        for file in files:
            sizes[file.rfilename] = file.size
            found = try_to_load_from_cache(repo, file.rfilename, cache_dir=options.get("cache_dir"), revision=info.sha)
            if not options.get("force_download") and isinstance(found, str) and Path(found).is_file():
                if file.size is not None and Path(found).stat().st_size == file.size:
                    cached[file.rfilename] = file.size
        return Download(scope, sizes, cached, repo)
    except Exception:
        # Progress metadata must never make a cached/offline model unusable.
        return None


def install_download_hooks():
    global _installed
    if _installed:
        _install_torch_hook()
        return
    import huggingface_hub as hub
    files = importlib.import_module("huggingface_hub.file_download")
    snapshots = importlib.import_module("huggingface_hub._snapshot_download")
    original_file, original_snapshot = files.hf_hub_download, snapshots.snapshot_download
    original_context = files._get_progress_bar_context
    original_xet = files.xet_get

    @wraps(original_file)
    def file_download(*args, **kwargs):
        if _scope is None:
            return original_file(*args, **kwargs)
        scope = _scope
        options = _call_arguments(original_file, args, kwargs)
        key = options["filename"]
        group = scope.group or Download(scope, detail=options["repo_id"])
        previous = getattr(_local, "transfer", None)
        _local.transfer = group, key
        try:
            path = original_file(*args, **kwargs)
            # New HF versions can return a dry-run record rather than a path.
            if isinstance(path, str) and group.started:
                group.complete(key, path)
            return path
        finally:
            _local.transfer = previous
            if scope.group is None and group.started:
                scope.loading()

    @wraps(original_snapshot)
    def snapshot_download(*args, **kwargs):
        if _scope is None:
            return original_snapshot(*args, **kwargs)
        scope = _scope
        options = _call_arguments(original_snapshot, args, kwargs)
        previous = scope.group
        group = scope.group = _snapshot_group(scope, options)
        try:
            kwargs["max_workers"] = 1  # Avoid Windows cache/symlink setup races.
            return original_snapshot(*args, **kwargs)
        finally:
            scope.group = previous
            if group is not None and group.started:
                scope.loading()

    def bar(total=None, initial=0, desc=None, **kwargs):
        previous = kwargs.get("_tqdm_bar")
        if isinstance(previous, ByteBar):
            return previous
        transfer = getattr(_local, "transfer", None)
        if _scope is not None:
            group, key = transfer or (Download(_scope), desc or "Model files")
            return ByteBar(group, key, total, initial)
        return original_context(total=total, initial=initial, desc=desc, **kwargs)

    @wraps(original_xet)
    def xet_download(*args, **kwargs):
        # HF 1.x's Xet reporter bypasses the HTTP byte-bar context. Supplying
        # an external reconstruction bar works for both installed HF versions.
        if _scope is not None:
            options = _call_arguments(original_xet, args, kwargs)
            kwargs["_tqdm_bar"] = bar(total=options.get("expected_size"), desc=options.get("displayed_filename"))
        return original_xet(*args, **kwargs)

    hub.hf_hub_download = files.hf_hub_download = snapshots.hf_hub_download = file_download
    hub.snapshot_download = snapshots.snapshot_download = snapshot_download
    files._get_progress_bar_context = bar
    files.xet_get = xet_download
    _installed = True
    _install_torch_hook()


def _install_torch_hook():
    hub = sys.modules.get("torch.hub")
    if hub is None or getattr(hub, "_subtitler_progress", False):
        return
    original_bar, original_download = hub.tqdm, hub.download_url_to_file

    def torch_bar(*args, **kwargs):
        transfer = getattr(_local, "transfer", None)
        if _scope is not None and transfer is not None:
            return ByteBar(*transfer, total=kwargs.get("total"), initial=kwargs.get("initial", 0))
        return original_bar(*args, **kwargs)

    @wraps(original_download)
    def torch_download(url, dst, *args, **kwargs):
        if _scope is None:
            return original_download(url, dst, *args, **kwargs)
        scope, previous = _scope, getattr(_local, "transfer", None)
        group, key = Download(scope, detail=url), Path(dst).name
        _local.transfer = group, key
        try:
            result = original_download(url, dst, *args, **kwargs)
            if group.started:
                group.complete(key, dst)
            return result
        finally:
            _local.transfer = previous
            if group.started:
                scope.loading()

    hub.tqdm, hub.download_url_to_file = torch_bar, torch_download
    hub._subtitler_progress = True


@contextmanager
def model_scope(kind, name, device=None):
    global _scope
    install_download_hooks()
    previous = _scope
    scope = _scope = ModelScope(kind, name, device)
    scope.loading()
    try:
        yield scope
    finally:
        _scope = previous


def loads_model(kind, label):
    def decorate(function):
        @wraps(function)
        def load(self, *args, **kwargs):
            with model_scope(kind, label(self), getattr(self, "device", None)):
                return function(self, *args, **kwargs)
        return load
    return decorate
