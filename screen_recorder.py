"""
Windows HD Screen Recorder (Python)
====================================
A high-performance, studio-quality screen recorder for Windows.
Features:
- Crystal clear HD / 2K / 4K capture with H.264 visually lossless encoding (CRF 18)
- Ultra-low latency screen capture with multi-threaded queue pipeline (up to 60+ FPS)
- Windows hardware mouse cursor overlay with optional click highlight rings
- Synchronized multi-channel audio recording (Microphone / System audio)
- Multi-monitor support (Primary, Secondary, Full Virtual Desktop, Custom region)
- Modern Dark-Themed GUI with live recording timer & status
- Global hotkeys (F9 to Start/Pause/Resume, F10 to Stop & Save)
- Full CLI mode for headless or scripted recordings
"""

import os
import sys
import time
import math
import queue
import ctypes
import argparse
import tempfile
import threading
import subprocess
from datetime import datetime
from typing import Optional, Tuple, Dict, Any, List

# Safe UTF-8 console encoding on Windows
if sys.stdout and hasattr(sys.stdout, 'reconfigure'):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
if sys.stderr and hasattr(sys.stderr, 'reconfigure'):
    try:
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

# Core third-party dependencies
import cv2
import mss
import numpy as np
import imageio_ffmpeg

# Audio recording (optional / fallback handled gracefully)
try:
    import sounddevice as sd
    import soundfile as sf
    AUDIO_AVAILABLE = True
except ImportError:
    AUDIO_AVAILABLE = False

# Global hotkeys (optional / fallback handled gracefully)
try:
    from pynput import keyboard as pynput_keyboard
    HOTKEYS_AVAILABLE = True
except ImportError:
    HOTKEYS_AVAILABLE = False


# ==============================================================================
# Windows Win32 API Helpers (Desktop Attachment & Hardware Cursor)
# ==============================================================================

class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

class CURSORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint),
        ("flags", ctypes.c_uint),
        ("hCursor", ctypes.c_void_p),
        ("ptScreenPos", POINT)
    ]

def attach_to_active_desktop():
    """Ensure the current thread is attached to the active user desktop."""
    try:
        user32 = ctypes.windll.user32
        hdesk = user32.OpenInputDesktop(0, False, 0x01FF)
        if hdesk:
            user32.SetThreadDesktop(hdesk)
    except Exception:
        pass


class CursorOverlay:
    """Detects and renders the Windows mouse cursor and click effects onto frames."""

    def __init__(self, highlight_clicks: bool = True):
        self.user32 = ctypes.windll.user32
        self.highlight_clicks = highlight_clicks
        self.cursor_info = CURSORINFO()
        self.cursor_info.cbSize = ctypes.sizeof(CURSORINFO)
        
        # Click ripple animation tracker: list of [x, y, radius, max_radius, alpha]
        self._active_ripples: List[List[float]] = []

    def get_cursor_state(self) -> Tuple[bool, int, int, bool]:
        """
        Returns:
            (is_visible, x, y, is_left_clicked)
        """
        try:
            res = self.user32.GetCursorInfo(ctypes.byref(self.cursor_info))
            if res and (self.cursor_info.flags & 0x00000001): # CURSOR_SHOWING
                cx = self.cursor_info.ptScreenPos.x
                cy = self.cursor_info.ptScreenPos.y
                # Check left mouse button state (0x01 = VK_LBUTTON, highest bit set means pressed)
                is_clicked = bool(self.user32.GetAsyncKeyState(0x01) & 0x8000)
                return True, cx, cy, is_clicked
        except Exception:
            pass
        return False, 0, 0, False

    def draw_on_frame(self, frame: np.ndarray, monitor_offset_x: int, monitor_offset_y: int):
        """Draws the mouse cursor and active click ripples directly onto a BGR frame."""
        visible, screen_x, screen_y, clicked = self.get_cursor_state()
        
        # Calculate local frame coordinates
        lx = screen_x - monitor_offset_x
        ly = screen_y - monitor_offset_y
        h, w = frame.shape[:2]

        # Trigger new click ripple if clicked inside frame
        if clicked and self.highlight_clicks and 0 <= lx < w and 0 <= ly < h:
            # Prevent spamming ripples at same point
            if not any(abs(r[0] - lx) < 6 and abs(r[1] - ly) < 6 for r in self._active_ripples):
                self._active_ripples.append([float(lx), float(ly), 4.0, 26.0, 1.0])

        # Draw and update click ripples
        if self._active_ripples:
            remaining_ripples = []
            for ripple in self._active_ripples:
                rx, ry, rad, max_rad, alpha = ripple
                if rad < max_rad and alpha > 0.05:
                    color = (0, int(200 * alpha), int(255 * alpha)) # Golden yellow/cyan glow
                    cv2.circle(frame, (int(rx), int(ry)), int(rad), color, thickness=2, lineType=cv2.LINE_AA)
                    ripple[2] += 2.2 # expand radius
                    ripple[4] -= 0.08 # fade out
                    remaining_ripples.append(ripple)
            self._active_ripples = remaining_ripples

        # Draw mouse pointer arrow
        if visible and 0 <= lx < w and 0 <= ly < h:
            # Modern anti-aliased cursor arrow polygon
            pts = np.array([
                [lx, ly],
                [lx, ly + 18],
                [lx + 4, ly + 14],
                [lx + 8, ly + 22],
                [lx + 11, ly + 20],
                [lx + 7, ly + 13],
                [lx + 13, ly + 13]
            ], dtype=np.int32)
            
            # Subtle drop shadow
            shadow_pts = pts + 1
            cv2.fillPoly(frame, [shadow_pts], (40, 40, 40), lineType=cv2.LINE_AA)
            # White fill with crisp black outline
            cv2.fillPoly(frame, [pts], (255, 255, 255), lineType=cv2.LINE_AA)
            cv2.polylines(frame, [pts], isClosed=True, color=(10, 10, 10), thickness=1, lineType=cv2.LINE_AA)


# ==============================================================================
# Audio Recording Engine
# ==============================================================================

class AudioRecorder:
    """Threaded audio recorder capturing microphone or system loopback using sounddevice."""

    def __init__(self, device_index: Optional[int] = None, sample_rate: int = 44100, channels: int = 2):
        self.device_index = device_index
        self.sample_rate = sample_rate
        self.channels = channels
        self.is_recording = False
        self.is_paused = False
        self._audio_chunks: List[np.ndarray] = []
        self._stream: Optional[sd.InputStream] = None
        self.temp_audio_file: Optional[str] = None

    def _audio_callback(self, indata, frames, time_info, status):
        if self.is_recording and not self.is_paused:
            self._audio_chunks.append(indata.copy())

    def start(self) -> bool:
        if not AUDIO_AVAILABLE:
            return False
        try:
            self._audio_chunks = []
            self.is_recording = True
            self.is_paused = False
            
            # Find working channels for device
            dev_info = sd.query_devices(self.device_index) if self.device_index is not None else sd.query_devices(kind='input')
            max_in = dev_info.get('max_input_channels', 2)
            channels_to_use = min(self.channels, max_in if max_in > 0 else 1)

            self._stream = sd.InputStream(
                device=self.device_index,
                channels=channels_to_use,
                samplerate=self.sample_rate,
                dtype='float32',
                callback=self._audio_callback
            )
            self._stream.start()
            return True
        except Exception as e:
            print(f"[Audio] Warning: Could not initialize audio device ({e}). Recording video only.")
            self.is_recording = False
            return False

    def pause(self):
        self.is_paused = True

    def resume(self):
        self.is_paused = False

    def stop(self) -> Optional[str]:
        self.is_recording = False
        if self._stream:
            try:
                self._stream.stop()
                self._stream.close()
            except Exception:
                pass
            self._stream = None

        if self._audio_chunks:
            try:
                full_audio = np.concatenate(self._audio_chunks, axis=0)
                temp_fd, self.temp_audio_file = tempfile.mkstemp(suffix=".wav")
                os.close(temp_fd)
                sf.write(self.temp_audio_file, full_audio, self.sample_rate)
                return self.temp_audio_file
            except Exception as e:
                print(f"[Audio] Error saving audio track: {e}")
        return None

    @staticmethod
    def get_input_devices() -> List[Dict[str, Any]]:
        """Returns list of usable audio recording devices."""
        if not AUDIO_AVAILABLE:
            return []
        devices = []
        try:
            for idx, dev in enumerate(sd.query_devices()):
                if dev.get('max_input_channels', 0) > 0:
                    name = dev.get('name', f'Device {idx}')
                    # Clean up long driver strings
                    if '\r\n;' in name:
                        name = name.split('\r\n;')[-1].replace(')', '')
                    devices.append({'index': idx, 'name': f"[{idx}] {name}", 'channels': dev['max_input_channels']})
        except Exception:
            pass
        return devices


# ==============================================================================
# Screen Recorder Core Engine
# ==============================================================================

class ScreenRecorder:
    """
    High-performance multi-threaded screen recorder.
    Uses mss for fast capture, high-precision frame pacing, and imageio-ffmpeg for H.264 visually lossless encoding.
    """

    def __init__(
        self,
        output_path: str = "recording.mp4",
        fps: int = 30,
        crf: int = 18,
        monitor_index: int = 1,
        custom_region: Optional[Dict[str, int]] = None,
        draw_cursor: bool = True,
        highlight_clicks: bool = True,
        record_audio: bool = False,
        audio_device_index: Optional[int] = None,
    ):
        self.output_path = os.path.abspath(output_path)
        self.fps = max(10, min(120, int(fps)))
        self.crf = crf
        self.monitor_index = monitor_index
        self.custom_region = custom_region
        self.draw_cursor = draw_cursor
        self.highlight_clicks = highlight_clicks
        self.record_audio = record_audio
        self.audio_device_index = audio_device_index

        self.is_recording = False
        self.is_paused = False
        self._stop_event = threading.Event()
        
        self._frame_queue: queue.Queue = queue.Queue(maxsize=120)
        self._capture_thread: Optional[threading.Thread] = None
        self._writer_thread: Optional[threading.Thread] = None
        
        self.audio_recorder: Optional[AudioRecorder] = None
        self.cursor_overlay = CursorOverlay(highlight_clicks=highlight_clicks) if draw_cursor else None
        
        self.width = 0
        self.height = 0
        self.frames_recorded = 0
        self.start_time: float = 0.0
        self.total_paused_duration: float = 0.0
        self._pause_start_time: float = 0.0

    @staticmethod
    def get_monitors() -> List[Dict[str, Any]]:
        """Returns list of available monitors and virtual screen."""
        attach_to_active_desktop()
        try:
            with mss.MSS() as sct:
                monitors = []
                for idx, m in enumerate(sct.monitors):
                    label = "All Monitors (Virtual Desktop)" if idx == 0 else f"Monitor {idx} ({m['width']}x{m['height']})"
                    monitors.append({
                        'index': idx,
                        'name': label,
                        'left': m['left'],
                        'top': m['top'],
                        'width': m['width'],
                        'height': m['height']
                    })
                return monitors
        except Exception:
            return [{'index': 1, 'name': 'Primary Display', 'left': 0, 'top': 0, 'width': 1920, 'height': 1080}]

    def _determine_capture_bounds(self, sct: mss.MSS) -> Dict[str, int]:
        """Calculates the bounding box for the selected monitor or region."""
        if self.custom_region:
            return self.custom_region
        
        monitors = sct.monitors
        if 0 <= self.monitor_index < len(monitors):
            m = monitors[self.monitor_index]
        else:
            m = monitors[1] if len(monitors) > 1 else monitors[0]
            
        # Ensure dimensions are even numbers (required by H.264/yuv420p encoders)
        w = m['width'] - (m['width'] % 2)
        h = m['height'] - (m['height'] % 2)
        return {
            'left': m['left'],
            'top': m['top'],
            'width': w,
            'height': h
        }

    def start(self) -> bool:
        """Starts recording asynchronously."""
        if self.is_recording:
            return False

        attach_to_active_desktop()
        self.is_recording = True
        self.is_paused = False
        self._stop_event.clear()
        self.frames_recorded = 0
        self.total_paused_duration = 0.0
        self._pause_start_time = 0.0
        self.start_time = time.perf_counter()

        # Initialize audio recording if requested
        if self.record_audio and AUDIO_AVAILABLE:
            self.audio_recorder = AudioRecorder(device_index=self.audio_device_index)
            self.audio_recorder.start()

        # Start capture and writer threads
        self._capture_thread = threading.Thread(target=self._capture_worker, daemon=True)
        self._writer_thread = threading.Thread(target=self._writer_worker, daemon=True)

        self._capture_thread.start()
        self._writer_thread.start()
        return True

    def pause(self):
        """Pauses recording."""
        if self.is_recording and not self.is_paused:
            self.is_paused = True
            self._pause_start_time = time.perf_counter()
            if self.audio_recorder:
                self.audio_recorder.pause()

    def resume(self):
        """Resumes recording."""
        if self.is_recording and self.is_paused:
            self.is_paused = False
            if self._pause_start_time > 0:
                self.total_paused_duration += (time.perf_counter() - self._pause_start_time)
                self._pause_start_time = 0.0
            if self.audio_recorder:
                self.audio_recorder.resume()

    def stop(self) -> str:
        """Stops recording, closes streams, merges audio/video, and returns final file path."""
        if not self.is_recording:
            return self.output_path

        self.is_recording = False
        self.is_paused = False
        self._stop_event.set()

        # Stop audio
        temp_audio_file = None
        if self.audio_recorder:
            temp_audio_file = self.audio_recorder.stop()

        # Wait for capture & writer workers to finish
        if self._capture_thread and self._capture_thread.is_alive():
            self._capture_thread.join(timeout=3.0)
        if self._writer_thread and self._writer_thread.is_alive():
            self._writer_thread.join(timeout=5.0)

        # Merge Audio & Video if audio was captured
        if temp_audio_file and os.path.exists(temp_audio_file) and os.path.exists(self._temp_video_path):
            self._merge_video_audio(self._temp_video_path, temp_audio_file, self.output_path)
            try:
                if os.path.exists(temp_audio_file):
                    os.remove(temp_audio_file)
                if os.path.exists(self._temp_video_path) and self._temp_video_path != self.output_path:
                    os.remove(self._temp_video_path)
            except Exception:
                pass
        elif hasattr(self, '_temp_video_path') and os.path.exists(self._temp_video_path) and self._temp_video_path != self.output_path:
            # If no audio, move temp video to final output
            if os.path.exists(self.output_path):
                try:
                    os.remove(self.output_path)
                except Exception:
                    pass
            os.replace(self._temp_video_path, self.output_path)

        return self.output_path

    def get_elapsed_seconds(self) -> float:
        """Returns the actual recorded duration in seconds."""
        if not self.is_recording:
            return 0.0
        now = time.perf_counter()
        current_pause = (now - self._pause_start_time) if self.is_paused else 0.0
        return max(0.0, (now - self.start_time) - self.total_paused_duration - current_pause)

    def _capture_worker(self):
        """Dedicated high-speed thread capturing frames from desktop."""
        attach_to_active_desktop()
        with mss.MSS() as sct:
            bounds = self._determine_capture_bounds(sct)
            self.width = bounds['width']
            self.height = bounds['height']
            left = bounds['left']
            top = bounds['top']

            frame_interval = 1.0 / self.fps
            next_frame_time = time.perf_counter()

            while not self._stop_event.is_set():
                now = time.perf_counter()
                
                if self.is_paused:
                    time.sleep(0.05)
                    next_frame_time = time.perf_counter() + frame_interval
                    continue

                if now >= next_frame_time:
                    try:
                        # Grab screen buffer via MSS (Lightning fast GDI / DXGI copy)
                        raw = sct.grab(bounds)
                        # Extract BGR frame
                        frame = np.frombuffer(raw.raw, dtype=np.uint8).reshape((self.height, self.width, 4))[:, :, :3].copy()
                        
                        # Draw mouse cursor & click highlight
                        if self.cursor_overlay:
                            self.cursor_overlay.draw_on_frame(frame, left, top)

                        # Enqueue for writer thread (non-blocking with drop protection)
                        try:
                            self._frame_queue.put_nowait(frame)
                            self.frames_recorded += 1
                        except queue.Full:
                            pass # Buffer full, discard oldest if needed

                    except Exception as e:
                        time.sleep(0.01)

                    # High-precision frame timing
                    next_frame_time += frame_interval
                    if next_frame_time < now:
                        next_frame_time = now + frame_interval
                else:
                    sleep_time = next_frame_time - now
                    if sleep_time > 0.002:
                        time.sleep(sleep_time * 0.7)

    def _writer_worker(self):
        """Dedicated thread writing frames into FFmpeg H.264 pipe or OpenCV writer."""
        # Determine temporary video output path
        output_dir = os.path.dirname(self.output_path)
        if output_dir and not os.path.exists(output_dir):
            os.makedirs(output_dir, exist_ok=True)

        if self.record_audio:
            temp_fd, self._temp_video_path = tempfile.mkstemp(suffix=".mp4")
            os.close(temp_fd)
        else:
            self._temp_video_path = self.output_path

        # Wait for first frame to know resolution
        while self.width == 0 or self.height == 0:
            if self._stop_event.is_set():
                return
            time.sleep(0.02)

        # Initialize FFmpeg H.264 Encoder Pipe
        ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
        ffmpeg_cmd = [
            ffmpeg_exe,
            "-y",
            "-f", "rawvideo",
            "-vcodec", "rawvideo",
            "-s", f"{self.width}x{self.height}",
            "-pix_fmt", "bgr24",
            "-r", str(self.fps),
            "-i", "-",
            "-c:v", "libx264",
            "-preset", "veryfast",     # Balanced encoding speed and CPU usage
            "-crf", str(self.crf),     # 18 = visually lossless HD quality
            "-pix_fmt", "yuv420p",     # Standard compatibility across all players/browsers
            "-movflags", "+faststart", # Allows progressive playback
            self._temp_video_path
        ]

        proc = None
        cv_writer = None
        try:
            proc = subprocess.Popen(
                ffmpeg_cmd,
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
        except Exception as e:
            print(f"[FFmpeg] Pipeline notice ({e}), falling back to OpenCV VideoWriter.")
            fourcc = cv2.VideoWriter_fourcc(*'mp4v')
            cv_writer = cv2.VideoWriter(self._temp_video_path, fourcc, self.fps, (self.width, self.height))

        while not self._stop_event.is_set() or not self._frame_queue.empty():
            try:
                frame = self._frame_queue.get(timeout=0.1)
                if proc and proc.stdin:
                    proc.stdin.write(frame.tobytes())
                elif cv_writer:
                    cv_writer.write(frame)
                self._frame_queue.task_done()
            except queue.Empty:
                continue
            except Exception:
                break

        # Flush and close video writer
        if proc:
            try:
                if proc.stdin:
                    proc.stdin.close()
                proc.wait(timeout=5.0)
            except Exception:
                pass
        if cv_writer:
            cv_writer.release()

    def _merge_video_audio(self, video_file: str, audio_file: str, output_file: str):
        """Muxes video and audio streams seamlessly using bundled FFmpeg."""
        ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
        cmd = [
            ffmpeg_exe,
            "-y",
            "-i", video_file,
            "-i", audio_file,
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "192k",
            "-shortest",
            output_file
        ]
        try:
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        except Exception as e:
            print(f"[Merge] Error merging audio: {e}")
            if os.path.exists(video_file) and not os.path.exists(output_file):
                os.replace(video_file, output_file)


# ==============================================================================
# Modern Dark-Theme GUI (Tkinter)
# ==============================================================================

class RecorderGUI:
    """Modern, sleek Dark-Themed GUI for the Windows Screen Recorder."""

    def __init__(self):
        import tkinter as tk
        from tkinter import ttk, filedialog, messagebox

        self.tk = tk
        self.ttk = ttk
        self.filedialog = filedialog
        self.messagebox = messagebox

        self.root = tk.Tk()
        self.root.title("⚡ Windows HD Screen Recorder")
        self.root.geometry("540x580")
        self.root.minsize(500, 540)
        self.root.configure(bg="#181825") # Catppuccin Mocha / VS Code dark tone

        self.recorder: Optional[ScreenRecorder] = None
        self.timer_running = False
        self.pulse_state = False

        self._init_styles()
        self._build_ui()
        self._init_hotkeys()

    def _init_styles(self):
        style = self.ttk.Style()
        style.theme_use("clam")
        
        # Configure modern colors
        style.configure("TFrame", background="#181825")
        style.configure("Card.TFrame", background="#1e1e2e", relief="flat")
        
        style.configure("TLabel", background="#181825", foreground="#cdd6f4", font=("Segoe UI", 10))
        style.configure("Card.TLabel", background="#1e1e2e", foreground="#cdd6f4", font=("Segoe UI", 10))
        style.configure("Header.TLabel", background="#181825", foreground="#89b4fa", font=("Segoe UI", 14, "bold"))
        style.configure("Timer.TLabel", background="#11111b", foreground="#a6e3a1", font=("Consolas", 28, "bold"))
        style.configure("Status.TLabel", background="#11111b", foreground="#f38ba8", font=("Segoe UI", 10, "bold"))

        style.configure("TCheckbutton", background="#1e1e2e", foreground="#cdd6f4", font=("Segoe UI", 10))
        style.map("TCheckbutton", background=[("active", "#1e1e2e")], foreground=[("active", "#89b4fa")])

        style.configure("TCombobox", fieldbackground="#313244", background="#45475a", foreground="#cdd6f4", font=("Segoe UI", 10))
        style.map("TCombobox", fieldbackground=[("readonly", "#313244")], foreground=[("readonly", "#ffffff")])

    def _build_ui(self):
        # Header banner
        header_frame = self.ttk.Frame(self.root, padding=(20, 15, 20, 10))
        header_frame.pack(fill="x")
        
        title_lbl = self.ttk.Label(header_frame, text="🎥 Windows HD Screen Recorder", style="Header.TLabel")
        title_lbl.pack(anchor="w")
        
        sub_lbl = self.ttk.Label(header_frame, text="Studio-quality H.264 1080p/4K 60 FPS recording with cursor & audio", font=("Segoe UI", 9), foreground="#a6adc8")
        sub_lbl.pack(anchor="w", pady=(2, 0))

        # Timer & Status Display Card
        timer_card = self.tk.Frame(self.root, bg="#11111b", bd=1, relief="solid", highlightbackground="#313244", highlightthickness=1)
        timer_card.pack(fill="x", padx=20, pady=8)

        self.timer_label = self.tk.Label(timer_card, text="00:00:00", bg="#11111b", fg="#a6e3a1", font=("Consolas", 28, "bold"))
        self.timer_label.pack(pady=(10, 0))

        self.status_label = self.tk.Label(timer_card, text="● READY TO RECORD", bg="#11111b", fg="#89b4fa", font=("Segoe UI", 10, "bold"))
        self.status_label.pack(pady=(2, 10))

        # Settings Card Frame
        settings_card = self.ttk.Frame(self.root, style="Card.TFrame", padding=(15, 12))
        settings_card.pack(fill="x", padx=20, pady=6)

        # Row 1: Monitor / Display Selector
        self.ttk.Label(settings_card, text="Screen / Monitor:", style="Card.TLabel").grid(row=0, column=0, sticky="w", pady=4)
        self.monitor_combo = self.ttk.Combobox(settings_card, state="readonly", width=32)
        monitors = ScreenRecorder.get_monitors()
        self.monitor_combo['values'] = [m['name'] for m in monitors]
        # Default to Primary display (index 1 if present, else 0)
        self.monitor_combo.current(1 if len(monitors) > 1 else 0)
        self.monitor_combo.grid(row=0, column=1, sticky="e", pady=4, padx=(10, 0))

        # Row 2: Framerate & Quality CRF
        self.ttk.Label(settings_card, text="Framerate & Quality:", style="Card.TLabel").grid(row=1, column=0, sticky="w", pady=4)
        fps_frame = self.ttk.Frame(settings_card, style="Card.TFrame")
        fps_frame.grid(row=1, column=1, sticky="e", pady=4, padx=(10, 0))
        
        self.fps_combo = self.ttk.Combobox(fps_frame, state="readonly", width=8, values=["60 FPS", "30 FPS", "24 FPS"])
        self.fps_combo.current(1) # Default 30 FPS
        self.fps_combo.pack(side="left", padx=(0, 6))

        self.quality_combo = self.ttk.Combobox(fps_frame, state="readonly", width=18, values=["Lossless HD (CRF 18)", "High Quality (CRF 21)", "Compact (CRF 25)"])
        self.quality_combo.current(0) # Default CRF 18
        self.quality_combo.pack(side="left")

        # Row 3: Audio Source
        self.ttk.Label(settings_card, text="Audio Input:", style="Card.TLabel").grid(row=2, column=0, sticky="w", pady=4)
        self.audio_combo = self.ttk.Combobox(settings_card, state="readonly", width=32)
        self.audio_devices = AudioRecorder.get_input_devices()
        audio_options = ["None (Mute Video)"] + [d['name'] for d in self.audio_devices]
        self.audio_combo['values'] = audio_options
        self.audio_combo.current(0)
        self.audio_combo.grid(row=2, column=1, sticky="e", pady=4, padx=(10, 0))

        # Row 4: Cursor & Click Highlights
        toggles_frame = self.ttk.Frame(settings_card, style="Card.TFrame")
        toggles_frame.grid(row=3, column=0, columnspan=2, sticky="w", pady=(8, 2))
        
        self.cursor_var = self.tk.BooleanVar(value=True)
        self.cursor_chk = self.ttk.Checkbutton(toggles_frame, text="Record Mouse Pointer", variable=self.cursor_var)
        self.cursor_chk.pack(side="left", padx=(0, 15))

        self.click_var = self.tk.BooleanVar(value=True)
        self.click_chk = self.ttk.Checkbutton(toggles_frame, text="Highlight Mouse Clicks (Ripples)", variable=self.click_var)
        self.click_chk.pack(side="left")

        # Output Path Card
        out_card = self.ttk.Frame(self.root, style="Card.TFrame", padding=(15, 10))
        out_card.pack(fill="x", padx=20, pady=6)

        self.ttk.Label(out_card, text="Save Video To:", style="Card.TLabel").pack(anchor="w")
        
        out_row = self.ttk.Frame(out_card, style="Card.TFrame")
        out_row.pack(fill="x", pady=(4, 0))
        
        default_save_path = os.path.join(os.path.expanduser("~"), "Videos", f"Recording_{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp4")
        if not os.path.exists(os.path.dirname(default_save_path)):
            default_save_path = os.path.join(os.getcwd(), f"Recording_{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp4")

        self.output_entry = self.tk.Entry(out_row, bg="#313244", fg="#ffffff", insertbackground="#ffffff", relief="flat", font=("Segoe UI", 9))
        self.output_entry.insert(0, default_save_path)
        self.output_entry.pack(side="left", fill="x", expand=True, ipady=4, padx=(0, 6))

        browse_btn = self.tk.Button(out_row, text="📁 Browse", command=self._browse_output, bg="#45475a", fg="#ffffff", activebackground="#585b70", activeforeground="#ffffff", relief="flat", font=("Segoe UI", 9), padx=8)
        browse_btn.pack(side="right")

        # Main Action Buttons (Start, Pause, Stop)
        btn_frame = self.ttk.Frame(self.root, padding=(20, 10, 20, 10))
        btn_frame.pack(fill="x")

        self.start_btn = self.tk.Button(btn_frame, text="▶ START RECORDING (F9)", command=self._toggle_record, bg="#a6e3a1", fg="#11111b", activebackground="#94e2d5", activeforeground="#11111b", relief="flat", font=("Segoe UI", 11, "bold"), pady=8, cursor="hand2")
        self.start_btn.pack(side="left", fill="x", expand=True, padx=(0, 6))

        self.pause_btn = self.tk.Button(btn_frame, text="⏸ Pause", command=self._toggle_pause, state="disabled", bg="#f9e2af", fg="#11111b", activebackground="#fab387", activeforeground="#11111b", relief="flat", font=("Segoe UI", 10, "bold"), pady=8, width=10, cursor="hand2")
        self.pause_btn.pack(side="left", padx=(0, 6))

        self.stop_btn = self.tk.Button(btn_frame, text="⏹ Stop & Save (F10)", command=self._stop_record, state="disabled", bg="#f38ba8", fg="#11111b", activebackground="#eba0ac", activeforeground="#11111b", relief="flat", font=("Segoe UI", 10, "bold"), pady=8, width=16, cursor="hand2")
        self.stop_btn.pack(side="right")

        # Footer Status & Hotkey guide
        footer = self.ttk.Frame(self.root, padding=(20, 4, 20, 12))
        footer.pack(fill="x", side="bottom")
        
        hotkey_lbl = self.ttk.Label(footer, text="⌨ Hotkeys:  [F9] Start/Pause/Resume   •   [F10] Stop & Save", font=("Segoe UI", 8), foreground="#6c7086")
        hotkey_lbl.pack(anchor="center")

    def _init_hotkeys(self):
        """Register global hotkeys via pynput if available."""
        if not HOTKEYS_AVAILABLE:
            return
        
        def on_press(key):
            try:
                if key == pynput_keyboard.Key.f9:
                    self.root.after(0, self._hotkey_f9)
                elif key == pynput_keyboard.Key.f10:
                    self.root.after(0, self._hotkey_f10)
            except Exception:
                pass

        try:
            listener = pynput_keyboard.Listener(on_press=on_press)
            listener.daemon = True
            listener.start()
        except Exception as e:
            print(f"[Hotkey] Notice: Could not register global keyboard hook ({e})")

    def _hotkey_f9(self):
        if self.recorder and self.recorder.is_recording:
            self._toggle_pause()
        else:
            self._toggle_record()

    def _hotkey_f10(self):
        if self.recorder and self.recorder.is_recording:
            self._stop_record()

    def _browse_output(self):
        filename = self.filedialog.asksaveasfilename(
            defaultextension=".mp4",
            filetypes=[("MP4 Video", "*.mp4"), ("All Files", "*.*")],
            initialfile=f"Recording_{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp4"
        )
        if filename:
            self.output_entry.delete(0, "end")
            self.output_entry.insert(0, filename)

    def _toggle_record(self):
        if not self.recorder or not self.recorder.is_recording:
            # Parse settings
            output_file = self.output_entry.get().strip()
            if not output_file:
                self.messagebox.showerror("Error", "Please specify a valid output video path.")
                return

            fps_map = {"60 FPS": 60, "30 FPS": 30, "24 FPS": 24}
            selected_fps = fps_map.get(self.fps_combo.get(), 30)

            crf_map = {"Lossless HD (CRF 18)": 18, "High Quality (CRF 21)": 21, "Compact (CRF 25)": 25}
            selected_crf = crf_map.get(self.quality_combo.get(), 18)

            monitor_idx = self.monitor_combo.current()
            
            # Audio device
            audio_idx = self.audio_combo.current()
            record_audio = (audio_idx > 0)
            audio_dev_idx = self.audio_devices[audio_idx - 1]['index'] if record_audio and (audio_idx - 1) < len(self.audio_devices) else None

            # Create and start recorder
            self.recorder = ScreenRecorder(
                output_path=output_file,
                fps=selected_fps,
                crf=selected_crf,
                monitor_index=monitor_idx,
                draw_cursor=self.cursor_var.get(),
                highlight_clicks=self.click_var.get(),
                record_audio=record_audio,
                audio_device_index=audio_dev_idx
            )
            
            if self.recorder.start():
                self.start_btn.config(state="disabled", bg="#45475a")
                self.pause_btn.config(state="normal", text="⏸ Pause", bg="#f9e2af")
                self.stop_btn.config(state="normal", bg="#f38ba8")
                self.status_label.config(text="● RECORDING LIVE...", fg="#f38ba8")
                self.timer_running = True
                self._update_timer()
            else:
                self.messagebox.showerror("Error", "Failed to start screen recorder.")

    def _toggle_pause(self):
        if not self.recorder or not self.recorder.is_recording:
            return
        
        if self.recorder.is_paused:
            self.recorder.resume()
            self.pause_btn.config(text="⏸ Pause", bg="#f9e2af")
            self.status_label.config(text="● RECORDING LIVE...", fg="#f38ba8")
        else:
            self.recorder.pause()
            self.pause_btn.config(text="▶ Resume", bg="#a6e3a1")
            self.status_label.config(text="⏸ RECORDING PAUSED", fg="#f9e2af")

    def _stop_record(self):
        if not self.recorder or not self.recorder.is_recording:
            return

        self.timer_running = False
        self.status_label.config(text="⚙ Finalizing video encoding...", fg="#89b4fa")
        self.root.update()

        final_path = self.recorder.stop()
        
        # Reset UI
        self.start_btn.config(state="normal", bg="#a6e3a1")
        self.pause_btn.config(state="disabled", text="⏸ Pause", bg="#45475a")
        self.stop_btn.config(state="disabled", bg="#45475a")
        self.status_label.config(text="✔ RECORDING SAVED", fg="#a6e3a1")

        # Update filename for next recording
        next_path = os.path.join(os.path.dirname(final_path), f"Recording_{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp4")
        self.output_entry.delete(0, "end")
        self.output_entry.insert(0, next_path)

        # Prompt user with completion dialog
        if os.path.exists(final_path):
            file_size_mb = os.path.getsize(final_path) / (1024 * 1024)
            msg = f"Video successfully recorded and saved to:\n\n{final_path}\n\nFile Size: {file_size_mb:.2f} MB\n\nWould you like to open the output folder?"
            if self.messagebox.askyesno("Recording Finished", msg):
                try:
                    subprocess.Popen(f'explorer /select,"{final_path}"')
                except Exception:
                    pass

    def _update_timer(self):
        if not self.timer_running or not self.recorder:
            return
        
        elapsed = int(self.recorder.get_elapsed_seconds())
        hours = elapsed // 3600
        minutes = (elapsed % 3600) // 60
        seconds = elapsed % 60
        self.timer_label.config(text=f"{hours:02d}:{minutes:02d}:{seconds:02d}")

        # Pulsate status indicator dot
        if not self.recorder.is_paused:
            self.pulse_state = not self.pulse_state
            dot = "●" if self.pulse_state else "○"
            self.status_label.config(text=f"{dot} RECORDING LIVE ({self.recorder.frames_recorded} frames)")

        self.root.after(500, self._update_timer)

    def run(self):
        self.root.mainloop()


# ==============================================================================
# CLI Entry Point
# ==============================================================================

def run_cli(args):
    """Runs screen recorder in command-line mode."""
    print("=" * 60)
    print(" 🎥 Windows HD Screen Recorder (CLI Mode)")
    print("=" * 60)
    
    recorder = ScreenRecorder(
        output_path=args.output,
        fps=args.fps,
        crf=args.crf,
        monitor_index=args.monitor,
        draw_cursor=not args.no_cursor,
        highlight_clicks=not args.no_clicks,
        record_audio=args.audio,
        audio_device_index=args.audio_device
    )

    print(f"[Config] Output File: {recorder.output_path}")
    print(f"[Config] Framerate:   {recorder.fps} FPS")
    print(f"[Config] Quality CRF: {recorder.crf} (18=Lossless HD)")
    print(f"[Config] Monitor:     Index {recorder.monitor_index}")
    print(f"[Config] Audio:       {'Enabled' if recorder.record_audio else 'Disabled'}")
    print(f"[Config] Cursor:      {'Included' if recorder.draw_cursor else 'Hidden'}")
    print("-" * 60)

    if not recorder.start():
        print("[Error] Failed to initialize screen recording engine.")
        sys.exit(1)

    print("[Status] Recording started. Press Ctrl+C or Enter to stop recording...")
    start_t = time.time()
    try:
        if args.duration and args.duration > 0:
            print(f"[Timer] Recording for {args.duration} seconds...")
            while time.time() - start_t < args.duration:
                elapsed = int(recorder.get_elapsed_seconds())
                print(f"\r[Recording] Elapsed: {elapsed:02d}s | Frames: {recorder.frames_recorded}", end="", flush=True)
                time.sleep(0.5)
            print()
        else:
            # Interactive wait
            while True:
                elapsed = int(recorder.get_elapsed_seconds())
                print(f"\r[Recording] Elapsed: {elapsed:02d}s | Frames: {recorder.frames_recorded} (Press Ctrl+C to stop)", end="", flush=True)
                time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n[Status] Stop requested by user.")
    finally:
        print("\n[Status] Finalizing video encoding and saving...")
        saved_file = recorder.stop()
        if os.path.exists(saved_file):
            size_mb = os.path.getsize(saved_file) / (1024 * 1024)
            print(f"✔ Successfully saved HD video: {saved_file} ({size_mb:.2f} MB)")
        else:
            print(f"[Error] Output file was not generated: {saved_file}")


def main():
    parser = argparse.ArgumentParser(description="Windows HD Screen Recorder (Python)")
    parser.add_argument("--output", "-o", type=str, default="recording.mp4", help="Output MP4 filename / path (default: recording.mp4)")
    parser.add_argument("--fps", type=int, default=30, help="Framerate (e.g. 30, 60; default: 30)")
    parser.add_argument("--crf", type=int, default=18, help="H.264 CRF quality level (18=Lossless HD, 23=Standard; default: 18)")
    parser.add_argument("--monitor", "-m", type=int, default=1, help="Monitor index to record (0=Virtual Desktop, 1=Monitor 1; default: 1)")
    parser.add_argument("--duration", "-d", type=float, default=0, help="Duration in seconds (0 = record until stopped)")
    parser.add_argument("--audio", "-a", action="store_true", help="Record audio with video")
    parser.add_argument("--audio-device", type=int, default=None, help="Audio input device index")
    parser.add_argument("--no-cursor", action="store_true", help="Hide mouse cursor in recording")
    parser.add_argument("--no-clicks", action="store_true", help="Disable click ripple highlight animations")
    parser.add_argument("--no-gui", action="store_true", help="Run in CLI mode without opening GUI")
    parser.add_argument("--list-devices", action="store_true", help="List all monitors and audio devices then exit")

    args = parser.parse_args()

    if args.list_devices:
        print("\n=== MONITORS ===")
        for m in ScreenRecorder.get_monitors():
            print(f"  [{m['index']}] {m['name']} (Offset: {m['left']},{m['top']} - Size: {m['width']}x{m['height']})")
        
        print("\n=== AUDIO INPUT DEVICES ===")
        devs = AudioRecorder.get_input_devices()
        if not devs:
            print("  (No input audio devices detected)")
        for d in devs:
            print(f"  [{d['index']}] {d['name']} ({d['channels']} channels)")
        print()
        return

    # If --no-gui or any specific automation arguments are provided, run in CLI mode
    if args.no_gui or args.duration > 0 or len(sys.argv) > 1 and any(arg.startswith(("-o", "--output", "-d", "--duration")) for arg in sys.argv[1:]):
        run_cli(args)
    else:
        # Launch modern GUI by default
        app = RecorderGUI()
        app.run()


if __name__ == "__main__":
    main()
