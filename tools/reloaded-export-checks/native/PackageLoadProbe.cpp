// Injected only into the disposable SCCT editor launched by Check-NativePackages.ps1.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <share.h>
#include <string>
#include <vector>

namespace {
std::filesystem::path directory;
HWND frame;
HHOOK hook;
FILE* report;
constexpr UINT runMessage = WM_APP + 0x361;
constexpr UINT finishMessage = WM_APP + 0x362;
bool loaded;
std::atomic<bool> commandsDone;
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

void Bounds() {
    auto editor = *reinterpret_cast<unsigned char**>(editorAddress);
    auto level = editor ? *reinterpret_cast<unsigned char**>(editor + 0x130) : nullptr;
    auto model = level ? *reinterpret_cast<unsigned char**>(level + 0x13C) : nullptr;
    if (!model) return;
    auto points = *reinterpret_cast<float**>(model + 0x84);
    int count = *reinterpret_cast<int*>(model + 0x88);
    if (count <= 0 || count > 10000000) return;
    float lo[3] = {INFINITY,INFINITY,INFINITY}, hi[3] = {-INFINITY,-INFINITY,-INFINITY};
    for (int i=0; i<count; ++i) for (int j=0; j<3; ++j) {
        lo[j] = (std::min)(lo[j],points[i*3+j]); hi[j] = (std::max)(hi[j],points[i*3+j]);
    }
    fprintf(report,"bounds points=%d min=%.5f,%.5f,%.5f max=%.5f,%.5f,%.5f\n",count,lo[0],lo[1],lo[2],hi[0],hi[1],hi[2]); fflush(report);
}

void Run() {
    static std::vector<std::string> commands;
    static size_t next;
    if (!report) {
        report = _fsopen((directory / "package-load-report.txt").string().c_str(), "w", _SH_DENYNO);
        if (!report) { commandsDone = true; return; }
        fprintf(report, "ready pid=%lu\n", GetCurrentProcessId()); fflush(report);
        for (int wait = 0; std::filesystem::exists(directory / "package-load-wait.txt") && wait < 600; ++wait) Sleep(100);
        std::ifstream input(directory / "package-load-commands.txt");
        std::string command;
        while (std::getline(input, command)) {
            if (!command.empty() && command.back() == '\r') command.pop_back();
            if (!command.empty()) commands.push_back(command);
        }
    }
    if (next < commands.size()) {
        if (!Exec(commands[next++])) { fputs("FAIL Native command was refused.\n", report); fflush(report); commandsDone = true; return; }
        Bounds();
        if (next < commands.size()) return;
    }
    commandsDone = true;
    auto editor = *reinterpret_cast<unsigned char**>(editorAddress);
    auto level = *reinterpret_cast<unsigned char**>(editor + 0x130);
    auto model = level ? *reinterpret_cast<unsigned char**>(level + 0x13C) : nullptr;
    int actors = level ? *reinterpret_cast<int*>(level + 0x30) : 0;
    int surfaces = model ? *reinterpret_cast<int*>(model + 0x98) : 0;
    fprintf(report, "loaded actors=%d surfaces=%d\n", actors, surfaces);
    loaded = actors > 1 && surfaces > 0;
    if (!loaded) fputs("FAIL The loaded map has no actors or geometry.\n", report);
    fflush(report);
}

LRESULT CALLBACK Message(int code, WPARAM w, LPARAM l) {
    if (code >= 0 && reinterpret_cast<CWPSTRUCT*>(l)->message == runMessage) {
        Run();
    }
    if (code >= 0 && reinterpret_cast<CWPSTRUCT*>(l)->message == finishMessage) {
        UnhookWindowsHookEx(hook); hook = nullptr;
        if (loaded) { fputs("PASS Native interface/map loaded, reopened and remained stable through viewport frames.\n", report); fflush(report); }
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
            if (hook) {
                // Let viewport/camera state settle between commands, as separate File menu actions do.
                while (!commandsDone) {
                    if (!SendMessageTimeoutA(frame, runMessage, 0, 0, SMTO_ABORTIFHUNG, 180000, &result)) return 1;
                    Sleep(750);
                }
                Sleep(12000);
                SendMessageTimeoutA(frame, finishMessage, 0, 0, SMTO_ABORTIFHUNG, 30000, &result);
            }
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
