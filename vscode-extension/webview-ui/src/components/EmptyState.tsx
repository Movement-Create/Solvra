import { useEffect, useRef } from 'react';
import { OrbitLogo } from './Icon';

const SUGGESTIONS = [
  'Explain how this project is structured',
  'Find and fix a bug in the open file',
  'Write tests for the code I selected',
];

export function EmptyState({ onPick, workspaceName }: { onPick: (text: string) => void; workspaceName: string | null }) {
  return (
    <div className="empty">
      <div className="logo-wrap">
        <Glow />
        <OrbitLogo size={44} className="logo" />
      </div>
      <h1 className="empty-title">What should we work on{workspaceName ? ` in ${workspaceName}` : ''}?</h1>
      <div className="suggestions">
        {SUGGESTIONS.map((s) => <button key={s} className="suggestion" onClick={() => onPick(s)}>{s}</button>)}
      </div>
    </div>
  );
}

const VERT = `attribute vec2 p; varying vec2 uv; void main(){ uv = p * 0.5 + 0.5; gl_Position = vec4(p, 0.0, 1.0); }`;
const FRAG = `precision mediump float; varying vec2 uv; uniform float t; uniform vec3 c;
void main(){
  vec2 q = uv - 0.5;
  float a = atan(q.y, q.x);
  float r = length(q) * (1.0 + 0.06 * sin(a * 3.0 + t * 0.6) + 0.04 * sin(a * 5.0 - t * 0.4));
  float g = smoothstep(0.5, 0.0, r);
  gl_FragColor = vec4(c, g * g * 0.55);
}`;

/** A soft slate glow behind the logo. Runs only while on screen; static under reduced motion. */
function Glow() {
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const el = canvas.current;
    const gl = el?.getContext('webgl', { premultipliedAlpha: false, alpha: true, antialias: true });
    if (!el || !gl) return;
    const shader = (type: number, src: string) => { const s = gl.createShader(type)!; gl.shaderSource(s, src); gl.compileShader(s); return s; };
    const prog = gl.createProgram()!;
    gl.attachShader(prog, shader(gl.VERTEX_SHADER, VERT));
    gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return;
    gl.useProgram(prog);
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    const uT = gl.getUniformLocation(prog, 't');
    const uC = gl.getUniformLocation(prog, 'c');
    const accent = () => {
      const v = getComputedStyle(document.body).getPropertyValue('--accent').trim();
      const m = /^#([0-9a-f]{6})$/i.exec(v);
      const n = m ? parseInt(m[1], 16) : 0x5f6f8f;
      return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    };
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
    let raf = 0, visible = true, start = performance.now();
    const draw = (now: number) => {
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      if (el.width !== el.clientWidth * dpr) { el.width = el.clientWidth * dpr; el.height = el.clientHeight * dpr; }
      gl.viewport(0, 0, el.width, el.height);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.uniform1f(uT, (now - start) / 1000);
      gl.uniform3fv(uC, accent());
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    };
    const loop = (now: number) => { draw(now); if (visible && !reduced.matches && !document.hidden) raf = requestAnimationFrame(loop); };
    const restart = () => { cancelAnimationFrame(raf); if (visible && !reduced.matches && !document.hidden) raf = requestAnimationFrame(loop); else draw(start); };
    const io = new IntersectionObserver(([e]) => { visible = e.isIntersecting; restart(); });
    io.observe(el);
    document.addEventListener('visibilitychange', restart);
    reduced.addEventListener('change', restart);
    restart();
    return () => {
      cancelAnimationFrame(raf);
      io.disconnect();
      document.removeEventListener('visibilitychange', restart);
      reduced.removeEventListener('change', restart);
      gl.getExtension('WEBGL_lose_context')?.loseContext();
    };
  }, []);
  return <canvas ref={canvas} className="glow" aria-hidden="true" />;
}
