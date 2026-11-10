// Starts only the explicitly supplied isolated editor and injects its test DLLs.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <filesystem>
#include <string>

static bool Inject(HANDLE process, const wchar_t* dll) {
    const SIZE_T bytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return false;
    bool ok = WriteProcessMemory(process, remote, dll, bytes, nullptr) != FALSE;
    HANDLE thread = ok ? CreateRemoteThread(process, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW")),
        remote, 0, nullptr) : nullptr;
    DWORD code = 0;
    if (thread) {
        ok = WaitForSingleObject(thread, 30000) == WAIT_OBJECT_0
            && GetExitCodeThread(thread, &code) && code;
        CloseHandle(thread);
    } else ok = false;
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return ok;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 4) { fwprintf(stderr, L"Usage: driver <isolated editor.exe> <editor.dll> <probe.dll>\n"); return 2; }
    STARTUPINFOW startup{sizeof(startup)};
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION process{};
    std::wstring command = L"\"" + std::wstring(argv[1]) + L"\" -log";
    auto directory = std::filesystem::path(argv[1]).parent_path().wstring();
    if (!CreateProcessW(argv[1], command.data(), nullptr, nullptr, FALSE,
        CREATE_SUSPENDED, nullptr, directory.c_str(), &startup, &process)) {
        fprintf(stderr, "CreateProcess failed: %lu\n", GetLastError()); return 2;
    }
    bool ok = Inject(process.hProcess, argv[2]) && Inject(process.hProcess, argv[3]);
    if (!ok) {
        fprintf(stderr, "Injection failed: %lu\n", GetLastError());
        TerminateProcess(process.hProcess, 2);
    } else {
        printf("%lu\n", process.dwProcessId);
        ResumeThread(process.hThread);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return ok ? 0 : 2;
}
