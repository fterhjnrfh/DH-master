(() => {
  // 事件委托：仅在容器上监听 pointermove 与 wheel
  const container = document.getElementById('zoom-container');
  if (!container) return;

  // 每个元素的缩放状态
  const states = new WeakMap();
  const clamp = (v, min, max) => Math.min(max, Math.max(min, v));

  // ==== 坐标系绘制（不影响原交互）====
  function drawAxesForCard(card) {
    const canvas = card.querySelector('canvas.axes');
    if (!canvas) return;

    const rect = card.getBoundingClientRect();
    const dpr = Math.max(1, Math.floor(window.devicePixelRatio || 1));
    const width = Math.max(1, Math.round(rect.width));
    const height = Math.max(1, Math.round(rect.height));

    // 高 DPI 清晰度
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = width + 'px';
    canvas.style.height = height + 'px';

    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);

    // 背景网格
    const gridColor = '#223042';
    const axisColor = '#7dd3fc';
    const tickColor = '#94a3b8';
    const labelColor = '#cbd5e1';
    ctx.lineWidth = 1;

    const xStep = Math.max(40, Math.floor(width / 8));
    const yStep = Math.max(30, Math.floor(height / 6));

    ctx.strokeStyle = gridColor;
    for (let x = xStep; x < width; x += xStep) {
      ctx.beginPath();
      ctx.moveTo(x + 0.5, 0.5);
      ctx.lineTo(x + 0.5, height + 0.5);
      ctx.stroke();
    }
    for (let y = yStep; y < height; y += yStep) {
      ctx.beginPath();
      ctx.moveTo(0.5, y + 0.5);
      ctx.lineTo(width + 0.5, y + 0.5);
      ctx.stroke();
    }

    // 坐标轴（左、下）
    ctx.strokeStyle = axisColor;
    ctx.beginPath();
    // Y 轴在左侧
    ctx.moveTo(32.5, 0.5);
    ctx.lineTo(32.5, height - 20.5);
    // X 轴在底部
    ctx.moveTo(32.5, height - 20.5);
    ctx.lineTo(width - 0.5, height - 20.5);
    ctx.stroke();

    // 刻度
    ctx.strokeStyle = tickColor;
    ctx.fillStyle = labelColor;
    ctx.font = '11px system-ui,Segoe UI,Arial';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';

    const xTicks = Math.floor((width - 40) / xStep);
    for (let i = 0; i <= xTicks; i++) {
      const x = 32 + i * xStep;
      ctx.beginPath();
      ctx.moveTo(x + 0.5, height - 22.5);
      ctx.lineTo(x + 0.5, height - 16.5);
      ctx.stroke();
      ctx.fillText(String(i), x, height - 16);
    }

    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    const yTicks = Math.floor((height - 28) / yStep);
    for (let i = 0; i <= yTicks; i++) {
      const y = height - 20 - i * yStep;
      ctx.beginPath();
      ctx.moveTo(30.5, y + 0.5);
      ctx.lineTo(36.5, y + 0.5);
      ctx.stroke();
      ctx.fillText(String(i), 28, y);
    }
  }

  function initAxes() {
    const cards = document.querySelectorAll('.card[data-zoomable]');
    const ro = new ResizeObserver(entries => {
      for (const entry of entries) {
        drawAxesForCard(entry.target);
      }
    });
    cards.forEach(card => {
      drawAxesForCard(card);
      ro.observe(card);
    });
  }

  // DPI自适应：使用CSS像素坐标(clientX/Y + getBoundingClientRect)，无需手动换算
  // 响应式：布局使用CSS Grid，效果随屏幕自动适配

  // 记录最后一次指针位置（用于平滑动画）
  let lastEl = null;
  let lastPos = { x: 0, y: 0 };
  container.addEventListener('pointermove', (e) => {
    const el = e.target.closest('[data-zoomable]');
    if (!el) return;
    const rect = el.getBoundingClientRect();
    lastEl = el;
    lastPos.x = e.clientX - rect.left;
    lastPos.y = e.clientY - rect.top;
  }, { passive: true });

  // 使用 requestAnimationFrame 做平滑插值动画，避免布局抖动
  let rafId = 0;
  const animate = () => {
    rafId = 0;
    let needsNext = false;
    states.forEach((st, el) => {
      const speed = 0.18; // 动画速度（越大越快）
      const delta = st.target - st.scale;
      if (Math.abs(delta) > 0.001) {
        st.scale += delta * speed;
        needsNext = true;
      } else {
        st.scale = st.target;
      }
      const x = st.anchorX ?? el.clientWidth / 2;
      const y = st.anchorY ?? el.clientHeight / 2;

      // transform 仅改变合成层，不触发布局和回流
      el.style.transformOrigin = '0 0';
      el.style.willChange = 'transform';
      el.style.transform = `translate(${x}px, ${y}px) scale(${st.scale}) translate(${-x}px, ${-y}px)`;
    });
    if (needsNext) rafId = requestAnimationFrame(animate);
  };

  // 滚轮缩放：以鼠标所在点为中心；默认 0.5x ~ 3x
  container.addEventListener('wheel', (e) => {
    const el = e.target.closest('[data-zoomable]');
    if (!el) return;
    // 防止页面滚动影响交互
    e.preventDefault();

    const rect = el.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    const st = states.get(el) || { scale: 1, target: 1, anchorX: mx, anchorY: my };
    // deltaY < 0 上滚 => 放大；> 0 下滚 => 缩小
    const factor = e.deltaY < 0 ? 1.15 : 0.87;
    st.target = clamp(st.target * factor, 0.5, 3);
    st.anchorX = mx;
    st.anchorY = my;
    states.set(el, st);

    if (!rafId) rafId = requestAnimationFrame(animate);
  }, { passive: false });

  // 初始化坐标层
  initAxes();
})();