import os
import sys
import json
import time
import cv2
import numpy as np
import onnxruntime as ort

MODEL_DIR = r"C:\PS_Fix\models"
ALIGNER_PATH = os.path.join(MODEL_DIR, "lcnet100_docaligner.onnx")
SEG_PATH = os.path.join(MODEL_DIR, "deeplabv3_docseg.onnx")

# Initialize ONNX sessions with CPUExecutionProvider (DirectML / CPU fallback)
_sess_options = ort.SessionOptions()
_sess_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
_sess_options.intra_op_num_threads = 4

aligner_session = None
seg_session = None

try:
    if os.path.exists(ALIGNER_PATH):
        aligner_session = ort.InferenceSession(ALIGNER_PATH, _sess_options, providers=['CPUExecutionProvider'])
    if os.path.exists(SEG_PATH):
        seg_session = ort.InferenceSession(SEG_PATH, _sess_options, providers=['CPUExecutionProvider'])
except Exception as e:
    pass

def order_points(pts):
    rect = np.zeros((4, 2), dtype="float32")
    s = pts.sum(axis=1)
    rect[0] = pts[np.argmin(s)] # Top-Left
    rect[2] = pts[np.argmax(s)] # Bottom-Right

    diff = np.diff(pts, axis=1)
    rect[1] = pts[np.argmin(diff)] # Top-Right
    rect[3] = pts[np.argmax(diff)] # Bottom-Left
    return rect

def detect_ai_cascade(image_path):
    if not os.path.exists(image_path):
        return {"valid": False, "error": "file_not_found"}

    img = cv2.imread(image_path)
    if img is None:
        return {"valid": False, "error": "cannot_read_image"}

    orig_h, orig_w = img.shape[:2]
    t0 = time.time()

    # --- STAGE 1: Full-Page Glass Assessment ---
    # Fast color & gradient check to determine if the document is a full A4 sheet / Deed
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)

    bg_sample = np.concatenate([
        gray[int(orig_h * 0.90):, int(orig_w * 0.2):int(orig_w * 0.8)].flatten(),
        gray[0:int(orig_h * 0.04), :].flatten()
    ])
    bg_median = int(np.median(bg_sample))
    diff = np.abs(gray.astype(int) - bg_median).astype(np.uint8)

    # Gradient energy
    gx = cv2.Sobel(blurred, cv2.CV_16S, 1, 0, ksize=3)
    gy = cv2.Sobel(blurred, cv2.CV_16S, 0, 1, ksize=3)
    grad = cv2.addWeighted(cv2.convertScaleAbs(gx), 0.5, cv2.convertScaleAbs(gy), 0.5, 0)
    energy = cv2.bitwise_or((diff > 18).astype(np.uint8) * 255, (grad > 15).astype(np.uint8) * 255)

    # Zero outer 1% scanner bezel
    gx_pad = int(orig_w * 0.015)
    gy_pad = int(orig_h * 0.015)
    energy[:gy_pad, :] = 0
    energy[orig_h-gy_pad:, :] = 0
    energy[:, :gx_pad] = 0
    energy[:, orig_w-gx_pad:] = 0

    row_counts = np.sum(energy > 0, axis=1)
    col_counts = np.sum(energy > 0, axis=0)
    active_rows = np.where(row_counts > orig_w * 0.02)[0]
    active_cols = np.where(col_counts > orig_h * 0.02)[0]

    if len(active_rows) > 0 and len(active_cols) > 0:
        doc_w_span = active_cols[-1] - active_cols[0]
        doc_h_span = active_rows[-1] - active_rows[0]
        is_full_page = (doc_w_span > orig_w * 0.70 and doc_h_span > orig_h * 0.50)
    else:
        is_full_page = True

    if is_full_page:
        # Full A4 / Legal / Birth Certificate placed on scanner bed
        paper_bottom = int(active_rows[-1]) if len(active_rows) > 0 else int(orig_h)
        for y in range(min(orig_h - 5, paper_bottom + 40), max(20, paper_bottom - 20), -1):
            if np.mean(grad[y, int(orig_w * 0.2):int(orig_w * 0.8)]) > 6.0:
                paper_bottom = int(min(orig_h, y + 4))
                break

        return {
            "valid": True,
            "type": "full_page",
            "x": 0, "y": 0, "w": int(orig_w), "h": int(paper_bottom),
            "norm_x": 0.0, "norm_y": 0.0, "norm_w": 1.0,
            "norm_h": float(paper_bottom) / orig_h,
            "angle": 0.0,
            "corners": [[0, 0], [int(orig_w), 0], [int(orig_w), int(paper_bottom)], [0, int(paper_bottom)]],
            "infer_time_ms": float((time.time() - t0) * 1000.0),
            "orig_w": int(orig_w), "orig_h": int(orig_h)
        }

    # --- STAGE 2: DocAligner ONNX Corner Regression ---
    corners = None
    confidence = 0.0

    if aligner_session is not None:
        resized_256 = cv2.resize(img, (256, 256))
        rgb_256 = cv2.cvtColor(resized_256, cv2.COLOR_BGR2RGB)
        tensor_256 = np.transpose(rgb_256, (2, 0, 1)).astype(np.float32) / 255.0
        tensor_256 = np.expand_dims(tensor_256, axis=0)

        outputs = aligner_session.run(['heatmap'], {'img': tensor_256})
        heatmaps = outputs[0][0] # (4, 128, 128)

        pts = []
        conf_scores = []
        for i in range(4):
            hm = heatmaps[i]
            max_val = np.max(hm)
            conf_scores.append(max_val)
            idx = np.argmax(hm)
            y, x = np.unravel_index(idx, hm.shape)
            pts.append([(x / 128.0) * orig_w, (y / 128.0) * orig_h])

        confidence = float(np.mean(conf_scores))
        if confidence > 0.35:
            corners = np.array(pts, dtype=np.float32)

    # --- STAGE 3: Segmentation Fallback if DocAligner confidence is low ---
    if corners is None and seg_session is not None:
        resized_384 = cv2.resize(img, (384, 384))
        rgb_384 = cv2.cvtColor(resized_384, cv2.COLOR_BGR2RGB)
        mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
        std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
        norm = ((rgb_384 / 255.0) - mean) / std
        tensor_384 = np.transpose(norm, (2, 0, 1)).astype(np.float32)[None]

        outputs = seg_session.run(None, {'input': tensor_384})
        mask_logits = outputs[0][0]
        pred_mask = np.argmax(mask_logits, axis=0).astype(np.uint8)
        full_mask = cv2.resize(pred_mask, (orig_w, orig_h), interpolation=cv2.INTER_NEAREST)

        cnts, _ = cv2.findContours(full_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if cnts:
            largest = max(cnts, key=cv2.contourArea)
            rect = cv2.minAreaRect(largest)
            corners = cv2.boxPoints(rect)

    # Fallback to energy bounding box if AI fails
    if corners is None:
        min_x = int(active_cols[0])
        max_x = int(active_cols[-1])
        min_y = int(active_rows[0])
        max_y = int(active_rows[-1])
        corners = np.array([[min_x, min_y], [max_x, min_y], [max_x, max_y], [min_x, max_y]], dtype=np.float32)

    ordered = order_points(corners)
    (tl, tr, br, bl) = ordered

    # Measure angle
    dx = tr[0] - tl[0]
    dy = tr[1] - tl[1]
    angle = float(np.degrees(np.arctan2(dy, dx)))
    while angle > 45.0: angle -= 90.0
    while angle < -45.0: angle += 90.0
    if abs(angle) < 3.5: angle = 0.0

    # --- STAGE 4: Glass-Ruler Flush Snapping ---
    min_x = float(np.min(ordered[:, 0]))
    min_y = float(np.min(ordered[:, 1]))
    max_x = float(np.max(ordered[:, 0]))
    max_y = float(np.max(ordered[:, 1]))

    # If document is placed within 30px of the top glass ruler, snap to 0px
    if min_y <= orig_h * 0.035:
        min_y = 0.0
        tl[1] = 0.0
        tr[1] = 0.0

    if min_x <= orig_w * 0.035:
        min_x = 0.0
        tl[0] = 0.0
        bl[0] = 0.0

    final_w = max_x - min_x
    final_h = max_y - min_y

    return {
        "valid": True,
        "type": "isolated_card",
        "x": int(min_x),
        "y": int(min_y),
        "w": int(final_w),
        "h": int(final_h),
        "norm_x": float(min_x) / orig_w,
        "norm_y": float(min_y) / orig_h,
        "norm_w": float(final_w) / orig_w,
        "norm_h": float(final_h) / orig_h,
        "angle": angle,
        "corners": ordered.tolist(),
        "confidence": confidence,
        "infer_time_ms": (time.time() - t0) * 1000.0,
        "orig_w": orig_w,
        "orig_h": orig_h
    }

def crop_and_warp(img_path, out_path, doc_info):
    img = cv2.imread(img_path)
    if img is None: return False

    if abs(doc_info.get("angle", 0.0)) < 0.5:
        x = max(0, doc_info["x"])
        y = max(0, doc_info["y"])
        w = min(img.shape[1] - x, doc_info["w"])
        h = min(img.shape[0] - y, doc_info["h"])
        cropped = img[y:y+h, x:x+w]
        cv2.imwrite(out_path, cropped, [cv2.IMWRITE_JPEG_QUALITY, 98])
        return True

    corners = np.array(doc_info["corners"], dtype="float32")
    rect = order_points(corners)
    (tl, tr, br, bl) = rect

    wA = np.sqrt(((br[0] - bl[0]) ** 2) + ((br[1] - bl[1]) ** 2))
    wB = np.sqrt(((tr[0] - tl[0]) ** 2) + ((tr[1] - tl[1]) ** 2))
    max_w = max(int(wA), int(wB))

    hA = np.sqrt(((tr[0] - br[0]) ** 2) + ((tr[1] - br[1]) ** 2))
    hB = np.sqrt(((tl[0] - bl[0]) ** 2) + ((tl[1] - bl[1]) ** 2))
    max_h = max(int(hA), int(hB))

    dst = np.array([
        [0, 0],
        [max_w - 1, 0],
        [max_w - 1, max_h - 1],
        [0, max_h - 1]
    ], dtype="float32")

    M = cv2.getPerspectiveTransform(rect, dst)
    warped = cv2.warpPerspective(img, M, (max_w, max_h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT, borderValue=(255, 255, 255))
    cv2.imwrite(out_path, warped, [cv2.IMWRITE_JPEG_QUALITY, 98])
    return True

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python ai_doc_cascade.py <image_path> [--crop-out <out_path>]")
        sys.exit(1)

    img_path = sys.argv[1]
    res = detect_ai_cascade(img_path)

    if "--crop-out" in sys.argv:
        idx = sys.argv.index("--crop-out")
        if idx + 1 < len(sys.argv):
            out_file = sys.argv[idx + 1]
            crop_and_warp(img_path, out_file, res)

    print(json.dumps(res))
