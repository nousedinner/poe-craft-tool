"""Offline release filesystem transactions. Never invokes SSH, SCP, HTTP, or production paths."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import uuid

source = Path(__file__).with_name("publish-remote.py")
spec = importlib.util.spec_from_file_location("publish_remote", source)
remote = importlib.util.module_from_spec(spec)
spec.loader.exec_module(remote)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def rejected(action):
    try:
        action()
    except (ValueError, FileExistsError):
        return
    raise AssertionError("operation should have been rejected")


class Fixture:
    def __init__(self, root):
        self.root = root
        downloads = root / "downloads"
        downloads.mkdir()
        (downloads / "previous.zip").write_bytes(b"previous package")
        self.links = SimulatedLinks()
        self.links.create("previous.zip", downloads / "shike-latest.zip")
        self.previous = b'{"latest":"1.0.51","url":"https://open.cancanneed.top/download/shike-latest.zip"}'
        (root / "version.json").write_bytes(self.previous)
        self.package = b"new verified package"
        self.manifest = b'{"latest":"1.0.52","signed":"fixture"}'
        self.data = dict(root="/var/www/poead", version="1.0.52", transaction=uuid.uuid4().hex,
            packageSha256=hashlib.sha256(self.package).hexdigest().upper(), packageSize=len(self.package),
            manifestSha256=hashlib.sha256(self.manifest).hexdigest().upper())
        self.paths = None

    def call(self, operation, checkpoint=None):
        return remote.run(dict(self.data, operation=operation), _test_root=self.root, _links=self.links,
                          _checkpoint=checkpoint)

    def state_dir(self):
        return self.root / ".release-transactions" / self.data["transaction"]

    def stage(self):
        self.paths = self.call("begin")
        Path(self.paths["part"]).write_bytes(self.package)
        Path(self.paths["nextManifest"]).write_bytes(self.manifest)
        self.call("install-package")

    def unchanged(self):
        require((self.root / "version.json").read_bytes() == self.previous, "previous manifest changed")
        require(self.links.read(self.root / "downloads/shike-latest.zip") == "previous.zip", "previous alias changed")


class SimulatedLinks:
    """Windows CI has no symlink privilege. Only link behavior is simulated, explicitly."""
    def __init__(self):
        self.targets = {}

    def is_link(self, path):
        return path in self.targets or path.is_symlink()

    def resolve(self, path):
        return (path.parent / self.targets[path]).resolve() if path in self.targets else path.resolve()

    def read(self, path):
        return self.targets[path]

    def create(self, target, path):
        with open(path, "x", encoding="ascii") as stream:
            stream.write("offline simulated symlink")
        self.targets[path] = target

    def replace(self, source, target):
        os.replace(source, target)
        self.targets[target] = self.targets.pop(source)


def normal(f):
    f.stage()
    f.unchanged()
    f.call("switch-alias")
    require((f.root / "version.json").read_bytes() == f.previous, "alias switch must not announce latest")
    f.call("announce")
    require((f.root / "version.json").read_bytes() == f.manifest, "new manifest not announced")
    f.call("complete")
    require(not (f.root / ".release-publish-lock").exists(), "completed transaction retained lock")


def rollback_before_announcement(f):
    f.stage()
    f.call("switch-alias")
    f.call("rollback")
    f.unchanged()
    require(Path(f.paths["package"]).read_bytes() == f.package, "rollback deleted immutable release")


def rollback_after_announcement(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    f.call("rollback")
    f.unchanged()


def rollback_after_completed_release(f):
    normal(f)
    f.call("rollback")
    f.unchanged()
    f.call("rollback")  # Repeating explicit recovery of a completed release remains safe.
    f.unchanged()


def auto_rollback_rechecks_phase_under_guard(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    require(f.call("status")["phase"] == "announced", "stale status fixture must start announced")
    f.call("complete")  # 原来超时的complete在自动恢复取得guard之前完成。
    before_manifest = (f.root / "version.json").read_bytes()
    before_target = f.links.read(f.root / "downloads/shike-latest.zip")
    rejected(lambda: f.call("rollback-incomplete"))
    require((f.root / "version.json").read_bytes() == before_manifest, "automatic recovery undid completed manifest")
    require(f.links.read(f.root / "downloads/shike-latest.zip") == before_target, "automatic recovery undid completed alias")
    require(f.call("status")["phase"] == "complete" and not (f.root / ".release-publish-lock").exists(),
            "automatic recovery changed completed state or lock")
    f.call("rollback")  # 显式人工回退仍保留已约定的权限。
    f.unchanged()


def auto_rollback_recovers_incomplete_release(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    require(f.call("rollback-incomplete")["phase"] == "rolled-back", "incomplete release was not recovered")
    f.unchanged()
    f.call("rollback")  # Repeating recovery after a cache-purge failure remains safe.
    f.unchanged()


def status_can_resolve_lost_acknowledgement(f):
    require(f.call("status")["phase"] == "not-started", "missing transaction status unclear")
    f.call("begin")
    require(f.call("status")["phase"] == "begun", "remote begin acknowledgement cannot be recovered")
    f.call("rollback")
    require(f.call("status")["phase"] == "rolled-back", "rollback state not durable")


def forbid_out_of_order(f):
    f.call("begin")
    rejected(lambda: f.call("announce"))
    f.unchanged()
    f.call("rollback")


def wrong_upload(f):
    paths = f.call("begin")
    Path(paths["part"]).write_bytes(b"not the signed content")
    Path(paths["nextManifest"]).write_bytes(f.manifest)
    rejected(lambda: f.call("install-package"))
    f.unchanged()
    f.call("rollback")


def immutable_conflict(f):
    paths = f.call("begin")
    Path(paths["package"]).write_bytes(b"another release with same version")
    Path(paths["part"]).write_bytes(f.package)
    Path(paths["nextManifest"]).write_bytes(f.manifest)
    rejected(lambda: f.call("install-package"))
    require(Path(paths["package"]).read_bytes() == b"another release with same version", "immutable content overwritten")
    f.call("rollback")


def exclusive_lock(f):
    f.call("begin")
    original = f.data["transaction"]
    f.data["transaction"] = uuid.uuid4().hex
    rejected(lambda: f.call("begin"))
    rejected(lambda: f.call("rollback"))
    f.data["transaction"] = original
    f.call("rollback")


def external_change(f):
    f.stage()
    external = b'{"latest":"1.0.53","changed":"by another release operator"}'
    (f.root / "version.json").write_bytes(external)
    rejected(lambda: f.call("switch-alias"))
    rejected(lambda: f.call("rollback"))
    require((f.root / "version.json").read_bytes() == external, "rollback destroyed unrelated release")


def rollback_rejects_missing_previous_package(f):
    f.stage()
    f.call("switch-alias")
    (f.root / "downloads/previous.zip").unlink()
    rejected(lambda: f.call("rollback"))
    require(f.call("status")["phase"] == "alias-switched", "failed rollback falsely marked success")


def rollback_rejects_changed_previous_manifest(f):
    f.stage()
    f.call("switch-alias")
    backup = f.root / ".release-transactions" / f.data["transaction"] / "version.previous.json"
    backup.write_bytes(b"altered backup")
    rejected(lambda: f.call("rollback"))
    require(f.call("status")["phase"] == "alias-switched", "tampered backup falsely accepted")


def rolled_back_retry_releases_owned_residual_lock(f):
    f.call("begin")
    f.call("rollback")
    lock = f.root / ".release-publish-lock"
    os.link(f.state_dir() / "lock.claim.json", lock)
    f.call("rollback")
    require(not lock.exists(), "own residual rollback lock was not released")


def rolled_back_retry_preserves_another_owner_lock(f):
    f.call("begin")
    f.call("rollback")
    lock = f.root / ".release-publish-lock"
    original = f.data["transaction"]
    other = uuid.uuid4().hex
    f.data["transaction"] = other
    f.call("begin")
    other_claim = f.state_dir() / "lock.claim.json"
    f.data["transaction"] = original
    f.call("rollback")
    require(lock.exists() and os.path.samefile(lock, other_claim), "another transaction's lock was deleted")
    f.data["transaction"] = other
    f.call("rollback")


class Interrupted(BaseException):
    pass


def stop_at(expected):
    def checkpoint(actual):
        if actual == expected:
            raise Interrupted(expected)
    return checkpoint


def interrupted(action):
    try:
        action()
    except Interrupted:
        return
    raise AssertionError("interruption checkpoint was not reached")


def assert_early_interruption_recovers(f, point, phase, has_claim, owns_lock):
    interrupted(lambda: f.call("begin", stop_at(point)))
    lock = f.root / ".release-publish-lock"
    claim = f.state_dir() / "lock.claim.json"
    require(f.call("status")["phase"] == phase, "preparation interruption was falsely reported as not-started")
    require(claim.exists() == has_claim and lock.exists() == owns_lock, "interruption ownership differs")
    if owns_lock:
        require(os.path.samefile(claim, lock), "acquired lock does not reference the prepared claim")
    f.call("rollback")
    f.unchanged()
    require(not lock.exists(), "early recovery retained an owned lock")
    f.call("rollback")
    f.unchanged()


def interruption_before_claim_is_recoverable(f):
    assert_early_interruption_recovers(f, "journal-prepared", "prepared", False, False)


def interruption_after_claim_is_recoverable(f):
    assert_early_interruption_recovers(f, "claim-prepared", "prepared", True, False)


def interruption_before_atomic_acquisition_is_recoverable(f):
    assert_early_interruption_recovers(f, "acquiring-recorded", "acquiring", True, False)


def interruption_after_atomic_acquisition_is_recoverable(f):
    assert_early_interruption_recovers(f, "lock-acquired", "acquiring", True, True)


def interruption_after_rollback_journal_releases_lock_on_retry(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    interrupted(lambda: f.call("rollback", stop_at("rolled-back-recorded")))
    require(f.call("status")["phase"] == "rolled-back", "restored state not durable")
    require((f.root / ".release-publish-lock").exists(), "checkpoint must precede atomic lock release")
    f.call("rollback")
    f.unchanged()
    require(not (f.root / ".release-publish-lock").exists(), "retry did not release owned claim link")


def interruption_after_atomic_release_is_idempotent(f):
    f.call("begin")
    interrupted(lambda: f.call("rollback", stop_at("lock-released")))
    require(not (f.root / ".release-publish-lock").exists(), "lock release was not atomic")
    f.call("rollback")
    f.unchanged()


def interruption_after_complete_journal_is_recoverable(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    interrupted(lambda: f.call("complete", stop_at("complete-recorded")))
    require(f.call("status")["phase"] == "complete", "complete state not durable")
    f.call("complete")
    require(not (f.root / ".release-publish-lock").exists(), "complete retry retained the lock")
    f.call("rollback")
    f.unchanged()


def interruption_between_rollback_pointers_is_recoverable(f):
    f.stage()
    f.call("switch-alias")
    f.call("announce")
    interrupted(lambda: f.call("rollback", stop_at("rollback-manifest-restored")))
    require(f.call("status")["phase"] == "rolling-back", "partial pointer recovery was not journaled")
    require((f.root / "version.json").read_bytes() == f.previous, "manifest restore checkpoint not reached")
    require(f.links.resolve(f.root / "downloads/shike-latest.zip") == Path(f.paths["package"]), "test must retain new alias")
    f.call("rollback")
    f.unchanged()
    require(not (f.root / ".release-publish-lock").exists(), "partial pointer recovery retained lock")


def missing_journal_with_lock_is_not_reported_unstarted(f):
    f.call("begin")
    (f.state_dir() / "state.json").unlink()
    lock = f.root / ".release-publish-lock"
    require(f.call("status")["phase"] == "recovery-required", "missing journal hid an existing publication lock")
    rejected(lambda: f.call("rollback"))
    require(lock.exists(), "unrecognized lock was deleted without a journal")


def legacy_ownerless_directory_lock_is_preserved(f):
    lock = f.root / ".release-publish-lock"
    lock.mkdir()
    require(f.call("status")["phase"] == "recovery-required", "legacy ownerless lock was reported unstarted")
    rejected(lambda: f.call("begin"))
    rejected(lambda: f.call("rollback"))
    require(lock.is_dir(), "legacy unknown directory lock was blindly removed")


def preparation_without_journal_or_lock_is_explicit(f):
    f.state_dir().mkdir(parents=True)
    (f.state_dir() / "version.previous.json").write_bytes(b"partial preparation")
    status = f.call("status")
    require(status["phase"] == "not-started" and status["preparationPresent"], "preparation material was concealed")
    rejected(lambda: f.call("rollback"))
    f.unchanged()


def corrupted_journal_cannot_release_lock(f):
    f.call("begin")
    journal = f.state_dir() / "state.json"
    state = json.loads(journal.read_text(encoding="utf-8"))
    state["previous_package_sha256"] = "A" * 64
    journal.write_text(json.dumps(state), encoding="utf-8")
    rejected(lambda: f.call("status"))
    rejected(lambda: f.call("rollback"))
    require((f.root / ".release-publish-lock").exists(), "tampered journal released the publication lock")
    f.unchanged()


def copied_claim_bytes_do_not_prove_lock_ownership(f):
    f.call("begin")
    claim, lock = f.state_dir() / "lock.claim.json", f.root / ".release-publish-lock"
    lock.unlink()
    lock.write_bytes(claim.read_bytes())
    require(not os.path.samefile(claim, lock), "test must use different physical files")
    rejected(lambda: f.call("rollback"))
    require(lock.exists(), "matching owner text was accepted without inode identity")
    f.unchanged()


def duplicate_metadata_fields_cannot_release_lock(f):
    f.call("begin")
    journal = f.state_dir() / "state.json"
    text = journal.read_text(encoding="utf-8")
    journal.write_text(text.replace('"schema": 2', '"schema": 2, "schema": 2'), encoding="utf-8")
    rejected(lambda: f.call("rollback"))
    require((f.root / ".release-publish-lock").exists(), "ambiguous journal released the publication lock")


def prepared_recovery_preserves_another_transaction_lock(f):
    interrupted(lambda: f.call("begin", stop_at("journal-prepared")))
    original = f.data["transaction"]
    f.data["transaction"] = uuid.uuid4().hex
    other = f.data["transaction"]
    f.call("begin")
    other_claim = f.state_dir() / "lock.claim.json"
    f.data["transaction"] = original
    f.call("rollback")
    require(os.path.samefile(f.root / ".release-publish-lock", other_claim), "prepared recovery removed another lock")
    f.unchanged()
    f.data["transaction"] = other
    f.call("rollback")


def acquiring_recovery_rejects_changed_public_state(f):
    interrupted(lambda: f.call("begin", stop_at("lock-acquired")))
    external = b'{"latest":"1.0.54","changed":"externally"}'
    (f.root / "version.json").write_bytes(external)
    rejected(lambda: f.call("rollback"))
    require((f.root / "version.json").read_bytes() == external, "early recovery overwrote unowned public state")
    require((f.root / ".release-publish-lock").exists(), "inconsistent recovery silently released lock")


def duplicate_recovery_cannot_delete_next_owner_lock(f):
    f.call("begin")
    original = f.data["transaction"]
    released, finish = threading.Event(), threading.Event()
    errors = []

    def checkpoint(point):
        if point == "lock-released":
            released.set()
            if not finish.wait(5):
                raise AssertionError("duplicate recovery test timed out")

    def recover():
        try:
            f.call("rollback", checkpoint)
        except BaseException as error:
            errors.append(error)

    worker = threading.Thread(target=recover)
    worker.start()
    try:
        require(released.wait(3), "first recovery did not release fixed lock")
        rejected(lambda: f.call("rollback"))  # Same transaction is still serializing its response.
        f.data["transaction"] = uuid.uuid4().hex
        other = f.data["transaction"]
        f.call("begin")
        other_claim = f.state_dir() / "lock.claim.json"
        f.data["transaction"] = original
        rejected(lambda: f.call("rollback"))
        require(os.path.samefile(f.root / ".release-publish-lock", other_claim), "duplicate recovery removed new owner lock")
    finally:
        finish.set()
        worker.join(3)
    require(not worker.is_alive() and not errors, "recovery thread did not finish cleanly")
    f.call("rollback")
    require(os.path.samefile(f.root / ".release-publish-lock", other_claim), "completed duplicate recovery removed new owner lock")
    f.data["transaction"] = other
    f.call("rollback")


if __name__ == "__main__":
    base = Path(sys.argv[1]).resolve()
    if base.name != "发布工具离线测试" or "net10.0-windows" not in str(base):
        raise ValueError("offline test output must use the fixed artifact root")
    base.mkdir(parents=True, exist_ok=True)
    tests = [normal, rollback_before_announcement, rollback_after_announcement, rollback_after_completed_release,
             auto_rollback_rechecks_phase_under_guard, auto_rollback_recovers_incomplete_release,
             status_can_resolve_lost_acknowledgement, forbid_out_of_order, wrong_upload, immutable_conflict, exclusive_lock, external_change,
             rollback_rejects_missing_previous_package, rollback_rejects_changed_previous_manifest,
             rolled_back_retry_releases_owned_residual_lock, rolled_back_retry_preserves_another_owner_lock,
             interruption_before_claim_is_recoverable, interruption_after_claim_is_recoverable,
             interruption_before_atomic_acquisition_is_recoverable, interruption_after_atomic_acquisition_is_recoverable,
             interruption_after_rollback_journal_releases_lock_on_retry, interruption_after_atomic_release_is_idempotent,
             interruption_after_complete_journal_is_recoverable, missing_journal_with_lock_is_not_reported_unstarted,
             interruption_between_rollback_pointers_is_recoverable,
             legacy_ownerless_directory_lock_is_preserved, preparation_without_journal_or_lock_is_explicit,
             corrupted_journal_cannot_release_lock, copied_claim_bytes_do_not_prove_lock_ownership,
             duplicate_metadata_fields_cannot_release_lock, prepared_recovery_preserves_another_transaction_lock,
             acquiring_recovery_rejects_changed_public_state, duplicate_recovery_cannot_delete_next_owner_lock]
    for test in tests:
        with tempfile.TemporaryDirectory(prefix="release-", dir=base) as directory:
            test(Fixture(Path(directory)))
        print("PASS", test.__name__)
    print(f"REMOTE_RELEASE_RESULT total={len(tests)}, passed={len(tests)}, failed=0; filesystem=local; symlinks=simulated; hardlinks=real-local; network=none")
