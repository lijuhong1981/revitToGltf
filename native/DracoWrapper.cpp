// DracoWrapper.cpp — revitToGltf 的 Draco 编码 C 包装。
// 把三角网格(POSITION/NORMAL/TEXCOORD/索引数组)编码为 KHR_draco_mesh_compression
// 扩展所需的 Draco(.drc)字节流；导出单个 extern "C" 函数供 C# P/Invoke。
// 量化位数等教训取自 modelTo3DTiles 实践：位置 16bit(整模型大跨度世界坐标)、
// UV 14bit(真实世界平铺 UV 跨度大)，低于该值会出现接缝错位与贴图扭曲。
#include <cstring>
#include <cstdint>
#include "draco/attributes/geometry_attribute.h"
#include "draco/compression/encode.h"
#include "draco/mesh/mesh.h"

extern "C" {

// 返回写入 outBuffer 的字节数；失败(含缓冲不足)返回 0。
__declspec(dllexport) int DracoEncodeMesh(
    const float* positions, int vertexCount,
    const float* normals,               // 可为 NULL
    const float* uvs,                   // 可为 NULL
    const unsigned int* indices, int indexCount,   // 三角形索引，3 的倍数
    int compressionLevel,               // 0~10，越大体积越小、耗时越长
    int quantPosition,                  // 位置量化位数，0 = 不量化
    int quantNormal,
    int quantTexcoord,
    unsigned char* outBuffer, int outCapacity)
{
    if (positions == NULL || vertexCount <= 0 || indices == NULL || indexCount < 3 ||
        outBuffer == NULL || outCapacity <= 0)
        return 0;

    try {
        draco::Mesh mesh;
        mesh.set_num_points(static_cast<uint32_t>(vertexCount));

        // 属性按下标顺序添加：0=POSITION 1=NORMAL 2=TEXCOORD，
        // 与 glTF 扩展里 attributes 映射 {POSITION:0, NORMAL:1, TEXCOORD_0:2} 严格对应
        draco::GeometryAttribute posAtt;
        posAtt.Init(draco::GeometryAttribute::POSITION, NULL, 3, draco::DT_FLOAT32,
                    false, sizeof(float) * 3, 0);
        const int posId = mesh.AddAttribute(posAtt, true, vertexCount);
        int normalId = -1, uvId = -1;
        if (normals != NULL) {
            draco::GeometryAttribute att;
            att.Init(draco::GeometryAttribute::NORMAL, NULL, 3, draco::DT_FLOAT32,
                     false, sizeof(float) * 3, 0);
            normalId = mesh.AddAttribute(att, true, vertexCount);
        }
        if (uvs != NULL) {
            draco::GeometryAttribute att;
            att.Init(draco::GeometryAttribute::TEX_COORD, NULL, 2, draco::DT_FLOAT32,
                     false, sizeof(float) * 2, 0);
            uvId = mesh.AddAttribute(att, true, vertexCount);
        }

        for (int i = 0; i < vertexCount; i++) {
            mesh.attribute(posId)->SetAttributeValue(draco::AttributeValueIndex(i),
                                                     positions + i * 3);
            if (normalId >= 0)
                mesh.attribute(normalId)->SetAttributeValue(draco::AttributeValueIndex(i),
                                                            normals + i * 3);
            if (uvId >= 0)
                mesh.attribute(uvId)->SetAttributeValue(draco::AttributeValueIndex(i),
                                                       uvs + i * 2);
        }

        for (int i = 0; i + 2 < indexCount; i += 3) {
            draco::Mesh::Face face = {draco::PointIndex(indices[i]),
                                      draco::PointIndex(indices[i + 1]),
                                      draco::PointIndex(indices[i + 2])};
            mesh.AddFace(face);
        }

        draco::Encoder encoder;
        if (compressionLevel >= 0 && compressionLevel <= 10)
            encoder.SetSpeedOptions(10 - compressionLevel, 10 - compressionLevel);
        if (quantPosition > 0)
            encoder.SetAttributeQuantization(draco::GeometryAttribute::POSITION, quantPosition);
        if (quantNormal > 0)
            encoder.SetAttributeQuantization(draco::GeometryAttribute::NORMAL, quantNormal);
        if (quantTexcoord > 0)
            encoder.SetAttributeQuantization(draco::GeometryAttribute::TEX_COORD, quantTexcoord);

        draco::EncoderBuffer buffer;
        if (!encoder.EncodeMeshToBuffer(mesh, &buffer).ok())
            return 0;
        if (buffer.size() == 0 || static_cast<int>(buffer.size()) > outCapacity)
            return 0;
        memcpy(outBuffer, buffer.data(), buffer.size());
        return static_cast<int>(buffer.size());
    } catch (...) {
        return 0;
    }
}

}  // extern "C"
