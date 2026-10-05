#ifndef UNICODE
#define UNICODE
#endif

#include <windows.h>
#include <shlwapi.h>
#include <winternl.h>

#pragma comment(lib, "shlwapi.lib")

typedef LONG (WINAPI *ZxRtlGetVersionFn)(PRTL_OSVERSIONINFOW);

static BOOL IsSupportedOS(void)
{
    // 优先 RtlGetVersion：不受 app manifest 兼容层影响（zig 编译的 exe 无 manifest 时，
    // GetVersionExW 会把 Win10/11 误报成 6.x，导致误弹"操作系统不支持"）。
    HMODULE nt = GetModuleHandleW(L"ntdll.dll");
    if (nt) {
        ZxRtlGetVersionFn fn = (ZxRtlGetVersionFn)(void *)GetProcAddress(nt, "RtlGetVersion");
        if (fn) {
            RTL_OSVERSIONINFOW vi2 = { sizeof(vi2) };
            if (fn(&vi2) == 0 && vi2.dwMajorVersion > 0)
                return vi2.dwMajorVersion > 10 || (vi2.dwMajorVersion == 10 && vi2.dwBuildNumber >= 19041);
        }
    }
    // 回退：拿不到版本就别拦（宁放行不误杀）
    OSVERSIONINFOW vi = { sizeof(vi) };
    if (!GetVersionExW(&vi)) return TRUE;
    return vi.dwMajorVersion > 10 || (vi.dwMajorVersion == 10 && vi.dwBuildNumber >= 19041);
}

static BOOL LaunchExe(const WCHAR *exe, const WCHAR *workDir, const WCHAR *parameters, DWORD *exitCode)
{
    SHELLEXECUTEINFOW sei = { sizeof(sei) };
    sei.fMask = SEE_MASK_NOASYNC | SEE_MASK_NOCLOSEPROCESS;
    sei.lpVerb = L"open";
    sei.lpFile = exe;
    sei.lpDirectory = workDir;
    sei.lpParameters = parameters;
    sei.nShow = SW_SHOWNORMAL;

    if (!ShellExecuteExW(&sei)) return FALSE;

    if (sei.hProcess) {
        WaitForSingleObject(sei.hProcess, 15000);
        if (exitCode) GetExitCodeProcess(sei.hProcess, exitCode);
        CloseHandle(sei.hProcess);
    }
    return TRUE;
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, PWSTR lpCmdLine, int nCmdShow)
{
    WCHAR exePath[MAX_PATH];
    GetModuleFileNameW(NULL, exePath, MAX_PATH);

    WCHAR dir[MAX_PATH];
    wcscpy_s(dir, _countof(dir), exePath);
    PathRemoveFileSpecW(dir);

    WCHAR mainExe[MAX_PATH];
    PathCombineW(mainExe, dir, L"src\\TubaWinUi3.exe");

    WCHAR compatExe[MAX_PATH];
    PathCombineW(compatExe, dir, L"\u56FE\u5427\u5DE5\u5177\u7BB1Winui3\u517C\u5BB9\u7248.exe");

    if (!IsSupportedOS()) {
        if (GetFileAttributesW(compatExe) != INVALID_FILE_ATTRIBUTES) {
            LaunchExe(compatExe, dir, lpCmdLine, NULL);
            return 0;
        }
        MessageBoxW(NULL,
            L"当前操作系统不支持 WinUI 3 版本（需要 Windows 10 2004 / build 19041 及以上）。\n"
            L"兼容版程序也未找到，无法启动。",
            L"枕星 AI 助手", MB_OK | MB_ICONERROR);
        return 1;
    }

    if (GetFileAttributesW(mainExe) == INVALID_FILE_ATTRIBUTES) {
        if (GetFileAttributesW(compatExe) != INVALID_FILE_ATTRIBUTES) {
            LaunchExe(compatExe, dir, lpCmdLine, NULL);
            return 0;
        }
        WCHAR msg[MAX_PATH + 64];
        wsprintfW(msg, L"找不到程序文件：\n%s", mainExe);
        MessageBoxW(NULL, msg, L"枕星 AI 助手", MB_OK | MB_ICONERROR);
        return 1;
    }

    DWORD exitCode = 0;
    if (!LaunchExe(mainExe, dir, lpCmdLine, &exitCode)) {
        DWORD err = GetLastError();
        if (GetFileAttributesW(compatExe) != INVALID_FILE_ATTRIBUTES) {
            LaunchExe(compatExe, dir, lpCmdLine, NULL);
            return 0;
        }
        WCHAR msg[128];
        wsprintfW(msg, L"启动失败，错误代码：%lu", err);
        MessageBoxW(NULL, msg, L"枕星 AI 助手", MB_OK | MB_ICONERROR);
        return 1;
    }

    if (exitCode != 0 && exitCode != STILL_ACTIVE) {
        if (GetFileAttributesW(compatExe) != INVALID_FILE_ATTRIBUTES) {
            LaunchExe(compatExe, dir, lpCmdLine, NULL);
            return 0;
        }
    }

    return 0;
}