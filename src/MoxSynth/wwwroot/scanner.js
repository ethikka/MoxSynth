// Card scanner: camera preview with a card-shaped guide, crops the title line and the bottom-left info line
// (collector number + set code), runs OCR with Tesseract.js and hands the text to .NET for matching.
(function () {
    const TESSERACT_URL = 'https://cdn.jsdelivr.net/npm/tesseract.js@6.0.1/dist/tesseract.min.js';
    const CARD_ASPECT = 63 / 88;

    // Regions as fractions of the card (modern frame; good enough for most older frames too).
    // Generous on purpose: a hand-held card is never perfectly aligned with the guide.
    const TITLE = { x: 0.04, y: 0.02, w: 0.74, h: 0.10 };
    const BOTTOM = { x: 0.02, y: 0.86, w: 0.56, h: 0.13 };

    let stream = null, worker = null, timer = null, busy = false, session = 0;

    function loadScript(src) {
        return new Promise((resolve, reject) => {
            if (window.Tesseract) return resolve();
            const s = document.createElement('script');
            s.src = src; s.onload = resolve; s.onerror = () => reject(new Error('Could not load OCR engine'));
            document.head.appendChild(s);
        });
    }

    async function ensureWorker() {
        if (worker) return worker;
        await loadScript(TESSERACT_URL);
        worker = await Tesseract.createWorker('eng');
        return worker;
    }

    // Largest card-shaped rectangle (with margin) centred in a w×h frame.
    function guideRect(w, h, fill) {
        let gh = h * fill, gw = gh * CARD_ASPECT;
        if (gw > w * fill) { gw = w * fill; gh = gw / CARD_ASPECT; }
        return { x: (w - gw) / 2, y: (h - gh) / 2, w: gw, h: gh };
    }

    // Crops a card region, upscales it and returns a contrast-stretched greyscale copy (pixel array + size).
    function cropRegion(source, card, region) {
        const sx = card.x + region.x * card.w, sy = card.y + region.y * card.h;
        const sw = region.w * card.w, sh = region.h * card.h;
        const scale = Math.max(1, 110 / sh); // ~110px high strip works well for Tesseract
        const canvas = document.createElement('canvas');
        canvas.width = Math.round(sw * scale);
        canvas.height = Math.round(sh * scale);
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, sx, sy, sw, sh, 0, 0, canvas.width, canvas.height);

        const img = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const d = img.data, grey = new Float32Array(d.length / 4);
        let min = 255, max = 0;
        for (let i = 0, p = 0; i < d.length; i += 4, p++) {
            const g = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
            grey[p] = g;
            if (g < min) min = g;
            if (g > max) max = g;
        }
        const range = Math.max(1, max - min);
        for (let p = 0; p < grey.length; p++) grey[p] = (grey[p] - min) * 255 / range;
        return { grey, width: canvas.width, height: canvas.height };
    }

    // Renders a variant of the strip as black text on white for Tesseract.
    //   'normal'  dark text on a light background (most title bars)
    //   'invert'  light text on a dark background (info line on the black border, dark title bars)
    //   'dark'    keep only dark pixels (dark text on a busy/coloured background)
    //   'bright'  keep only bright pixels (white text over a coloured frame)
    function variant(strip, mode) {
        const canvas = document.createElement('canvas');
        canvas.width = strip.width;
        canvas.height = strip.height;
        const ctx = canvas.getContext('2d');
        const img = ctx.createImageData(strip.width, strip.height);
        const d = img.data, g = strip.grey;
        for (let p = 0, i = 0; p < g.length; p++, i += 4) {
            let v = g[p];
            if (mode === 'invert') v = 255 - v;
            else if (mode === 'dark') v = v < 110 ? 0 : 255;
            else if (mode === 'bright') v = v > 165 ? 0 : 255;
            d[i] = d[i + 1] = d[i + 2] = v;
            d[i + 3] = 255;
        }
        ctx.putImageData(img, 0, 0);
        return canvas;
    }

    // OCRs several variants of a strip and keeps the most confident non-empty reading.
    async function readStrip(w, strip, modes, psm) {
        await w.setParameters({ tessedit_pageseg_mode: psm });
        let best = { text: '', confidence: -1 };
        for (const mode of modes) {
            const { data } = await w.recognize(variant(strip, mode));
            const text = data.text.trim();
            const letters = (text.match(/[A-Za-z0-9]/g) || []).length;
            if (letters >= 3 && data.confidence > best.confidence) best = { text, confidence: data.confidence };
            if (best.confidence >= 85) break; // good enough, save time
        }
        return best.text;
    }

    async function ocr(source, card) {
        const w = await ensureWorker();
        const title = await readStrip(w, cropRegion(source, card, TITLE), ['normal', 'invert', 'dark'], '7');
        const bottom = await readStrip(w, cropRegion(source, card, BOTTOM), ['invert', 'bright'], '6');
        const result = { title, bottom };
        window.moxScanner.lastText = result; // handy when diagnosing recognition in the browser console
        return result;
    }

    function positionGuide(video, guide) {
        const r = guideRect(video.videoWidth, video.videoHeight, 0.85);
        guide.style.left = (r.x / video.videoWidth * 100) + '%';
        guide.style.top = (r.y / video.videoHeight * 100) + '%';
        guide.style.width = (r.w / video.videoWidth * 100) + '%';
        guide.style.height = (r.h / video.videoHeight * 100) + '%';
    }

    async function scanVideoFrame(video, dotnet, mySession) {
        if (busy || !video.videoWidth || mySession !== session) return;
        busy = true;
        try {
            const frame = document.createElement('canvas');
            frame.width = video.videoWidth;
            frame.height = video.videoHeight;
            frame.getContext('2d').drawImage(video, 0, 0);
            const text = await ocr(frame, guideRect(frame.width, frame.height, 0.85));
            if (mySession !== session) return;
            const matched = await dotnet.invokeMethodAsync('OnOcrText', text.title, text.bottom, false);
            if (matched) window.moxScanner.stop();
        } catch (e) {
            console.warn('scan failed', e);
        } finally {
            busy = false;
        }
    }

    // The chosen camera is remembered per browser; storage can be unavailable (private mode), so never fail on it.
    const CAMERA_KEY = 'moxScanner.camera';
    function loadCamera() {
        try { return localStorage.getItem(CAMERA_KEY); } catch { return null; }
    }
    function saveCamera(id) {
        try { id ? localStorage.setItem(CAMERA_KEY, id) : localStorage.removeItem(CAMERA_KEY); } catch { }
    }

    window.moxScanner = {
        isCameraAvailable: () => !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia) && window.isSecureContext,

        /**
         * Starts the camera and scans continuously. Returns an error message or null.
         * Uses <deviceId> when given (and remembers it), else the camera used last time, else the back camera.
         */
        start: async function (video, guide, dotnet, deviceId) {
            this.stop();
            const mySession = ++session;
            if (deviceId) saveCamera(deviceId);
            const wanted = deviceId || loadCamera();
            const size = { width: { ideal: 1920 }, height: { ideal: 1080 } };
            try {
                try {
                    stream = await navigator.mediaDevices.getUserMedia({
                        audio: false,
                        video: wanted ? { deviceId: { exact: wanted }, ...size } : { facingMode: { ideal: 'environment' }, ...size },
                    });
                } catch (e) {
                    if (!wanted || (e && e.name === 'NotAllowedError')) throw e;
                    // The remembered camera is gone (unplugged, other device): forget it and use the default.
                    saveCamera(null);
                    stream = await navigator.mediaDevices.getUserMedia({
                        audio: false,
                        video: { facingMode: { ideal: 'environment' }, ...size },
                    });
                }
            } catch (e) {
                return e && e.name === 'NotAllowedError' ? 'Camera access was denied.' : 'No camera available (' + (e && e.message || e) + ').';
            }
            if (mySession !== session) { stream.getTracks().forEach(t => t.stop()); return null; }
            video.srcObject = stream;
            await video.play();
            positionGuide(video, guide);
            ensureWorker().catch(() => { }); // warm up while the user positions the card
            timer = setInterval(() => scanVideoFrame(video, dotnet, mySession), 700);
            return null;
        },

        /**
         * The cameras to choose from and the one in use: { current, cameras: [{ id, label }] }.
         * Labels are only filled in once camera access was granted, so call this after start().
         */
        cameras: async function () {
            const devices = navigator.mediaDevices && navigator.mediaDevices.enumerateDevices
                ? await navigator.mediaDevices.enumerateDevices() : [];
            const cameras = devices.filter(d => d.kind === 'videoinput' && d.deviceId)
                .map((d, i) => ({ id: d.deviceId, label: d.label || ('Camera ' + (i + 1)) }));
            const track = stream && stream.getVideoTracks()[0];
            const current = (track && track.getSettings && track.getSettings().deviceId) || null;
            return { current, cameras };
        },

        /** One immediate scan of the current frame (button). */
        scanNow: function (video, dotnet) { return scanVideoFrame(video, dotnet, session); },

        /** Scans a photo (file input / phone camera app). The card should fill most of the photo. */
        scanFile: async function (input, dotnet) {
            const file = input.files && input.files[0];
            if (!file) return;
            const img = new Image();
            img.src = URL.createObjectURL(file);
            await img.decode();
            try {
                // Photo: assume the card fills the picture (largest card-shaped area, small margin).
                const text = await ocr(img, guideRect(img.naturalWidth, img.naturalHeight, 0.98));
                await dotnet.invokeMethodAsync('OnOcrText', text.title, text.bottom, true);
            } finally {
                URL.revokeObjectURL(img.src);
                input.value = '';
            }
        },

        stop: function () {
            session++;
            if (timer) { clearInterval(timer); timer = null; }
            if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
        },
    };
})();
