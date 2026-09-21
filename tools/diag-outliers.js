// 诊断:定位用户报告的"跑到车站外"的构件,看它们的世界坐标与是否实例化
const fs = require('fs');
const path = require('path');

const gltfPath = path.resolve(__dirname, '../output/ljdd_fine_1.00.gltf');
const gltf = JSON.parse(fs.readFileSync(gltfPath, 'utf8'));
const bin = fs.readFileSync(path.join(path.dirname(gltfPath), gltf.buffers[0].uri));

function accessorBounds(accIdx) {
  const acc = gltf.accessors[accIdx];
  if (acc.min && acc.max) return { min: acc.min.slice(), max: acc.max.slice() };
  const bv = gltf.bufferViews[acc.bufferView];
  const off = (bv.byteOffset || 0) + (acc.byteOffset || 0);
  let mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i < acc.count; i++) for (let k = 0; k < 3; k++) {
    const v = bin.readFloatLE(off + (i * 3 + k) * 4);
    if (v < mn[k]) mn[k] = v; if (v > mx[k]) mx[k] = v;
  }
  return { min: mn, max: mx };
}
function center(b) { return b.min.map((v, k) => (v + b.max[k]) / 2); }
function matMulVec(m, v) {
  return [m[0]*v[0]+m[4]*v[1]+m[8]*v[2]+m[12],
          m[1]*v[0]+m[5]*v[1]+m[9]*v[2]+m[13],
          m[2]*v[0]+m[6]*v[1]+m[10]*v[2]+m[14]];
}

const meshBounds = gltf.meshes.map(m => accessorBounds(m.primitives[0].attributes.POSITION));
const meshRefCount = new Array(gltf.meshes.length).fill(0);
for (const n of gltf.nodes) if (n.mesh != null) meshRefCount[n.mesh]++;

// 每个节点的世界位置(取网格局部包围盒中心变换后)
const nodeWorld = gltf.nodes.map(n => {
  if (n.mesh == null) return null;
  const m = n.matrix || [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  return matMulVec(m, center(meshBounds[n.mesh]));
});

// 整体包围盒
let wmn = [Infinity, Infinity, Infinity], wmx = [-Infinity, -Infinity, -Infinity];
for (const w of nodeWorld) if (w) for (let k = 0; k < 3; k++) {
  if (w[k] < wmn[k]) wmn[k] = w[k]; if (w[k] > wmx[k]) wmx[k] = w[k];
}
console.log('整体包围盒 min=(' + wmn.map(v=>v.toFixed(1)).join(', ') + ') max=(' + wmx.map(v=>v.toFixed(1)).join(', ') + ')');

// 用户报告的构件
const targets = ['柱Z6', 'JZ-墙体构造柱', '柱AZ2', '扶梯'];
console.log('\n=== 用户报告的构件 ===');
for (const t of targets) {
  let cnt = 0;
  for (let i = 0; i < gltf.nodes.length; i++) {
    const n = gltf.nodes[i];
    if (n.mesh == null) continue;
    const name = (n.extras && n.extras.name) || n.name || '';
    if (!name.includes(t)) continue;
    cnt++;
    if (cnt <= 8) {
      const w = nodeWorld[i];
      console.log(`  "${name}" mesh[${n.mesh}] refs=${meshRefCount[n.mesh]} 世界=(${w.map(v=>v.toFixed(1)).join(', ')})`);
    }
  }
  console.log(`  [${t}] 共 ${cnt} 个`);
}

// 找出所有"世界位置偏离车站主簇"的节点(简单启发式:离包围盒中心 > 某阈值 或 在包围盒外)
const cx = (wmn[0]+wmx[0])/2, cy = (wmn[1]+wmx[1])/2, cz = (wmn[2]+wmx[2])/2;
const outliers = [];
for (let i = 0; i < gltf.nodes.length; i++) {
  const n = gltf.nodes[i];
  if (n.mesh == null) continue;
  const w = nodeWorld[i];
  const d = Math.hypot(w[0]-cx, w[1]-cy, w[2]-cz);
  outliers.push({ i, name: (n.extras&&n.extras.name)||n.name||'', d, w, refs: meshRefCount[n.mesh], shared: meshRefCount[n.mesh]>1 });
}
outliers.sort((a,b)=>b.d-a.d);
console.log('\n=== 离包围盒中心最远的 30 个构件 (世界位置) ===');
for (const o of outliers.slice(0, 30))
  console.log(`  "${o.name}" d=${o.d.toFixed(1)}m 世界=(${o.w.map(v=>v.toFixed(1)).join(', ')}) ${o.shared?'共享':'唯一'}(refs=${o.refs})`);
