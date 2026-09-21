// 极简静态服务器：用于本地打开 model-compare.html（file:// 会被 CORS 拦截，必须走 HTTP）
// 用法：node tools/serve.js [端口]
const http = require('http');
const fs = require('fs');
const path = require('path');
const url = require('url');

const ROOT = path.resolve(__dirname, '..');   // revitToGltf 根目录
const PORT = parseInt(process.argv[2] || '8080', 10);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js':   'text/javascript; charset=utf-8',
  '.css':  'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.gltf': 'model/gltf+json',
  '.bin':  'application/octet-stream',
  '.fbx':  'application/octet-stream',
  '.png':  'image/png',
  '.jpg':  'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.rvt':  'application/octet-stream'
};

const server = http.createServer((req, res) => {
  const pathname = decodeURIComponent(url.parse(req.url).pathname);
  let filePath = path.join(ROOT, pathname);
  if (pathname.endsWith('/')) filePath = path.join(filePath, 'index.html');

  // 防目录穿越
  if (!filePath.startsWith(ROOT)) {
    res.writeHead(403).end('Forbidden');
    return;
  }

  fs.stat(filePath, (err, st) => {
    if (err || !st.isFile()) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('404 Not Found: ' + pathname);
      return;
    }
    const ext = path.extname(filePath).toLowerCase();
    res.writeHead(200, {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Content-Length': st.size,
      'Cache-Control': 'no-cache'
    });
    if (req.method === 'HEAD') { res.end(); return; }
    fs.createReadStream(filePath).pipe(res);
  });
});

server.listen(PORT, () => {
  console.log('服务已启动，根目录: ' + ROOT);
  console.log('对比页面: http://localhost:' + PORT + '/tools/model-compare.html');
});
