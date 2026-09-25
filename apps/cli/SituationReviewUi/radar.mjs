// Scene coordinates and heading vectors already use screen axes. Never flip Y again.
export function point(position, size = 1024) {
  return position && Number.isFinite(position.x) && Number.isFinite(position.y) ? { x: position.x * size, y: position.y * size } : null;
}
export function headingEnd(position, heading, length = 30) {
  const p = point(position);
  return p && heading && Number.isFinite(heading.x) && Number.isFinite(heading.y) ? { x: p.x + heading.x * length, y: p.y + heading.y * length } : null;
}
export function drawRadar(canvas, scene, background) {
  const c = canvas.getContext('2d'), size = 1024;
  c.clearRect(0, 0, size, size); c.fillStyle = '#111923'; c.fillRect(0, 0, size, size);
  if (background) { c.globalAlpha = .8; c.drawImage(background, 0, 0, size, size); c.globalAlpha = 1; }
  c.strokeStyle = '#465769'; c.lineWidth = 1;
  for (let x = 0; x <= size; x += size / 10) { c.beginPath(); c.moveTo(x, 0); c.lineTo(x, size); c.stroke(); c.beginPath(); c.moveTo(0, x); c.lineTo(size, x); c.stroke(); }
  c.font = '18px sans-serif'; c.fillStyle = '#dce4ed'; c.fillText('0,0', 8, 23); c.fillText('1,1', 980, 1015);
  const color = side => side === 'T' ? '#ffcb69' : side === 'CT' ? '#78c9ff' : '#d4dce3';
  const path = (points, stroke) => {
    c.strokeStyle = stroke; c.lineWidth = 4; c.beginPath(); let started = false;
    for (const value of points ?? []) { const p = point(value.position ?? value); if (!p) { started = false; continue; } if (!started) c.moveTo(p.x, p.y); else c.lineTo(p.x, p.y); started = true; } c.stroke();
  };
  for (const effect of scene.effects ?? []) {
    const p = point(effect.position); c.fillStyle = effect.type === 'fire' ? '#ff702c66' : '#d7e0ea55'; c.strokeStyle = effect.type === 'fire' ? '#ff9658' : '#d7e0ea'; c.lineWidth = 3;
    if (effect.area?.length >= 3 && effect.area.every(v => point(v))) { c.beginPath(); effect.area.forEach((v, i) => { const q = point(v); i ? c.lineTo(q.x, q.y) : c.moveTo(q.x, q.y); }); c.closePath(); c.fill(); c.stroke(); }
    else if (p && Number.isFinite(effect.radius)) { c.beginPath(); c.arc(p.x, p.y, Math.max(0, effect.radius * size), 0, Math.PI * 2); c.fill(); c.stroke(); }
    if (p) { c.fillStyle = '#fff'; c.fillText(effect.type, p.x + 10, p.y - 10); }
  }
  for (const utility of scene.utilities ?? []) {
    path(utility.trajectory?.points, color(utility.side)); const p = point(utility.position);
    if (p) { c.fillStyle = color(utility.side); c.fillRect(p.x - 5, p.y - 5, 10, 10); c.fillText(utility.type, p.x + 9, p.y - 8); }
  }
  for (const player of scene.players ?? []) {
    path(player.trajectory?.points, color(player.side)); const p = point(player.position); if (!p) continue;
    const end = headingEnd(player.position, player.heading); c.strokeStyle = color(player.side); c.lineWidth = 5;
    if (end) { c.beginPath(); c.moveTo(p.x, p.y); c.lineTo(end.x, end.y); c.stroke(); const angle = Math.atan2(end.y - p.y, end.x - p.x); c.beginPath(); c.moveTo(end.x, end.y); c.lineTo(end.x - 10 * Math.cos(angle - .5), end.y - 10 * Math.sin(angle - .5)); c.moveTo(end.x, end.y); c.lineTo(end.x - 10 * Math.cos(angle + .5), end.y - 10 * Math.sin(angle + .5)); c.stroke(); }
    c.fillStyle = color(player.side); c.beginPath(); c.arc(p.x, p.y, 10, 0, Math.PI * 2); c.fill();
    c.fillStyle = '#fff'; c.strokeStyle = '#10151d'; c.lineWidth = 5; const label = `${player.slot} ${player.health ?? '?'} HP`; c.strokeText(label, p.x + 14, p.y + 23); c.fillText(label, p.x + 14, p.y + 23);
  }
  const bomb = scene.bomb, p = point(bomb?.position);
  if (p) { c.fillStyle = '#ff637a'; c.fillRect(p.x - 9, p.y - 9, 18, 18); c.font = 'bold 22px sans-serif'; c.strokeStyle = '#10151d'; c.lineWidth = 5; c.strokeText('C4', p.x + 12, p.y - 12); c.fillText('C4', p.x + 12, p.y - 12); }
}
