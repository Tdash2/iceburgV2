import subprocess
import threading
import time
import json
import os
import socket
import io

from PIL import Image, ImageDraw, ImageFont
from flask import Flask, request, jsonify, render_template_string
import logging

log = logging.getLogger('werkzeug')
log.setLevel(logging.ERROR)

# =========================================================
# CONFIG
# =========================================================


FONT_PATH = "/usr/share/fonts/truetype/roboto/unhinted/RobotoCondensed-Bold.ttf"
DECKLINK_DEVICE = "DeckLink SDI"
name = "Output 1"
serverport = 8100



BOX_PNG = Image.open("logo.png").convert("RGBA")


new_height = 200
ratio = new_height / BOX_PNG.height
new_width = int(BOX_PNG.width * ratio)

BOX_PNG = BOX_PNG.resize((new_width, new_height))


# =========================================================
# GLOBALS (FIXED MEMORY MODEL)
# =========================================================
app = Flask(__name__)
WIDTH, HEIGHT = 1280, 720
FPS = 30
PROGRAM_START = time.time()
PREVIEW_FPS = 8
preview_interval = 1.0 / PREVIEW_FPS

STATE_FILE = "state.json"

ffmpeg_process = None
state_lock = threading.Lock()

latest_frame_jpeg = None
latest_frame_lock = threading.Lock()

frame_lock = threading.Lock()

frame_a = Image.new("RGBA", (WIDTH, HEIGHT))
frame_b = Image.new("RGBA", (WIDTH, HEIGHT))
frame_write = frame_a
frame_read = None

# =========================================================
# STATE
# =========================================================

DEFAULT_STATE = {
    "text1": "Test Signal Generator",
    "text2": "Version 2.0",
    "muted": False,
    "box_x": 100,
    "box_y": 600,
    "box_w": 200,
    "box_h": 100,
    "box_speed_x": 10,
    "box_speed_y": 11,
    "output_mode": "1080i",
    "show_web_ui": True,
}

def load_state():
    if not os.path.exists(STATE_FILE):
        save_state(DEFAULT_STATE)
        return DEFAULT_STATE.copy()
    try:
        with open(STATE_FILE, "r") as f:
            return json.load(f)
    except:
        return DEFAULT_STATE.copy()

def save_state(s):
    with open(STATE_FILE, "w") as f:
        json.dump(s, f)

state = load_state()

# =========================================================
# NETWORK
# =========================================================

def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except:
        return "unknown"

LOCAL_IP = get_local_ip()
IP_TEXT = f"WEB UI: http://{LOCAL_IP}:"+str(serverport)

# =========================================================
# FONTS
# =========================================================

font_big = ImageFont.truetype(FONT_PATH, 150)
font_med = ImageFont.truetype(FONT_PATH, 80)
font_med_small = ImageFont.truetype(FONT_PATH, 55)
font_small = ImageFont.truetype(FONT_PATH, 40)

# =========================================================
# FFmpeg
# =========================================================

def get_video_mode():
    mode = state.get("output_mode", "1080i")

    if mode == "1080p":
        return {
            "w": 1920,
            "h": 1080,
            "fps": "60000/1001",
            "interlace": False
        }

    if mode == "720p":
        return {
            "w": 1280,
            "h": 720,
            "fps": "60000/1001",
            "interlace": False
        }

    # default = 1080i
    return {
        "w": 1920,
        "h": 1080,
        "fps": "60000/1001",
        "interlace": True
    }

# =========================================================
# FFmpeg (NOW PROPERLY MODE-AWARE)
# =========================================================

def start_ffmpeg():
    global ffmpeg_process

    video = get_video_mode()

    audio_input = (
      "aevalsrc="
      "0.1*sin(2*PI*1000*t)|"
      "0.056234*sin(2*PI*1000*t)|"
      "0.031623*sin(2*PI*1000*t)|"
      "0.017783*sin(2*PI*1000*t)|"
      "0.010000*sin(2*PI*1000*t)|"
      "0.005623*sin(2*PI*1000*t)|"
      "0.003162*sin(2*PI*1000*t)|"
      "0.001778*sin(2*PI*1000*t):"
      "s=48000"
      if not state["muted"]
      else
      "anullsrc=r=48000:cl=7.1"
    )

    # overlay graph
    if video["interlace"]:
        vf = (
            f"[1:v]scale={video['w']}:{video['h']}:flags=fast_bilinear[ov];"
            f"[0:v][ov]overlay=0:0[v0];"
            f"[v0]tinterlace=mode=interleave_top[v]"
        )
    else:
        vf = (
            f"[1:v]scale={video['w']}:{video['h']}:flags=fast_bilinear[ov];"
            f"[0:v][ov]overlay=0:0[v]"
        )

    cmd = [
        "ffmpeg",
        "-hide_banner",
        #"-loglevel", "quiet",
        "-nostats",
        
        "-fflags", "nobuffer",
        "-flags", "low_delay",
        "-avioflags", "direct",

         "-vsync", "passthrough",



        "-f", "lavfi",
        "-i", f"nullsrc=size={video['w']}x{video['h']}:rate={video['fps']}",

        "-thread_queue_size", "0",
        "-f", "rawvideo",
        "-pix_fmt", "rgba",
        "-s", f"{WIDTH}x{HEIGHT}",
        "-r", str(FPS),
        "-i", "-",

        "-f", "lavfi",
        "-i", audio_input,

        "-filter_complex", vf,

        "-map", "[v]",
        "-map", "2:a",

        "-pix_fmt", "uyvy422",
        "-c:v", "v210",
        "-field_order", "tt" if video["interlace"] else "progressive",

        "-c:a", "pcm_s16le",
        "-ac", "8",
        "-ar", "48000",
        

        "-f", "decklink",
        DECKLINK_DEVICE
    ]

    print("FFmpeg mode:", state["output_mode"])

    if ffmpeg_process:
        try:
            ffmpeg_process.kill()
            ffmpeg_process.wait(timeout=2)
        except:
            pass

    ffmpeg_process = subprocess.Popen(cmd, stdin=subprocess.PIPE, bufsize=0)

# =========================================================
# DRAW HELPERS
# =========================================================

def draw_text_box(draw, xy, text, font, padding=10):
    x, y = xy
    l, t, r, b = draw.textbbox((0, 0), text, font=font)
    w, h = r - l, b - t

    draw.rectangle((x - padding, y - padding, x + w + padding, y + h + padding),
                   fill=(0, 0, 0, 200))

    draw.text((x - l, y - t), text, font=font, fill=(255, 255, 255, 255))


def draw_centered_box(draw, center_y, text, font, width, padding=20):
    l, t, r, b = draw.textbbox((0, 0), text, font=font)
    tw, th = r - l, b - t

    bx1 = (width - tw) / 2
    by1 = center_y - th / 2

    draw.rectangle((bx1 - padding, by1 - padding,
                    bx1 + tw + padding, by1 + th + padding),
                   fill=(0, 0, 0, 255))

    draw.text((bx1 - l, by1 - t), text, font=font, fill=(255, 255, 255, 255))


def draw_smpte_bars(draw, w, h):
    colors = [
        (192, 192, 192),
        (192, 192, 0),
        (0, 192, 192),
        (0, 192, 0),
        (192, 0, 192),
        (192, 0, 0),
        (0, 0, 192),
    ]

    bar_w = w // len(colors)

    for i, c in enumerate(colors):
        draw.rectangle((i * bar_w, 0, (i + 1) * bar_w, h), fill=c)

    draw.rectangle((0, h, w, h), fill=(16, 16, 16))

# =========================================================
# GRAPHICS LOOP (FIXED - NO COPY PER FRAME)
# =========================================================

def graphics_loop():
    global frame_write, frame_read, ffmpeg_process, state

    frame_time = 1.0 / FPS
    next_frame = time.perf_counter()

    box = state

    png_w = BOX_PNG.width
    png_h = BOX_PNG.height

    # Make sure these exist in state
    box.setdefault("box_x", 200)
    box.setdefault("box_y", 200)
    box.setdefault("box_speed_x", 4)
    box.setdefault("box_speed_y", 3)

    while True:

        draw = ImageDraw.Draw(frame_write)

        # Clear frame
        draw.rectangle((0, 0, WIDTH, HEIGHT), fill=(0, 0, 0, 0))

        # Draw SMPTE bars
        draw_smpte_bars(draw, WIDTH, HEIGHT)

        # -------------------------
        # DVD LOGO MOVEMENT
        # -------------------------
        box["box_x"] += box["box_speed_x"]
        box["box_y"] += box["box_speed_y"]

        # Left / Right bounce
        if box["box_x"] <= 0:
            box["box_x"] = 0
            box["box_speed_x"] *= -1
        elif box["box_x"] >= WIDTH - png_w:
            box["box_x"] = WIDTH - png_w
            box["box_speed_x"] *= -1

        # Top / Bottom bounce
        if box["box_y"] <= 0:
            box["box_y"] = 0
            box["box_speed_y"] *= -1
        elif box["box_y"] >= HEIGHT - png_h:
            box["box_y"] = HEIGHT - png_h
            box["box_speed_y"] *= -1

        # Draw PNG (now fully dynamic position)


        # Clock
        draw_centered_box(
            draw,
            200,
            time.strftime("%H:%M:%S"),
            font_big,
            WIDTH,
            10
        )

        # Text
        draw_centered_box(
            draw,
            324,
            box["text1"],
            font_med,
            WIDTH,
            3
        )

        draw_centered_box(
            draw,
            400,
            box["text2"],
            font_med_small,
            WIDTH,
            3
        )
        frame_write.paste(
            BOX_PNG,
            (int(box["box_x"]), int(box["box_y"])),
            BOX_PNG
        )

        # Web UI address
        if (
            state.get("show_web_ui", True)
            or (time.time() - PROGRAM_START) < 20
        ):
            draw_text_box(
                draw,
                (20, 20),
                IP_TEXT,
                font_small,
                4
            )
            

        # Swap buffers
        with frame_lock:
            frame_read = frame_write
            frame_write = frame_b if frame_write is frame_a else frame_a

        # Send to ffmpeg
        if ffmpeg_process and ffmpeg_process.stdin:
            try:
                ffmpeg_process.stdin.write(frame_read.tobytes())
            except:
                pass

        # Frame timing
        next_frame += frame_time
        sleep = next_frame - time.perf_counter()

        if sleep > 0:
            time.sleep(sleep)
        else:
            next_frame = time.perf_counter()
# =========================================================
# PREVIEW WORKER (NO COPY)
# =========================================================

def preview_worker():
    global latest_frame_jpeg

    next_time = 0

    while True:
        now = time.perf_counter()

        if now < next_time:
            time.sleep(0.01)
            continue

        next_time = now + preview_interval

        with frame_lock:
            frame = frame_read

        if frame is None:
            continue

        frame = frame.resize((640, 360)).convert("RGB")

        buf = io.BytesIO()
        frame.save(buf, format="JPEG", quality=65)

        with latest_frame_lock:
            latest_frame_jpeg = buf.getvalue()

# =========================================================
# WATCHDOG
# =========================================================

def watchdog():
    global ffmpeg_process

    while True:
        time.sleep(5)
        if ffmpeg_process and ffmpeg_process.poll() is not None:
            start_ffmpeg()

# =========================================================
# FLASK API (RESTORED FULLY)
# =========================================================

@app.route("/")
def index():
    return render_template_string(HTML)

@app.route("/state")
def api_state():
    return jsonify(state)

@app.route("/update", methods=["POST"])
def update():
    with state_lock:
        state["text1"] = request.form.get("text1", "")
        state["text2"] = request.form.get("text2", "")
        save_state(state)
    return ("", 204)

@app.route("/preview.jpg")
def preview():
    with latest_frame_lock:
        if latest_frame_jpeg is None:
            return ("No frame", 404)

        return latest_frame_jpeg, 200, {
            "Content-Type": "image/jpeg",
            "Cache-Control": "no-store"
        }

@app.route("/mute", methods=["POST"])
def mute():
    with state_lock:
        state["muted"] = not state["muted"]
        save_state(state)
    start_ffmpeg()
    return ("", 204)

@app.route("/mode", methods=["POST"])
def set_mode():
    with state_lock:
        state["output_mode"] = request.form.get("mode", "1080i")
        save_state(state)
    start_ffmpeg()
    return ("", 204)

@app.route("/speed", methods=["POST"])
def speed():
    with state_lock:
        try:
            state["box_speed"] = int(request.form.get("speed"))
        except:
            pass
        save_state(state)
    return ("", 204)
@app.route("/set_webui", methods=["POST"])
def set_webui():
    with state_lock:
        state["show_web_ui"] = (
            request.form.get("show", "false") == "true"
        )
        save_state(state)

    return ("", 204)

# =========================================================
# WEB UI (UNCHANGED)
# =========================================================

HTML = """
<!DOCTYPE html>
<html>
<title>Test Signal Generator """+ name +"""</title>
<link rel='stylesheet' href='http://"""+LOCAL_IP+"""/depends/bootstrap.min.css'>
<script src='http://"""+LOCAL_IP+"""/depends/jquery.min.js'></script>
<script src='http://"""+LOCAL_IP+"""/depends/bootstrap.min.js'></script>
<style>body { background-color:#232323; color:#FFF; }</style></head><body>

<body style="font-family:sans-serif;padding:40px;">


<div class='container'><div class='py-5 text-center'><h2>Test Signal Generator """+ name +"""</h2></div>

<div class='form-group'><label>First Line Text</label>
<input class='form-control' id="text1" size="40"></div>

<div class='form-group'><label>Second Line Text</h3></label>
<input  class='form-control' id="text2" size="40"></div>

<div class='form-group'><label>Output Format</label>
<select class='form-control' id="mode" onchange="setMode()">
  <option value="1080p">1920x1080 @ 59.94p</option>
  <option value="1080i">1080x1080 @ 59.94i</option>
  <option value="720p">1280x720 @ 59.94p</option>
</select></div>

<div class="form-check">
  <input class="form-check-input"
         type="checkbox"
         id="showweb"
         onchange="setWebUI()">

  <label class="form-check-label">
    Show WEB UI address on output
  </label>
</div>

<div class='form-group'><label>Preview</label>
<img id="prev" class="form-control" src="/preview.jpg?t=1779764323151" width="640" style="
    height: 100%;
">
</div>

<script>
function setWebUI() {
    const fd = new FormData();
    fd.append("show", showweb.checked);

    fetch("/set_webui", {
        method: "POST",
        body: fd
    });
}
function setMode() {
    const fd = new FormData();
    fd.append("mode", mode.value);

    fetch("/mode", {
        method: "POST",
        body: fd
    });
}

async function refresh(){
    const r = await fetch("/state");
    const s = await r.json();

    // only update if user is NOT typing
    if (document.activeElement !== text1) text1.value = s.text1;
    if (document.activeElement !== text2) text2.value = s.text2;

    showweb.checked = s.show_web_ui;
    mode.value = s.output_mode;
}



function update(){
    const fd = new FormData();
    fd.append("text1", text1.value);
    fd.append("text2", text2.value);
    fetch("/update",{method:"POST",body:fd});
    
}

function updateSpeed(){
    const fd = new FormData();
    fd.append("speed", speed.value);
    fetch("/speed",{method:"POST",body:fd});
}

function toggleMute(){
    fetch("/mute",{method:"POST"});
}

text1.oninput = update;
text2.oninput = update;


setInterval(() => {
    document.getElementById("prev").src =
        "/preview.jpg?t=" + Date.now();
}, 300); // 5 fps preview


refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
"""

# =========================================================
# MAIN
# =========================================================

if __name__ == "__main__":
    start_ffmpeg()

    threading.Thread(target=graphics_loop, daemon=True).start()
    threading.Thread(target=preview_worker, daemon=True).start()
    threading.Thread(target=watchdog, daemon=True).start()

    app.run(host="0.0.0.0", port=serverport, debug=False, use_reloader=False)