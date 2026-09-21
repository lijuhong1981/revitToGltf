// 绕过 EndOffset 的递归解析,直接扫描 "PolygonVertexIndex"/"Vertices" 节点并读取其后数组,
// 统计 FBX 的三角形数、顶点数、几何/模型/材质对象数。用于和 glTF 对比。
const fs = require('fs');
const zlib = require('zlib');

const path = process.argv[2];
const buf = fs.readFileSync(path);

function findAll(name) {
  const nb = Buffer.from(name);
  const idx = [];
  let p = buf.indexOf(nb);
  while (p !== -1) { idx.push(p); p = buf.indexOf(nb, p + 1); }
  return idx;
}

// 读取紧跟 name 后面的数组头: type byte + count + encoding + compressedLen
function readArrayHeader(nameEnd) {
  const type = String.fromCharCode(buf[nameEnd]); // 'i','d','f','l','b'
  const count = buf.readUInt32LE(nameEnd + 1);
  const encoding = buf.readUInt32LE(nameEnd + 5);
  const compressedLen = buf.readUInt32LE(nameEnd + 9);
  return { type, count, encoding, compressedLen, dataStart: nameEnd + 13 };
}

function arrayData(a) {
  if (a.encoding === 0) return buf.slice(a.dataStart, a.dataStart + a.count * ({b:1,f:4,d:8,i:4,l:8}[a.type]));
  return zlib.inflateSync(buf.slice(a.dataStart, a.dataStart + a.compressedLen));
}

function countTriangles(a) {
  const data = arrayData(a);
  const es = {b:1,f:4,d:8,i:4,l:8}[a.type];
  let tris = 0, polys = 0, cur = 0;
  let triPolys = 0, quadPolys = 0, nGon = 0;
  for (let i = 0; i < a.count; i++) {
    const v = data.readInt32LE(i * es);
    if (v < 0) { const verts = cur + 1; if (verts >= 3) tris += verts - 2; polys++; if (verts===3) triPolys++; else if (verts===4) quadPolys++; else nGon++; cur = 0; }
    else cur++;
  }
  return { tris, polys, triPolys, quadPolys, nGon };
}

const pvi = findAll('PolygonVertexIndex');
let totalIndices = 0, totalTris = 0, totalPolys = 0;
let totalTriPolys = 0, totalQuadPolys = 0, totalNGon = 0;
let pviCount = 0;
for (const off of pvi) {
  // 校验: 前一字节应为 NameLen(18),后一字节应为属性类型 'i'(0x69)
  if (buf[off - 1] !== 18) continue;      // NameLen = len("PolygonVertexIndex")=18
  const type = buf[off + 18];
  if (type !== 0x69 /*'i'*/) continue;    // 只接受 int 数组
  const a = readArrayHeader(off + 18);
  if (a.count === 0 || a.count > 1e9) continue;
  totalIndices += a.count;
  const r = countTriangles(a);
  totalTris += r.tris;
  totalPolys += r.polys;
  totalTriPolys += r.triPolys;
  totalQuadPolys += r.quadPolys;
  totalNGon += r.nGon;
  pviCount++;
}

const verts = findAll('Vertices');
let totalVerts = 0, vertCount = 0;
for (const off of verts) {
  if (buf[off - 1] !== 8) continue;       // NameLen = len("Vertices")=8
  const type = buf[off + 8];
  if (type !== 0x64 /*'d'*/) continue;    // double 数组
  const a = readArrayHeader(off + 8);
  if (a.count === 0 || a.count > 1e9) continue;
  totalVerts += a.count;   // 顶点坐标个数 = 顶点数 * 3
  vertCount++;
}

// 对象计数: 找 Objects 段里 Geometry/Model/Material 节点 (NameLen 前缀)
function countNodes(name) {
  const n = Buffer.from(name);
  let cnt = 0, p = buf.indexOf(n);
  while (p !== -1) {
    if (buf[p - 1] === name.length) cnt++;
    p = buf.indexOf(n, p + 1);
  }
  return cnt;
}

console.log('PolygonVertexIndex 节点数:', pviCount);
console.log('索引总数:', totalIndices);
console.log('多边形数:', totalPolys, '= 三角面', totalTriPolys, '+ 四边面', totalQuadPolys, '+ N边形', totalNGon);
console.log('三角形总数:', totalTris);
console.log('Vertices 节点数:', vertCount);
console.log('顶点坐标数(未去重):', totalVerts, '= 顶点', (totalVerts/3).toFixed(0));
console.log('Geometry 节点数:', countNodes('Geometry'));
console.log('Model 节点数:', countNodes('Model'));
