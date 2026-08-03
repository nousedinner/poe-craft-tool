"""Shared UI widgets."""
from PySide6.QtWidgets import QWidget, QHBoxLayout, QSlider, QSpinBox
from PySide6.QtCore import Qt


class SliderInput(QWidget):
    """Slider + SpinBox combo for time interval input. Range 10-200ms."""

    def __init__(self, default=33, min_val=10, max_val=200, suffix=" ms", width=80):
        super().__init__()
        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(4)

        self.slider = QSlider(Qt.Horizontal)
        self.slider.setRange(min_val, max_val)
        self.slider.setValue(default)
        self.slider.setFixedWidth(120)

        self.spinbox = QSpinBox()
        self.spinbox.setRange(min_val, max_val)
        self.spinbox.setValue(default)
        self.spinbox.setSuffix(suffix)
        self.spinbox.setFixedWidth(width)
        self.spinbox.setStyleSheet("""
            QSpinBox {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1e2a3a; padding: 4px; border-radius: 6px;
            }
        """)

        self.slider.valueChanged.connect(self.spinbox.setValue)
        self.spinbox.valueChanged.connect(self.slider.setValue)

        layout.addWidget(self.slider)
        layout.addWidget(self.spinbox)

    def value(self) -> int:
        return self.spinbox.value()

    def setValue(self, v: int):
        self.spinbox.setValue(v)
