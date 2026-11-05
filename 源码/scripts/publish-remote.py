#!/usr/bin/env python3
"""Bounded release transactions; no credentials or implicit server migration.

The publication lock is a hard link to a complete immutable claim. The journal
exists before acquisition. A kernel-owned per-transaction guard serializes
duplicate recovery commands and is released automatically after interruption.
"""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid


PHASES = {"prepared", "acquiring", "begun", "package-installed", "switching-alias",
          "alias-switched", "announcing", "announced", "rolling-back", "complete", "rolled-back"}
IDENTITY_FIELDS = ("root", "version", "transaction", "claim_nonce", "previous_target",
                   "previous_manifest_sha256", "previous_package_sha256", "previous_package_size",
                   "expected_package_sha256", "expected_package_size", "expected_manifest_sha256")


def digest(path):
    with open(path, "rb") as stream:
        value = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1048576), b""):
            value.update(chunk)
        return value.hexdigest().upper()


def inside(path, directory):
    return os.path.commonpath((str(path), str(directory))) == str(directory)


def valid_hash(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9A-Fa-f]{64}", value) is not None


def sync_directory(path):
    if os.name == "nt":  # Linux production supports directory fsync; Windows CI does not.
        return
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


class NativeLinks:
    @staticmethod
    def is_link(path):
        return path.is_symlink()

    @staticmethod
    def resolve(path):
        return path.resolve()

    @staticmethod
    def read(path):
        return os.readlink(path)

    @staticmethod
    def create(target, path):
        os.symlink(target, path)

    @staticmethod
    def replace(source, target):
        os.replace(source, target)


def read_json(path, limit=16384):
    with open(path, "rb") as stream:
        content = stream.read(limit + 1)
    if len(content) > limit:
        raise ValueError("transaction metadata too large")
    def unique_pairs(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError("duplicate transaction metadata field")
            value[key] = item
        return value
    value = json.loads(content, object_pairs_hook=unique_pairs)
    if not isinstance(value, dict):
        raise ValueError("transaction metadata must be an object")
    return value


def atomic_json(path, value):
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".writing")
    with open(temp, "x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)
    sync_directory(path.parent)


def immutable_json(path, value):
    """Publish complete claim bytes without overwriting a concurrently made claim."""
    if path.exists():
        if read_json(path) != value:
            raise ValueError("immutable claim differs from journal")
        return
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".writing")
    try:
        with open(temp, "x", encoding="utf-8") as stream:
            json.dump(value, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temp, path, follow_symlinks=False)
            sync_directory(path.parent)
        except FileExistsError:
            if read_json(path) != value:
                raise ValueError("immutable claim differs from journal")
    finally:
        if temp.exists():
            temp.unlink()


@contextmanager
def transaction_guard(path):
    """Serialize a transaction on a separate file, never its claim or journal."""
    descriptor = os.open(path, os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o600)
    acquired = False
    try:
        if not stat.S_ISREG(os.fstat(descriptor).st_mode):
            raise ValueError("transaction operation guard must be a regular file")
        try:
            if os.name == "nt":
                import msvcrt
                os.lseek(descriptor, 0, os.SEEK_SET)
                msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            acquired = True
        except OSError as error:
            raise ValueError("another operation owns this transaction") from error
        yield
    finally:
        if acquired:
            if os.name == "nt":
                import msvcrt
                os.lseek(descriptor, 0, os.SEEK_SET)
                msvcrt.locking(descriptor, msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(descriptor, fcntl.LOCK_UN)
        os.close(descriptor)


def run(data, *, _test_root=None, _links=None, _checkpoint=None):
    root_text, version, transaction = data["root"], data["version"], data["transaction"]
    if not re.fullmatch(r"/[A-Za-z0-9_./-]+", root_text) or ".." in root_text.split("/"):
        raise ValueError("invalid release root")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError("invalid version")
    if not re.fullmatch(r"[a-f0-9]{32}", transaction):
        raise ValueError("invalid transaction")
    if not valid_hash(data.get("packageSha256")) or not valid_hash(data.get("manifestSha256")) or \
            type(data.get("packageSize")) is not int or not 0 < data["packageSize"] <= 134217728:
        raise ValueError("invalid prepared release metadata")
    # These dependencies are test-only; CLI JSON cannot supply them.
    root = Path(root_text) if _test_root is None else Path(_test_root).resolve()
    links = NativeLinks() if _links is None else _links
    checkpoint = (lambda _: None) if _checkpoint is None else _checkpoint
    if root.resolve() != root or not root.is_dir() or not os.access(root, os.W_OK):
        raise ValueError("release root must be a writable real directory")
    downloads = root / "downloads"
    if downloads.resolve() != downloads or not downloads.is_dir():
        raise ValueError("downloads must be a real directory")
    latest, manifest = downloads / "shike-latest.zip", root / "version.json"
    releases, transactions = downloads / "releases", root / ".release-transactions"
    lock = root / ".release-publish-lock"
    state_dir = transactions / transaction
    journal, claim = state_dir / "state.json", state_dir / "lock.claim.json"
    operation_guard = state_dir / "operation.guard"
    previous_manifest = state_dir / "version.previous.json"
    package_dir = releases / version
    package = package_dir / "shike-win-x64.zip"
    part = package_dir / ("package-" + transaction + ".part")
    next_manifest = state_dir / "version.next.json"
    operation = data["operation"]

    def guard_paths():
        for path in (releases, transactions, state_dir, package_dir, manifest, package, part, next_manifest,
                     lock, journal, claim, operation_guard, previous_manifest):
            if links.is_link(path) or not inside(path.resolve(), root):
                raise ValueError("release paths cannot contain links or escape root")

    def load_state():
        state = read_json(journal)
        if set(state) != set(IDENTITY_FIELDS) | {"schema", "phase"} or state["schema"] != 2 or \
                state["root"] != root_text or state["version"] != version or state["transaction"] != transaction or \
                state["phase"] not in PHASES or not isinstance(state["claim_nonce"], str) or \
                not re.fullmatch(r"[a-f0-9]{32}", state["claim_nonce"]):
            raise ValueError("transaction journal identity or schema is invalid")
        for name in ("previous_manifest_sha256", "previous_package_sha256", "expected_package_sha256", "expected_manifest_sha256"):
            if not valid_hash(state[name]):
                raise ValueError("transaction journal contains an invalid hash")
        if any(type(state[name]) is not int or not 0 < state[name] <= 134217728
               for name in ("previous_package_size", "expected_package_size")) or \
                not isinstance(state["previous_target"], str) or not state["previous_target"] or \
                len(state["previous_target"]) > 4096 or "\0" in state["previous_target"]:
            raise ValueError("transaction journal contains invalid release metadata")
        if state["expected_package_sha256"] != data["packageSha256"].upper() or \
                state["expected_package_size"] != data["packageSize"] or \
                state["expected_manifest_sha256"] != data["manifestSha256"].upper():
            raise ValueError("transaction does not match the prepared release")
        return state

    def claim_value(state):
        snapshot = json.dumps({key: state[key] for key in IDENTITY_FIELDS}, sort_keys=True,
                              separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        return dict(owner="ShiKe.ReleaseClaim.v2", transaction=transaction, version=version,
                    nonce=state["claim_nonce"], snapshotSha256=hashlib.sha256(snapshot).hexdigest().upper())

    def validate_claim(state):
        guard_paths()
        if not claim.is_file() or read_json(claim) != claim_value(state):
            raise ValueError("transaction claim is missing or does not match its journal")

    def owns_lock(state):
        validate_claim(state)
        if not lock.exists() or not lock.is_file():
            return False  # Old directory locks and unrelated material are never removed.
        return os.path.samefile(claim, lock) and read_json(lock) == claim_value(state)

    def acquire_lock(state):
        validate_claim(state)
        try:
            os.link(claim, lock, follow_symlinks=False)
            sync_directory(root)
        except FileExistsError:
            if not owns_lock(state):
                raise ValueError("another or unrecognized publication lock exists")
        if not owns_lock(state):
            raise ValueError("publication lock identity could not be confirmed")

    def release_owned_lock(state):
        # operation.guard prevents concurrent same-owner recovery from checking
        # an old lock and then unlinking a new transaction's lock after an ABA.
        if owns_lock(state):
            lock.unlink()
            sync_directory(root)
            checkpoint("lock-released")

    def verify_previous(state):
        previous_package = (downloads / state["previous_target"]).resolve()
        if not inside(previous_package, downloads) or not previous_package.is_file():
            raise ValueError("previous ZIP target is missing or outside downloads; cannot report rollback success")
        if previous_package.stat().st_size != state["previous_package_size"] or digest(previous_package) != state["previous_package_sha256"]:
            raise ValueError("previous ZIP changed since release began")
        if digest(previous_manifest) != state["previous_manifest_sha256"]:
            raise ValueError("previous manifest backup changed")

    def verify_previous_is_active(state):
        verify_previous(state)
        if digest(manifest) != state["previous_manifest_sha256"] or not links.is_link(latest) or \
                links.read(latest) != state["previous_target"]:
            raise ValueError("published release changed; do not restore unowned state")

    def verify_new_is_active(state):
        if digest(manifest) != state["expected_manifest_sha256"] or not links.is_link(latest) or links.resolve(latest) != package:
            raise ValueError("a newer or unrelated release is active")

    def rollback_result(state):
        return dict(transaction=transaction, phase="rolled-back", immutablePackageRetained=True,
                    previousManifestSha256=state["previous_manifest_sha256"], previousPackageSha256=state["previous_package_sha256"],
                    previousPackageSize=state["previous_package_size"])

    guard_paths()
    if operation == "status":
        if not journal.is_file():
            # Missing metadata cannot prove an existing lock is safe to discard.
            phase = "recovery-required" if lock.exists() else "not-started"
            return dict(transaction=transaction, phase=phase, preparationPresent=state_dir.exists())
        state = load_state()
        if claim.exists():
            validate_claim(state)
        elif state["phase"] not in ("prepared", "acquiring"):
            raise ValueError("active transaction has lost its claim")
        return dict(transaction=transaction, phase=state["phase"])

    if operation == "begin":
        if state_dir.exists():
            raise ValueError("transaction already exists; inspect status or explicitly roll it back")
        if lock.exists():
            raise ValueError("another or interrupted publication lock exists")
        if not manifest.is_file() or not links.is_link(latest):
            raise ValueError("existing version.json and latest ZIP symlink are required; no implicit migration")
        old_target = links.read(latest)
        old_package = links.resolve(latest)
        if not inside(old_package, downloads) or not old_package.is_file():
            raise ValueError("latest ZIP target must be an existing file inside downloads")
        previous = manifest.read_bytes()
        if len(previous) > 1048576:
            raise ValueError("previous manifest too large")
        old_version = json.loads(previous)["latest"]
        if not re.fullmatch(r"\d+\.\d+\.\d+", old_version) or tuple(map(int, old_version.split("."))) >= tuple(map(int, version.split("."))):
            raise ValueError("release must increase the announced version")
        releases.mkdir(exist_ok=True)
        transactions.mkdir(exist_ok=True)
        state_dir.mkdir()
        with open(previous_manifest, "xb") as stream:
            stream.write(previous)
            stream.flush()
            os.fsync(stream.fileno())
        state = dict(schema=2, root=root_text, version=version, transaction=transaction, claim_nonce=uuid.uuid4().hex,
                     previous_target=old_target, previous_manifest_sha256=hashlib.sha256(previous).hexdigest().upper(),
                     previous_package_sha256=digest(old_package), previous_package_size=old_package.stat().st_size,
                     expected_package_sha256=data["packageSha256"].upper(), expected_package_size=data["packageSize"],
                     expected_manifest_sha256=data["manifestSha256"].upper(), phase="prepared")
        atomic_json(journal, state)
        checkpoint("journal-prepared")
        immutable_json(claim, claim_value(load_state()))
        checkpoint("claim-prepared")
    elif not journal.is_file():
        raise ValueError("transaction does not exist; an unrecognized lock must be preserved")

    state = load_state()
    if not claim.exists() and state["phase"] in ("prepared", "acquiring"):
        immutable_json(claim, claim_value(state))
    validate_claim(state)
    with transaction_guard(operation_guard):
        # Another operation may have completed before this process acquired the guard.
        state = load_state()
        validate_claim(state)
        if operation == "rollback-incomplete":
            # 客户端先前读取的status可能已过期；自动恢复必须在同一guard内判定。
            # 人工显式rollback仍可撤回已完成发行，自动catch绝不能使用该权限。
            if state["phase"] == "complete":
                raise ValueError("completed publication cannot be rolled back automatically")
            operation = "rollback"
        if operation == "begin":
            if state["phase"] != "prepared":
                raise ValueError("transaction preparation was already handled")
            verify_previous_is_active(state)
            state["phase"] = "acquiring"
            atomic_json(journal, state)
            checkpoint("acquiring-recorded")
            acquire_lock(state)
            checkpoint("lock-acquired")
            verify_previous_is_active(state)
            package_dir.mkdir(exist_ok=True)
            state["phase"] = "begun"
            atomic_json(journal, state)
            return dict(part=str(part), nextManifest=str(next_manifest), package=str(package), transaction=transaction)

        if operation == "rollback" and state["phase"] in ("prepared", "acquiring", "rolled-back"):
            # These stages never changed public pointers or have already restored
            # them. Do not acquire another publisher's lock or rewrite public files.
            verify_previous_is_active(state)
            if state["phase"] != "rolled-back":
                state["phase"] = "rolled-back"
                atomic_json(journal, state)
                checkpoint("rolled-back-recorded")
            release_owned_lock(state)
            return rollback_result(state)

        if operation == "complete" and state["phase"] == "complete":
            verify_new_is_active(state)
            release_owned_lock(state)
            return dict(transaction=transaction, phase="complete", immutablePackageRetained=True)

        if operation == "rollback" and state["phase"] == "complete" and not lock.exists():
            verify_new_is_active(state)
            acquire_lock(state)
            verify_new_is_active(state)
        if not owns_lock(state):
            raise ValueError("transaction does not own the publication lock")

        if operation == "install-package":
            if state["phase"] != "begun":
                raise ValueError("package already finalized")
            if package.exists():
                if package.stat().st_size != state["expected_package_size"] or digest(package) != state["expected_package_sha256"]:
                    raise ValueError("immutable version already exists with different content")
                if part.exists():
                    if digest(part) != state["expected_package_sha256"]:
                        raise ValueError("uploaded partial file differs from immutable package")
                    part.unlink()
            else:
                if part.stat().st_size != state["expected_package_size"] or digest(part) != state["expected_package_sha256"]:
                    raise ValueError("uploaded package differs from prepared release")
                os.rename(part, package)
            if digest(next_manifest) != state["expected_manifest_sha256"]:
                raise ValueError("uploaded manifest differs from prepared release")
            state["phase"] = "package-installed"
            atomic_json(journal, state)
        elif operation == "switch-alias":
            if state["phase"] != "package-installed" or digest(manifest) != state["previous_manifest_sha256"]:
                raise ValueError("release state or current manifest changed externally")
            if not links.is_link(latest) or links.read(latest) != state["previous_target"]:
                raise ValueError("latest alias changed externally")
            temp = downloads / ("latest-" + transaction + ".tmp")
            state["phase"] = "switching-alias"
            atomic_json(journal, state)
            links.create(os.path.relpath(package, downloads), temp)
            links.replace(temp, latest)
            state["phase"] = "alias-switched"
            atomic_json(journal, state)
        elif operation == "announce":
            if state["phase"] != "alias-switched" or digest(manifest) != state["previous_manifest_sha256"]:
                raise ValueError("current manifest or phase changed before announcement")
            if digest(next_manifest) != state["expected_manifest_sha256"] or links.resolve(latest) != package:
                raise ValueError("release files changed before announcement")
            state["phase"] = "announcing"
            atomic_json(journal, state)
            os.replace(next_manifest, manifest)
            state["phase"] = "announced"
            atomic_json(journal, state)
        elif operation in ("complete", "rollback"):
            if operation == "rollback":
                verify_previous(state)
                if digest(manifest) not in (state["previous_manifest_sha256"], state["expected_manifest_sha256"]):
                    raise ValueError("current manifest changed externally; preserve journal for recovery")
                expected_target = os.path.relpath(package, downloads)
                if not links.is_link(latest) or links.read(latest) not in (state["previous_target"], expected_target):
                    raise ValueError("latest alias changed externally; preserve journal for recovery")
                state["phase"] = "rolling-back"
                atomic_json(journal, state)
                restore_manifest = state_dir / ("version.restore-" + uuid.uuid4().hex + ".json")
                with open(restore_manifest, "xb") as stream:
                    stream.write(previous_manifest.read_bytes())
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(restore_manifest, manifest)
                checkpoint("rollback-manifest-restored")
                restore_link = downloads / ("restore-" + transaction + "-" + uuid.uuid4().hex + ".tmp")
                links.create(state["previous_target"], restore_link)
                links.replace(restore_link, latest)
                state["phase"] = "rolled-back"
                atomic_json(journal, state)
                checkpoint("rolled-back-recorded")
            elif state["phase"] != "announced":
                raise ValueError("cannot complete an unannounced release")
            else:
                verify_new_is_active(state)
                state["phase"] = "complete"
                atomic_json(journal, state)
                checkpoint("complete-recorded")
            release_owned_lock(state)
        else:
            raise ValueError("unsupported release operation")
        return rollback_result(state) if state["phase"] == "rolled-back" else dict(
            transaction=transaction, phase=state["phase"], immutablePackageRetained=True)


if __name__ == "__main__":
    try:
        print(json.dumps(run(json.loads(sys.argv[1])), ensure_ascii=False))
    except BaseException as error:
        print(json.dumps(dict(error=type(error).__name__, message=str(error))), file=sys.stderr)
        sys.exit(1)
