import sys
import random
import math
import socket
from PyQt6.QtWidgets import QApplication, QWidget, QSystemTrayIcon, QMenu
from PyQt6.QtCore import Qt, QTimer, QPointF, QRectF
from PyQt6.QtGui import (
    QPainter, QColor, QPen, QBrush, QPolygonF, QFont,
    QPainterPath, QIcon, QPixmap, QAction, QCursor
)

try:
    import Quartz
    HAS_QUARTZ = True
except ImportError:
    HAS_QUARTZ = False


def get_visible_windows_mac(own_pid):
    windows = []
    if not HAS_QUARTZ:
        return windows

    options = Quartz.kCGWindowListOptionOnScreenOnly | Quartz.kCGWindowListExcludeDesktopElements
    win_list = Quartz.CGWindowListCopyWindowInfo(options, Quartz.kCGNullWindowID)
    if not win_list:
        return windows

    for win in win_list:
        pid = win.get('kCGWindowOwnerPID', 0)
        if pid == own_pid:
            continue
        layer = win.get('kCGWindowLayer', 0)
        if layer != 0:  # Только обычные окна приложений (игнорируем Dock, MenuBar, уведомления)
            continue
        bounds = win.get('kCGWindowBounds')
        if not bounds:
            continue

        x = float(bounds.get('X', 0))
        y = float(bounds.get('Y', 0))
        w = float(bounds.get('Width', 0))
        h = float(bounds.get('Height', 0))

        if w >= 120 and h >= 50 and y > 28:
            win_id = win.get('kCGWindowNumber', 0)
            windows.append((win_id, x, y, x + w, y + h))
    return windows


class Point:
    def __init__(self, x, y, drag=0.90):
        self.x = float(x)
        self.y = float(y)
        self.old_x = float(x)
        self.old_y = float(y)
        self.drag = drag
        self.pinned = False

    def update(self, gravity=0.45):
        if self.pinned:
            return
        vx = (self.x - self.old_x) * self.drag
        vy = (self.y - self.old_y) * self.drag
        self.old_x = self.x
        self.old_y = self.y
        self.x += vx
        self.y += vy + gravity


class Link:
    def __init__(self, p1, p2, length, stiffness=0.5):
        self.p1 = p1
        self.p2 = p2
        self.length = float(length)
        self.stiffness = stiffness

    def solve(self):
        dx = self.p2.x - self.p1.x
        dy = self.p2.y - self.p1.y
        dist = math.hypot(dx, dy)
        if dist == 0:
            return
        diff = (self.length - dist) / dist * self.stiffness
        ox = dx * diff
        oy = dy * diff

        if not self.p1.pinned and not self.p2.pinned:
            self.p1.x -= ox * 0.5
            self.p1.y -= oy * 0.5
            self.p2.x += ox * 0.5
            self.p2.y += oy * 0.5
        elif self.p1.pinned and not self.p2.pinned:
            self.p2.x += ox
            self.p2.y += oy
        elif not self.p1.pinned and self.p2.pinned:
            self.p1.x -= ox
            self.p1.y -= oy


class RagdollCatMac(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint |
            Qt.WindowType.WindowStaysOnTopHint |
            Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.resize(280, 280)

        self.own_pid = QApplication.applicationPid()
        screen = QApplication.primaryScreen().availableGeometry()
        self.screen_left = float(screen.left())
        self.screen_right = float(screen.right())
        self.dock_top_y = float(screen.bottom()) + 1.0

        self.cat_x = float(random.randint(int(self.screen_left + 150), int(self.screen_right - 150)))
        self.cat_y = self.dock_top_y

        self.current_window_id = None
        self.surface_left = self.screen_left + 35.0
        self.surface_right = self.screen_right - 35.0
        self.surface_y = self.dock_top_y
        self.windows_cache = []
        self.frame_counter = 0

        self.state = 'walk'
        self.direction = 1
        self.time_sec = 0.0
        self.show_meow = False

        self.snow_target_x = None
        self.snow_max_height = 0.0
        self.snow_cooldown = 0.0
        self.jump_vy = 0.0
        self.jump_height = 0.0
        self.setup_snow_bridge()

        self.dragging = False
        self.grabbed_point = None
        self.grab_offset_x = 0.0
        self.grab_offset_y = 0.0

        self.init_ragdoll(self.cat_x, self.cat_y)
        self.sync_window_to_cat()
        self.setup_tray()

        self.fps_timer = QTimer(self)
        self.fps_timer.timeout.connect(self.tick_60fps)
        self.fps_timer.start(16)

        self.action_timer = QTimer(self)
        self.action_timer.timeout.connect(self.change_action)
        self.action_timer.start(10000)

        self.meow_timer = QTimer(self)
        self.meow_timer.timeout.connect(self.try_meow)
        self.meow_timer.start(4000)

    def setup_snow_bridge(self):
        self.snow_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.snow_sock.setblocking(False)
        try:
            self.snow_sock.bind(('127.0.0.1', 50506))
        except Exception:
            pass
        self.snow_addr = ('127.0.0.1', 50505)

    def send_snow_jump(self, x, direction):
        try:
            msg = f"JUMP:{x:.1f}:{direction}".encode('utf-8')
            self.snow_sock.sendto(msg, self.snow_addr)
        except Exception:
            pass

    def poll_snow_bridge(self):
        try:
            while True:
                data, _ = self.snow_sock.recvfrom(256)
                parts = data.decode('utf-8').split(':')
                if len(parts) == 3 and parts[0] == 'DRIFT':
                    self.snow_target_x = float(parts[1])
                    self.snow_max_height = float(parts[2])
        except Exception:
            pass

    def init_ragdoll(self, cx, floor_y):
        paw_y = floor_y - 4.5
        self.head = Point(cx + 22, paw_y - 28, drag=0.91)
        self.chest = Point(cx + 8, paw_y - 22, drag=0.91)
        self.pelvis = Point(cx - 18, paw_y - 22, drag=0.91)

        self.paw_fl = Point(cx + 12, paw_y, drag=0.88)
        self.paw_fr = Point(cx + 4, paw_y, drag=0.88)
        self.paw_bl = Point(cx - 14, paw_y, drag=0.88)
        self.paw_br = Point(cx - 22, paw_y, drag=0.88)

        self.tail1 = Point(cx - 32, paw_y - 30, drag=0.89)
        self.tail2 = Point(cx - 44, paw_y - 40, drag=0.89)
        self.tail3 = Point(cx - 52, paw_y - 52, drag=0.89)

        self.ear_l = Point(cx + 18, paw_y - 44, drag=0.84)
        self.ear_r = Point(cx + 28, paw_y - 44, drag=0.84)

        self.points = [
            self.head, self.chest, self.pelvis,
            self.paw_fl, self.paw_fr, self.paw_bl, self.paw_br,
            self.tail1, self.tail2, self.tail3,
            self.ear_l, self.ear_r
        ]

        self.links = [
            Link(self.head, self.chest, 20, 0.7),
            Link(self.chest, self.pelvis, 28, 0.7),
            Link(self.head, self.pelvis, 46, 0.25),
            Link(self.chest, self.paw_fl, 22, 0.6),
            Link(self.chest, self.paw_fr, 22, 0.6),
            Link(self.pelvis, self.paw_bl, 22, 0.6),
            Link(self.pelvis, self.paw_br, 22, 0.6),
            Link(self.pelvis, self.tail1, 15, 0.65),
            Link(self.tail1, self.tail2, 15, 0.65),
            Link(self.tail2, self.tail3, 15, 0.65),
            Link(self.head, self.ear_l, 16, 0.5),
            Link(self.head, self.ear_r, 16, 0.5),
            Link(self.ear_l, self.ear_r, 12, 0.4),
        ]

    def sync_window_to_cat(self):
        win_x = int(self.chest.x - self.width() * 0.5)
        win_y = int(self.chest.y - self.height() * 0.5)
        if self.x() != win_x or self.y() != win_y:
            self.move(win_x, win_y)

    def setup_tray(self):
        pixmap = QPixmap(32, 32)
        pixmap.fill(Qt.GlobalColor.transparent)
        p = QPainter(pixmap)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setBrush(QBrush(QColor(25, 25, 30)))
        p.setPen(Qt.PenStyle.NoPen)
        p.drawEllipse(4, 8, 24, 20)
        p.drawPolygon(QPolygonF([QPointF(6, 14), QPointF(8, 2), QPointF(15, 10)]))
        p.drawPolygon(QPolygonF([QPointF(26, 14), QPointF(24, 2), QPointF(17, 10)]))
        p.setBrush(QBrush(QColor(255, 215, 0)))
        p.drawEllipse(10, 15, 4, 5)
        p.drawEllipse(18, 15, 4, 5)
        p.end()

        self.tray_icon = QSystemTrayIcon(QIcon(pixmap), self)
        self.tray_icon.setToolTip("Black Cat")

        menu = QMenu()
        reset_action = QAction("Reset to Dock", self)
        reset_action.triggered.connect(self.reset_position)
        menu.addAction(reset_action)
        menu.addSeparator()
        quit_action = QAction("Quit Black Cat", self)
        quit_action.triggered.connect(QApplication.quit)
        menu.addAction(quit_action)

        self.tray_icon.setContextMenu(menu)
        self.tray_icon.show()

    def reset_position(self):
        self.state = 'walk'
        self.jump_height = 0.0
        self.current_window_id = None
        self.surface_left = self.screen_left + 35.0
        self.surface_right = self.screen_right - 35.0
        self.surface_y = self.dock_top_y
        self.cat_x = (self.screen_left + self.screen_right) * 0.5
        self.cat_y = self.dock_top_y
        self.init_ragdoll(self.cat_x, self.cat_y)
        self.sync_window_to_cat()

    def change_action(self):
        if self.dragging or self.state in ('chase_snow', 'jump_snow'):
            return
        if (self.current_window_id is None and self.snow_cooldown <= 0
                and self.snow_max_height > 20.0 and self.snow_target_x is not None):
            self.state = 'chase_snow'
            return

        self.state = random.choice(['walk', 'lie', 'sit_wash'])
        if self.state == 'walk':
            self.direction = random.choice([-1, 1])

    def try_meow(self):
        if not self.show_meow and random.random() < 0.35:
            self.show_meow = True
            QTimer.singleShot(2500, self.hide_meow)

    def hide_meow(self):
        self.show_meow = False

    def pull_towards(self, pt, target_x, target_y, strength):
        if pt.pinned:
            return
        pt.x += (target_x - pt.x) * strength
        pt.y += (target_y - pt.y) * strength

    def update_surface_tracking(self):
        self.frame_counter += 1
        if self.frame_counter % 4 == 0:
            self.windows_cache = get_visible_windows_mac(self.own_pid)

        if self.current_window_id is not None:
            found = next((w for w in self.windows_cache if w[0] == self.current_window_id), None)
            if found is None:
                self.current_window_id = None
                self.surface_left = self.screen_left + 35.0
                self.surface_right = self.screen_right - 35.0
                self.surface_y = self.dock_top_y
            else:
                _, l, t, r, _ = found
                dy = float(t) - self.surface_y
                if dy > 25.0:
                    self.current_window_id = None
                    self.surface_left = self.screen_left + 35.0
                    self.surface_right = self.screen_right - 35.0
                    self.surface_y = self.dock_top_y
                else:
                    self.surface_left = float(l) + 32.0
                    self.surface_right = float(r) - 32.0
                    if self.surface_right <= self.surface_left:
                        self.surface_left = float(l) + 10.0
                        self.surface_right = float(r) - 10.0
                    self.surface_y = float(t)
                    if abs(dy) > 0.1 and self.cat_y >= self.surface_y - 10.0:
                        self.cat_y = self.surface_y
                        for pt in self.points:
                            pt.y += dy
                            pt.old_y += dy

    def find_landing_surface(self, x, y):
        for wid, l, t, r, b in self.windows_cache:
            if (l + 15) <= x <= (r - 15) and (t - 25) <= y <= (t + 35):
                return wid, float(l) + 32.0, float(r) - 32.0, float(t)
        return None, self.screen_left + 35.0, self.screen_right - 35.0, self.dock_top_y

    def tick_60fps(self):
        dt = 0.016
        self.time_sec += dt
        if self.snow_cooldown > 0:
            self.snow_cooldown -= dt

        self.poll_snow_bridge()
        self.update_surface_tracking()

        if (not self.dragging and self.current_window_id is None
                and self.state not in ('chase_snow', 'jump_snow')
                and self.snow_cooldown <= 0 and self.snow_max_height > 34.0 and self.snow_target_x is not None):
            self.state = 'chase_snow'

        d = self.direction

        if not self.dragging:
            if self.cat_y < self.surface_y:
                self.cat_y = min(self.surface_y, self.cat_y + 8.5)
                self.cat_x = self.chest.x

            cx = self.cat_x
            paw_floor = self.cat_y - 4.5

            if self.state == 'walk':
                self.cat_x += 1.4 * d
                if self.cat_x <= self.surface_left:
                    self.cat_x = self.surface_left
                    self.direction = 1
                elif self.cat_x >= self.surface_right:
                    self.cat_x = self.surface_right
                    self.direction = -1
                d = self.direction

                self.pull_towards(self.chest, cx + 10 * d, paw_floor - 22, 0.28)
                self.pull_towards(self.pelvis, cx - 18 * d, paw_floor - 22, 0.28)
                self.pull_towards(self.head, cx + 24 * d, paw_floor - 31, 0.28)

                t = self.time_sec * 9.5
                for paw, base_x, phase in (
                    (self.paw_fl, 12.0, 0.0),
                    (self.paw_fr, 10.0, math.pi),
                    (self.paw_bl, -16.0, math.pi),
                    (self.paw_br, -18.0, 0.0),
                ):
                    ang = t + phase
                    step_forward = math.cos(ang) * 8.5 * d
                    step_lift = max(0.0, math.sin(ang) * 7.5)
                    self.pull_towards(paw, cx + base_x * d + step_forward, paw_floor - step_lift, 0.38)

                wag = math.sin(self.time_sec * 5.0) * 7.0
                self.pull_towards(self.tail1, cx - 30 * d, paw_floor - 30, 0.14)
                self.pull_towards(self.tail2, cx - 42 * d, paw_floor - 42 + wag * 0.5, 0.10)
                self.pull_towards(self.tail3, cx - 48 * d, paw_floor - 54 + wag, 0.07)

            elif self.state == 'chase_snow':
                if self.snow_target_x is None or self.current_window_id is not None:
                    self.state = 'walk'
                else:
                    diff_x = self.snow_target_x - self.cat_x
                    if abs(diff_x) < 55.0:
                        self.state = 'jump_snow'
                        self.jump_vy = 11.5
                        self.jump_height = 1.0
                        self.show_meow = True
                        QTimer.singleShot(2000, self.hide_meow)
                    else:
                        self.direction = 1 if diff_x > 0 else -1
                        d = self.direction
                        self.cat_x += 4.3 * d
                        self.cat_x = max(self.surface_left, min(self.surface_right, self.cat_x))

                        t = self.time_sec * 18.0
                        bob = abs(math.sin(t)) * 4.5
                        self.pull_towards(self.chest, cx + 12 * d, paw_floor - 21 - bob, 0.34)
                        self.pull_towards(self.pelvis, cx - 18 * d, paw_floor - 21 - bob, 0.34)
                        self.pull_towards(self.head, cx + 26 * d, paw_floor - 29 - bob, 0.34)

                        for paw, base_x, phase in (
                            (self.paw_fl, 14.0, 0.0),
                            (self.paw_fr, 10.0, -0.35),
                            (self.paw_bl, -16.0, math.pi),
                            (self.paw_br, -20.0, math.pi - 0.35),
                        ):
                            ang = t + phase
                            step_forward = math.cos(ang) * 11.5 * d
                            step_lift = max(0.0, math.sin(ang) * 10.5)
                            self.pull_towards(paw, cx + base_x * d + step_forward, paw_floor - step_lift, 0.42)

                        self.pull_towards(self.tail1, cx - 28 * d, paw_floor - 34, 0.16)
                        self.pull_towards(self.tail2, cx - 35 * d, paw_floor - 50, 0.12)
                        self.pull_towards(self.tail3, cx - 38 * d, paw_floor - 64, 0.10)

            elif self.state == 'jump_snow':
                self.cat_x += 3.6 * d
                self.cat_x = max(self.surface_left, min(self.surface_right, self.cat_x))

                self.jump_height += self.jump_vy
                self.jump_vy -= 0.75

                if self.frame_counter % 4 == 0:
                    self.send_snow_jump(self.cat_x, d)

                if self.jump_height <= 0.0:
                    self.jump_height = 0.0
                    self.send_snow_jump(self.cat_x, d)
                    self.send_snow_jump(self.cat_x + 15 * d, d)
                    self.snow_cooldown = 7.0
                    self.state = 'walk'
                else:
                    jh = self.jump_height
                    self.pull_towards(self.chest, cx + 12 * d, paw_floor - 22 - jh, 0.35)
                    self.pull_towards(self.pelvis, cx - 18 * d, paw_floor - 21 - jh * 0.9, 0.35)
                    self.pull_towards(self.head, cx + 26 * d, paw_floor - 32 - jh * 1.05, 0.35)

                    self.pull_towards(self.paw_fl, cx + 28 * d, paw_floor - 10 - jh, 0.35)
                    self.pull_towards(self.paw_fr, cx + 24 * d, paw_floor - 6 - jh, 0.35)
                    self.pull_towards(self.paw_bl, cx - 32 * d, paw_floor - 8 - jh, 0.35)
                    self.pull_towards(self.paw_br, cx - 28 * d, paw_floor - 4 - jh, 0.35)

            elif self.state == 'lie':
                breath = math.sin(self.time_sec * 2.5) * 1.4
                self.pull_towards(self.pelvis, cx - 16 * d, paw_floor - 8, 0.24)
                self.pull_towards(self.chest, cx + 10 * d, paw_floor - 8 - breath, 0.24)
                self.pull_towards(self.head, cx + 25 * d, paw_floor - 12, 0.24)
                self.pull_towards(self.paw_fl, cx + 33 * d, paw_floor, 0.26)
                self.pull_towards(self.paw_fr, cx + 27 * d, paw_floor, 0.26)
                self.pull_towards(self.paw_bl, cx - 25 * d, paw_floor, 0.22)
                self.pull_towards(self.paw_br, cx - 31 * d, paw_floor, 0.22)

                wag = math.sin(self.time_sec * 2.0) * 6.0
                self.pull_towards(self.tail2, cx - 42 * d, paw_floor - 5 + wag, 0.06)
                self.pull_towards(self.tail3, cx - 54 * d, paw_floor - 3 - wag, 0.05)

            elif self.state == 'sit_wash':
                self.pull_towards(self.pelvis, cx - 6 * d, paw_floor - 11, 0.28)
                self.pull_towards(self.chest, cx + 2 * d, paw_floor - 31, 0.28)
                nod = math.sin(self.time_sec * 6.0) * 2.5
                self.pull_towards(self.head, cx + 12 * d, paw_floor - 47 + nod, 0.28)

                self.pull_towards(self.paw_fl, cx + 10 * d, paw_floor, 0.32)
                wash_y = paw_floor - 43 + math.sin(self.time_sec * 7.0) * 7.0
                self.pull_towards(self.paw_fr, cx + 20 * d, wash_y, 0.36)

                self.pull_towards(self.paw_bl, cx - 2 * d, paw_floor, 0.28)
                self.pull_towards(self.paw_br, cx - 10 * d, paw_floor, 0.28)
                self.pull_towards(self.tail2, cx - 22 * d, paw_floor - 2, 0.14)
                self.pull_towards(self.tail3, cx - 12 * d, paw_floor, 0.14)

            self.pull_towards(self.ear_l, self.head.x - 6 * d, self.head.y - 16, 0.22)
            self.pull_towards(self.ear_r, self.head.x + 6 * d, self.head.y - 16, 0.22)
        else:
            if self.grabbed_point:
                g_pos = QCursor.pos()
                self.grabbed_point.x = float(g_pos.x()) + self.grab_offset_x
                self.grabbed_point.y = float(g_pos.y()) + self.grab_offset_y
                self.cat_x = self.chest.x
                self.cat_y = self.chest.y + 22.0

        grav = 0.65 if self.dragging else 0.25
        for pt in self.points:
            pt.update(gravity=grav)

        paw_floor_limit = self.cat_y - 4.5
        for _ in range(6):
            for link in self.links:
                link.solve()
            if not self.dragging:
                for pt in self.points:
                    if pt.y > paw_floor_limit:
                        pt.y = paw_floor_limit
                        pt.old_x = pt.x - (pt.x - pt.old_x) * 0.7

        self.sync_window_to_cat()
        self.update()

    def draw_smooth_limb(self, painter, p1, p2, w1, w2, color, ox, oy):
        x1, y1 = p1.x - ox, p1.y - oy
        x2, y2 = p2.x - ox, p2.y - oy
        dx = x2 - x1
        dy = y2 - y1
        dist = math.hypot(dx, dy)
        if dist < 0.001:
            painter.setBrush(QBrush(color))
            painter.drawEllipse(QPointF(x1, y1), w1, w1)
            return

        nx = -dy / dist
        ny = dx / dist

        path = QPainterPath()
        path.moveTo(x1 + nx * w1, y1 + ny * w1)
        path.lineTo(x2 + nx * w2, y2 + ny * w2)
        path.lineTo(x2 - nx * w2, y2 - ny * w2)
        path.lineTo(x1 - nx * w1, y1 - ny * w1)
        path.closeSubpath()

        painter.setBrush(QBrush(color))
        painter.setPen(Qt.PenStyle.NoPen)
        painter.drawPath(path)
        painter.drawEllipse(QPointF(x1, y1), w1, w1)
        painter.drawEllipse(QPointF(x2, y2), w2, w2)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        ox = float(self.x())
        oy = float(self.y())

        fur_dark = QColor(24, 24, 28)
        fur_back = QColor(16, 16, 20)
        eye_color = QColor(255, 215, 0)
        nose_pink = QColor(255, 145, 165)

        tail_path = QPainterPath()
        tail_path.moveTo(self.pelvis.x - ox, self.pelvis.y - oy)
        tail_path.cubicTo(
            self.tail1.x - ox, self.tail1.y - oy,
            self.tail2.x - ox, self.tail2.y - oy,
            self.tail3.x - ox, self.tail3.y - oy
        )
        pen_tail = QPen(fur_dark, 6.5, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
        painter.setPen(pen_tail)
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(tail_path)

        self.draw_smooth_limb(painter, self.chest, self.paw_fr, 5.5, 4.2, fur_back, ox, oy)
        self.draw_smooth_limb(painter, self.pelvis, self.paw_br, 6.0, 4.2, fur_back, ox, oy)

        self.draw_smooth_limb(painter, self.pelvis, self.chest, 13.5, 13.0, fur_dark, ox, oy)
        self.draw_smooth_limb(painter, self.chest, self.head, 12.0, 10.5, fur_dark, ox, oy)

        self.draw_smooth_limb(painter, self.chest, self.paw_fl, 5.5, 4.5, fur_dark, ox, oy)
        self.draw_smooth_limb(painter, self.pelvis, self.paw_bl, 6.5, 4.5, fur_dark, ox, oy)

        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QBrush(fur_dark))

        hx = self.head.x - ox
        hy = self.head.y - oy
        ear_l_poly = QPolygonF([
            QPointF(hx - 11, hy - 4),
            QPointF(self.ear_l.x - ox, self.ear_l.y - oy),
            QPointF(hx - 1, hy - 10)
        ])
        ear_r_poly = QPolygonF([
            QPointF(hx + 1, hy - 10),
            QPointF(self.ear_r.x - ox, self.ear_r.y - oy),
            QPointF(hx + 11, hy - 4)
        ])
        painter.drawPolygon(ear_l_poly)
        painter.drawPolygon(ear_r_poly)

        painter.drawEllipse(QPointF(hx, hy), 14.5, 13.5)

        face_shift = 5.0 * self.direction
        if self.state == 'lie' and not self.dragging:
            painter.setPen(QPen(eye_color, 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            painter.drawArc(int(hx + face_shift - 5), int(hy - 2), 6, 4, 0, -180 * 16)
            painter.drawArc(int(hx + face_shift + 2), int(hy - 2), 6, 4, 0, -180 * 16)
        else:
            eye_r = 3.2 if (self.dragging or self.state == 'jump_snow') else 2.6
            painter.setBrush(QBrush(eye_color))
            painter.setPen(Qt.PenStyle.NoPen)
            painter.drawEllipse(QPointF(hx + face_shift - 3.5, hy - 1.5), eye_r, eye_r + 0.5)
            painter.drawEllipse(QPointF(hx + face_shift + 4.5, hy - 1.5), eye_r, eye_r + 0.5)

            painter.setBrush(QBrush(QColor(15, 15, 15)))
            painter.drawEllipse(QPointF(hx + face_shift - 3.5, hy - 1.5), 1.1, 2.0)
            painter.drawEllipse(QPointF(hx + face_shift + 4.5, hy - 1.5), 1.1, 2.0)

        painter.setBrush(QBrush(nose_pink))
        painter.drawEllipse(QPointF(hx + face_shift + 0.5, hy + 2.5), 1.6, 1.1)

        if self.show_meow:
            bubble = QRectF(hx - 26, hy - 48, 52, 22)
            painter.setBrush(QBrush(QColor(255, 255, 255, 240)))
            painter.setPen(QPen(QColor(40, 40, 45), 1.5))
            painter.drawRoundedRect(bubble, 9, 9)
            painter.setPen(QColor(20, 20, 20))
            painter.setFont(QFont("Helvetica", 9, QFont.Weight.Bold))
            painter.drawText(bubble, Qt.AlignmentFlag.AlignCenter, "мяу")

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton:
            g_pos = event.globalPosition()
            gx, gy = float(g_pos.x()), float(g_pos.y())
            closest = min(self.points, key=lambda p: math.hypot(p.x - gx, p.y - gy))
            if math.hypot(closest.x - gx, closest.y - gy) < 48.0:
                self.dragging = True
                self.grabbed_point = closest
                self.grabbed_point.pinned = True
                self.grab_offset_x = closest.x - gx
                self.grab_offset_y = closest.y - gy

                self.show_meow = True
                QTimer.singleShot(2000, self.hide_meow)

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton and self.dragging:
            if self.grabbed_point:
                self.grabbed_point.pinned = False
                self.grabbed_point = None
            self.dragging = False

            self.windows_cache = get_visible_windows_mac(self.own_pid)
            foot_y = max(self.paw_fl.y, self.paw_bl.y, self.pelvis.y + 15.0)
            wid, s_left, s_right, s_y = self.find_landing_surface(self.chest.x, foot_y)

            self.current_window_id = wid
            self.surface_left = s_left
            self.surface_right = s_right
            self.surface_y = s_y
            self.cat_x = max(s_left, min(s_right, self.chest.x))
            self.cat_y = max(foot_y, s_y) if abs(foot_y - s_y) < 15.0 else foot_y

            if self.current_window_id is None and self.cat_y > self.dock_top_y - 45.0:
                self.send_snow_jump(self.cat_x, self.direction)


if __name__ == '__main__':
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    cat = RagdollCatMac()
    cat.show()
    sys.exit(app.exec())