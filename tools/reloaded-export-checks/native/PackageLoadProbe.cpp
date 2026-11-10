// Injected only into the disposable SCCT editor launched by Check-NativePackages.ps1.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <share.h>
#include <string>

namespace {
std::filesystem::path directory;
HWND frame;
HHOOK hook;
FILE* report;
constexpr UINT runMessage = WM_APP + 0x361;
constexpr uintptr_t editorAddress = 0x1165DFA0, outputAddress = 0x115BEFB0;

int Exec(const std::string& command) {
    fprintf(report, "exec %s\n", command.c_str()); fflush(report);
    auto editor = *reinterpret_cast<unsigned char**>(editorAddress);
    auto output = *reinterpret_cast<void**>(outputAddress);
    if (!editor || !output) return 0;
    void* exec = editor + 0x28;
    using Fn = int(__thiscall*)(void*, const char*, void*);
    return reinterpret_cast<Fn>((*reinterpret_cast<void***>(exec))[0])(exec, command.c_str(), output);
}

void Run() {
    report = _fsopen((directory / "package-load-report.txt").string().c_str(), "w", _SH_DENYNO);
    if (!report) return;
    std::ifstream commands(directory / "package-load-commands.txt");
    std::string command;
    while (std::getline(commands, command)) {
        if (!command.empty() && command.back() == '\r') command.pop_back();
        if (command.empty()) continue;
        if (!Exec(command)) { fputs("FAIL Native command was refused.\n", report); fflush(report); return; }
    }
    auto editor = *reinterpret_cast<unsigned char**>(editorAddress);
    auto level = *reinterpret_cast<unsigned char**>(editor + 0x130);
    auto model = level ? *reinterpret_cast<unsigned char**>(level + 0x13C) : nullptr;
    int actors = level ? *reinterpret_cast<int*>(level + 0x30) : 0;
    int surfaces = model ? *reinterpret_cast<int*>(model + 0x98) : 0;
    fprintf(report, "loaded actors=%d surfaces=%d\n", actors, surfaces);
    fputs(actors > 1 && surfaces > 0 ? "PASS Native interface and map loaded through their original extensions.\n"
        : "FAIL The loaded map has no actors or geometry.\n", report);
    fflush(report);
}

LRESULT CALLBACK Message(int code, WPARAM w, LPARAM l) {
    if (code >= 0 && reinterpret_cast<CWPSTRUCT*>(l)->message == runMessage) {
        UnhookWindowsHookEx(hook); hook = nullptr; Run();
    }
    return CallNextHookEx(hook, code, w, l);
}

BOOL CALLBACK FindFrame(HWND window, LPARAM threadAddress) {
    DWORD pid; DWORD thread = GetWindowThreadProcessId(window, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    ShowWindow(window, SW_HIDE);
    char name[128]{}; GetClassNameA(window, name, sizeof(name));
    if (strstr(name, "WEditorFrame")) { frame = window; *reinterpret_cast<DWORD*>(threadAddress) = thread; }
    return TRUE;
}

DWORD WINAPI Start(void*) {
    for (int attempt = 0; attempt < 240; ++attempt) {
        DWORD thread = 0; EnumWindows(FindFrame, reinterpret_cast<LPARAM>(&thread));
        auto editor = *reinterpret_cast<unsigned char**>(editorAddress);
        if (thread && editor && *reinterpret_cast<void**>(editor + 0x130)) {
            Sleep(5000);
            hook = SetWindowsHookExA(WH_CALLWNDPROC, Message, nullptr, thread);
            DWORD_PTR result;
            if (hook) SendMessageTimeoutA(frame, runMessage, 0, 0, SMTO_ABORTIFHUNG, 180000, &result);
            return 0;
        }
        Sleep(250);
    }
    return 1;
}
}

BOOL WINAPI DllMain(HMODULE module, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        char path[MAX_PATH]{}; GetModuleFileNameA(module, path, MAX_PATH);
        directory = std::filesystem::path(path).parent_path();
        if (HANDLE worker = CreateThread(nullptr, 0, Start, nullptr, 0, nullptr)) CloseHandle(worker);
    }
    return TRUE;
}
