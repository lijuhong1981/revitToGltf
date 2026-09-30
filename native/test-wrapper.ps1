# DracoWrapper.dll smoke test (ASCII only - Windows PowerShell reads no-BOM files as ANSI).
# Usage: powershell -File native/test-wrapper.ps1 [dll path]
param([string]$DllPath = "$PSScriptRoot\build\Release\DracoWrapper.dll")

if (-not (Test-Path $DllPath)) { Write-Output "dll not found: $DllPath"; exit 1 }

$src = @"
using System;
using System.Runtime.InteropServices;

public static class DracoTest {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string f);

    [DllImport("DracoWrapper.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int DracoEncodeMesh(
        float[] positions, int vertexCount,
        float[] normals, float[] uvs,
        uint[] indices, int indexCount,
        int compressionLevel, int quantPosition, int quantNormal, int quantTexcoord,
        byte[] outBuffer, int outCapacity);

    public static string Run(string dir) {
        if (LoadLibrary(dir + "\\DracoWrapper.dll") == IntPtr.Zero)
            return "LoadLibrary failed err=" + Marshal.GetLastWin32Error();

        // 6 vertices, 2 triangles
        float[] pos = { 0,0,0, 1,0,0, 0,1,0, 1,0,0, 1,1,0, 0,1,0 };
        float[] nrm = { 0,0,1, 0,0,1, 0,0,1, 0,0,1, 0,0,1, 0,0,1 };
        float[] uv  = { 0,0, 1,0, 0,1, 1,0, 1,1, 0,1 };
        uint[] idx  = { 0,1,2, 3,4,5 };

        int raw = pos.Length*4 + nrm.Length*4 + uv.Length*4 + idx.Length*4;
        var buf = new byte[raw + 4096];
        int size = DracoEncodeMesh(pos, 6, nrm, uv, idx, 6, 7, 16, 10, 14, buf, buf.Length);
        if (size <= 0) return "encode failed, return " + size;

        byte[] magic = new byte[5];
        Array.Copy(buf, 0, magic, 0, 5);
        string m = System.Text.Encoding.ASCII.GetString(magic);

        int size2 = DracoEncodeMesh(pos, 6, null, null, idx, 6, 7, 16, 10, 14, buf, buf.Length);
        int size3 = DracoEncodeMesh(null, 0, null, null, null, 0, 7, 16, 10, 14, buf, buf.Length);

        return "OK magic=" + m + " raw=" + raw + "B encoded=" + size + "B | noNormalUv=" + size2 + "B | invalidInput=" + size3;
    }
}
"@
Add-Type -TypeDefinition $src
Write-Output ([DracoTest]::Run((Split-Path $DllPath)))
