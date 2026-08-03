"""Configuration tab: mode selection, currency grid, affix rules."""
from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QGridLayout, QGroupBox,
    QLabel, QLineEdit, QPushButton, QComboBox, QScrollArea,
    QFrame, QButtonGroup, QRadioButton, QSizePolicy, QLayout, QCheckBox,
    QInputDialog, QMessageBox
)
from PySide6.QtCore import Qt, Signal, QRect, QSize
from widgets import SliderInput
import os

from models import Mode, CurrencyType, AffixRule, CraftRules
from storage import list_presets, load_preset, save_preset, delete_preset


BTN_STYLE = """
    QPushButton {
        background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
        border-radius: 6px; color: #556; font-size: 11px; padding: 4px 10px;
    }
    QPushButton:hover { border-color: rgba(26,111,181,0.4); background: rgba(255,255,255,0.7); }
"""
BTN_RECORDING = """
    QPushButton {
        background: rgba(26,111,181,0.12); border: 1.5px solid #1a6fb5;
        border-radius: 6px; color: #1a6fb5; font-size: 11px; padding: 4px 10px; font-weight: bold;
    }
"""
BTN_SAVED = """
    QPushButton {
        background: rgba(27,138,62,0.08); border: 1px solid rgba(27,138,62,0.3);
        border-radius: 6px; color: #1b8a3e; font-size: 11px; padding: 4px 10px;
    }
"""
HIT_BTN_STYLE = """
    QPushButton {
        background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
        border-radius: 4px; color: #556; font-size: 11px; padding: 3px 8px;
        min-width: 24px;
    }
    QPushButton:hover { border-color: #1a6fb5; }
"""
HIT_BTN_CHECKED = """
    QPushButton {
        background: #1a6fb5; border: 1px solid #1a6fb5;
        border-radius: 4px; color: white; font-size: 11px; padding: 3px 8px;
        min-width: 24px; font-weight: bold;
    }
"""


class FlowLayout(QLayout):
    """A simple flow layout that wraps items automatically."""
    
    def __init__(self, parent=None, margin=0, spacing=-1):
        super().__init__(parent)
        if margin:
            self.setContentsMargins(margin, margin, margin, margin)
        self._items = []
        self._spacing = spacing if spacing >= 0 else 0
    
    def addItem(self, item):
        self._items.append(item)
    
    def count(self):
        return len(self._items)
    
    def itemAt(self, index):
        if 0 <= index < len(self._items):
            return self._items[index]
        return None
    
    def takeAt(self, index):
        if 0 <= index < len(self._items):
            return self._items.pop(index)
        return None
    
    def expandingDirections(self):
        return Qt.Orientation(0)
    
    def hasHeightForWidth(self):
        return True
    
    def heightForWidth(self, width):
        height = self._do_layout(QRect(0, 0, width, 0), test_only=True)
        return height
    
    def setGeometry(self, rect):
        super().setGeometry(rect)
        self._do_layout(rect, test_only=False)
        # 更新容器高度
        parent = self.parentWidget()
        if parent:
            height = self._do_layout(rect, test_only=True)
            parent.setMinimumHeight(max(30, height + 10))
    
    def sizeHint(self):
        return self.minimumSize()
    
    def minimumSize(self):
        size = QSize()
        for item in self._items:
            size = size.expandedTo(item.minimumSize())
        margins = self.contentsMargins()
        size += QSize(margins.left() + margins.right(), margins.top() + margins.bottom())
        return size
    
    def _do_layout(self, rect, test_only=False):
        x = rect.x()
        y = rect.y()
        line_height = 0
        space = self._spacing
        
        for item in self._items:
            wid = item.widget()
            if wid:
                space_x = space
                space_y = space
            else:
                space_x = 0
                space_y = 0
            
            next_x = x + item.sizeHint().width() + space_x
            if next_x - space_x > rect.right() and line_height > 0:
                x = rect.x()
                y = y + line_height + space_y
                next_x = x + item.sizeHint().width() + space_x
                line_height = 0
            
            if not test_only:
                item.setGeometry(QRect(x, y, item.sizeHint().width(), item.sizeHint().height()))
            
            x = next_x
            line_height = max(line_height, item.sizeHint().height())
        
        return y + line_height - rect.y()


class CurrencyCard(QFrame):
    """A single currency card in the grid."""
    selected = Signal(str)
    coord_clicked = Signal(str)  # Separate signal for coordinate capture
    coord_changed = Signal(str, object)  # key, (x,y) or None

    def __init__(self, currency_key: str):
        super().__init__()
        self.currency_key = currency_key
        self.coord = None
        self.setFixedSize(150, 90)
        self._update_style(False)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 6, 8, 6)
        layout.setSpacing(4)

        # Name label (clickable to select)
        self.name_label = QLabel(CurrencyType.LABELS.get(currency_key, currency_key))
        self.name_label.setStyleSheet("color: #1e2a3a; font-size: 13px; font-weight: bold; border: none;")
        self.name_label.setCursor(Qt.PointingHandCursor)
        self.name_label.mousePressEvent = lambda e: self.selected.emit(self.currency_key)
        layout.addWidget(self.name_label)

        # Coordinate button row
        coord_row = QHBoxLayout()
        coord_row.setSpacing(4)

        self.coord_btn = QPushButton("设定坐标")
        self.coord_btn.setStyleSheet(BTN_STYLE)
        self.coord_btn.setCursor(Qt.PointingHandCursor)
        self.coord_btn.clicked.connect(self._on_coord_click)
        coord_row.addWidget(self.coord_btn, 1)

        self.clear_btn = QPushButton("✕")
        self.clear_btn.setFixedSize(26, 26)
        self.clear_btn.setStyleSheet("QPushButton { background: rgba(192,57,43,0.08); border: 1px solid rgba(192,57,43,0.2); border-radius: 4px; color: #c39; font-size: 10px; } QPushButton:hover { background: rgba(192,57,43,0.15); }")
        self.clear_btn.setCursor(Qt.PointingHandCursor)
        self.clear_btn.clicked.connect(self._clear_coord)
        self.clear_btn.setVisible(False)
        coord_row.addWidget(self.clear_btn)

        layout.addLayout(coord_row)

    def _on_coord_click(self):
        """Click to enter recording mode — emit coord_clicked, NOT selected."""
        self.coord_clicked.emit(self.currency_key)
        # Get current hotkey from parent config tab
        hotkey = self._get_coord_hotkey()
        self.coord_btn.setText(f"移动鼠标后按 {hotkey}...")
        self.coord_btn.setStyleSheet(BTN_RECORDING)

    def _get_coord_hotkey(self) -> str:
        """Get the current coordinate capture hotkey from parent ConfigTab."""
        parent = self
        while parent:
            if hasattr(parent, '_hotkey_set_coord'):
                return parent._hotkey_set_coord
            parent = parent.parentWidget()
        return "F8"

    def set_coordinate(self, coord: tuple | None):
        self.coord = coord
        if coord:
            self.coord_btn.setText(f"✓ ({coord[0]}, {coord[1]})")
            self.coord_btn.setStyleSheet(BTN_SAVED)
            self.clear_btn.setVisible(True)
        else:
            self.coord_btn.setText("设定坐标")
            self.coord_btn.setStyleSheet(BTN_STYLE)
            self.clear_btn.setVisible(False)

    def _clear_coord(self):
        self.coord = None
        self.set_coordinate(None)
        self.coord_changed.emit(self.currency_key, None)

    def set_selected(self, selected: bool):
        self._update_style(selected)

    def _update_style(self, selected: bool):
        if selected:
            self.setStyleSheet("""
                QFrame {
                    background: rgba(26,111,181,0.08);
                    border: 2px solid #1a6fb5;
                    border-radius: 8px;
                }
            """)
        else:
            self.setStyleSheet("""
                QFrame {
                    background: rgba(255,255,255,0.45);
                    border: 1px solid rgba(0,80,160,0.1);
                    border-radius: 8px;
                }
                QFrame:hover { border-color: rgba(26,111,181,0.4); background: rgba(255,255,255,0.65); }
            """)


class AffixTag(QFrame):
    """An affix tag with remove button."""
    remove_clicked = Signal(QFrame)

    def __init__(self, rule: AffixRule, tag_type: str = "primary"):
        super().__init__()
        self.rule = rule
        colors = {
            "primary": ("rgba(27,138,62,0.12)", "rgba(27,138,62,0.3)"),
            "secondary": ("rgba(26,111,181,0.12)", "rgba(26,111,181,0.3)"),
            "exclude": ("rgba(192,57,43,0.12)", "rgba(192,57,43,0.3)"),
        }
        bg, border = colors.get(tag_type, colors["primary"])

        self.setStyleSheet(f"""
            QFrame {{
                background: {bg};
                border: 1px solid {border};
                border-radius: 12px;
                padding: 2px 4px;
            }}
        """)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(8, 2, 4, 2)
        layout.setSpacing(4)

        # Display text (just the affix text)
        label = QLabel(rule.text)
        label.setStyleSheet("color: #1e2a3a; font-size: 11px; border: none;")
        layout.addWidget(label)

        remove_btn = QLabel("✕")
        remove_btn.setStyleSheet("color: #888; font-size: 11px; border: none; padding: 0 2px;")
        remove_btn.setCursor(Qt.PointingHandCursor)
        remove_btn.mousePressEvent = lambda e: self.remove_clicked.emit(self)
        layout.addWidget(remove_btn)


class ConfigTab(QWidget):
    """Main configuration tab."""
    mode_changed = Signal(Mode)

    def __init__(self, coordinates: dict, rules: CraftRules, settings: dict = None):
        super().__init__()
        self.coordinates = coordinates
        self.rules = rules
        self._settings = settings or {}
        self._hotkey_set_coord = "F8"
        self.currency_cards = {}  # populated below
        self._dirty = False       # True if affixes changed since last save/load
        self._primary_hit = 0     # current effective primary count
        self._secondary_hit = 0   # current effective secondary count
        self._build_ui()
        self._load_state()   # ensure initial grid & mode

    def set_hotkey_coord(self, hotkey: str):
        """Update the coordinate capture hotkey display."""
        self._hotkey_set_coord = hotkey

    def get_delay_ms(self) -> int:
        """Get the crafting delay from the UI."""
        return self.delay_slider.value()

    def get_mode2_scour_alch(self) -> bool:
        """Check if Mode 2 uses Scour+Alch sub-mode."""
        return self.mode2_scour_alch.isChecked()

    def get_use_exalt(self) -> bool:
        """Check if Mode 3 uses Exalted after Regal."""
        return self.exalt_checkbox.isChecked()

    def _build_ui(self):
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setStyleSheet("QScrollArea { border: none; }")

        container = QWidget()
        layout = QVBoxLayout(container)
        layout.setSpacing(10)

        # === Mode Selection ===
        mode_group = QGroupBox()
        mode_group.setStyleSheet("""
            QGroupBox {
                background: rgba(255,255,255,0.55); border: 1px solid rgba(255,255,255,0.7);
                border-radius: 10px; padding: 12px; margin-top: 0;
            }
        """)
        mode_layout = QHBoxLayout(mode_group)
        mode_layout.setSpacing(8)

        self.mode_buttons = QButtonGroup()
        mode_info = [
            (Mode.SINGLE, "🔨", "Mode 1", "单通货"),
            (Mode.ALT_AUG, "⚡", "Mode 2", "改造+增幅"),
            (Mode.ALT_AUG_REGAL, "💎", "Mode 3", "改+增+富"),
        ]
        for mode, icon, name, desc in mode_info:
            btn = QRadioButton(f"{icon} {name}\n{desc}")
            btn.setStyleSheet("""
                QRadioButton {
                    color: #555; font-size: 12px; padding: 10px 12px;
                    spacing: 0px;
                }
                QRadioButton::indicator { width: 0px; height: 0px; }
                QRadioButton:checked {
                    color: white; background: rgba(26,111,181,0.85);
                    border-radius: 8px; padding: 10px 12px;
                }
                QRadioButton:hover { color: #333; background: rgba(255,255,255,0.7); border-radius: 8px; }
            """)
            btn.setMinimumHeight(50)
            self.mode_buttons.addButton(btn, int(mode))
            mode_layout.addWidget(btn)

        self.mode_buttons.idClicked.connect(self._on_mode_changed)
        layout.addWidget(mode_group)

        # === Mode 2 sub-mode: Alt+Aug vs Scour+Alch ===
        self.mode2_frame = QFrame()
        self.mode2_frame.setStyleSheet("QFrame { border-bottom: 1px solid rgba(0,80,160,0.08); }")
        m2_layout = QHBoxLayout(self.mode2_frame)
        m2_layout.setContentsMargins(0, 4, 0, 4)
        m2_lbl = QLabel("Mode 2 子模式:")
        m2_lbl.setStyleSheet("color: #334455; font-size: 12px;")
        m2_layout.addWidget(m2_lbl)
        self.mode2_alt_aug = QRadioButton("改造+增幅")
        self.mode2_alt_aug.setChecked(True)
        self.mode2_alt_aug.setStyleSheet("color: #334455; font-size: 12px;")
        m2_layout.addWidget(self.mode2_alt_aug)
        self.mode2_scour_alch = QRadioButton("重铸+点金")
        self.mode2_scour_alch.setStyleSheet("color: #334455; font-size: 12px;")
        m2_layout.addWidget(self.mode2_scour_alch)
        m2_layout.addStretch()
        self.mode2_frame.setVisible(False)
        layout.addWidget(self.mode2_frame)

        # === Mode 3: Exalted checkbox ===
        self.mode3_frame = QFrame()
        self.mode3_frame.setStyleSheet("QFrame { border-bottom: 1px solid rgba(0,80,160,0.08); }")
        m3_layout = QHBoxLayout(self.mode3_frame)
        m3_layout.setContentsMargins(0, 4, 0, 4)
        self.exalt_checkbox = QCheckBox("富豪后使用崇高石")
        self.exalt_checkbox.setStyleSheet("color: #B45F06; font-size: 13px; font-weight: bold;")
        m3_layout.addWidget(self.exalt_checkbox)
        m3_layout.addStretch()
        self.mode3_frame.setVisible(False)
        layout.addWidget(self.mode3_frame)

        # === 操作延迟 (only affects crafting) ===
        delay_row = QHBoxLayout()
        delay_lbl = QLabel("操作延迟:")
        delay_lbl.setStyleSheet("color: #334455; font-size: 12px;")
        delay_row.addWidget(delay_lbl)
        self.delay_slider = SliderInput(default=self._settings.get("delay_ms", 33))
        delay_row.addWidget(self.delay_slider)
        delay_hint = QLabel("影响洗词缀操作速度")
        delay_hint.setStyleSheet("color: #8a9aaa; font-size: 10px; padding-left: 6px;")
        delay_row.addWidget(delay_hint)
        delay_row.addStretch()
        layout.addLayout(delay_row)

        # === Currency Grid (built dynamically via _on_mode_changed) ===
        currency_group = QGroupBox("💰 通货选择")
        currency_group.setStyleSheet("""
            QGroupBox {
                background: rgba(255,255,255,0.55); border: 1px solid rgba(255,255,255,0.7);
                border-radius: 10px; padding: 12px;
                color: #1a6fb5; font-size: 13px; font-weight: bold;
            }
            QGroupBox::title { subcontrol-origin: margin; left: 12px; }
        """)
        self._currency_grid = QGridLayout(currency_group)
        self._currency_grid.setSpacing(6)

        # Create all currency cards and add them to grid immediately (static grid, no flicker)
        self.currency_cards = {}
        all_currencies = [
            CurrencyType.ALTERATION, CurrencyType.AUGMENTATION, CurrencyType.ALCHEMY, CurrencyType.SCOURING,
            CurrencyType.CHAOS, CurrencyType.CUSTOM,
            CurrencyType.REGAL, CurrencyType.TRANSMUTATION, CurrencyType.EXALTED,
            CurrencyType.DIVINE,
        ]
        row, col = 0, 0
        for curr_key in all_currencies:
            card = CurrencyCard(curr_key)
            coord = self.coordinates.get(curr_key)
            card.set_coordinate(coord)
            card.selected.connect(self._on_currency_selected)
            self.currency_cards[curr_key] = card
            self._currency_grid.addWidget(card, row, col)
            col += 1
            if col >= 4:
                col = 0
                row += 1

        # Pre-create the item coordinate row and add it to the last row of the grid
        self._item_row_layout = QHBoxLayout()
        item_label = QLabel("📍 装备坐标")
        item_label.setStyleSheet("color: #1a6fb5; font-size: 11px; font-weight: bold;")
        self._item_row_layout.addWidget(item_label)

        self.item_coord_btn = QPushButton("设定坐标")
        self.item_coord_btn.setStyleSheet(BTN_STYLE)
        self.item_coord_btn.setCursor(Qt.PointingHandCursor)
        self.item_coord_btn.clicked.connect(self._on_item_coord_click)
        self._item_row_layout.addWidget(self.item_coord_btn, 1)

        self.item_clear_btn = QPushButton("✕")
        self.item_clear_btn.setFixedSize(26, 26)
        self.item_clear_btn.setStyleSheet("QPushButton { background: rgba(192,57,43,0.08); border: 1px solid rgba(192,57,43,0.2); border-radius: 4px; color: #c39; font-size: 10px; } QPushButton:hover { background: rgba(192,57,43,0.15); }")
        self.item_clear_btn.setCursor(Qt.PointingHandCursor)
        self.item_clear_btn.clicked.connect(self._clear_item_coord)
        self.item_clear_btn.setVisible(False)
        self._item_row_layout.addWidget(self.item_clear_btn)

        # 一键清空所有坐标
        clear_all_btn = QPushButton("🗑 清空全部")
        clear_all_btn.setToolTip("清除所有通货与装备坐标")
        clear_all_btn.setStyleSheet(BTN_STYLE)
        clear_all_btn.setCursor(Qt.PointingHandCursor)
        clear_all_btn.clicked.connect(self._clear_all_coordinates)
        self._item_row_layout.addWidget(clear_all_btn)

        self._item_row_layout.addStretch()
        
         # 把装备坐标行添加到网格最后
        next_row = row + 1
        self._currency_grid.addLayout(self._item_row_layout, next_row, 0, 1, 4)

       

        layout.addWidget(currency_group)

        # === Affix Rules ===
        affix_group = QGroupBox("📋 词缀规则")
        affix_group.setStyleSheet(currency_group.styleSheet())
        affix_layout = QVBoxLayout(affix_group)

        # ---- Preset row ----
        preset_row = QHBoxLayout()
        preset_row.addWidget(QLabel("配置预设:"))
        self.preset_combo = QComboBox()
        self.preset_combo.setStyleSheet("""
            QComboBox {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1e2a3a; padding: 4px 8px; border-radius: 6px; font-size: 11px;
            }
            QComboBox QAbstractItemView {
                background: rgba(255,255,255,0.9); color: #1e2a3a;
                selection-background-color: rgba(26,111,181,0.15);
            }
        """)
        self.preset_combo.setMinimumWidth(160)
        self._refresh_preset_combo()
        # 展开下拉框时刷新
        self.preset_combo.activated.connect(lambda _: self._refresh_preset_combo())
        preset_row.addWidget(self.preset_combo)

        self.preset_combo.currentIndexChanged.connect(self._on_preset_selected)

        save_btn = QPushButton("💾 保存")
        save_btn.setStyleSheet(BTN_STYLE)
        save_btn.setCursor(Qt.PointingHandCursor)
        save_btn.clicked.connect(self._save_preset)
        preset_row.addWidget(save_btn)

        folder_btn = QPushButton("📁")
        folder_btn.setFixedSize(28, 28)
        folder_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1a6fb5; border-radius: 6px; font-size: 14px;
            }
            QPushButton:hover { border-color: #1a6fb5; }
        """)
        folder_btn.setToolTip("打开预设文件夹")
        folder_btn.clicked.connect(self._open_presets_folder)
        preset_row.addWidget(folder_btn)
        preset_row.addStretch()
        affix_layout.addLayout(preset_row)

        # Primary
        primary_header = QHBoxLayout()
        primary_header.addWidget(QLabel("主词缀池"))
        primary_clear_btn = QPushButton("🗑 清空")
        primary_clear_btn.setStyleSheet("color: #c0392b; font-size: 10px; border: none; padding: 2px 6px;")
        primary_clear_btn.setCursor(Qt.PointingHandCursor)
        primary_clear_btn.clicked.connect(lambda: self._clear_pool("primary"))
        primary_header.addWidget(primary_clear_btn)
        primary_header.addStretch()
        primary_header.addWidget(QLabel("命中数:"))

        # Primary hit count button group [0][1][2][3]
        self.primary_count_group = QButtonGroup()
        for val in range(4):
            btn = QPushButton(str(val))
            btn.setCheckable(True)
            btn.setStyleSheet(HIT_BTN_STYLE)
            btn.clicked.connect(lambda checked, v=val, b=btn: self._on_hit_count_clicked(b, v))
            self.primary_count_group.addButton(btn, val)
            primary_header.addWidget(btn)
        affix_layout.addLayout(primary_header)

        primary_input = QHBoxLayout()
        self.primary_input = QLineEdit()
        self.primary_input.setPlaceholderText("输入主词缀，如：最大生命")
        self.primary_input.setStyleSheet("background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15); color: #1e2a3a; padding: 6px; border-radius: 6px;")
        primary_input.addWidget(self.primary_input)

        add_primary_btn = QPushButton("+ 添加")
        add_primary_btn.setStyleSheet("background: #2d6a4f; color: white; border: none; padding: 6px 14px; border-radius: 4px;")
        add_primary_btn.clicked.connect(lambda: self._add_affix("primary"))
        primary_input.addWidget(add_primary_btn)
        affix_layout.addLayout(primary_input)

        self.primary_tags_layout = FlowLayout(spacing=4)
        self.primary_tags_container = QWidget()
        self.primary_tags_container.setLayout(self.primary_tags_layout)
        self.primary_tags_container.setMinimumHeight(30)
        affix_layout.addWidget(self.primary_tags_container)
        self.primary_tags = []

        # Secondary
        secondary_header = QHBoxLayout()
        secondary_header.addWidget(QLabel("次级词缀池"))
        secondary_clear_btn = QPushButton("🗑 清空")
        secondary_clear_btn.setStyleSheet("color: #c0392b; font-size: 10px; border: none; padding: 2px 6px;")
        secondary_clear_btn.setCursor(Qt.PointingHandCursor)
        secondary_clear_btn.clicked.connect(lambda: self._clear_pool("secondary"))
        secondary_header.addWidget(secondary_clear_btn)
        secondary_header.addStretch()
        secondary_header.addWidget(QLabel("命中数:"))

        # Secondary hit count button group [0][1][2][3]
        self.secondary_count_group = QButtonGroup()
        for val in range(4):
            btn = QPushButton(str(val))
            btn.setCheckable(True)
            btn.setStyleSheet(HIT_BTN_STYLE)
            btn.clicked.connect(lambda checked, v=val, b=btn: self._on_hit_count_clicked(b, v))
            self.secondary_count_group.addButton(btn, val)
            secondary_header.addWidget(btn)
        affix_layout.addLayout(secondary_header)

        secondary_input = QHBoxLayout()
        self.secondary_input = QLineEdit()
        self.secondary_input.setPlaceholderText("输入次级词缀，如：火焰抗性")
        self.secondary_input.setStyleSheet(self.primary_input.styleSheet())
        secondary_input.addWidget(self.secondary_input)

        add_secondary_btn = QPushButton("+ 添加")
        add_secondary_btn.setStyleSheet("background: #2a6496; color: white; border: none; padding: 6px 14px; border-radius: 4px;")
        add_secondary_btn.clicked.connect(lambda: self._add_affix("secondary"))
        secondary_input.addWidget(add_secondary_btn)
        affix_layout.addLayout(secondary_input)

        self.secondary_tags_layout = FlowLayout(spacing=4)
        self.secondary_tags_container = QWidget()
        self.secondary_tags_container.setLayout(self.secondary_tags_layout)
        self.secondary_tags_container.setMinimumHeight(30)
        affix_layout.addWidget(self.secondary_tags_container)
        self.secondary_tags = []

        # Exclude
        exclude_header = QHBoxLayout()
        exclude_label = QLabel("排除词缀")
        exclude_header.addWidget(exclude_label)
        exclude_clear_btn = QPushButton("🗑 清空")
        exclude_clear_btn.setStyleSheet("color: #c0392b; font-size: 10px; border: none; padding: 2px 6px;")
        exclude_clear_btn.setCursor(Qt.PointingHandCursor)
        exclude_clear_btn.clicked.connect(lambda: self._clear_pool("exclude"))
        exclude_header.addWidget(exclude_clear_btn)
        exclude_header.addStretch()
        exclude_hint = QLabel("出现任一即判定失败")
        exclude_hint.setStyleSheet("color: #c0392b; font-size: 11px;")
        exclude_header.addWidget(exclude_hint)
        affix_layout.addLayout(exclude_header)

        exclude_input = QHBoxLayout()
        self.exclude_input = QLineEdit()
        self.exclude_input.setPlaceholderText("输入排除词缀")
        self.exclude_input.setStyleSheet(self.primary_input.styleSheet())
        exclude_input.addWidget(self.exclude_input)

        add_exclude_btn = QPushButton("+ 添加")
        add_exclude_btn.setStyleSheet("background: #962a2a; color: white; border: none; padding: 6px 14px; border-radius: 4px;")
        add_exclude_btn.clicked.connect(lambda: self._add_affix("exclude"))
        exclude_input.addWidget(add_exclude_btn)
        affix_layout.addLayout(exclude_input)

        self.exclude_tags_layout = FlowLayout(spacing=4)
        self.exclude_tags_container = QWidget()
        self.exclude_tags_container.setLayout(self.exclude_tags_layout)
        self.exclude_tags_container.setMinimumHeight(30)
        affix_layout.addWidget(self.exclude_tags_container)
        self.exclude_tags = []

        layout.addWidget(affix_group)

        # Allow Enter key to add affixes
        self.primary_input.returnPressed.connect(lambda: self._add_affix("primary"))
        self.secondary_input.returnPressed.connect(lambda: self._add_affix("secondary"))
        self.exclude_input.returnPressed.connect(lambda: self._add_affix("exclude"))

        scroll.setWidget(container)

        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(scroll)

        

        self.selected_currency = None


    # ===== Hit count button logic =====

    def _on_hit_count_clicked(self, btn: QPushButton, val: int):
        """Update hit button style when clicked, with real-time validation."""
        # Determine which group
        if btn in self.primary_count_group.buttons():
            group = self.primary_count_group
            other_group = self.secondary_count_group
        else:
            group = self.secondary_count_group
            other_group = self.primary_count_group

        other_val = self._get_hit_count(other_group)
        total = val + other_val

        # Validate against mode limits
        if self.rules.mode == Mode.ALT_AUG and total > 2:
            QMessageBox.warning(self, "配置提示", "Mode 2 总词缀命中数不能超过 2，已恢复之前的值。")
            self._set_hit_count(group, self._get_old_hit(group))
            return
        elif self.rules.mode == Mode.ALT_AUG_REGAL and total > 3:
            QMessageBox.warning(self, "配置提示", "Mode 3 总词缀命中数不能超过 3，已恢复之前的值。")
            self._set_hit_count(group, self._get_old_hit(group))
            return

        # Accept new value
        for b in group.buttons():
            b.setStyleSheet(HIT_BTN_CHECKED if b.isChecked() else HIT_BTN_STYLE)
        self._update_hit_var(group, val)
        self._set_dirty()

    def _get_old_hit(self, group: QButtonGroup) -> int:
        """Return the currently stored effective hit count for the given group."""
        if group is self.primary_count_group:
            return self._primary_hit
        else:
            return self._secondary_hit

    def _update_hit_var(self, group: QButtonGroup, val: int):
        if group is self.primary_count_group:
            self._primary_hit = val
        else:
            self._secondary_hit = val

    def _get_hit_count(self, group: QButtonGroup) -> int:
        checked = group.checkedButton()
        return int(checked.text()) if checked else 0

    def _set_hit_count(self, group: QButtonGroup, value: int):
        btn = group.button(value)
        if btn:
            btn.setChecked(True)
            for b in group.buttons():
                b.setStyleSheet(HIT_BTN_CHECKED if b.isChecked() else HIT_BTN_STYLE)
            self._update_hit_var(group, value)

    # ===== Preset logic =====

    def _refresh_preset_combo(self):
        """Refresh the preset dropdown from disk."""
        current = self.preset_combo.currentData()
        self.preset_combo.blockSignals(True)
        self.preset_combo.clear()
        self.preset_combo.addItem("无", None)
        for name in list_presets():
            self.preset_combo.addItem(name, name)
        if current:
            idx = self.preset_combo.findData(current)
            if idx >= 0:
                self.preset_combo.setCurrentIndex(idx)
        self.preset_combo.blockSignals(False)

    def _get_current_preset_data(self) -> dict:
        """Serialize current UI rules to a preset dict."""
        return {
            "primary_affixes": [t.rule.text for t in self.primary_tags],
            "primary_hit_count": self._get_hit_count(self.primary_count_group),
            "secondary_affixes": [t.rule.text for t in self.secondary_tags],
            "secondary_hit_count": self._get_hit_count(self.secondary_count_group),
            "exclude_affixes": [t.rule.text for t in self.exclude_tags],
        }

    def _apply_preset_data(self, data: dict):
        """Apply a preset dict to current UI (clear and rebuild)."""
        self._clear_pool("primary")
        self._clear_pool("secondary")
        self._clear_pool("exclude")
        for text in data.get("primary_affixes", []):
            self._create_tag(AffixRule(text=text), "primary")
        self._set_hit_count(self.primary_count_group, data.get("primary_hit_count", 0))
        for text in data.get("secondary_affixes", []):
            self._create_tag(AffixRule(text=text), "secondary")
        self._set_hit_count(self.secondary_count_group, data.get("secondary_hit_count", 0))
        for text in data.get("exclude_affixes", []):
            self._create_tag(AffixRule(text=text), "exclude")

    def _on_preset_selected(self, index: int):
        """用户在下拉框选择预设时触发。"""
        name = self.preset_combo.currentData()
        if name is None:
            return  # 选中"无"
        # 检查是否有未保存修改
        if self._dirty:
            reply = QMessageBox.question(
                self, "未保存修改",
                "当前词缀尚未保存，切换预设将丢失现有词缀。是否继续？",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if reply == QMessageBox.No:
                # 恢复之前选中项
                self.preset_combo.blockSignals(True)
                self.preset_combo.setCurrentIndex(0)
                self.preset_combo.blockSignals(False)
                return
        data = load_preset(name)
        if data:
            self._apply_preset_data(data)
            self._dirty = False

    def _save_preset(self):
        """保存当前词缀为预设。"""
        name, ok = QInputDialog.getText(self, "保存预设", "请输入预设名称：")
        if not ok or not name.strip():
            return
        name = name.strip()
        # 检查是否已存在
        if name in list_presets():
            reply = QMessageBox.question(
                self, "覆盖确认",
                f"已存在同名预设「{name}」，是否覆盖？",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if reply == QMessageBox.No:
                return
        data = self._get_current_preset_data()
        save_preset(name, data)
        self._dirty = False
        self._refresh_preset_combo()
        # 选中刚保存的
        idx = self.preset_combo.findData(name)
        if idx >= 0:
            self.preset_combo.setCurrentIndex(idx)

    def _open_presets_folder(self):
        """打开预设文件夹。"""
        from storage import _presets_dir
        d = _presets_dir()
        if os.path.exists(str(d)):
            os.startfile(str(d))
        self._refresh_preset_combo()

    # ===== Dirty flag =====

    def _set_dirty(self):
        self._dirty = True

    # ===== Load / Save state =====

    def _load_state(self):
        """Load saved state into UI."""
        # Mode
        mode_btn = self.mode_buttons.button(int(self.rules.mode))
        if mode_btn:
            mode_btn.setChecked(True)
            self._on_mode_changed(int(self.rules.mode))

        # Currency
        self.selected_currency = self.rules.single_currency
        self._update_currency_selection()

        # Primary affixes
        self._set_hit_count(self.primary_count_group, self.rules.primary_hit_count)
        for rule in self.rules.primary_affixes:
            self._create_tag(rule, "primary")

        # Secondary affixes
        self._set_hit_count(self.secondary_count_group, self.rules.secondary_hit_count)
        for rule in self.rules.secondary_affixes:
            self._create_tag(rule, "secondary")

        # Exclude affixes
        for rule in self.rules.exclude_affixes:
            self._create_tag(rule, "exclude")

        # Item coordinate
        item_coord = self.coordinates.get("item")
        if item_coord:
            self.item_coord_btn.setText(f"✓ ({item_coord[0]}, {item_coord[1]})")
            self.item_coord_btn.setStyleSheet(BTN_SAVED)
            self.item_clear_btn.setVisible(True)
        else:
            self.item_coord_btn.setText("设定坐标")
            self.item_coord_btn.setStyleSheet(BTN_STYLE)
            self.item_clear_btn.setVisible(False)

        self._currency_grid.invalidate()
        self._currency_grid.update()

        self._dirty = False

    def _on_mode_changed(self, mode_id: int):
        # 防闪烁：整个重建过程不刷新界面
        self.setUpdatesEnabled(False)
        
        mode = Mode(mode_id)
        self.rules.mode = mode
        relevant = CurrencyType.MODE_CURRENCIES.get(mode, [])

        # 先隐藏所有卡片
        for card in self.currency_cards.values():
            card.setVisible(False)

        # 清空网格
        self._clear_grid(self._currency_grid)

        # 重新添加可见卡片
        row, col = 0, 0
        for key, card in self.currency_cards.items():
            if key in relevant:
                card.setVisible(True)
                self._currency_grid.addWidget(card, row, col)
                col += 1
                if col >= 4:
                    col = 0
                    row += 1

        # 添加装备坐标行
        next_row = row + 1
        self._currency_grid.addLayout(self._item_row_layout, next_row, 0, 1, 4)

        # Mode 2/3 子选项
        self.mode2_frame.setVisible(mode == Mode.ALT_AUG)
        self.mode3_frame.setVisible(mode == Mode.ALT_AUG_REGAL)

        # Mode 1 自动选通货
        if mode == Mode.SINGLE:
            if self.selected_currency not in relevant:
                self.selected_currency = relevant[0] if relevant else None
                self._update_currency_selection()

        # 恢复刷新，强制立即绘制完整布局
        self.setUpdatesEnabled(True)
        self._currency_grid.invalidate()
        self._currency_grid.update()

    @staticmethod
    def _clear_grid(grid: QGridLayout):
        while grid.count():
            item = grid.takeAt(0)

    def _on_currency_selected(self, currency_key: str):
        self.selected_currency = currency_key
        self._update_currency_selection()

    def _on_item_coord_click(self):
        """Click item coord button to enter recording mode."""
        self._update_currency_selection()
        self.item_coord_btn.setText(f"移动鼠标后按 {self._hotkey_set_coord}...")
        self.item_coord_btn.setStyleSheet(BTN_RECORDING)

    def _clear_item_coord(self):
        """Clear item coordinate."""
        self.coordinates["item"] = None
        self.item_coord_btn.setText("设定坐标")
        self.item_coord_btn.setStyleSheet(BTN_STYLE)
        self.item_clear_btn.setVisible(False)

    def _clear_all_coordinates(self):
        """一键清空所有通货和装备坐标。"""
        # 清空通货坐标
        for key, card in self.currency_cards.items():
            self.coordinates[key] = None
            card.set_coordinate(None)
        # 清空装备坐标
        self._clear_item_coord()
        # 持久化
        from storage import save_coordinates
        save_coordinates(self.coordinates)

    def reset_coord_buttons(self):
        """Reset all coord buttons from recording state to normal."""
        for card in self.currency_cards.values():
            if card.coord:
                card.set_coordinate(card.coord)
            else:
                card.coord_btn.setText("设定坐标")
                card.coord_btn.setStyleSheet(BTN_STYLE)
        item_coord = self.coordinates.get("item")
        if item_coord:
            self.item_coord_btn.setText(f"✓ ({item_coord[0]}, {item_coord[1]})")
            self.item_coord_btn.setStyleSheet(BTN_SAVED)
            self.item_clear_btn.setVisible(True)
        else:
            self.item_coord_btn.setText("设定坐标")
            self.item_coord_btn.setStyleSheet(BTN_STYLE)
            self.item_clear_btn.setVisible(False)

    def _update_currency_selection(self):
        for key, card in self.currency_cards.items():
            card.set_selected(key == self.selected_currency)

    def _add_affix(self, affix_type: str):
        """Add an affix rule from the input field."""
        if affix_type == "primary":
            text = self.primary_input.text().strip()
            if not text:
                return
            rule = AffixRule(text=text)
            self._create_tag(rule, "primary")
            self.primary_input.clear()
        elif affix_type == "secondary":
            text = self.secondary_input.text().strip()
            if not text:
                return
            rule = AffixRule(text=text)
            self._create_tag(rule, "secondary")
            self.secondary_input.clear()
        elif affix_type == "exclude":
            text = self.exclude_input.text().strip()
            if not text:
                return
            rule = AffixRule(text=text)
            self._create_tag(rule, "exclude")
            self.exclude_input.clear()
        self._set_dirty()

    def _create_tag(self, rule: AffixRule, tag_type: str):
        """Create and add a tag widget."""
        tag = AffixTag(rule, tag_type)
        tag.remove_clicked.connect(lambda t: self._remove_tag(t, tag_type))

        if tag_type == "primary":
            self.primary_tags.append(tag)
            self.primary_tags_layout.addWidget(tag)
        elif tag_type == "secondary":
            self.secondary_tags.append(tag)
            self.secondary_tags_layout.addWidget(tag)
        elif tag_type == "exclude":
            self.exclude_tags.append(tag)
            self.exclude_tags_layout.addWidget(tag)

    def _remove_tag(self, tag: AffixTag, tag_type: str):
        """Remove a tag widget."""
        if tag_type == "primary":
            self.primary_tags.remove(tag)
            self.primary_tags_layout.removeWidget(tag)
        elif tag_type == "secondary":
            self.secondary_tags.remove(tag)
            self.secondary_tags_layout.removeWidget(tag)
        elif tag_type == "exclude":
            self.exclude_tags.remove(tag)
            self.exclude_tags_layout.removeWidget(tag)
        tag.deleteLater()
        self._set_dirty()

    def _clear_pool(self, pool_type: str):
        """Clear all tags from a pool."""
        if pool_type == "primary":
            for tag in self.primary_tags[:]:
                self._remove_tag(tag, "primary")
        elif pool_type == "secondary":
            for tag in self.secondary_tags[:]:
                self._remove_tag(tag, "secondary")
        elif pool_type == "exclude":
            for tag in self.exclude_tags[:]:
                self._remove_tag(tag, "exclude")
        self._set_dirty()

    def update_rules(self, rules: CraftRules):
        """Update rules object from current UI state."""
        rules.mode = self.rules.mode
        rules.single_currency = self.selected_currency or CurrencyType.ALTERATION
        rules.primary_hit_count = self._get_hit_count(self.primary_count_group)
        rules.secondary_hit_count = self._get_hit_count(self.secondary_count_group)
        rules.primary_affixes = [t.rule for t in self.primary_tags]
        rules.secondary_affixes = [t.rule for t in self.secondary_tags]
        rules.exclude_affixes = [t.rule for t in self.exclude_tags]

    def clear_all(self):
        """Clear all affix rules."""
        self._clear_pool("primary")
        self._clear_pool("secondary")
        self._clear_pool("exclude")
        self._set_hit_count(self.primary_count_group, 0)
        self._set_hit_count(self.secondary_count_group, 0)