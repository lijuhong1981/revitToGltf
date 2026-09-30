# revitToGltf 的 Draco 编码包装：静态链接 draco，产出单文件 DracoWrapper.dll (x64)。
# 用法：
#   cmake -S native -B native/build -G "Visual Studio 16 2019" -A x64
#   cmake --build native/build --config Release --target DracoWrapper
cmake_minimum_required(VERSION 3.16)
project(revitToGltfDraco CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# 静态链接 CRT(/MT)：dll 自包含，目标机器无需安装 VC 运行库
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

# draco 静态库（不构建其工具/测试/安装目标）
add_subdirectory(draco EXCLUDE_FROM_ALL)

add_library(DracoWrapper SHARED DracoWrapper.cpp)
target_include_directories(DracoWrapper PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/draco/src
    ${CMAKE_BINARY_DIR})   # 生成文件 build/draco/draco_features.h，include 形如 draco/draco_features.h
target_compile_options(DracoWrapper PRIVATE /utf-8)   # 源码为 UTF-8(含中文注释)
target_link_libraries(DracoWrapper PRIVATE draco)
# 只导出 DracoEncodeMesh，缩小 dll 体积并避免符号冲突
target_compile_definitions(DracoWrapper PRIVATE DRACO_WRAPPER_EXPORTS)
