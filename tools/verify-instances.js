// 验证 Symbol.get_Geometry() 修复后:
// 1) 共享网格局部包围盒中心距原点 <10m (修复前最远 265.6m)
// 2) "腋角" 实例的世界位置是否落回车站内 (修复前跑到车站外)
// 3) 整体世界包围盒
const fs = require('fs');
const path = require('path');

const gltfPath = path.resolve(__dirname, '../output/ljdd_fine_1.00.gltf');
const gltf = JSON.parse(fs.readFileSync(gltfPath, 'utf8'));
const bin = fs.readFileSync(path.join(path.dirname(gltfPath), gltf.buffers[0].uri));

function accessorBounds(accessorIndex) {
  const acc = gltf.accessors[accessorIndex];
  const bv = gltf.bufferViews[acc.bufferView];
  const min = acc.min, max = acc.max;
  if (min && max) return { min: min.slice(), max: max.slice() };
  // fallback: 扫顶点
  const off = (bv.byteOffset || 0) + (acc.byteOffset || 0);
  let mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
  const count = acc.count;
  for (let i = 0; i < count; i++) {
    for (let k = 0; k < 3; k++) {
      const v = bin.readFloatLE(off + (i * 3 + k) * 4);
      if (v < mn[k]) mn[k] = v;
      if (v > mx[k]) mx[k] = v;
    }
  }
  return { min: mn, max: mx };
}

function center(b) { return b.min.map((v, k) => (v + b.max[k]) / 2); }
function len(v) { return Math.sqrt(v.reduce((s, x) => s + x * x, 0)); }

// 收集每个 mesh 的局部包围盒 (共享网格在前, unique 在后)
const meshBounds = gltf.meshes.map(m => {
  const prim = m.primitives[0];
  const posAcc = prim.attributes.POSITION;
  return accessorBounds(posAcc);
});

// 共享网格 = 被多个节点引用的 mesh
const meshRefCount = new Array(gltf.meshes.length).fill(0);
for (const n of gltf.nodes) if (n.mesh != null) meshRefCount[n.mesh]++;

const sharedMeshIdx = [];
for (let i = 0; i < gltf.meshes.length; i++) if (meshRefCount[i] > 1) sharedMeshIdx.push(i);

console.log('=== 1) 共享网格局部包围盒中心距原点 (米) ===');
let worst = { d: 0, idx: -1, c: null };
let badCount = 0;
const list = sharedMeshIdx.map(i => {
  const c = center(meshBounds[i]);
  const d = len(c);
  return { i, c, d, refs: meshRefCount[i], name: gltf.meshes[i].name };
}).sort((a, b) => b.d - a.d);
for (const it of list) {
  if (it.d > 10) badCount++;
  if (it.d > worst.d) worst = { d: it.d, idx: it.i, c: it.c };
}
console.log(`共享网格数: ${sharedMeshIdx.length}`);
console.log(`中心距原点 >10m 的共享网格: ${badCount} 个 (修复前 59 个)`);
console.log(`最远: mesh[${worst.idx}] "${gltf.meshes[worst.idx].name}" 中心=${worst.c.map(v=>v.toFixed(2))} 距离=${worst.d.toFixed(2)}m`);
console.log(`前 10 远:`);
for (const it of list.slice(0, 10))
  console.log(`  mesh[${it.i}] "${it.name}" d=${it.d.toFixed(2)}m refs=${it.refs}`);

console.log('\n=== 2) 腋角 实例世界位置 ===');
function matMulVec(m, v) {
  return [
    m[0]*v[0] + m[4]*v[1] + m[8]*v[2] + m[12],
    m[1]*v[0] + m[5]*v[1] + m[9]*v[2] + m[13],
    m[2]*v[0] + m[6]*v[1] + m[10]*v[2] + m[14],
  ];
}
const yjInstances = [];
for (const n of gltf.nodes) {
  if (n.mesh == null) continue;
  const name = (n.extras && n.extras.name) || n.name || '';
  if (name.includes('腋角')) {
    const c = center(meshBounds[n.mesh]);
    const m = n.matrix || [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
    const world = matMulVec(m, c);
    yjInstances.push({ name, world, mesh: n.mesh, refs: meshRefCount[n.mesh] });
  }
}
console.log(`腋角 实例数: ${yjInstances.length} (修复前 17 个)`);
for (const it of yjInstances.slice(0, 20))
  console.log(`  "${it.name}" mesh[${it.mesh}] refs=${it.refs} 世界坐标=(${it.world.map(v=>v.toFixed(2)).join(', ')})`);

console.log('\n=== 3) 整体世界包围盒 (米) ===');
let wmn = [Infinity, Infinity, Infinity], wmx = [-Infinity, -Infinity, -Infinity];
for (const n of gltf.nodes) {
  if (n.mesh == null) continue;
  const b = meshBounds[n.mesh];
  const m = n.matrix || [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  for (const corner of [
    [b.min[0],b.min[1],b.min[2]],[b.max[0],b.min[1],b.min[2]],
    [b.min[0],b.max[1],b.min[2]],[b.max[0],b.max[1],b.min[2]],
    [b.min[0],b.min[1],b.max[2]],[b.max[0],b.min[1],b.max[2]],
    [b.min[0],b.max[1],b.max[2]],[b.max[0],b.max[1],b.max[2]],
  ]) {
    const w = matMulVec(m, corner);
    for (let k = 0; k < 3; k++) { if (w[k] < wmn[k]) wmn[k] = w[k]; if (w[k] > wmx[k]) wmx[k] = w[k]; }
  }
}
const size = wmx.map((v, k) => v - wmn[k]);
console.log(`min=(${wmn.map(v=>v.toFixed(1)).join(', ')})  max=(${wmx.map(v=>v.toFixed(1)).join(', ')})`);
console.log(`尺寸=(${size.map(v=>v.toFixed(1)).join(', ')}) (修复前 317.5×296.7×200.5)`);
