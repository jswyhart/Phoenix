// The playing video, drawn inside the window: the helper sends each frame as planar YUV over a local socket and a
// small WebGL program turns it into the picture, so the app's own controls can sit over it.
import { host, on } from "./api";

const VERTEX = `#version 300 es
in vec2 pos;
uniform vec2 crop;
out vec2 uv;
void main() { uv = vec2((pos.x + 1.0) * 0.5 * crop.x, (1.0 - pos.y) * 0.5 * crop.y); gl_Position = vec4(pos, 0.0, 1.0); }`;

// studio-range YUV to RGB: BT.709 for HD pictures, BT.601 for smaller ones
const FRAGMENT = `#version 300 es
precision mediump float;
in vec2 uv;
uniform sampler2D ty, tu, tv;
uniform bool hd;
out vec4 color;
void main() {
  float y = (texture(ty, uv).r - 0.0627) * 1.1644;
  float u = texture(tu, uv).r - 0.5, v = texture(tv, uv).r - 0.5;
  vec3 rgb = hd ? vec3(y + 1.7927 * v, y - 0.2132 * u - 0.5329 * v, y + 2.1124 * u)
                : vec3(y + 1.5960 * v, y - 0.3918 * u - 0.8130 * v, y + 2.0172 * u);
  color = vec4(rgb, 1.0);
}`;

export class VideoView {
  readonly canvas = document.createElement("canvas");
  /** Frames drawn so far, the picture's size, and the average time to draw one (for the self-test and measurement). */
  frames = 0;
  width = 0;
  height = 0;
  drawMs = 0;
  private gl: WebGL2RenderingContext | null = null;
  private tex: WebGLTexture[] = [];
  private crop: WebGLUniformLocation | null = null;
  private hd: WebGLUniformLocation | null = null;
  private socket: WebSocket | null = null;
  private wanted = false;
  private texW = 0;
  private texH = 0;

  constructor(parent: HTMLElement) {
    this.canvas.style.objectFit = "contain";
    parent.append(this.canvas);
    on("video.cleared", () => this.blank());
  }

  /** Connects while a video is playing; lets go of the socket otherwise. */
  setActive(active: boolean) {
    this.wanted = active;
    if (active && !this.socket) void this.connect();
    if (!active && this.socket) { this.socket.close(); this.socket = null; }
  }

  resetStats() { this.frames = 0; this.drawMs = 0; }

  private async connect() {
    let url: string;
    try { url = await host.videoSocket(); } catch { return; }
    if (!this.wanted || this.socket || !url) return;
    const ws = new WebSocket(url);
    ws.binaryType = "arraybuffer";
    this.socket = ws;
    ws.onmessage = e => {
      if (e.data instanceof ArrayBuffer) this.draw(e.data);
      // drawn: ask for the next frame once this one is on screen
      requestAnimationFrame(() => { if (ws.readyState === WebSocket.OPEN) ws.send("k"); });
    };
    ws.onclose = () => {
      if (this.socket === ws) this.socket = null;
      if (this.wanted) setTimeout(() => { if (this.wanted && !this.socket) void this.connect(); }, 500);
    };
  }

  private setup(): WebGL2RenderingContext | null {
    if (this.gl) return this.gl;
    const gl = this.canvas.getContext("webgl2", { alpha: false, antialias: false, depth: false, preserveDrawingBuffer: true, desynchronized: true });
    if (!gl) return null;
    const shader = (type: number, source: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, source);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? "shader");
      return s;
    };
    const program = gl.createProgram()!;
    gl.attachShader(program, shader(gl.VERTEX_SHADER, VERTEX));
    gl.attachShader(program, shader(gl.FRAGMENT_SHADER, FRAGMENT));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? "link");
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const pos = gl.getAttribLocation(program, "pos");
    gl.enableVertexAttribArray(pos);
    gl.vertexAttribPointer(pos, 2, gl.FLOAT, false, 0, 0);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    ["ty", "tu", "tv"].forEach((name, i) => {
      const t = gl.createTexture()!;
      gl.activeTexture(gl.TEXTURE0 + i);
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.uniform1i(gl.getUniformLocation(program, name), i);
      this.tex.push(t);
    });
    this.crop = gl.getUniformLocation(program, "crop");
    this.hd = gl.getUniformLocation(program, "hd");
    return (this.gl = gl);
  }

  private draw(data: ArrayBuffer) {
    const started = performance.now();
    const head = new DataView(data, 0, 16);
    const w = head.getInt32(0, true), h = head.getInt32(4, true), ys = head.getInt32(8, true), cs = head.getInt32(12, true);
    const ch = h >> 1, ySize = ys * h, cSize = cs * ch;
    if (w <= 0 || h <= 0 || data.byteLength < 16 + ySize + 2 * cSize) return;
    let gl: WebGL2RenderingContext | null;
    try { gl = this.setup(); } catch (e) { console.error(e); return; }
    if (!gl) return;
    if (this.canvas.width !== w || this.canvas.height !== h) { this.canvas.width = w; this.canvas.height = h; }
    gl.viewport(0, 0, w, h);
    const fresh = this.texW !== ys || this.texH !== h;
    this.texW = ys;
    this.texH = h;
    const plane = (unit: number, width: number, height: number, offset: number, size: number) => {
      gl!.activeTexture(gl!.TEXTURE0 + unit);
      const bytes = new Uint8Array(data, offset, size);
      if (fresh) gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.R8, width, height, 0, gl!.RED, gl!.UNSIGNED_BYTE, bytes);
      else gl!.texSubImage2D(gl!.TEXTURE_2D, 0, 0, 0, width, height, gl!.RED, gl!.UNSIGNED_BYTE, bytes);
    };
    plane(0, ys, h, 16, ySize);
    plane(1, cs, ch, 16 + ySize, cSize);
    plane(2, cs, ch, 16 + ySize + cSize, cSize);
    // rows are padded beyond the picture's width: show only the picture (half a texel in, so the edge doesn't bleed)
    gl.uniform2f(this.crop, (w - 0.5) / ys, 1);
    gl.uniform1i(this.hd, h >= 720 ? 1 : 0);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    this.width = w;
    this.height = h;
    this.frames++;
    this.drawMs += performance.now() - started;
  }

  /** The colour in the middle of the picture as drawn (self-test). */
  centre(): [number, number, number] {
    const gl = this.gl;
    if (!gl) return [0, 0, 0];
    const px = new Uint8Array(4);
    gl.readPixels(this.canvas.width >> 1, this.canvas.height >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
    return [px[0], px[1], px[2]];
  }

  private blank() {
    const gl = this.gl;
    if (!gl) return;
    gl.clearColor(0, 0, 0, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
  }
}
