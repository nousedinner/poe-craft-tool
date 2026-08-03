"""Auto-operator v8. Single daemon thread — alive forever.
Correct PoE crafting flow: right-click once, shift+click repeatedly.
No thread creation/destruction, no join(), no race conditions.
v8: Added polling-based stop detection as fallback for keyboard callback.
v7: Ctrl+Alt+C for Chinese PoE client, fixed Mode 2/3 flows.
"""
import time
import threading
import random
import pyautogui
import pyperclip
import keyboard
import sys
from pathlib import Path
from models import Mode, CurrencyType

pyautogui.PAUSE = 0
pyautogui.FAILSAFE = True  # 鼠标移到屏幕角落 = 强制停止


class CraftOperator:

    def __init__(self):
        self._running = False
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        self._run_id = 0
        self.on_status_update = None
        self.on_match_found = None
        self.on_stopped = None
        self.on_error = None
        self.on_show_popup = None
        self.use_count = 0
        self.match_count = 0
        self._last_status_time = 0
        self._target_process = ""  # Foreground process filter
        self._mode2_use_scour_alch = False  # Mode 2: Scour+Alch sub-mode
        self._use_exalt = False  # Mode 3: use Exalted after Regal

        # Instance variables set by start()
        self._rules = None
        self._coordinates = None
        self._delay = 0.05
        self._sound_enabled = False
        self._popup_enabled = False
        self._selected_sound = "default_ding.wav"
        self._exhaustion_threshold = 15
        self._stop_hotkey = "F6"

        # ONE thread, starts immediately, never dies
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _check_stop_key(self) -> bool:
        """Polling-based stop detection — fallback if keyboard callback doesn't fire."""
        try:
            if keyboard.is_pressed(self._stop_hotkey):
                return True
        except Exception:
            pass
        return False

    @property
    def is_running(self):
        return self._running

    def set_target_process(self, name: str):
        self._target_process = name

    def set_mode2_scour_alch(self, use: bool):
        self._mode2_use_scour_alch = use

    def set_use_exalt(self, use: bool):
        self._use_exalt = use

    def start(self, rules, coordinates, delay_ms, sound_enabled, popup_enabled, selected_sound="default_ding.wav", exhaustion_threshold=15, stop_hotkey="F6"):
        with self._lock:
            if self._running:
                return
            missing = self._check_coordinates(rules, coordinates)
            if missing:
                if self.on_error:
                    self.on_error(f"缺少坐标: {missing}")
                return
            # Store parameters
            self._rules = rules
            self._coordinates = coordinates
            self._delay = delay_ms / 1000.0
            self._sound_enabled = sound_enabled
            self._popup_enabled = popup_enabled
            self._selected_sound = selected_sound
            self._exhaustion_threshold = exhaustion_threshold
            self._stop_hotkey = stop_hotkey
            self.use_count = 0
            self.match_count = 0
            self._run_id += 1
            # Set event first to wake up the daemon thread, then set running flag
            self._running = True
            self._stop_event.set()

    def _check_coordinates(self, rules, coordinates):
        """Check that all required coordinates are valid (not None and not (0,0))."""
        def is_valid(coord):
            if coord is None:
                return False
            if isinstance(coord, (tuple, list)) and len(coord) >= 2:
                # Allow (0, y) or (x, 0) but not (0, 0)
                if coord[0] == 0 and coord[1] == 0:
                    return False
                return True
            return False
        
        if rules.mode == Mode.SINGLE:
            curr = coordinates.get(rules.single_currency)
            if not is_valid(curr):
                return f"{CurrencyType.LABELS.get(rules.single_currency, rules.single_currency)}坐标"
        elif rules.mode == Mode.ALT_AUG:
            for c in [CurrencyType.ALTERATION, CurrencyType.AUGMENTATION]:
                if not is_valid(coordinates.get(c)):
                    return f"{CurrencyType.LABELS.get(c, c)}坐标"
        elif rules.mode == Mode.ALT_AUG_REGAL:
            for c in [CurrencyType.ALTERATION, CurrencyType.AUGMENTATION,
                      CurrencyType.REGAL, CurrencyType.SCOURING, CurrencyType.TRANSMUTATION]:
                if not is_valid(coordinates.get(c)):
                    return f"{CurrencyType.LABELS.get(c, c)}坐标"
        
        if not is_valid(coordinates.get("item")):
            return "装备坐标"
        return None

    # ===== Perpetual loop =====
    def _run(self):
        """Runs forever. Idle when _running=False, executes crafting when True.
        Uses _stop_event.wait() with no timeout — thread stays completely asleep
        when idle, zero CPU usage."""
        while True:
            if not self._running:
                self._stop_event.clear()  # Ensure we sleep, not busy-loop
                self._stop_event.wait()  # Sleep indefinitely until woken by start()
                continue

            # start() set _stop_event to wake us — clear it now so
            # _interruptible_sleep() properly waits for its timeout
            self._stop_event.clear()

            # Capture run_id at start — must be before any operation that could fail
            my_run_id = self._run_id

            try:
                self._release_all()
                self._do_crafting()
            except pyautogui.FailSafeException:
                pass  # F6 stop triggered mouse to corner
            except Exception as e:
                # 先停线程再发信号，确保槽里处理时不会再有后续信号排队
                with self._lock:
                    if self._run_id == my_run_id:
                        self._running = False
                        self._stop_event.set()
                if self.on_error:
                    self._cb(self.on_error, f"异常: {e}")
            finally:
                # Only flip if this is still the current run (not stale)
                with self._lock:
                    if self._run_id == my_run_id:
                        self._running = False
                        self._stop_event.set()
                if self.on_stopped:
                    self._cb(self.on_stopped, "已停止")

    def stop(self):
        """Stop crafting immediately. Clean event-based stop, no mouse tricks."""
        with self._lock:
            self._running = False
            self._stop_event.set()

    def _do_crafting(self):
        """Execute one crafting session using instance variables."""
        rules = self._rules
        coordinates = self._coordinates
        delay = self._delay
        sound_enabled = self._sound_enabled
        popup_enabled = self._popup_enabled

        if rules.mode == Mode.SINGLE:
            self._mode1(rules, coordinates, delay, sound_enabled, popup_enabled)
        elif rules.mode == Mode.ALT_AUG:
            if self._mode2_use_scour_alch:
                self._mode2_scour_alch(rules, coordinates, delay, sound_enabled, popup_enabled)
            else:
                self._mode2(rules, coordinates, delay, sound_enabled, popup_enabled)
        elif rules.mode == Mode.ALT_AUG_REGAL:
            self._mode3(rules, coordinates, delay, sound_enabled, popup_enabled)

    # ===== Low-level =====
    def _interruptible_sleep(self, seconds):
        """Sleep using stop_event.wait() — releases GIL, allows keyboard callbacks to fire.
        Also polls the stop key and foreground process as fallbacks."""
        self._stop_event.wait(timeout=seconds)
        # Fallback: poll the stop key directly
        if self._check_stop_key():
            self.stop()
        # Foreground process check — pause if not in target process
        if self._target_process and self._running:
            try:
                from foreground import is_foreground_process
                while self._running and not is_foreground_process(self._target_process):
                    self._stop_event.wait(timeout=0.2)
                    self._stop_event.clear()
            except Exception:
                pass

    def _release_all(self):
        for k in ['shift', 'ctrl', 'alt']:
            try:
                pyautogui.keyUp(k)
            except Exception:
                pass
        self._interruptible_sleep(0.05)

    def _move_to(self, coord):
        pyautogui.moveTo(int(coord[0]), int(coord[1]), duration=0)
        self._interruptible_sleep(0.03)

    def _right_click(self, coord, delay):
        """Right-click at coordinate."""
        self._move_to(coord)
        if not self._running:
            return
        self._interruptible_sleep(delay)
        if not self._running:
            return
        pyautogui.rightClick()
        self._interruptible_sleep(delay * 3)

    def _shift_click(self, coord, delay):
        """Move to coord with ±10px random offset, click (shift should already be held)."""
        x = int(coord[0]) + random.randint(-10, 10)
        y = int(coord[1]) + random.randint(-10, 10)
        pyautogui.moveTo(x, y, duration=0)
        self._interruptible_sleep(delay)
        if not self._running:
            return
        # Split click into mouseDown/mouseUp for interruptibility
        pyautogui.mouseDown()
        self._interruptible_sleep(0.02)
        pyautogui.mouseUp()
        self._interruptible_sleep(delay * 2)

    def _hold_shift(self):
        pyautogui.keyDown('shift')
        self._interruptible_sleep(0.05)

    def _release_shift(self):
        pyautogui.keyUp('shift')
        self._interruptible_sleep(0.05)

    def _copy_clipboard(self, delay):
        """Copy item text using Ctrl+Alt+C (Chinese PoE client). Shift should be held."""
        self._interruptible_sleep(0.05)
        if not self._running:
            return ""
        pyautogui.hotkey('ctrl', 'alt', 'c')
        self._interruptible_sleep(max(delay * 5, 0.15))
        try:
            return pyperclip.paste()
        except Exception:
            return ""

    def _check_item(self, text, rules):
        """Parse and check item text against rules. Returns AffixCheckResult."""
        from affix_engine import parse_item_text, check_affixes
        affixes = parse_item_text(text)
        return check_affixes(affixes, rules)

    def _get_affix_count(self, text):
        """Get total affix count from clipboard text."""
        from affix_engine import parse_item_text
        return len(parse_item_text(text))

    def _check_aug_close(self, result, rules):
        """After Aug, check if we're close enough for Regal to complete.
        Regal adds 1 affix (primary or secondary). Accept if either path works."""
        p, s = result.primary_hits, result.secondary_hits
        tp, ts = rules.primary_hit_count, rules.secondary_hit_count
        # If Regal adds primary: p+1 >= tp and s >= ts
        if p + 1 >= tp and s >= ts:
            return True
        # If Regal adds secondary: p >= tp and s+1 >= ts
        if p >= tp and s + 1 >= ts:
            return True
        return False

    # ===== Callbacks =====
    def _cb(self, fn, *args):
        if fn:
            try:
                fn(*args)
            except Exception:
                pass

    def _status(self, text, force=False):
        now = time.time()
        if not force and now - self._last_status_time < 0.25:
            return
        self._last_status_time = now
        self._cb(self.on_status_update, text, self.use_count, self.match_count)

    def _on_success(self, sound_enabled, popup_enabled):
        self._status(f"匹配成功! 共{self.use_count}次", force=True)
        self._cb(self.on_match_found, None)
        self._play_sound(sound_enabled)
        if popup_enabled:
            self._cb(self.on_show_popup, f"匹配成功!\n使用次数: {self.use_count}")

    # ===== Mode 1: Single Currency =====
    # Right-click currency once → hold shift (never release) → repeatedly:
    #   click item → ctrl+alt+c (while shift held) → check → if match, release shift
    def _mode1(self, rules, coordinates, delay, sound_enabled, popup_enabled):
        """Single-currency crafting (chaos, exalt, etc.)."""
        curr_type = rules.single_currency
        curr_coord = coordinates.get(curr_type)
        item_coord = coordinates.get("item")
        
        # 验证坐标有效性
        def is_valid(coord):
            if coord is None:
                return False
            if isinstance(coord, (tuple, list)) and len(coord) >= 2:
                if coord[0] == 0 and coord[1] == 0:
                    return False
                return True
            return False
        
        if not is_valid(curr_coord):
            self._status(f"缺少通货坐标: {CurrencyType.LABELS.get(curr_type, curr_type)}", force=True)
            return
        if not is_valid(item_coord):
            self._status("缺少装备坐标", force=True)
            return
        
        print(f"[模式1] >> 右键通货 → 移动到 {curr_coord} (通货类型: {curr_type})")
        self._right_click(curr_coord, delay)
        print(f"[模式1] >> 右键完成")
        
        if not self._running:
            return
        
        # Step 2: Hold shift — never release until match or stop
        print(f"[模式1] >> 按住 shift (全程不松)")
        self._hold_shift()

        last_clip = ""
        clip_same_count = 0

        try:
            while self._running:
                # Step 3: Shift+click item (shift already held)
                print(f"[模式1] >> shift+左键装备 → 移动到 {item_coord}")
                self._shift_click(item_coord, delay)
                self.use_count += 1
                print(f"[模式1] >> 左键完成, 第{self.use_count}次")

                # Step 4: Copy while shift is still held
                text = self._copy_clipboard(delay)
                if not self._running:
                    return

                # [DEBUG] Print parsed affixes on first 2 iterations
                if self.use_count <= 2:
                    from affix_engine import parse_item_text
                    debug_affixes = parse_item_text(text)
                    print(f"[模式1] >> 解析到 {len(debug_affixes)} 条词缀:")
                    for da in debug_affixes:
                        print(f"  - name=\"{da['name']}\" desc=\"{da['description'][:30]}...\"")
                    print(f"[模式1] >> 规则: 主={[r.text for r in rules.primary_affixes]} 次={[r.text for r in rules.secondary_affixes]} 排除={[r.text for r in rules.exclude_affixes]}")

                print(f"[模式1] >> 剪贴板: {text[:60]}...")

                # Exhaustion check: same clipboard (currency ran out, item unchanged)
                if text and text == last_clip:
                    clip_same_count += 1
                    print(f"[模式1] >> 剪贴板相同 x{clip_same_count}")
                    if clip_same_count >= self._exhaustion_threshold:
                        print(f"[模式1] >> 判定耗尽(剪贴板相同x{self._exhaustion_threshold})!")
                        self._status(f"通货可能已耗尽 (连续{clip_same_count}次未变)", force=True)
                        self._play_sound(sound_enabled)
                        return
                else:
                    clip_same_count = 0
                    last_clip = text

                result = self._check_item(text, rules)
                self.match_count = result.primary_hits + result.secondary_hits

                status = f"#{self.use_count} 主:{result.primary_hits} 次:{result.secondary_hits}"
                if result.has_exclude:
                    status += " [排除]"
                self._status(status)
                print(f"[模式1] >> 检查结果: 主={result.primary_hits} 次={result.secondary_hits} 排除={result.has_exclude}")

                if result.meets_final_rules(rules):
                    print(f"[模式1] >> 匹配成功!")
                    self._on_success(sound_enabled, popup_enabled)
                    return

                # Not matched — shift is still held, loop back to click item again
        finally:
            # Ensure shift is released when loop exits (stop or match)
            self._release_shift()

    # ===== Mode 2: Alt + Aug =====
    # 改造(洗蓝) → 检查词缀数 → 如果1条则增幅(加词) → 最终检查
    # 增幅只在装备只有1条词缀时才使用
    def _mode2(self, rules, coordinates, delay, sound_enabled, popup_enabled):
        alt_coord = coordinates.get(CurrencyType.ALTERATION)
        aug_coord = coordinates.get(CurrencyType.AUGMENTATION)
        item_coord = coordinates.get("item")

        def is_valid(coord):
            if coord is None:
                return False
            if isinstance(coord, (tuple, list)) and len(coord) >= 2:
                if coord[0] == 0 and coord[1] == 0:
                    return False
                return True
            return False

        if not is_valid(alt_coord):
            self._status("缺少改造石坐标", force=True)
            return
        if not is_valid(aug_coord):
            self._status("缺少增幅石坐标", force=True)
            return
        if not is_valid(item_coord):
            self._status("缺少装备坐标", force=True)
            return

        self._status("启动改造+增幅模式...", force=True)

        while self._running:
            # Phase 1: Roll with Alt (改造阶段)
            self._right_click(alt_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()

            try:
                while self._running:
                    self._shift_click(item_coord, delay)
                    self.use_count += 1

                    text = self._copy_clipboard(delay)
                    if not self._running:
                        return

                    result = self._check_item(text, rules)
                    self.match_count = result.primary_hits + result.secondary_hits

                    if result.has_exclude:
                        self._status(f"#{self.use_count} 排除命中，继续改造...")
                        continue

                    # 改造阶段：至少命中1条词缀（主或次）即可
                    if result.primary_hits >= 1 or result.secondary_hits >= 1:
                        break

                    self._status(f"#{self.use_count} 改造中...")
            finally:
                self._release_shift()

            if not self._running:
                return

            # 检查词缀数：2条则跳过增幅，直接最终检查
            affix_count = self._get_affix_count(text)
            if affix_count >= 2:
                # 已有2条词缀，跳过增幅，直接检查最终规则
                result = self._check_item(text, rules)
                if result.meets_final_rules(rules):
                    self._on_success(sound_enabled, popup_enabled)
                    return
                self._status(f"#{self.use_count} 2条词缀不满足，重新改造...")
                continue

            # Phase 2: Augment (增幅阶段) — 仅在只有1条词缀时使用
            self._right_click(aug_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1

                text = self._copy_clipboard(delay)
                if not self._running:
                    return

                result = self._check_item(text, rules)
                self.match_count = result.primary_hits + result.secondary_hits

                # 增幅后最终检查
                if result.meets_final_rules(rules):
                    self._on_success(sound_enabled, popup_enabled)
                    return
            finally:
                self._release_shift()

            self._status(f"#{self.use_count} 增幅后不满足，重新改造...")

    # ===== Mode 2 Scour+Alch: 重铸+点金 =====
    # 循环: 点金(白→黄) → 检查最终规则 → 不满足则重铸(黄→白) → 再点金
    def _mode2_scour_alch(self, rules, coordinates, delay, sound_enabled, popup_enabled):
        alch_coord = coordinates.get(CurrencyType.ALCHEMY)
        scour_coord = coordinates.get(CurrencyType.SCOURING)
        item_coord = coordinates.get("item")

        def is_valid(coord):
            if coord is None:
                return False
            if isinstance(coord, (tuple, list)) and len(coord) >= 2:
                if coord[0] == 0 and coord[1] == 0:
                    return False
                return True
            return False

        if not is_valid(alch_coord):
            self._status("缺少点金石坐标", force=True)
            return
        if not is_valid(scour_coord):
            self._status("缺少重铸石坐标", force=True)
            return
        if not is_valid(item_coord):
            self._status("缺少装备坐标", force=True)
            return

        self._status("启动重铸+点金模式...", force=True)

        while self._running:
            # 点金 (白→黄)
            self._right_click(alch_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1

                text = self._copy_clipboard(delay)
                if not self._running:
                    return

                result = self._check_item(text, rules)
                self.match_count = result.primary_hits + result.secondary_hits

                if result.meets_final_rules(rules):
                    self._on_success(sound_enabled, popup_enabled)
                    return
            finally:
                self._release_shift()

            # 不满足 → 重铸 (黄→白)
            self._status(f"#{self.use_count} 点金后不满足，重铸重来...")
            self._right_click(scour_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1
            finally:
                self._release_shift()
            if not self._running:
                return

    # ===== Mode 3: Alt + Aug + Regal + optional Exalted =====
    # 外层循环: 蜕变 → 魔法阶段(改造+增幅) → 富豪 → [崇高] → 失败则重铸重来
    # 内层循环(魔法阶段): 改造 → 检查词缀数和门槛 → 决定继续改造/增幅/退出
    # 增幅只在装备只有1条词缀时使用，增幅失败继续改造（不重铸）
    # 门槛 = max(0, 命中总数 - 1)，因为富豪还会加1条词缀
    def _mode3(self, rules, coordinates, delay, sound_enabled, popup_enabled):
        alt_coord = coordinates.get(CurrencyType.ALTERATION)
        aug_coord = coordinates.get(CurrencyType.AUGMENTATION)
        regal_coord = coordinates.get(CurrencyType.REGAL)
        scour_coord = coordinates.get(CurrencyType.SCOURING)
        trans_coord = coordinates.get(CurrencyType.TRANSMUTATION)
        exalt_coord = coordinates.get(CurrencyType.EXALTED)
        item_coord = coordinates.get("item")
        use_exalt = getattr(self, '_use_exalt', False)

        def is_valid(coord):
            if coord is None:
                return False
            if isinstance(coord, (tuple, list)) and len(coord) >= 2:
                if coord[0] == 0 and coord[1] == 0:
                    return False
                return True
            return False

        required = [("改造石", alt_coord), ("增幅石", aug_coord), ("富豪石", regal_coord), ("重铸石", scour_coord), ("蜕变石", trans_coord), ("装备", item_coord)]
        if use_exalt:
            required.append(("崇高石", exalt_coord))
        for name, coord in required:
            if not is_valid(coord):
                self._status(f"缺少{name}坐标", force=True)
                return

        self._status("启动改造+增幅+富豪模式...", force=True)
        threshold = max(0, rules.primary_hit_count + rules.secondary_hit_count - 1)

        while self._running:
            # Phase 1: Transmute (蜕变: 白→蓝)
            self._right_click(trans_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1
            finally:
                self._release_shift()
            if not self._running:
                return

            # ===== 魔法阶段内层循环 (改造 + 可选增幅) =====
            go_to_regal = False
            while self._running:
                # Phase 2: Alt (改造: 洗蓝) — 右键一次，shift+click循环
                self._right_click(alt_coord, delay)
                if not self._running:
                    return
                self._interruptible_sleep(0.15)
                self._hold_shift()

                try:
                    while self._running:
                        self._shift_click(item_coord, delay)
                        self.use_count += 1

                        text = self._copy_clipboard(delay)
                        if not self._running:
                            return

                        result = self._check_item(text, rules)
                        self.match_count = result.primary_hits + result.secondary_hits

                        if result.has_exclude:
                            self._status(f"#{self.use_count} 排除命中，继续改造...")
                            continue

                        hits = result.primary_hits + result.secondary_hits
                        affix_count = self._get_affix_count(text)

                        if affix_count >= 2:
                            if hits >= threshold:
                                go_to_regal = True
                                break  # 2词缀达标，退出Alt循环
                            # 2词缀未达标，继续shift+click重roll（不释放Shift）
                            self._status(f"#{self.use_count} 2词缀命中{hits}不足{threshold}，继续改造...")
                        else:  # 1词缀
                            if hits >= 1:
                                break  # 1词缀命中，退出Alt循环去增幅
                            self._status(f"#{self.use_count} 改造中...")
                finally:
                    self._release_shift()

                if not self._running:
                    return
                if go_to_regal:
                    break  # 退出魔法阶段，进入富豪

                # 只有1条词缀且命中≥1 → 使用增幅
                self._right_click(aug_coord, delay)
                if not self._running:
                    return
                self._interruptible_sleep(0.15)
                self._hold_shift()
                try:
                    self._shift_click(item_coord, delay)
                    self.use_count += 1

                    text = self._copy_clipboard(delay)
                    if not self._running:
                        return

                    result = self._check_item(text, rules)
                    self.match_count = result.primary_hits + result.secondary_hits
                    hits = result.primary_hits + result.secondary_hits

                    if result.meets_final_rules(rules):
                        self._on_success(sound_enabled, popup_enabled)
                        return

                    if hits >= threshold:
                        break  # 退出魔法阶段，进入富豪
                    else:
                        self._status(f"#{self.use_count} 增幅后命中{hits}不足{threshold}，继续改造...")
                        continue  # 继续改造（内层循环，不重铸）
                finally:
                    self._release_shift()

            if not self._running:
                return

            # ===== Phase 4: Regal (富豪: 蓝→黄) =====
            self._right_click(regal_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1

                text = self._copy_clipboard(delay)
                if not self._running:
                    return

                result = self._check_item(text, rules)
                self.match_count = result.primary_hits + result.secondary_hits

                if result.meets_final_rules(rules):
                    self._on_success(sound_enabled, popup_enabled)
                    return
            finally:
                self._release_shift()

            # ===== Phase 4.5: Exalted (崇高: 可选) =====
            if use_exalt and self._running:
                self._right_click(exalt_coord, delay)
                if not self._running:
                    return
                self._interruptible_sleep(0.15)
                self._hold_shift()
                try:
                    self._shift_click(item_coord, delay)
                    self.use_count += 1

                    text = self._copy_clipboard(delay)
                    if not self._running:
                        return

                    result = self._check_item(text, rules)
                    self.match_count = result.primary_hits + result.secondary_hits

                    if result.meets_final_rules(rules):
                        self._on_success(sound_enabled, popup_enabled)
                        return
                finally:
                    self._release_shift()

            # ===== Phase 5: Scour (重铸: 黄→白) → 回到外层循环(蜕变) =====
            self._status(f"#{self.use_count} 富豪失败，重铸重来...")
            self._right_click(scour_coord, delay)
            if not self._running:
                return
            self._interruptible_sleep(0.15)
            self._hold_shift()
            try:
                self._shift_click(item_coord, delay)
                self.use_count += 1
            finally:
                self._release_shift()
            if not self._running:
                return

    # ===== Sound & Popup =====
    def _play_sound(self, enabled):
        if not enabled:
            return
        selected = getattr(self, '_selected_sound', 'default_ding.wav')
        sound_dirs = []
        if getattr(sys, '_MEIPASS', None):
            sound_dirs.append(Path(sys.executable).parent / "sounds")
            sound_dirs.append(Path(sys._MEIPASS) / "sounds")
        else:
            sound_dirs.append(Path(__file__).parent / "sounds")
        sound_file = None
        audio_exts = ['.wav', '.mp3', '.ogg', '.flac']

        # 第一步：按原名精确查找
        for d in sound_dirs:
            if (d / selected).exists():
                sound_file = d / selected
                break

        # 第二步：如果未找到，尝试同名但不同后缀（用于默认叮声等情况）
        if not sound_file:
            base = Path(selected).stem  # 去掉后缀的文件名，如 default_ding
            for d in sound_dirs:
                for ext in audio_exts:
                    candidate = d / f"{base}{ext}"
                    if candidate.exists():
                        sound_file = candidate
                        break
                if sound_file:
                    break

        if not sound_file:
            print(f"[音效] 未找到音效文件: {selected}, 搜索目录: {sound_dirs}")
            return
        try:
            import ctypes
            winmm = ctypes.windll.winmm
            # 关闭旧媒体
            winmm.mciSendStringW("close pohelper_snd", None, 0, None)
            
            path = str(sound_file)
            # 优先让系统自动识别编码
            cmd = f'open "{path}" alias pohelper_snd'
            ret = winmm.mciSendStringW(cmd, None, 0, None)
            if ret != 0:
                # 自动识别失败再尝试 waveaudio (用于真 WAV)
                winmm.mciSendStringW("close pohelper_snd", None, 0, None)
                cmd = f'open "{path}" type waveaudio alias pohelper_snd'
                ret = winmm.mciSendStringW(cmd, None, 0, None)
                if ret != 0:
                    print(f"[音效] 无法打开: {path}")
                    return
            
            winmm.mciSendStringW("play pohelper_snd", None, 0, None)
            print(f"[音效] 播放: {sound_file}")
        except Exception as e:
            print(f"[音效] 播放失败: {e}")

    def _show_popup(self, msg):
        try:
            import ctypes
            ctypes.windll.user32.MessageBoxW(0, msg, "poe小助手", 0x40)
        except Exception:
            pass
