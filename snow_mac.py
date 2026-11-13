import sys
import math
import random
import socket
import time
from PyQt6.QtWidgets import QApplication, QWidget, QSystemTrayIcon, QMenu
from PyQt6.QtCore import Qt, QTimer, QPointF
from PyQt6.QtGui import (
    QPainter, QColor, QPen, QBrush, QPolygonF,
    QPainterPath, QIcon, QPixmap, QAction, QCursor
)

try:
    import Quartz
    HAS_QUARTZ = True
except ImportError:
    HAS_QUARTZ = False

BIN_SIZE = 3
MAX_SLOPE_DIFF = 1.25


def get_visible_windows_mac(own_pid):
    windows = []
    if not HAS_QUARTZ:
        return windows

    options = Quartz.kCGWindowListOptionOnScreenOnly | Quartz.kCGWindowListExcludeDesktopElements
    win_list = Quartz.CGWindowListCopyWindowInfo(options, Quartz.kCGNullWindowID)
    if not win_list:
        return windows

    for win in win_list:
        if win.get('kCGWindowOwnerPID', 0) == own_pid:
            continue
        if win.get('kCGWindowLayer', 0) != 0:
            continue
        bounds = win.get('kCGWindowBounds')
        if not bounds:
            continue

        x = int(bounds.get('X', 0))
        y = int(bounds.get('Y', 0))
        w = int(bounds.get('Width', 0))
        h = int(bounds.get('Height', 0))

        if w >= 100 and h >= 50 and y > 28:
            wid = win.get('kCGWindowNumber', 0)
            windows.append((wid, x, y, x + w, y + h))
    return windows


class WindowShelf:
    def __init__(self, wid, left, top, right, bottom):
        self.wid = wid
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
        bins = max(1, (right - left) // BIN_SIZE)
        self.heights = [0.0] * bins
        self.seen = True


class DesktopSnowMac(QWidget):
    def __init__(self):
        super().__init__()
        # Прозрачное клик-сквозь окно поверх всего экрана
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint |
            Qt.WindowType.WindowStaysOnTopHint |
            Qt.WindowType.WindowTransparentForInput |
            Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)

        self.own_pid = QApplication.applicationPid()
        screen_full = QApplication.primaryScreen().geometry()
        screen_avail = QApplication.primaryScreen().availableGeometry()

        self.setGeometry(screen_full)
        self.screen_w = screen_full.width()
        self.screen_h = screen_full.height()
        self.ground_y = float(screen_avail.bottom()) + 1.0

        total_bins = max(1, self.screen_w // BIN_SIZE)
        self.ground_heights = [0.0] * total_bins
        self.window_shelves = {}

        # Частицы: [x, y, vx, vy, radius, phase]
        self.particles = []
        for _ in range(140):
            self.spawn_flake(
                random.uniform(0, self.screen_w),
                random.uniform(0, self.ground_y * 0.75)
            )

        self.prev_mouse = QCursor.pos()
        self.smooth_mvx = 0.0
        self.smooth_mvy = 0.0
        self.vortex_strength = 0.0
        self.vortex_dir = 1.0

        self.last_time = time.perf_counter()
        self.frame_count = 0

        self.setup_cat_bridge()
        self.setup_tray()

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.step)
        self.timer.start(16)

    def setup_cat_bridge(self):
        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.setblocking(False)
        try:
            self.udp.bind(('127.0.0.1', 50505))
        except Exception:
            pass
        self.cat_addr = ('127.0.0.1', 50506)

    def process_cat_bridge(self):
        try:
            while True:
                data, _ = self.udp.recvfrom(256)
                parts = data.decode('utf-8').split(':')
                if len(parts) >= 3 and parts[0] == 'JUMP':
                    cat_x = float(parts[1])
                    direction = float(parts[2])
                    self.apply_cat_jump(cat_x, direction)
        except Exception:
            pass

        if self.frame_count % 20 == 0 and self.ground_heights:
            max_h = 0.0
            max_bin = 0
            for i in range(10, len(self.ground_heights) - 10):
                if self.ground_heights[i] > max_h:
                    max_h = self.ground_heights[i]
                    max_bin = i
            world_x = max_bin * BIN_SIZE
            msg = f"DRIFT:{world_x:.1f}:{max_h:.1f}".encode('utf-8')
            try:
                self.udp.sendto(msg, self.cat_addr)
            except Exception:
                pass

    def apply_cat_jump(self, cat_x, direction):
        center_bin = int(cat_x // BIN_SIZE)
        radius_bins = 18
        for b in range(center_bin - radius_bins, center_bin + radius_bins + 1):
            if 0 <= b < len(self.ground_heights):
                h = self.ground_heights[b]
                if h < 1.0:
                    continue
                dist_norm = abs(b - center_bin) / float(radius_bins)
                kick = h * max(0.25, min(0.95, 1.0 - dist_norm * 0.65))
                self.ground_heights[b] -= kick

                side = direction if b == center_bin else (1.0 if b > center_bin else -1.0)
                svx = side * random.uniform(45.0, 125.0) + direction * 25.0
                svy = -random.uniform(65.0, 155.0)
                self.emit_particles(b * BIN_SIZE, self.ground_y - h * 0.5, kick * 1.3, svx, svy)

    def setup_tray(self):
        pixmap = QPixmap(32, 32)
        pixmap.fill(Qt.GlobalColor.transparent)
        p = QPainter(pixmap)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setBrush(QBrush(QColor(30, 110, 210, 230)))
        p.setPen(Qt.PenStyle.NoPen)
        p.drawEllipse(2, 2, 28, 28)
        pen = QPen(QColor(255, 255, 255), 2.2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
        p.setPen(pen)
        for a in range(3):
            ang = a * math.pi / 3.0
            dx, dy = math.cos(ang) * 9.0, math.sin(ang) * 9.0
            p.drawLine(QPointF(16 - dx, 16 - dy), QPointF(16 + dx, 16 + dy))
        p.end()

        self.tray = QSystemTrayIcon(QIcon(pixmap), self)
        self.tray.setToolTip("Desktop Snow")

        menu = QMenu()
        clear_act = QAction("Clear Snow", self)
        clear_act.triggered.connect(self.clear_snow)
        menu.addAction(clear_act)
        menu.addSeparator()
        quit_act = QAction("Quit Desktop Snow", self)
        quit_act.triggered.connect(QApplication.quit)
        menu.addAction(quit_act)

        self.tray.setContextMenu(menu)
        self.tray.show()

    def clear_snow(self):
        self.ground_heights = [0.0] * len(self.ground_heights)
        for shelf in self.window_shelves.values():
            shelf.heights = [0.0] * len(shelf.heights)
        self.particles.clear()

    def spawn_flake(self, x, y, vx=None, vy=None, r=None):
        if len(self.particles) > 2500:
            return
        if r is None:
            r = random.uniform(1.1, 2.3)
        if vx is None:
            vx = random.uniform(-6.0, 6.0)
        if vy is None:
            vy = 22.0 + r * 12.0
        self.particles.append([x, y, vx, vy, r, random.uniform(0, math.tau)])

    def emit_particles(self, x, y, height_px, base_vx, base_vy):
        count = min(8, int(math.ceil(height_px / 1.8)))
        for _ in range(count):
            self.spawn_flake(
                x + random.uniform(0, BIN_SIZE),
                y + random.uniform(-min(height_px, 12.0), 0),
                base_vx + random.uniform(-20.0, 20.0),
                base_vy + random.uniform(-15.0, 15.0),
                random.uniform(1.0, 1.8)
            )

    def update_windows(self):
        wins = get_visible_windows_mac(self.own_pid)
        for shelf in self.window_shelves.values():
            shelf.seen = False

        for wid, l, t, r, b in wins:
            if wid in self.window_shelves:
                shelf = self.window_shelves[wid]
                shelf.seen = True
                dx = l - shelf.left
                dy = t - shelf.top
                if abs(dx) > 45 or abs(dy) > 25:
                    for i, h in enumerate(shelf.heights):
                        if h > 0.3:
                            self.emit_particles(shelf.left + i * BIN_SIZE, shelf.top - h * 0.5, h, dx * 2.5, dy * 1.5)
                    new_bins = max(1, (r - l) // BIN_SIZE)
                    shelf.heights = [0.0] * new_bins
                elif dx != 0 or dy != 0 or (r - l) != (shelf.right - shelf.left):
                    new_bins = max(1, (r - l) // BIN_SIZE)
                    new_h = [0.0] * new_bins
                    for i, h in enumerate(shelf.heights):
                        if h > 0.1:
                            shed = h * 0.18
                            if i < new_bins:
                                new_h[i] = h - shed
                            self.emit_particles(shelf.left + i * BIN_SIZE, shelf.top - h, shed, dx * 2.0, -15.0)
                    shelf.heights = new_h
                shelf.left, shelf.top, shelf.right, shelf.bottom = l, t, r, b
            else:
                self.window_shelves[wid] = WindowShelf(wid, l, t, r, b)

        to_remove = []
        for wid, shelf in self.window_shelves.items():
            if not shelf.seen:
                for i, h in enumerate(shelf.heights):
                    if h > 0.3:
                        self.emit_particles(shelf.left + i * BIN_SIZE, shelf.top - h * 0.5, h, 0.0, 12.0)
                to_remove.append(wid)
        for wid in to_remove:
            del self.window_shelves[wid]

    def relax_heights(self, heights):
        for i in range(len(heights) - 1):
            diff = heights[i] - heights[i + 1]
            if abs(diff) > MAX_SLOPE_DIFF:
                tr = (abs(diff) - MAX_SLOPE_DIFF) * 0.35
                if diff > 0:
                    heights[i] -= tr
                    heights[i + 1] += tr
                else:
                    heights[i] += tr
                    heights[i + 1] -= tr

    def step(self):
        now = time.perf_counter()
        dt = max(0.005, min(0.035, now - self.last_time))
        self.last_time = now
        self.frame_count += 1

        if self.frame_count % 5 == 0:
            self.update_windows()

        self.process_cat_bridge()

        # Мышь и вихри
        mpos = QCursor.pos()
        raw_vx = (mpos.x() - self.prev_mouse.x()) / dt
        raw_vy = (mpos.y() - self.prev_mouse.y()) / dt
        self.prev_mouse = mpos

        self.smooth_mvx += (raw_vx - self.smooth_mvx) * min(1.0, dt * 14.0)
        self.smooth_mvy += (raw_vy - self.smooth_mvy) * min(1.0, dt * 14.0)
        speed = math.hypot(self.smooth_mvx, self.smooth_mvy)

        if abs(self.smooth_mvx) > 25.0:
            self.vortex_dir = 1.0 if self.smooth_mvx > 0 else -1.0

        target_swirl = min(1500.0, speed * 0.85)
        self.vortex_strength += (target_swirl - self.vortex_strength) * min(1.0, dt * 6.0)

        if speed > 55.0:
            g_bin = int(mpos.x() // BIN_SIZE)
            for b in range(g_bin - 7, g_bin + 8):
                if 0 <= b < len(self.ground_heights):
                    h = self.ground_heights[b]
                    if h > 1.2 and abs(mpos.y() - (self.ground_y - h)) < 26.0:
                        kick = min(h, min(12.0, speed * 0.01))
                        self.ground_heights[b] -= kick
                        self.emit_particles(b * BIN_SIZE, self.ground_y - h, kick, self.smooth_mvx * 0.2, -45.0)

        # Новые снежинки
        if random.random() < 0.85:
            self.spawn_flake(random.uniform(0, self.screen_w), -5.0)

        wind = math.sin(now * 0.35) * 14.0
        vortex_active = self.vortex_strength > 20.0
        v_rad = min(200.0, 75.0 + self.vortex_strength * 0.1)
        v_rad_sq = v_rad * v_rad

        alive = []
        shelves_list = list(self.window_shelves.values())

        for p in self.particles:
            x, y, vx, vy, r, phase = p
            flutter = math.sin(now * 2.2 + phase) * 9.0
            vx += ((wind + flutter) - vx) * 2.0 * dt
            vy += ((24.0 + r * 12.0) - vy) * 2.5 * dt

            if vortex_active:
                dx = x - mpos.x()
                dy = y - mpos.y()
                dsq = dx * dx + dy * dy
                if 4.0 < dsq < v_rad_sq:
                    dist = math.sqrt(dsq)
                    inf = (1.0 - dist / v_rad) ** 2
                    nx, ny = dx / dist, dy / dist
                    tx, ty = -ny * self.vortex_dir, nx * self.vortex_dir
                    vx += (tx * self.vortex_strength * 2.2 + self.smooth_mvx * 0.4) * inf * dt
                    vy += (ty * self.vortex_strength * 2.2 - self.vortex_strength * 0.3) * inf * dt

            prev_y = y
            x = (x + vx * dt) % self.screen_w
            y += vy * dt

            landed = False
            if vy > 0:
                for shelf in shelves_list:
                    if shelf.left <= x < shelf.right:
                        b = int((x - shelf.left) // BIN_SIZE)
                        if 0 <= b < len(shelf.heights):
                            surf_y = shelf.top - shelf.heights[b]
                            if prev_y <= surf_y + 5.0 and y >= surf_y:
                                shelf.heights[b] += r * 0.45
                                landed = True
                                break

                if not landed:
                    gb = int(x // BIN_SIZE)
                    if 0 <= gb < len(self.ground_heights):
                        gsurf = self.ground_y - self.ground_heights[gb]
                        if y >= gsurf:
                            self.ground_heights[gb] += r * 0.5
                            if gb > 0:
                                self.ground_heights[gb - 1] += r * 0.15
                            if gb < len(self.ground_heights) - 1:
                                self.ground_heights[gb + 1] += r * 0.15
                            landed = True

            if not landed and y < self.screen_h + 20:
                alive.append([x, y, vx, vy, r, phase])

        self.particles = alive
        self.relax_heights(self.ground_heights)
        for shelf in shelves_list:
            self.relax_heights(shelf.heights)

        self.update()

    def draw_strip(self, painter, heights, start_x, base_y):
        seg_start = -1
        n = len(heights)
        for i in range(n + 1):
            has = i < n and heights[i] >= 0.8
            if has and seg_start == -1:
                seg_start = i
            elif not has and seg_start != -1:
                poly = QPolygonF()
                poly.append(QPointF(start_x + seg_start * BIN_SIZE, base_y + 1))
                for b in range(seg_start, i):
                    poly.append(QPointF(start_x + b * BIN_SIZE + BIN_SIZE * 0.5, base_y - heights[b]))
                poly.append(QPointF(start_x + i * BIN_SIZE, base_y + 1))
                painter.drawPolygon(poly)
                seg_start = -1

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QBrush(QColor(255, 255, 255, 242)))

        for x, y, _, _, r, _ in self.particles:
            painter.drawEllipse(QPointF(x, y), r, r)

        for shelf in self.window_shelves.values():
            self.draw_strip(painter, shelf.heights, shelf.left, shelf.top)

        self.draw_strip(painter, self.ground_heights, 0, self.ground_y)


if __name__ == '__main__':
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    snow = DesktopSnowMac()
    snow.show()
    sys.exit(app.exec())